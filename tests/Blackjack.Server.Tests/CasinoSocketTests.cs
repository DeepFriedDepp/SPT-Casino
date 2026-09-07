using System.Net.WebSockets;
using System.Text;
using Casino.Server;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace Blackjack.Server.Tests;

/// <summary>
/// The push channel, with a stand-in for the socket.
///
/// These live here rather than in a project of their own because
/// `Blackjack.Server.Tests` is the only suite that already references `Casino.Server`,
/// and a second test project for one class would be a worse trade than a slightly
/// misnamed folder. The class under test belongs to no table.
///
/// `System.Net.WebSockets.WebSocket` is abstract, which is the thing that makes any of
/// this testable: everything except the network is real code. What is *not* covered is
/// said plainly at the bottom of this file.
/// </summary>
public class CasinoSocketTests
{
    private readonly CasinoSocket _socket = new(new SilentLogger<CasinoSocket>());

    // ---------------------------------------------------------------------------
    // Reading the session out of the header. This is the part that, when it is wrong,
    // does not fail -- it addresses somebody else.
    // ---------------------------------------------------------------------------

    [Fact]
    public void AWellFormedBasicHeaderYieldsTheSession()
    {
        Assert.Equal(
            "65f1b2c3d4e5f60718293a4b",
            CasinoSocket.SessionFromAuthorization(Basic("65f1b2c3d4e5f60718293a4b", string.Empty)));
    }

