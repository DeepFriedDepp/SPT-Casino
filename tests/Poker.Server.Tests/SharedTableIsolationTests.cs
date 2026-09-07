using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace Poker.Server.Tests;

/// <summary>
/// One player, one poker table.
///
/// ## The defect these were written against
///
/// **There is one escrow row per session, and shared tables gave a session two places to
/// owe money from.** `escrow-poker.json` holds a single <see cref="OutstandingStack"/>
/// keyed by session, which was correct while a player could only sit at their own table.
///
/// `PokerService.RefundAbandoned` runs on sit, on state and on cash-out, and decides a
/// stack is orphaned by asking whether the PRIVATE <see cref="TableStore"/> has a table
/// for this session. A stack belonging to a SHARED table fails that test, so the whole
/// buy-in is handed back as a refund: the player is credited money they have not lost, the
/// row is dropped, and standing up from the shared table then pays their stack out again.
///
/// Found while building blackjack's shared tables, which would have shipped the identical
/// hole. This one is not hypothetical -- it is in the poker people are already playing,
/// and a buy-in is two million roubles.
///
/// The rule is the one the game already implies -- **you are at one table** -- enforced
/// from both sides, because either half alone leaves the opposite order open.
/// </summary>
public class SharedTableIsolationTests
{
    private const int BuyIn = 2_000_000;

    private const int Stash = 20_000_000;

    private readonly MongoId _session = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedTableStore _shared = new();
    private readonly TableStore _private = new();

    private readonly SessionGate _sessions = new();
    private readonly TableGate _tableGates = new();

    public SharedTableIsolationTests()
    {
        _bank.Seed(Wallet.Roubles, Stash);
        _profiles.Names[_session.ToString()] = "Ragman_Fan";
    }

    /// <summary>
    /// The mint, in the order a player would stumble into it: buy into a shared table,
    /// then open the private one.
    ///
    /// Without the guard the private table refunds the shared buy-in on sight.
    /// </summary>
    [Fact]
    public async Task OpeningThePrivateTableDoesNotRefundASharedTablesBuyIn()
    {
        await SitShared();

        var balanceBefore = _bank.GetBalance(_session, Wallet.Roubles);
        var creditsBefore = _bank.Credits;

        var state = await Solo().StateAsync(_session, new ItemEventRouterResponse());

        // Money first, so a failure names the mint rather than the flag.
        Assert.Equal(balanceBefore, _bank.GetBalance(_session, Wallet.Roubles));
        Assert.Equal(creditsBefore, _bank.Credits);

        // And the shared table is still recorded as holding the stack. A refund that took
        // the row would leave the cash-out to settle against nothing.
        Assert.NotNull(_escrow.Get(_session));

        Assert.False(state.Ok);
    }

    /// <summary>
    /// The same defect through the sit route, which takes a second buy-in on top.
    /// </summary>
    [Fact]
    public async Task SittingAtThePrivateTableIsRefusedWhileAtASharedOne()
    {
        await SitShared();

        var balanceBefore = _bank.GetBalance(_session, Wallet.Roubles);

        var sat = await Solo().SitAsync(Open(), _session, new ItemEventRouterResponse());

        Assert.Equal(balanceBefore, _bank.GetBalance(_session, Wallet.Roubles));
        Assert.NotNull(_escrow.Get(_session));

        Assert.False(sat.Ok);
        Assert.Contains("shared table", sat.Error);
    }

    /// <summary>
    /// The other order: a private table already holds a stack, and the player tries to buy
    /// into a shared one. Left open, the shared cash-out releases the row and the private
    /// stack stops being recoverable.
    /// </summary>
    [Fact]
    public async Task BuyingIntoASharedTableIsRefusedWhileThePrivateOneHoldsAStack()
    {
        await Solo().SitAsync(Open(), _session, new ItemEventRouterResponse());

        Assert.NotNull(_escrow.Get(_session));

        var opened = await Shared().CreateAsync(Open(), _session, new ItemEventRouterResponse());

        Assert.False(opened.Ok);

        // Refused before anything was claimed, so they are not stranded at a table that
        // does not exist.
        Assert.Null(_shared.For(_session));
    }

    /// <summary>
    /// The guard must not fire for the ordinary player, who has never seen a shared table.
    /// A rule that also refuses the common case is not a fix.
    /// </summary>
    [Fact]
    public async Task APlayerWhoIsNotAtASharedTableSitsNormally()
    {
        var sat = await Solo().SitAsync(Open(), _session, new ItemEventRouterResponse());

        Assert.True(sat.Ok, sat.Error);
        Assert.Equal(Stash - BuyIn, _bank.GetBalance(_session, Wallet.Roubles));
    }

    /// <summary>
    /// And standing up gives the private table back. A guard that latched would lock a
    /// player out of the solo game for the rest of the server's life.
    /// </summary>
    [Fact]
    public async Task LeavingASharedTableGivesThePrivateTableBack()
    {
        await SitShared();

        var left = await Shared().LeaveAsync(_session, new ItemEventRouterResponse());
        Assert.True(left.Ok, left.Error);

        var sat = await Solo().SitAsync(Open(), _session, new ItemEventRouterResponse());
        Assert.True(sat.Ok, sat.Error);
    }

    private static SitRequest Open() =>
        new() { Seats = 4, BuyIn = BuyIn, BigBlind = 20_000, Seed = 1, Wallet = nameof(Wallet.Roubles) };

    /// <summary>
    /// Both services over one bank, one escrow and one pair of gates -- which is the point:
    /// in the server they are two singletons sharing exactly these.
    /// </summary>
    private PokerService Solo() => new(
        _bank, _sessions, _profiles, _private, _escrow, new FakeNames(), new SilentLog(), _shared);

    private SharedPokerService Shared() => new(
        _bank,
        _tableGates,
        _sessions,
        _shared,
        _profiles,
        _escrow,
        new FakeNames(),
        new CasinoSocket(new Silent<CasinoSocket>()),
        new SilentLog(),
        _private);

    private async Task SitShared()
    {
        var opened = await Shared().CreateAsync(Open(), _session, new ItemEventRouterResponse());

        Assert.True(opened.Ok, opened.Error);
    }

    private sealed class SilentLog : IPokerLog
    {
        public void Info(string message)
        {
        }

        public void Detail(string message)
        {
        }

        public void Error(string message)
        {
        }

        public Poker.Game.IGameLog ForEngine() => Poker.Game.GameLog.Null;
    }

    private sealed class Silent<T> : ISptLogger<T>
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
