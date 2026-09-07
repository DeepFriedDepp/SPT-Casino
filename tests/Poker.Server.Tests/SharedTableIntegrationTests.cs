using System.Text.Json;
using Casino.Server;
using Poker.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace Poker.Server.Tests;

/// <summary>
/// Two people at one table, end to end through the service.
///
/// This is the milestone the whole shared-table effort is aimed at, and it is provable
/// with no client and no server: the socket is real but nobody is connected to it, which
/// is exactly the state a push has to survive anyway.
///
/// What it checks is the three things that could each be wrong on their own -- the
/// seating, the money, and the privacy -- with the last of those asserted against the
/// serialised wire form rather than a property, because "hidden" data that is present in
/// the JSON is not hidden.
/// </summary>
public class SharedTableIntegrationTests
{
    private const int BuyIn = 2_000_000;

    private const int Stash = 20_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly SharedTableStore _store = new();
    private readonly SharedPokerService _service;

    public SharedTableIntegrationTests()
    {
        _bank.Seed(Wallet.Roubles, Stash);

        _service = new SharedPokerService(
            _bank,
            new TableGate(),
            new SessionGate(),
            _store,
            new FakeProfiles(),
            _escrow,
            new FakeNames(),
            new CasinoSocket(new Silent<CasinoSocket>()),
            new SilentLog());
    }

    private static SitRequest Open(int seats = 4) =>
        new() { Seats = seats, BuyIn = BuyIn, BigBlind = 20_000, Seed = 1, Wallet = nameof(Wallet.Roubles) };

    private static ItemEventRouterResponse Output() => new();

    [Fact]
    public async Task TwoPeopleSitAtOneTableAndSeeOnlyTheirOwnCards()
    {
        var opened = await _service.CreateAsync(Open(), _alice, Output());
        Assert.True(opened.Ok);

        var id = _service.List().Single().Id;

        var joined = await _service.JoinAsync(id, _bob, Output());
        Assert.True(joined.Ok, joined.Error);

        // One table, both of them on it, and it is the SAME table.
        Assert.Equal(id, _store.For(_alice)?.Id);
        Assert.Equal(id, _store.For(_bob)?.Id);

        var table = _store.Get(id)!;
        Assert.Equal(2, table.HumanSeats.Count());

        // Deal, then look at what each of them is allowed to see.
        var dealt = await _service.DealAsync(_alice);
        Assert.True(dealt.Ok, dealt.Error);

        var aliceSeat = table.SeatOf(_alice)!.Index;
        var bobSeat = table.SeatOf(_bob)!.Index;
        Assert.NotEqual(aliceSeat, bobSeat);

        var aliceSees = JsonSerializer.Serialize((await _service.StateAsync(_alice)).Table);
        var bobSees = JsonSerializer.Serialize((await _service.StateAsync(_bob)).Table);

        var aliceCards = table.Table.Seats[aliceSeat].Cards.Select(card => card.Code).ToList();
        var bobCards = table.Table.Seats[bobSeat].Cards.Select(card => card.Code).ToList();

        Assert.NotEmpty(aliceCards);
        Assert.NotEmpty(bobCards);

        foreach (var code in aliceCards)
        {
            Assert.Contains($"\"{code}\"", aliceSees);
            Assert.DoesNotContain($"\"{code}\"", bobSees);
        }

        foreach (var code in bobCards)
        {
            Assert.Contains($"\"{code}\"", bobSees);
            Assert.DoesNotContain($"\"{code}\"", aliceSees);
        }
    }

