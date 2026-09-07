using System.Text.Json;
using Blackjack.Game;
using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// Two people at one blackjack table, end to end through the service.
///
/// Provable with no client and no server: the socket is real but nobody is connected to
/// it, which is exactly the state a push has to survive anyway.
///
/// What it checks is the three things that could each be wrong on their own -- the
/// seating, the money, and the names -- and one thing poker did not have to: that the
/// table is **not** filtered per viewer. Blackjack's cards are face up, so a view that
/// hid a neighbour's hand would be a bug rather than a privacy win.
/// </summary>
public class SharedTableIntegrationTests
{
    private const int Stake = 50_000;

    private const int Stash = 1_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedBlackjackStore _store = new();
    private readonly TableStore _solo = new();
    private readonly SharedBlackjackService _service;

    public SharedTableIntegrationTests()
    {
        _bank.SetBalance(Wallet.Roubles, Stash);

        _profiles.Names[_alice.ToString()] = "Ragman_Fan";
        _profiles.Names[_bob.ToString()] = "Nikita";

        _service = new SharedBlackjackService(
            _bank,
            new TableGate(),
            new SessionGate(),
            _store,
            _profiles,
            _escrow,
            _solo,
            new CasinoSocket(new QuietLogger<CasinoSocket>()));
    }

    private static OpenTableRequest Open(int seats = 4) =>
        new() { Seats = seats, Wallet = nameof(Wallet.Roubles) };

    private static DealRequest Bet(int wager = Stake) =>
        new() { Wager = wager, Wallet = nameof(Wallet.Roubles) };

