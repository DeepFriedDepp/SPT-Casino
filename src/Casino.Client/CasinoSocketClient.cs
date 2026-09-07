using System;
using System.Collections.Concurrent;
using SPT.Common.Http;
using WebSocket = WebSocketSharp.WebSocket;
using WebSocketState = WebSocketSharp.WebSocketState;

namespace Casino.Client
{
    /// <summary>
    /// The client end of the casino's push channel. One socket, all tables.
    ///
    /// The server end is <c>Casino.Server.CasinoSocket</c>, and its remarks carry the
    /// reasoning for why there is one of these rather than one per table. In short: a
    /// player is at one table at a time, so four sockets would be three idle ones and
    /// four reconnect clocks racing each other to notice the server came back.
    ///
    /// ## Everything here happens on two threads, and that is the whole design
    ///
    /// websocket-sharp raises <c>OnMessage</c>, <c>OnClose</c> and <c>OnError</c> on its
    /// own receive thread. **Unity's API cannot be touched from there** -- a panel that
    /// redraws a card in a message handler throws
    /// "can only be called from the main thread", and it throws inside a library
    /// callback where the stack trace names nothing recognisable.
    ///
    /// So the callbacks below do exactly one thing: put the message in a queue. Nothing
    /// else in this class runs on that thread. <see cref="Pump"/> drains the queue and
    /// raises <see cref="MessageReceived"/> on whatever thread calls it, and the owner
    /// calls it from <c>Update</c> or a coroutine -- which is the main thread, which is
    /// where the panel can be touched.
    ///
    /// ## Reconnection is driven by Pump too, and there is no timer
    ///
    /// A player whose socket drops must not be stuck at a table with a dead pipe, and the
    /// panel polls as a fallback, so this has to come back on its own. The obvious way is
    /// a <c>System.Threading.Timer</c>; it is not used, and deliberately. A timer is a
    /// third thread, with its own race against <see cref="Close"/>, to do work that is
    /// already needed once a frame. <see cref="Pump"/> is called every frame by an owner
    /// who is looking at a table, so the reconnect decision lives there: cheap when
    /// connected, and it stops entirely when nobody is pumping -- which is exactly when
    /// nobody is listening anyway.
    ///
    /// The backoff exists because the failure this is most likely to hit is a server that
    /// is not running. Retrying every frame would be several hundred connection attempts
    /// a second against a machine that is not answering, which is worse than the outage.
    /// </summary>
    internal sealed class CasinoSocketClient
    {
        /// <summary>
        /// Must match <c>Casino.Server.CasinoSocket.HookUrl</c> exactly. SPT dispatches
        /// on this string with no negotiation and no error when it does not match: the
        /// socket opens against nothing and simply never delivers anything.
        /// </summary>
        internal const string HookPath = "/casino/ws";

        /// <summary>
        /// First retry after this, doubling to <see cref="MaxRetry"/>.
        ///
        /// A second is short enough that a player who alt-tabbed through a brief server
        /// hiccup never sees it, and long enough that a server which is down is not being
        /// hammered once a frame.
        /// </summary>
        private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The ceiling on the backoff. Fifteen seconds is the longest a player should sit
        /// at a table wondering why nothing is happening, and the panel is polling the
        /// whole time, so nothing is actually lost in the gap -- it is only slower.
        /// </summary>
        private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How many undelivered messages are kept before the oldest start being dropped.
        ///
        /// Unbounded is the wrong answer for a queue whose drain is a caller's promise:
        /// an owner that stops calling <see cref="Pump"/> without calling
        /// <see cref="Close"/> would otherwise grow this forever, which is a leak that
        /// only shows up in a long session. Dropping the *oldest* because these are
        /// notifications rather than a ledger -- the newest state is the one worth having,
        /// and 256 frames behind is already far past the point where the panel's polling
        /// fallback has taken over.
        /// </summary>
        private const int MaxQueued = 256;

        /// <summary>
        /// Filled on websocket-sharp's thread, drained on Unity's. A queue rather than a
        /// lock and a list because the two ends genuinely are two threads and this is the
        /// one place they meet.
        /// </summary>
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();

        private WebSocket _socket;

        /// <summary>
        /// Whether the owner has asked to be connected. Every reconnect decision reads
        /// this, so <see cref="Close"/> is enough to stop the whole thing: without it, a
        /// close would be immediately followed by the OnClose handler reconnecting.
        ///
        /// Volatile because <see cref="HandleClose"/> reads it on websocket-sharp's thread
        /// while <see cref="Close"/> writes it on Unity's.
        /// </summary>
        private volatile bool _wanted;

