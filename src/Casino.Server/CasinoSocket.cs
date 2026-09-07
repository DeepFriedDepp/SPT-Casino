using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Ws;

namespace Casino.Server;

/// <summary>
/// The casino's one push channel: server to client, one socket per player.
///
/// ## What it is for
///
/// Every route this mod owns is request/response, which is exactly right while a table
/// has one player at it: nothing happens that the player did not just ask for. Two
/// humans at one table breaks that. When the other seat raises, this seat has to hear
/// about it without having asked, and the only alternatives are polling -- a request per
/// player per frame, forever, on a server that is also running a raid -- or this.
///
/// It carries **notifications only**. Every action still goes over the HTTP routes,
/// because actions move money: they need the session gate, and they need an
/// `ItemEventRouterResponse` to hang the inventory changes off, neither of which exists
/// on a socket frame. A bet placed over this channel would be a bet that never reached
/// the player's stash. If you find yourself wanting to send a move up this pipe, the
/// answer is a route.
///
/// ## One socket for the whole casino, not one per table
///
/// A player can be at exactly one table at a time -- the lobby opens one panel and the
/// escape key closes it -- so a socket per table would spend three of its four
/// connections idle, and the fourth would be doing what a single one already does.
///
/// The cost of the other shape is not the socket, it is everything around it: SPT
/// dispatches by hook URL, so four tables means four handler classes, four
/// `[Injectable]` singletons, four connection maps, and on the client four objects each
/// with its own reconnect clock, each racing the others to notice the server came back.
/// Against that, one socket needs the message to say which table it came from, which is
/// one field in an envelope the integrator was going to need anyway.
///
/// It also matches how the rest of the server already thinks. <see cref="SessionGate"/>
/// keys by session because the thing being protected is the profile rather than the
/// table; the thing being addressed here is the player rather than the table, for the
/// same reason.
///
/// ## The parameter that is not what it says
///
/// `OnConnection` and `OnClose` are handed a third argument called `sessionIdContext`.
/// **It is not a session id.** `WebSocketServer` sets it to
/// `DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")` -- a per-connection timestamp. Key
/// the map by it and two things go wrong at once: nothing can address a player, because
/// no caller has that string; and two players connecting inside the same millisecond
/// get the same key, so one of them silently receives the other's hand.
///
/// The real session arrives in the `Authorization` header, which the client sets by
/// asking websocket-sharp for pre-emptive basic auth. Recovering it is
/// <see cref="SessionFromAuthorization"/>, and that method is where the tests are,
/// because it is the piece that addresses the wrong player if it is wrong rather than
/// failing.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class CasinoSocket : IWebSocketConnectionHandler
{
    /// <summary>
    /// Where the client connects. SPT dispatches on this string, so it and the path the
    /// client builds have to agree exactly; there is no negotiation and no error if
    /// they do not, just a socket that opens against nothing.
    /// </summary>
    public const string HookUrl = "/casino/ws";

    /// <summary>
    /// How long a single push is given, both to get its turn on the socket and to reach
    /// the wire once it has it.
    ///
    /// It is bounded rather than infinite because of what the failure looks like. A
    /// player who alt-F4s does not close the TCP connection -- the socket stays Open,
    /// the send buffer fills, and `SendAsync` simply stops completing. With an unbounded
    /// wait the table's push never returns, the next push queues behind it, and the seat
    /// wedges for everyone else at the table. Five seconds is several orders of
    /// magnitude more than a loopback push needs and short enough that it cannot outlast
    /// a turn timer.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Keyed by session id, case-insensitively.
    ///
    /// The key is a `string` and not a <see cref="MongoId"/> on purpose: it comes out of
    /// a header, so it can be anything at all, and `MongoId` is a packed 12-byte struct
    /// that rejects what is not 24 hex characters. Parsing it here would turn a
    /// malformed header into an exception thrown out of a connection callback rather
    /// than a connection quietly refused.
    ///
    /// Ordinal-ignore-case because session ids are hex, so no two distinct ids can
    /// collide under it, and because a single difference of case between what the client
    /// sends and what a caller passes would otherwise mean every push to that player
    /// silently goes nowhere.
    /// </summary>
    private readonly ConcurrentDictionary<string, Connection> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ISptLogger<CasinoSocket> _logger;

    public CasinoSocket(ISptLogger<CasinoSocket> logger)
    {
        _logger = logger;
    }

    /// <summary>How many players are on the channel. For diagnostics and tests.</summary>
    public int ConnectedCount => _connections.Count;

    public string GetHookUrl() => HookUrl;

    public string GetSocketId() => "Casino";

    /// <summary>
    /// Whether this player is on the channel right now.
    ///
    /// A table uses this to decide whether it can rely on being able to tell somebody
    /// something, or whether it has to wait for them to poll. It is a snapshot and
    /// nothing more -- the socket can die between this returning true and the push -- so
    /// it is a hint, never a guard. <see cref="SendAsync(MongoId, string, CancellationToken)"/>
    /// is safe on its own.
    /// </summary>
    public bool IsConnected(MongoId sessionId) =>
        _connections.TryGetValue(sessionId.ToString(), out var connection)
        && connection.Socket.State == WebSocketState.Open;

    /// <summary>
    /// Pushes one message to one player.
    /// </summary>
    /// <returns>
    /// True if it reached the socket. False means the player was not connected, or the
    /// socket died on the way -- both of which are ordinary. Somebody closing the game
    /// mid-hand is not an error condition, it is Tuesday, so this returns a bool instead
    /// of throwing and the caller carries on dealing.
    /// </returns>
    public Task<bool> SendAsync(
        MongoId sessionId,
        string message,
        CancellationToken cancellationToken = default) =>
        SendCoreAsync(sessionId.ToString(), message, cancellationToken);

    /// <summary>
    /// Pushes one message to several players -- everyone seated at a table, typically.
    /// </summary>
    /// <returns>How many of them it reached.</returns>
    public async Task<int> SendAsync(
        IEnumerable<MongoId> sessionIds,
        string message,
        CancellationToken cancellationToken = default)
    {
        // Concurrently, and this is the one place it matters. The sends are to different
        // sockets, so nothing here needs serialising -- and doing them in a loop would
        // mean one player whose client has stopped reading makes every other player at
        // the table wait out that player's SendTimeout before they see the card that was
        // just turned over.
        var pushes = sessionIds
            .Select(id => SendCoreAsync(id.ToString(), message, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(pushes).ConfigureAwait(false);

        return results.Count(delivered => delivered);
    }

    public Task OnConnection(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var sessionId = SessionFromAuthorization(context.Request.Headers.Authorization.ToString());

        if (sessionId is null)
        {
            _logger.Warning(
                "[Casino] a websocket arrived with no usable session in its Authorization header "
                + "and was refused. See Casino.Server.CasinoSocket.SessionFromAuthorization.");

            // Refused rather than kept: a connection nobody can address is a socket the
            // server reads from forever for no reason.
            return ws.CloseAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                "casino: no session",
                CancellationToken.None);
        }

        var connection = new Connection(ws);

        // Replacing, not adding. The template this was written from uses `TryAdd`, which
        // does nothing when the key is already there -- so a player who drops and
        // reconnects stays mapped to the socket that just died, and every push for the
        // rest of the session goes to a corpse. A reconnect is the ordinary case here,
        // not the exceptional one, so it has to win.
        //
        // Whatever it displaces is aborted rather than closed: the reason there is a new
        // socket is usually that the old one's peer is gone, and a close handshake with a
        // peer that is gone waits for a reply that is never coming.
        _connections.TryGetValue(sessionId, out var displaced);
        _connections[sessionId] = connection;
        displaced?.Abort();

        _logger.Debug($"[Casino] session {sessionId} joined the push channel.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The channel is one-way, so this does nothing on purpose.
    ///
    /// See the class remarks: a move sent up this pipe would have no session gate and no
    /// `ItemEventRouterResponse`, so it could not pay anybody. Anything a player does
    /// goes over a route.
    /// </summary>
    public Task OnMessage(
        byte[] rawData,
        WebSocketMessageType messageType,
        WebSocket ws,
        HttpContext context) => Task.CompletedTask;

    public Task OnClose(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        // Matched on the socket, because the argument that looks like a session id is a
        // timestamp -- see the class remarks. Matching on the socket is also what makes
        // the late close harmless: a player who dropped and reconnected has already had
        // the old socket replaced in the map, so the scan below finds nothing and the
        // live connection is left alone. Look the session up any other way and delete it,
        // and the dead socket's OnClose evicts the socket the player just opened.
        //
        // `TryRemove(KeyValuePair)` rather than `TryRemove(key, out _)` closes the rest
        // of that: a reconnect can land between this scan reading the pair and the
        // removal running, and the pair overload will not delete a value that is no
        // longer the one it read. The window is small, and it is exactly one frame of one
        // player's reconnect, which is the moment the map is most likely to be changing.
        foreach (var pair in _connections)
        {
            if (ReferenceEquals(pair.Value.Socket, ws))
            {
                _connections.TryRemove(pair);
                _logger.Debug($"[Casino] session {pair.Key} left the push channel.");
                break;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Digs the session id out of a basic-auth header: <c>Basic base64(session:password)</c>.
    ///
    /// This is the part that has tests, because it is the part that fails quietly. Every
    /// other mistake in this file produces a socket that does not work; a mistake here
    /// produces a socket that works and is attached to the wrong player.
    ///
    /// It is strict about the shape, and each piece of that is a crash or a
    /// misidentification it is refusing:
    ///
    /// - **No header at all** is the normal case for anything that is not our client, and
    ///   `Headers.Authorization.ToString()` gives back an empty string rather than null
    ///   for it, so both are handled.
    /// - **Exactly two space-separated parts.** The template does `Split(' ')[1]`, which
    ///   throws `IndexOutOfRangeException` out of a connection callback on any header
    ///   without a space -- and quietly takes the middle word of a three-part one.
    /// - **Base64 that decodes.** `Convert.FromBase64String` throws `FormatException` on
    ///   anything else, and that too would come out of the callback.
    /// - **A colon must be present.** Without one there is no field boundary, and the
    ///   template treats the entire decoded string as the session. Our client calls
    ///   `SetCredentials(sessionId, "", true)`, which always emits `sessionId:`, so a
    ///   header with no colon did not come from us and guessing at it is how a socket
    ///   ends up addressed to a session nobody meant.
    /// - **A non-empty session.** `":password"` decodes fine and yields nothing to
    ///   address.
    /// </summary>
    /// <returns>The session id, or null if the header does not carry one.</returns>
    public static string? SessionFromAuthorization(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var parts = authorizationHeader.Split(' ');

        if (parts.Length != 2 || parts[1].Length == 0)
        {
            return null;
        }

        string decoded;

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':');

        if (separator <= 0)
        {
            // -1 is no colon, 0 is an empty session. Both are unaddressable.
            return null;
        }

        // The first field only, and by index rather than by Split, so that a password
        // containing a colon cannot change which characters count as the session.
        return decoded.Substring(0, separator);
    }

    private async Task<bool> SendCoreAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(sessionId, out var connection))
        {
            return false;
        }

        var delivered = await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);

        // Dropped only when the socket itself is finished. A push can also fail because
        // the *caller's* token was cancelled, and evicting a perfectly healthy connection
        // for that would disconnect a player because somebody else's request timed out.
        if (!delivered && connection.Socket.State != WebSocketState.Open)
        {
            _connections.TryRemove(new KeyValuePair<string, Connection>(sessionId, connection));
        }

        return delivered;
    }

    /// <summary>
    /// One player's socket, and the lock that stops two pushes tearing each other's
    /// frame in half.
    ///
    /// `WebSocket.SendAsync` is explicitly not safe to call concurrently on one socket.
    /// It is not a data race in the usual sense -- it writes interleaved fragments of two
    /// messages into one stream, and the client sees a single message made of both, which
    /// then fails to parse as JSON somewhere in the middle. That is a hard failure to
    /// read backwards from, and it is easy to reach: two seats acting in the same instant
    /// at one table is the whole point of the feature this exists for.
    ///
    /// So every push takes this semaphore first. It is the same shape as
    /// <see cref="SessionGate"/> and bounded for the same reason, but deliberately not
    /// that class -- that one guards money and is keyed by session, and a push waiting on
    /// the gate would make a notification able to block a payout.
    /// </summary>
    private sealed class Connection(WebSocket socket)
    {
        private readonly SemaphoreSlim _sending = new(1, 1);

        internal WebSocket Socket { get; } = socket;

        internal async Task<bool> SendAsync(string message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return false;
            }

            try
            {
                if (!await _sending.WaitAsync(SendTimeout, cancellationToken).ConfigureAwait(false))
                {
                    // Somebody else has been mid-frame on this socket for five seconds,
                    // which means their send is not coming back. The socket cannot be
                    // reused -- a half-written frame is unfinishable -- so it is aborted,
                    // which also unblocks the stuck sender and lets it release this.
                    Abort();
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                // Re-checked inside the lock: it may have closed while this push was
                // queued behind another one.
                if (Socket.State != WebSocketState.Open)
                {
                    return false;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(SendTimeout);

                await Socket
                    .SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        deadline.Token)
                    .ConfigureAwait(false);

                return true;
            }
            catch (Exception)
            {
                // Deliberately everything. The exceptions a dead socket produces are
                // WebSocketException, ObjectDisposedException, InvalidOperationException
                // and OperationCanceledException depending on precisely how it died, and
                // enumerating them means the one that was missed takes down whichever
                // table was pushing. A player who is gone must cost the table nothing.
                //
                // Aborted in every case, including a cancelled send: a send that did not
                // finish leaves the frame incomplete, so the socket is unusable whatever
                // the reason.
                Abort();
                return false;
            }
            finally
            {
                _sending.Release();
            }
        }

        internal void Abort()
        {
            try
            {
                Socket.Abort();
            }
            catch (Exception)
            {
                // Abort on an already-disposed socket throws, and there is nothing left
                // to do about a socket that is more dead than we were trying to make it.
            }
        }
    }
}