    /// <summary>
    /// The milestone: two people sit at one table, both bet, one deal, and each is paid
    /// their own result.
    /// </summary>
    [Fact]
    public async Task TwoPeopleSitAtOneTableAndAreEachPaidTheirOwnResult()
    {
        var opened = await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        Assert.True(opened.Ok, opened.Error);
        Assert.Equal(0, opened.YourSeat);

        var joined = await _service.JoinAsync(TableId(), _bob);
        Assert.True(joined.Ok, joined.Error);
        Assert.Equal(1, joined.YourSeat);

        // Nothing has been taken yet. A blackjack seat costs nothing until it bets, which
        // is the whole difference from poker's buy-in.
        Assert.Empty(_bank.Debits);

        Assert.True((await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse())).Ok);
        Assert.True((await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse())).Ok);

        Assert.Equal(2, _bank.Debits.Count);
        Assert.Equal(Stash - Stake, _bank.GetBalance(_alice, Wallet.Roubles));

        // Anybody seated may start the round -- a table that only its host could deal
        // stops dead the moment that person walks away.
        var dealt = await _service.DealAsync(_bob, new ItemEventRouterResponse());
        Assert.True(dealt.Ok, dealt.Error);

        var table = dealt.SharedTable!;

        Assert.Equal(2, table.Seats.Count(seat => seat.IsInRound));
        Assert.All(
            table.Seats.Where(seat => seat.IsInRound),
            seat => Assert.Equal(2, seat.Hands[0].Cards.Count));

        // Play it out. Everybody stands, which is legal from any total and settles the
        // round without the test having to know what was dealt.
        await StandEverybodyOut();

        var settled = await _service.StateAsync(_alice);

        Assert.Equal(RoundPhase.Settled, settled.SharedTable!.Phase);

        // The money each seat is owed is its own. That is the assertion that matters:
        // the dealer is the house, so one box winning has no bearing on the other's, and
        // a settlement that crossed them would pay one player out of the other's stake.
        foreach (var (session, seat) in new[] { (_alice, 0), (_bob, 1) })
        {
            var box = settled.SharedTable.Seats[seat];

            Assert.Equal(Stake, box.TotalWagered);
            Assert.Equal(
                Stash - Stake + box.TotalReturned,
                _bank.GetBalance(session, Wallet.Roubles));
        }

        // Nothing is owed once the round is over. A row left behind would refund a stake
        // that has already been settled.
        Assert.Null(_escrow.Get(_alice));
        Assert.Null(_escrow.Get(_bob));
    }

    /// <summary>
    /// Both boxes carry their owner's PMC nickname, and neither carries "You".
    ///
    /// The engine's fallback for an occupied seat with no name is the word "You", which is
    /// a relationship to whoever is looking rather than a name -- so it reaches everybody
    /// unchanged and each player sees their friend labelled as themselves. That shipped in
    /// poker and somebody hit it at a real table; this is the assertion that keeps it out
    /// of blackjack.
    /// </summary>
    [Fact]
    public async Task BothBoxesCarryTheirOwnersRealName()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        var view = (await _service.StateAsync(_bob)).SharedTable!;

        Assert.Equal("Ragman_Fan", view.Seats[0].Name);
        Assert.Equal("Nikita", view.Seats[1].Name);

        Assert.DoesNotContain(view.Seats, seat => seat.Name == "You");
    }

    /// <summary>
    /// A seat with no profile behind it falls back to the box number rather than refusing.
    ///
    /// A nameless box is cosmetic and a refused seat is not; but the fallback must not be
    /// the engine's, because the engine's is "You".
    /// </summary>
    [Fact]
    public async Task ANamelessPlayerGetsTheirBoxNumberAndNotTheWordYou()
    {
        var nobody = new MongoId();

        await _service.OpenAsync(Open(), nobody, new ItemEventRouterResponse());

        var view = (await _service.StateAsync(nobody)).SharedTable!;

        Assert.Equal("Box 0", view.Seats[0].Name);
    }

    /// <summary>
    /// **Every hand is visible to everybody, including in the JSON.**
    ///
    /// The opposite of poker's test, and deliberately so: hole cards are secret in hold'em
    /// and face up in blackjack. Asserted against the serialised wire form rather than a
    /// property, because a client renders what it is sent -- if a neighbour's cards are not
    /// in the JSON they are not on the table, whatever the object says.
    /// </summary>
    [Fact]
    public async Task EveryHandIsOnTheWireForEveryone()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());
        await _service.DealAsync(_alice, new ItemEventRouterResponse());

        var json = JsonSerializer.Serialize((await _service.StateAsync(_alice)).SharedTable);
        var view = (await _service.StateAsync(_alice)).SharedTable!;

        foreach (var card in view.Seats.Where(s => s.IsInRound).SelectMany(s => s.Hands[0].Cards))
        {
            Assert.Contains(card, json);
        }
    }

    /// <summary>
    /// The dealer's hole card is the one thing hidden, and it is hidden from everybody --
    /// the host included, who has no more right to it than anybody else.
    /// </summary>
    [Fact]
    public async Task TheDealersHoleCardIsHiddenFromBothPlayers()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        // A round that settled on the deal has already turned the hole card over, which is
        // correct and not what this is about.
        if (dealt.SharedTable!.Phase != RoundPhase.PlayerTurn)
        {
            return;
        }

        foreach (var session in new[] { _alice, _bob })
        {
            var view = (await _service.StateAsync(session)).SharedTable!;

            Assert.Single(view.Dealer.Cards);
        }
    }

    /// <summary>
    /// A box that did not bet is dealt nothing and settles nothing -- which is how a player
    /// who has walked away from the keyboard sits out without stopping the table.
    /// </summary>
    [Fact]
    public async Task ABoxThatDidNotBetSitsTheRoundOut()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());
        var table = dealt.SharedTable!;

        Assert.True(table.Seats[0].IsInRound);
        Assert.False(table.Seats[1].IsInRound);

        Assert.Equal(Stash, _bank.GetBalance(_bob, Wallet.Roubles));
        Assert.Null(_escrow.Get(_bob));
    }

    /// <summary>
    /// Somebody else's turn is not yours, however loudly the phase says "PlayerTurn".
    /// </summary>
    [Fact]
    public async Task ActingOutOfTurnIsRefusedAndMovesNothing()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        if (dealt.SharedTable!.ActiveSeat is not { } turn)
        {
            return;
        }

        var wrong = turn == 0 ? _bob : _alice;
        var before = _bank.Debits.Count;

        var refused = await _service.ActAsync(
            new ActionRequest { Action = "Hit" },
            wrong,
            new ItemEventRouterResponse());

        Assert.False(refused.Ok);
        Assert.Equal("It is not your turn.", refused.Error);
        Assert.Equal(before, _bank.Debits.Count);
    }

    /// <summary>
    /// Standing up between rounds hands back a bet that was never dealt, and frees the box.
    /// </summary>
    [Fact]
    public async Task LeavingBetweenRoundsReturnsAnUndealtBet()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());
        Assert.Equal(Stash - Stake, _bank.GetBalance(_bob, Wallet.Roubles));

        var left = await _service.LeaveAsync(_bob, new ItemEventRouterResponse());

        Assert.True(left.Ok, left.Error);
        Assert.Equal(Stash, _bank.GetBalance(_bob, Wallet.Roubles));
        Assert.Null(_escrow.Get(_bob));

        // And the box is free for somebody else. Alice is still at the table, so it did
        // not close behind him.
        var view = (await _service.StateAsync(_alice)).SharedTable!;

        Assert.False(view.Seats[1].IsOccupied);
    }

    /// <summary>The last person out closes the table behind them.</summary>
    [Fact]
    public async Task TheLastPlayerOutClosesTheTable()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());

        var id = TableId();

        Assert.True((await _service.LeaveAsync(_alice, new ItemEventRouterResponse())).Ok);

        Assert.Null(_store.Get(id));
        Assert.Empty(_service.List());
    }

    /// <summary>
    /// And the last person out is free to sit down somewhere else.
    ///
    /// **This is the one the closing test above misses.** `store.Remove` frees everybody at
    /// a table by walking its occupants -- so removing the leaver from `Occupants` BEFORE
    /// calling it leaves nobody to walk, and the claim on the person who just left survives
    /// the table they left. They are then held at a table that does not exist, and every
    /// subsequent open or join is refused with "You are already at a table" for the rest of
    /// the server's life. Nothing short of a restart clears it.
    ///
    /// Poker gets this right by accident of ordering -- it calls `store.Remove` before it
    /// touches the seat -- which is exactly why translating one implementation into another
    /// needs its own test rather than its own confidence.
    /// </summary>
    [Fact]
    public async Task TheLastPlayerOutCanSitDownAgain()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        Assert.True((await _service.LeaveAsync(_alice, new ItemEventRouterResponse())).Ok);

        // Asserted by opening again rather than by reading the store, because the leak is
        // INVISIBLE to the obvious check: `store.For` returns null either way -- the table
        // really is gone -- and only `TryClaim` can see the claim, and only by refusing.
        var again = await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());

        Assert.True(again.Ok, again.Error);
    }

    /// <summary>
    /// The same, for the last player out of a table somebody else opened -- which is the
    /// ordinary way a table empties: the host leaves first and a guest closes it.
    /// </summary>
    [Fact]
    public async Task TheLastGuestOutCanSitDownAgain()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        Assert.True((await _service.LeaveAsync(_alice, new ItemEventRouterResponse())).Ok);
        Assert.True((await _service.LeaveAsync(_bob, new ItemEventRouterResponse())).Ok);

        Assert.True((await _service.OpenAsync(Open(), _bob, new ItemEventRouterResponse())).Ok);
    }

    /// <summary>
    /// **A player who closes the game mid-round must not freeze the table.**
    ///
    /// Blackjack has no fold, and standing up is refused while a round is running -- so a
    /// seat whose player is gone holds the turn, and every other player at the table is
    /// stuck behind it with their stake in escrow. Nothing in the process ever moves it
    /// on: `LastSeenUtc` is written on every request and read nowhere.
    ///
    /// The only escape is a server restart, after which the table is gone and the stakes
    /// come back as orphans -- which is a real recovery, but it is not one anybody should
    /// have to reach for because their friend's game crashed.
    /// </summary>
    [Fact]
    public async Task AnAbsentPlayerDoesNotFreezeTheTable()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        if (dealt.SharedTable!.ActiveSeat is not { } turn)
        {
            return;
        }

        // Whoever the table is waiting on has closed their game. Backdated rather than
        // waited out, because a test that sleeps for the real timeout is a test nobody
        // runs.
        var absent = turn == 0 ? _alice : _bob;
        var present = turn == 0 ? _bob : _alice;

        _store.All.Single().LastSeenUtc[absent.ToString()] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

        // The player who is still here asks for the table, which is what pressing REFRESH
        // does. The absent seat should be stood and the round should move on without them.
        var state = await _service.StateAsync(present);

        Assert.True(
            state.SharedTable!.ActiveSeat != turn,
            $"The table is still waiting on box {turn}, whose player left an hour ago. "
            + "Everybody else is stuck behind them and cannot even stand up.");
    }

    /// <summary>
    /// And once the table has played on without them, everybody else is free.
    ///
    /// The freeze is only half the cost. The other half is that standing up is refused
    /// mid-round, so the players who are still there cannot even walk away from their own
    /// money -- which is why this asserts the escape and not merely the turn moving.
    /// </summary>
    [Fact]
    public async Task ThePlayersLeftBehindCanFinishAndCashOut()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        if (dealt.SharedTable!.ActiveSeat is not { } turn)
        {
            return;
        }

        var absent = turn == 0 ? _alice : _bob;
        var present = turn == 0 ? _bob : _alice;

        _store.All.Single().LastSeenUtc[absent.ToString()] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

        // The one who is still here plays their hand out, standing whenever it is theirs.
        for (var guard = 0; guard < 20; guard++)
        {
            var view = (await _service.StateAsync(present)).SharedTable!;

            if (view.Phase == RoundPhase.Settled)
            {
                break;
            }

            if (view.ActiveSeat is null)
            {
                break;
            }

            await _service.ActAsync(
                new ActionRequest { Action = "Stand" },
                present,
                new ItemEventRouterResponse());
        }

        var left = await _service.LeaveAsync(present, new ItemEventRouterResponse());

        Assert.True(left.Ok, $"Still trapped at the table: {left.Error}");

        // And both were paid what the engine says they were owed -- the absent player
        // included. Being disconnected is not a reason to lose a hand that won.
        Assert.Null(_escrow.Get(present));
        Assert.Null(_escrow.Get(absent));

        foreach (var session in new[] { _alice, _bob })
        {
            Assert.True(
                _bank.GetBalance(session, Wallet.Roubles) >= Stash - Stake,
                $"{session} ended below their stake. Nobody may lose more than they bet.");
        }
    }

    /// <summary>
    /// The timeout must NOT fire for somebody who is simply thinking.
    ///
    /// A rule that also takes hands off present players is not a fix -- it is a worse bug
    /// than the one it replaces, because it moves real money on a table somebody is
    /// looking at.
    /// </summary>
    [Fact]
    public async Task APlayerWhoIsStillHereKeepsTheirTurn()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(TableId(), _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        if (dealt.SharedTable!.ActiveSeat is not { } turn)
        {
            return;
        }

        // Nobody is backdated. Everyone was heard from a moment ago.
        var other = turn == 0 ? _bob : _alice;

        var view = (await _service.StateAsync(other)).SharedTable!;

        Assert.Equal(turn, view.ActiveSeat);
        Assert.Equal(RoundPhase.PlayerTurn, view.Phase);
    }

    private string TableId() => _store.All.Single().Id;

    /// <summary>
    /// Stands every seat in turn until the round settles.
    ///
    /// Bounded rather than looped on the phase alone: a bug that left the turn on one seat
    /// would otherwise hang the test rather than fail it, and a hang tells nobody anything.
    /// Seven seats times a few split hands is well inside twenty.
    /// </summary>
    private async Task StandEverybodyOut()
    {
        for (var guard = 0; guard < 20; guard++)
        {
            var view = (await _service.StateAsync(_alice)).SharedTable!;

            if (view.ActiveSeat is not { } seat)
            {
                return;
            }

            var who = seat == 0 ? _alice : _bob;

            await _service.ActAsync(
                new ActionRequest { Action = "Stand" },
                who,
                new ItemEventRouterResponse());
        }

        Assert.Fail("The round never settled -- the turn is stuck on a seat.");
    }
}