    /// <summary>
    /// Two buy-ins leave two stashes, and each person gets their own stack back.
    ///
    /// The money is per player and always was -- that is what let the existing
    /// single-profile money code stay correct under a shared table. This pins it.
    /// </summary>
    [Fact]
    public async Task EachPersonPaysTheirOwnBuyInAndTakesTheirOwnStackBack()
    {
        await _service.CreateAsync(Open(), _alice, Output());
        var id = _service.List().Single().Id;
        await _service.JoinAsync(id, _bob, Output());

        // One debit each. The fake bank shares a balance, so two buy-ins is two debits.
        Assert.Equal(2, _bank.Debits);
        Assert.Equal(Stash - (2 * BuyIn), _bank.GetBalance(_alice, Wallet.Roubles));

        // Each is recorded as owed, individually.
        Assert.Equal(BuyIn, _escrow.Get(_alice)?.Chips);
        Assert.Equal(BuyIn, _escrow.Get(_bob)?.Chips);

        var left = await _service.LeaveAsync(_bob, Output());
        Assert.True(left.Ok, left.Error);

        // Bob's stack came back and his row is gone; Alice's is untouched.
        Assert.Null(_escrow.Get(_bob));
        Assert.Equal(BuyIn, _escrow.Get(_alice)?.Chips);
        Assert.Null(_store.For(_bob));
        Assert.Equal(id, _store.For(_alice)?.Id);

        // Nobody minted anything: one credit, for exactly the stack Bob was sitting on.
        Assert.Equal(1, _bank.Credits);
        Assert.Equal(Stash - BuyIn, _bank.GetBalance(_bob, Wallet.Roubles));
    }

    /// <summary>
    /// A seat cannot act out of turn, and being told so does not cost anybody anything.
    /// </summary>
    [Fact]
    public async Task OnlyTheSeatToActCanAct()
    {
        await _service.CreateAsync(Open(), _alice, Output());
        var id = _service.List().Single().Id;
        await _service.JoinAsync(id, _bob, Output());
        await _service.DealAsync(_alice);

        var table = _store.Get(id)!;
        var actor = table.Table.ActorSeat;
        Assert.NotNull(actor);

        var waiting = table.SeatOf(_alice)!.Index == actor ? _bob : _alice;

        var refused = await _service.ActAsync(new ActRequest { Move = "Fold" }, waiting);

        Assert.False(refused.Ok);
        Assert.Equal("It is not your turn.", refused.Error);

        // Refused, and still holding their cards -- a rejected action must not fold them.
        Assert.False(table.Table.Seats[table.SeatOf(waiting)!.Index].Folded);
    }

    [Fact]
    public async Task NobodyJoinsAHandAlreadyInProgress()
    {
        await _service.CreateAsync(Open(), _alice, Output());
        var id = _service.List().Single().Id;
        await _service.DealAsync(_alice);

        var refused = await _service.JoinAsync(id, _bob, Output());

        Assert.False(refused.Ok);
        Assert.Contains("hand is in progress", refused.Error);

        // And crucially the buy-in was never taken -- refused before the money moved.
        Assert.Equal(1, _bank.Debits);
        Assert.Null(_escrow.Get(_bob));
        Assert.Null(_store.For(_bob));
    }

    /// <summary>
    /// The last person leaving closes the table rather than leaving bots playing
    /// themselves, and everybody's claim is freed so they can sit somewhere else.
    /// </summary>
    [Fact]
    public async Task TheLastPersonOutClosesTheTable()
    {
        await _service.CreateAsync(Open(), _alice, Output());
        var id = _service.List().Single().Id;
        await _service.JoinAsync(id, _bob, Output());

        await _service.LeaveAsync(_bob, Output());
        await _service.LeaveAsync(_alice, Output());

        Assert.Null(_store.Get(id));
        Assert.Empty(_service.List());
        Assert.Null(_store.For(_alice));

        // Both whole again: two buy-ins out, two stacks back.
        Assert.Equal(Stash, _bank.GetBalance(_alice, Wallet.Roubles));
        Assert.Null(_escrow.Get(_alice));
        Assert.Null(_escrow.Get(_bob));
    }

    [Fact]
    public async Task AFullTableTurnsPeopleAway()
    {
        await _service.CreateAsync(Open(seats: 2), _alice, Output());
        var id = _service.List().Single().Id;

        Assert.True((await _service.JoinAsync(id, _bob, Output())).Ok);

        // Two seats, two people: it is no longer listed and a third is turned away.
        Assert.Empty(_service.List());

        var third = await _service.JoinAsync(id, new MongoId(), Output());
        Assert.False(third.Ok);
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
