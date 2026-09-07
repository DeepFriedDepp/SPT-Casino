using Casino.Server;
using Poker.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace Poker.Server.Tests;

/// <summary>
/// A player who closes the game mid-hand must not freeze the table.
///
/// ## The defect these were written against
///
/// The engine runs its own bots, so only a HUMAN seat needs a request to act. When that
/// person's game closes, nobody sends one -- and the table waits on them forever. Standing
/// up is refused mid-hand ("Finish the hand first"), so everybody else is stuck behind that
/// seat with their chips on the table and no way to reach them. Only a server restart ends
/// it, and only by throwing the hand away.
///
/// `SharedSeat.LastSeenUtc` was written on every request and **read nowhere**. Worse than
/// that, <see cref="SharedPokerService.PushAsync"/> carried a comment claiming "the table
/// plays on and their seat times out", describing behaviour that did not exist -- a
/// documented guarantee is not a small thing to get wrong, because the next person to read
/// it stops looking.
///
/// Found in blackjack first, where the same fields were written and unread; poker had it
/// too, and poker is the one people have actually been playing.
///
/// ## Why folding, and not standing
///
/// The opposite choice from blackjack's, and both follow the game. Blackjack has no fold,
/// so an absent seat stands -- it keeps its hand and is paid whatever that hand wins.
/// Hold'em does have a fold, and it is the only move that is legal from every position
/// without putting more money in. Checking would be free where checking is legal and
/// illegal the moment there is a bet to answer; calling would spend an absent player's
/// chips on a hand nobody is playing.
///
/// Folding costs them what they have already put in, which is the ordinary rule for a
/// player who leaves the table, and it is what poker's own design notes said from the
/// start.
/// </summary>
public class AbsentSeatTests
{
    private const int BuyIn = 2_000_000;

    private const int Stash = 20_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedTableStore _store = new();
    private readonly SharedPokerService _service;

    public AbsentSeatTests()
    {
        _bank.Seed(Wallet.Roubles, Stash);

        _profiles.Names[_alice.ToString()] = "Ragman_Fan";
        _profiles.Names[_bob.ToString()] = "Nikita";

        _service = new SharedPokerService(
            _bank,
            new TableGate(),
            new SessionGate(),
            _store,
            _profiles,
            _escrow,
            new FakeNames(),
            new CasinoSocket(new Silent<CasinoSocket>()),
            new SilentLog(),
            new TableStore());
    }

    private static SitRequest Open() =>
        new() { Seats = 4, BuyIn = BuyIn, BigBlind = 20_000, Seed = 1, Wallet = nameof(Wallet.Roubles) };

    private static ItemEventRouterResponse Output() => new();

    /// <summary>
    /// The turn moves on without the player who left.
    /// </summary>
    [Fact]
    public async Task AnAbsentSeatDoesNotHoldTheTable()
    {
        var (table, absent, present, seat) = await ATableWaitingOnSomebodyWhoLeft();

        if (table is null)
        {
            return;
        }

        // The one who is still here asks for the table, which is what the panel's REFRESH
        // does and what it does on every redraw.
        await _service.StateAsync(present);

        Assert.True(
            table.Table.ActorSeat != seat,
            $"The table is still waiting on seat {seat}, whose player left an hour ago. "
            + "Everybody else is stuck behind them and cannot even stand up.");
    }

    /// <summary>
    /// And the players left behind can finish and take their chips.
    ///
    /// The frozen turn is only half the cost. The other half is that standing up is refused
    /// mid-hand, so the people still there cannot walk away from their own money -- which
    /// is why this asserts the escape rather than just the turn moving.
    /// </summary>
    [Fact]
    public async Task ThePlayersLeftBehindCanStandUp()
    {
        var (table, absent, present, _) = await ATableWaitingOnSomebodyWhoLeft();

        if (table is null)
        {
            return;
        }

        // Play on. Folding whenever it is theirs is enough to finish the hand.
        for (var guard = 0; guard < 40; guard++)
        {
            await _service.StateAsync(present);

            if (table.Table.Street is HoldemStreet.Idle or HoldemStreet.Showdown)
            {
                break;
            }

            if (table.Table.ActorSeat is not { } actor)
            {
                break;
            }

            if (table.SeatOf(present)?.Index != actor)
            {
                break;
            }

            await _service.ActAsync(new ActRequest { Move = "Fold" }, present);
        }

        var left = await _service.LeaveAsync(present, Output());

        Assert.True(left.Ok, $"Still trapped at the table: {left.Error}");

        // Nobody lost more than they brought. An absent player folds, which costs what they
        // had already put in -- never more.
        foreach (var session in new[] { _alice, _bob })
        {
            Assert.True(
                _bank.GetBalance(session, Wallet.Roubles) >= Stash - BuyIn,
                $"{session} ended below their buy-in. Folding cannot cost more than the "
                + "chips already on the table.");
        }
    }

    /// <summary>
    /// The timeout must NOT fire for somebody who is simply thinking.
    ///
    /// A rule that also folds present players is worse than the bug it replaces: it throws
    /// away a live hand, for real chips, on a table somebody is looking at.
    /// </summary>
    [Fact]
    public async Task APlayerWhoIsStillHereKeepsTheirTurn()
    {
        await _service.CreateAsync(Open(), _alice, Output());

        var id = _service.List().Single().Id;

        await _service.JoinAsync(id, _bob, Output());
        await _service.DealAsync(_alice);

        var table = _store.Get(id)!;
        var before = table.Table.ActorSeat;

        if (before is null)
        {
            return;
        }

        // Nobody is backdated -- everyone was heard from a moment ago.
        var waiting = table.Seats[before.Value].SessionId == _alice.ToString() ? _bob : _alice;

        await _service.StateAsync(waiting);

        Assert.Equal(before, table.Table.ActorSeat);
    }

    /// <summary>
    /// Deals a hand and backdates whoever the table is waiting on, so they read as gone.
    ///
    /// Backdated rather than waited out: a test that sleeps for the real timeout is a test
    /// nobody runs. Returns nulls when the deal left a bot to act or the hand was over
    /// before a human had to decide, which are legitimate hands with nothing to assert.
    /// </summary>
    private async Task<(SharedTable? Table, MongoId Absent, MongoId Present, int Seat)>
        ATableWaitingOnSomebodyWhoLeft()
    {
        await _service.CreateAsync(Open(), _alice, Output());

        var id = _service.List().Single().Id;

        await _service.JoinAsync(id, _bob, Output());
        await _service.DealAsync(_alice);

        var table = _store.Get(id)!;

        if (table.Table.ActorSeat is not { } seat)
        {
            return (null, default, default, -1);
        }

        var occupant = table.Seats[seat];

        if (occupant.Kind != SeatKind.Human || occupant.SessionId is null)
        {
            // A bot has the turn. Bots act on their own and never go quiet, so there is
            // nothing here to time out.
            return (null, default, default, -1);
        }

        var absent = new MongoId(occupant.SessionId);
        var present = absent == _alice ? _bob : _alice;

        occupant.LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

        return (table, absent, present, seat);
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

        public IGameLog ForEngine() => GameLog.Null;
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