        /// <summary>
        /// Set on the socket's thread when a connection opens, and cleared by
        /// <see cref="Pump"/>, which is the only thing that touches
        /// <see cref="_backoff"/>.
        ///
        /// A flag rather than resetting the backoff from the callback directly: a
        /// `TimeSpan` is a struct with no atomic write, so a socket thread assigning it
        /// while the main thread is reading it can be torn into a value neither of them
        /// meant. A `volatile bool` cannot be, and it keeps every piece of the retry
        /// arithmetic on one thread.
        /// </summary>
        private volatile bool _opened;

        private TimeSpan _backoff = FirstRetry;

        private DateTime _nextAttemptUtc = DateTime.MinValue;

        /// <summary>
        /// Raised once per message, on the thread that called <see cref="Pump"/>.
        ///
        /// That is the contract and it matters: raise it from the socket's own thread
        /// instead and every handler becomes a Unity-thread violation waiting to happen.
        /// The payload is the raw text the server sent; this class does not know what is
        /// in it and deliberately does not parse it, because the envelope belongs to
        /// whoever is sending.
        /// </summary>
        internal event Action<string> MessageReceived;

        /// <summary>Whether the socket is open right now.</summary>
        internal bool Connected => _socket != null && _socket.ReadyState == WebSocketState.Open;

        /// <summary>
        /// Open, or still trying. <see cref="Pump"/> uses this rather than
        /// <see cref="Connected"/> to decide whether to attempt, and the difference is
        /// the point: a connect that is still in flight when the backoff elapses would
        /// otherwise be torn down and started again, forever, against exactly the slow
        /// server that most needs to be left alone to finish.
        /// </summary>
        private bool Busy =>
            _socket != null
            && (_socket.ReadyState == WebSocketState.Open
                || _socket.ReadyState == WebSocketState.Connecting);