    /// <summary>
    /// What our own client actually sends: `SetCredentials(sessionId, "", true)`, so an
    /// empty password and a trailing colon.
    /// </summary>
    [Fact]
    public void AnEmptyPasswordIsWhatTheRealClientSendsAndIsFine()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("abc123:"));

        Assert.Equal("abc123", CasinoSocket.SessionFromAuthorization(header));
    }

    /// <summary>
    /// A colon in the password must not move the field boundary. Splitting and taking
    /// element zero gets this right; anything that reasons about the last colon, or
    /// counts fields, does not.
    /// </summary>
    [Fact]
    public void AColonInThePasswordDoesNotChangeTheSession()
    {
        Assert.Equal("abc123", CasinoSocket.SessionFromAuthorization(Basic("abc123", "pass:word:")));
    }

    /// <summary>
    /// No header is the ordinary case for anything that is not our client. ASP.NET gives
    /// back an empty string rather than null for a missing header, so both shapes are
    /// asserted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingHeaderYieldsNothing(string? header)
    {
        Assert.Null(CasinoSocket.SessionFromAuthorization(header));
    }

    /// <summary>
    /// Malformed headers, every one of which crashes the implementation this was written
    /// from. `Split(' ')[1]` throws on the one-word cases, `FromBase64String` throws on
    /// the rest, and both throw out of a connection callback rather than refusing a
    /// connection.
    /// </summary>
    [Theory]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    [InlineData("YWJjOg==")]
    [InlineData("Basic not-base-64!!")]
    [InlineData("Basic YWJjOg== extra")]
    public void AMalformedHeaderYieldsNothingRatherThanThrowing(string header)
    {
        Assert.Null(CasinoSocket.SessionFromAuthorization(header));
    }

    /// <summary>
    /// No colon means no field boundary, and the implementation this was written from
    /// takes the whole decoded string as the session. Refused instead: our client always
    /// emits a colon, so a header without one did not come from us, and the value would
    /// be a session id nobody chose.
    /// </summary>
    [Fact]
    public void AHeaderWithNoColonYieldsNothing()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("abc123"));

        Assert.Null(CasinoSocket.SessionFromAuthorization(header));
    }

    /// <summary>An empty session is as unaddressable as no session.</summary>
    [Fact]
    public void AnEmptySessionYieldsNothing()
    {
        Assert.Null(CasinoSocket.SessionFromAuthorization(Basic(string.Empty, "password")));
    }

    // ---------------------------------------------------------------------------
    // The connection map.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AConnectedPlayerIsAddressableByTheSessionFromTheHeader()
    {
        var session = new MongoId();
        var ws = await Connect(session);

        Assert.True(_socket.IsConnected(session));
        Assert.True(await _socket.SendAsync(session, "{\"Table\":\"poker\"}"));
        Assert.Equal("{\"Table\":\"poker\"}", Assert.Single(ws.Sent));
    }

    /// <summary>
    /// A connection with a header nobody can read is refused, not kept. If this fails,
    /// the server is holding a socket it can never address.
    /// </summary>
    [Fact]
    public async Task AConnectionWithNoUsableSessionIsClosed()
    {
        var ws = new FakeWebSocket();
        var context = new DefaultHttpContext();

        await _socket.OnConnection(ws, context, "20260907120000000");

        Assert.Equal(0, _socket.ConnectedCount);
        Assert.True(ws.Closed);
    }

    /// <summary>
    /// The alt-F4 case, which is the one that has to be boring. Nobody is connected, and
    /// the push is a no-op rather than a `KeyNotFoundException` coming out of the middle
    /// of a hand.
    /// </summary>
    [Fact]
    public async Task PushingToSomebodyWhoIsNotConnectedIsANoOp()
    {
        var absent = new MongoId();

        Assert.False(_socket.IsConnected(absent));
        Assert.False(await _socket.SendAsync(absent, "anybody there?"));
    }

    [Fact]
    public async Task PushingToSeveralReachesTheOnesWhoAreThereAndCountsThem()
    {
        var here = new MongoId();
        var alsoHere = new MongoId();
        var gone = new MongoId();

        var first = await Connect(here);
        var second = await Connect(alsoHere);

        var reached = await _socket.SendAsync([here, alsoHere, gone], "the flop");

        Assert.Equal(2, reached);
        Assert.Equal("the flop", Assert.Single(first.Sent));
        Assert.Equal("the flop", Assert.Single(second.Sent));
    }

    /// <summary>
    /// Reconnecting replaces the old socket.
    ///
    /// This is the defect in the implementation this was written from, which uses
    /// `TryAdd` -- so the second connection is dropped on the floor and every push for
    /// the rest of the session goes to the socket that just died. A player who drops and
    /// comes back is the common case, not the rare one.
    /// </summary>
    [Fact]
    public async Task ReconnectingReplacesTheOldSocketAndTheOldOneIsAborted()
    {
        var session = new MongoId();

        var dropped = await Connect(session);
        var fresh = await Connect(session);

        Assert.True(dropped.Aborted);
        Assert.Equal(1, _socket.ConnectedCount);
        Assert.True(await _socket.SendAsync(session, "you are back"));

        Assert.Empty(dropped.Sent);
        Assert.Equal("you are back", Assert.Single(fresh.Sent));
    }

    /// <summary>
    /// The late close: a dropped socket's `OnClose` arrives after the player has already
    /// reconnected, and must not evict the live connection.
    ///
    /// What actually protects that is `OnClose` matching on the **socket**, so a stale
    /// close finds nothing left to remove. Anything that recovers the session some other
    /// way and deletes by key breaks this, and that is what this test is here to catch --
    /// it does, verified: changing `OnConnection` to the template's `TryAdd` fails this
    /// with `Assert.True() Failure`.
    ///
    /// It does **not** cover the `TryRemove(KeyValuePair)` overload in `OnClose`, which
    /// guards a narrower race between the scan and the removal; swapping that for
    /// `TryRemove(key, out _)` leaves this green. Said out loud rather than left implied,
    /// because a test that is believed to cover something it does not is worse than no
    /// test at all.
    /// </summary>
    [Fact]
    public async Task ACloseFromTheOldSocketDoesNotEvictTheNewOne()
    {
        var session = new MongoId();

        var dropped = await Connect(session);
        var fresh = await Connect(session);

        await _socket.OnClose(dropped, new DefaultHttpContext(), "20260907120000000");

        Assert.True(_socket.IsConnected(session));
        Assert.True(await _socket.SendAsync(session, "still here"));
        Assert.Equal("still here", Assert.Single(fresh.Sent));
    }

    [Fact]
    public async Task ClosingRemovesTheConnection()
    {
        var session = new MongoId();
        var ws = await Connect(session);

        await _socket.OnClose(ws, new DefaultHttpContext(), "20260907120000000");

        Assert.False(_socket.IsConnected(session));
        Assert.Equal(0, _socket.ConnectedCount);
    }

    /// <summary>
    /// A socket that throws is forgotten rather than pushed to forever. Without this the
    /// map grows for the life of the server and every broadcast pays for every player who
    /// ever visited.
    /// </summary>
    [Fact]
    public async Task ASocketThatFailsOnSendIsDropped()
    {
        var session = new MongoId();
        var ws = await Connect(session);

        ws.ThrowOnSend = true;

        Assert.False(await _socket.SendAsync(session, "into the void"));
        Assert.Equal(0, _socket.ConnectedCount);
        Assert.False(_socket.IsConnected(session));
    }

    /// <summary>
    /// A push abandoned by its own caller must not cost the player their connection.
    ///
    /// The drop is deliberately conditional on the socket having actually died, and this
    /// is the case that distinguishes the two. Drop on any failed push and one request
    /// timing out somewhere else disconnects a player who is sitting there perfectly
    /// happily, and the only symptom is that their table goes quiet.
    /// </summary>
    [Fact]
    public async Task APushCancelledByItsCallerLeavesTheConnectionAlone()
    {
        var session = new MongoId();
        var ws = await Connect(session);

        var abandoned = await _socket.SendAsync(session, "never mind", new CancellationToken(true));

        Assert.False(abandoned);
        Assert.Empty(ws.Sent);
        Assert.False(ws.Aborted);
        Assert.True(_socket.IsConnected(session));
        Assert.Equal(1, _socket.ConnectedCount);
    }

    // ---------------------------------------------------------------------------
    // Two pushes, one socket.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Two pushes to one player must not be inside `SendAsync` at the same time.
    ///
    /// This is not a tidiness point. Concurrent sends on one `WebSocket` interleave the
    /// fragments of two messages into one stream, and what arrives is a single message
    /// made of both halves that fails to parse somewhere in the middle -- a fault that is
    /// very hard to read backwards from. Two seats acting in the same instant at one
    /// table is precisely what the shared-table work makes ordinary.
    ///
    /// Deterministic on purpose, in the same shape as `SessionGateTests`: the first push
    /// is held inside the fake socket until the test lets it go, rather than the test
    /// sleeping and hoping the overlap happens.
    /// </summary>
    [Fact]
    public async Task TwoPushesToOnePlayerDoNotOverlap()
    {
        var session = new MongoId();
        var ws = await Connect(session);

        ws.HoldTheFirstSend();

        var first = _socket.SendAsync(session, "first");
        await ws.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var second = _socket.SendAsync(session, "second");

        // Given 200ms to get it wrong rather than asserting on an instant that has not
        // happened yet. If the semaphore were gone, the second send would be inside the
        // socket already.
        var tooEarly = await Task.WhenAny(second, Task.Delay(200));
        Assert.NotSame(second, tooEarly);
        Assert.Equal(1, ws.MaxConcurrentSends);

        ws.ReleaseTheFirstSend();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, ws.MaxConcurrentSends);
        Assert.Equal(["first", "second"], ws.Sent);
    }

    // ---------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Drives a real connection through the real `OnConnection`, header and all, so the
    /// tests above exercise the parsing and the map together rather than reaching into
    /// the dictionary.
    /// </summary>
    private async Task<FakeWebSocket> Connect(MongoId session)
    {
        var ws = new FakeWebSocket();
        var context = new DefaultHttpContext();

        context.Request.Headers.Authorization = Basic(session.ToString(), string.Empty);

        // The third argument is a timestamp, not a session -- see CasinoSocket's remarks.
        // Passed as one here so that a future change keying off it fails these tests.
        await _socket.OnConnection(ws, context, "20260907120000000");

        return ws;
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    /// <summary>
    /// A socket that records instead of writing.
    ///
    /// Only the members `CasinoSocket` touches do anything; the rest exist because the
    /// base class is abstract. `ReceiveAsync` never returns, which is what a real idle
    /// socket does, and nothing in these tests awaits it.
    /// </summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly object _lock = new();

        private readonly TaskCompletionSource _firstSendStarted = new();

        private TaskCompletionSource? _held;

        private bool _heldUsed;

        private int _sendsInFlight;

        private WebSocketState _state = WebSocketState.Open;

        internal List<string> Sent { get; } = [];

        internal bool Closed { get; private set; }

        internal bool Aborted { get; private set; }

        internal bool ThrowOnSend { get; set; }

        internal int MaxConcurrentSends { get; private set; }

        /// <summary>Completes when a send has actually entered the socket.</summary>
        internal Task FirstSendStarted => _firstSendStarted.Task;

        internal void HoldTheFirstSend() => _held = new TaskCompletionSource();

        internal void ReleaseTheFirstSend() => _held?.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        /// <summary>
        /// A real `Abort` moves the socket to `Aborted`, and that transition is load
        /// bearing rather than cosmetic: it is how `CasinoSocket` tells a socket that
        /// died from a push that merely lost a race with the caller's own cancellation.
        /// A fake that stayed `Open` here would let a dropped-connection bug pass.
        /// </summary>
        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            Closed = true;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            Closed = true;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            new TaskCompletionSource<WebSocketReceiveResult>().Task;

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _sendsInFlight++;
                MaxConcurrentSends = Math.Max(MaxConcurrentSends, _sendsInFlight);
            }

            try
            {
                if (ThrowOnSend)
                {
                    throw new WebSocketException("the player closed the game");
                }

                TaskCompletionSource? held = null;

                lock (_lock)
                {
                    // Only the FIRST send waits, and the flag rather than clearing the
                    // field is the point: the second send must be blocked by the
                    // semaphore inside CasinoSocket rather than by this fake, or the test
                    // proves nothing -- and `ReleaseTheFirstSend` still needs something
                    // to release when it is called.
                    if (_held is not null && !_heldUsed)
                    {
                        _heldUsed = true;
                        held = _held;
                    }
                }

                if (held is not null)
                {
                    _firstSendStarted.TrySetResult();
                    await held.Task.ConfigureAwait(false);
                }

                lock (_lock)
                {
                    Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
                }
            }
            finally
            {
                lock (_lock)
                {
                    _sendsInFlight--;
                }
            }
        }
    }

    /// <summary>Swallows everything, so a test run stays readable.</summary>
    private sealed class SilentLogger<T> : ISptLogger<T>
    {
        public void LogWithColor(
            string data,
            LogTextColor? textColor = null,
            LogBackgroundColor? backgroundColor = null,
            Exception? ex = null)
        {
        }

        public void Success(string data, Exception? ex = null)
        {
        }

        public void Error(string data, Exception? ex = null)
        {
        }

        public void Warning(string data, Exception? ex = null)
        {
        }

        public void Info(string data, Exception? ex = null)
        {
        }

        public void Debug(string data, Exception? ex = null)
        {
        }

        public void Critical(string data, Exception? ex = null)
        {
        }

        public void Log(
            LogLevel level,
            string data,
            LogTextColor? textColor = null,
            LogBackgroundColor? backgroundColor = null,
            Exception? ex = null)
        {
        }

        public bool IsLogEnabled(LogLevel level) => false;

        public void DumpAndStop()
        {
        }
    }
}