        /// <summary>
        /// Asks to be connected, and keeps asking. Cheap and safe to call again.
        ///
        /// It does not connect here -- <see cref="Pump"/> does, on the next call. That
        /// keeps every connection attempt on one code path, so the first one and the
        /// hundredth reconnect behave identically rather than the first one being a
        /// special case that gets tested and the other one not.
        /// </summary>
        internal void Open()
        {
            _wanted = true;
            _backoff = FirstRetry;
            _nextAttemptUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Call once a frame from Unity's main thread while a table is open.
        ///
        /// Drains whatever arrived and reconnects if it is time to. Doing both here is
        /// what keeps this class down to the one background thread that websocket-sharp
        /// insists on.
        /// </summary>
        internal void Pump()
        {
            string message;

            // Bounded by what is in the queue when the loop starts, near enough: a burst
            // is drained in one frame, and a server sending faster than the game renders
            // is a different problem than this loop.
            while (_incoming.TryDequeue(out message))
            {
                try
                {
                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    // A panel that throws on one message must not stop the rest being
                    // delivered, and must not take the frame down with it. The socket is
                    // fine; it is the handler that is not.
                    CasinoPlugin.Log?.LogError($"[Casino] a socket message handler threw: {ex.Message}");
                }
            }

            if (_opened)
            {
                // A connection came up since the last frame. Reset here rather than in the
                // callback -- see the field.
                _opened = false;
                _backoff = FirstRetry;
            }

            if (!_wanted || Busy || DateTime.UtcNow < _nextAttemptUtc)
            {
                return;
            }

            Attempt();
        }

        /// <summary>
        /// Stops wanting to be connected and lets the socket go.
        ///
        /// <c>CloseAsync</c> rather than <c>Close</c>: websocket-sharp's synchronous
        /// close waits for the server's close frame, and this is called from a panel
        /// closing on a keypress. A player pressing escape must not hold the frame for a
        /// network round trip, still less for a timeout when the server is the thing that
        /// went away.
        /// </summary>
        internal void Close()
        {
            _wanted = false;

            var socket = _socket;
            _socket = null;

            if (socket == null)
            {
                return;
            }

            Detach(socket);

            try
            {
                socket.CloseAsync();
            }
            catch (Exception ex)
            {
                CasinoPlugin.Log?.LogWarning($"[Casino] closing the socket threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the websocket URL from whatever SPT is currently pointed at.
        ///
        /// <c>Replace("http", "ws")</c> looks like a bug and is not: it turns
        /// <c>http://</c> into <c>ws://</c> and, because the substring is a prefix of it,
        /// <c>https://</c> into <c>wss://</c>. Both are what is wanted, and a scheme-aware
        /// version would have to special-case the one this already gets right.
        ///
        /// Read fresh on every attempt rather than cached in a constructor, because the
        /// constructor can run before the player has picked a profile. A host captured
        /// then is a host from before login.
        /// </summary>
        internal static string BuildUrl(string host) =>
            (host ?? string.Empty).Replace("http", "ws") + HookPath;

        private void Attempt()
        {
            // Backed off first, so that every early return below still costs one attempt
            // rather than spinning. Doubling with a ceiling; reset only by a connection
            // that actually opened, in HandleOpen.
            _nextAttemptUtc = DateTime.UtcNow + _backoff;

            var doubled = _backoff + _backoff;
            _backoff = doubled > MaxRetry ? MaxRetry : doubled;

            string host;
            string session;

            try
            {
                host = RequestHandler.Host;
                session = RequestHandler.SessionId;
            }
            catch (Exception ex)
            {
                // Asked for before SPT is ready. Not an error -- the backoff will bring
                // it back round, and by then the player will have logged in.
                CasinoPlugin.Log?.LogDebug($"[Casino] no backend to connect to yet: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(session))
            {
                // The same case, without the exception. The server reads the session out
                // of the Authorization header and refuses a connection that has none, so
                // connecting now would burn a socket to be told what is already known.
                return;
            }

            // A fresh WebSocket per attempt rather than reconnecting the one that just
            // died. websocket-sharp will let a closed instance be connected again, but it
            // carries its old state and its old handlers across, and reconnection on a
            // reused instance is the part of that library people report as flaky. A few
            // objects a minute in the worst case is not a cost worth reasoning about.
            var previous = _socket;

            if (previous != null)
            {
                // Detached before it is let go, so its close cannot come back through the
                // handlers of a client that has already moved on to a new socket.
                Detach(previous);

                try
                {
                    previous.CloseAsync();
                }
                catch (Exception)
                {
                    // It was already dead -- that is why there is a new one.
                }
            }

            var socket = new WebSocket(BuildUrl(host));

            // Pre-emptive, which is the whole point of the third argument. With it false,
            // websocket-sharp waits to be challenged with a 401 before it sends anything
            // -- and the SPT handshake never challenges, so the Authorization header would
            // never be sent, the server would find no session, and it would refuse the
            // connection. The password is empty because the server only reads the field
            // before the colon.
            socket.SetCredentials(session, string.Empty, true);

            socket.OnOpen += HandleOpen;
            socket.OnMessage += HandleMessage;
            socket.OnError += HandleError;
            socket.OnClose += HandleClose;

            _socket = socket;

            try
            {
                // Async because the synchronous Connect blocks until the TCP connection
                // is made or fails. On the main thread that is a frozen game for as long
                // as the operating system takes to give up on a server that is not
                // listening, which is the single most likely thing to be wrong.
                socket.ConnectAsync();
            }
            catch (Exception ex)
            {
                CasinoPlugin.Log?.LogWarning($"[Casino] websocket connect failed: {ex.Message}");
            }
        }

        private void Detach(WebSocket socket)
        {
            socket.OnOpen -= HandleOpen;
            socket.OnMessage -= HandleMessage;
            socket.OnError -= HandleError;
            socket.OnClose -= HandleClose;
        }

        /// <summary>
        /// The only thing that clears the backoff, and it has to be a connection that
        /// actually opened rather than one that was merely attempted.
        ///
        /// Clear it on close instead and the backoff does nothing at all: a server that is
        /// refusing connections closes each one the instant it arrives, so every retry
        /// would start again from one second and the ceiling would never be reached.
        /// </summary>
        private void HandleOpen(object sender, EventArgs e)
        {
            _opened = true;
            CasinoPlugin.Log?.LogDebug("[Casino] push channel open.");
        }

        private void HandleMessage(object sender, WebSocketSharp.MessageEventArgs e)
        {
            // Text only. Ping and binary frames both arrive here on some configurations,
            // and an empty keepalive delivered to a panel that parses JSON is an exception
            // once a minute for no reason.
            if (!e.IsText || e.Data == null)
            {
                return;
            }

            // Queued, never dispatched here: this is websocket-sharp's thread, and the
            // handler on the other end of the event draws things. See the class remarks.
            _incoming.Enqueue(e.Data);

            string dropped;

            while (_incoming.Count > MaxQueued && _incoming.TryDequeue(out dropped))
            {
            }
        }

        private void HandleError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            CasinoPlugin.Log?.LogWarning($"[Casino] websocket error: {e.Message}");
        }

        private void HandleClose(object sender, WebSocketSharp.CloseEventArgs e)
        {
            // Nothing to schedule: Pump reconnects on its own once the socket stops being
            // Busy, and the backoff is already where the last attempt left it. See
            // HandleOpen for why it is not reset here.
            //
            // Logged only for the current socket, so a stale one closing after a reconnect
            // does not report an outage that is already over.
            if (ReferenceEquals(sender, _socket) && _wanted)
            {
                CasinoPlugin.Log?.LogDebug($"[Casino] push channel closed ({e.Code}), will retry.");
            }
        }
    }
}
