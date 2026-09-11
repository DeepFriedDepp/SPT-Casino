using Blackjack.Game;
using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// Doubling and splitting are the only actions that cost money, and the only ones the
/// engine can refuse AFTER it has been paid for.
///
/// ## The defect these were written against
///
/// `ChargeDoubleAsync` debits before the engine is told, which is right -- the engine deals
/// a card on a double, and refusing after that is a card the player got for nothing. The
/// cost of that ordering is that a refusal on the OTHER side has to hand the money back,
/// and the first version did not: it caught the engine's exception, returned the refusal,
/// and left the stake debited and sitting in escrow. At settlement the row was released
/// against a hand that had never been staked that much, and the money was simply gone.
///
/// Reachable without doing anything unusual. A double stops being legal the moment a hand
/// has three cards, so a stale view, a double-clicked button or a retried request all
/// arrive as a double that cannot happen -- and each one cost a full extra stake, silently.
///
/// Found by an adversarial audit of this file, whose probe dealt two hundred rounds until
/// one left DOUBLE illegal with the seat still to act. Its dump, on the broken build:
///
///     balance before DOUBLE:              900,000
///     balance right after refused DOUBLE: 800,000
///     escrow after refused DOUBLE:        200,000
///     seat TotalWagered: 100,000, TotalReturned: 200,000
///     final balance: 1,000,000, honest balance would be 1,100,000
///
/// Fixed on both sides: the action is checked against the engine's own `AvailableActions`
/// before a rouble moves, and anything the engine refuses anyway is refunded.
/// </summary>
public class DoubleAndSplitMoneyTests
{
    private const int Stake = 100_000;

    private const int Stash = 1_000_000;

    private readonly MongoId _alice = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedBlackjackStore _store = new();
    private readonly SharedBlackjackService _service;

    public DoubleAndSplitMoneyTests()
    {
        _bank.SetBalance(Wallet.Roubles, Stash);
        _profiles.Names[_alice.ToString()] = "Ragman_Fan";

        _service = new SharedBlackjackService(
            _bank,
            new TableGate(),
            new SessionGate(),
            _store,
            _profiles,
            _escrow,
            new TableStore(),
            new FakeOutputs(),
            new CasinoSocket(new QuietLogger<CasinoSocket>()));
    }

    /// <summary>
    /// A DOUBLE that arrives when doubling is no longer legal must cost nothing.
    /// </summary>
    [Fact]
    public async Task ADoubleTheEngineRefusesCostsNothing()
    {
        await ADealtHandThatCannotDouble();

        var before = _bank.GetBalance(_alice, Wallet.Roubles);
        var heldBefore = _escrow.Get(_alice)?.Amount ?? 0;

        var refused = await _service.ActAsync(
            new ActionRequest { Action = "Double" }, _alice, new ItemEventRouterResponse());

        Assert.False(refused.Ok);

        // Money first, so a failure names what it costs rather than naming a flag.
        Assert.Equal(before, _bank.GetBalance(_alice, Wallet.Roubles));
        Assert.Equal(heldBefore, _escrow.Get(_alice)?.Amount ?? 0);
    }

    /// <summary>
    /// And the books still balance once the round it happened in settles.
    ///
    /// The immediate assertion above would pass on a version that debited and then credited
    /// twice. This is the one that says the player ended where the engine says they should.
    /// </summary>
    [Fact]
    public async Task ARefusedDoubleLeavesTheBooksBalanced()
    {
        var opening = await ADealtHandThatCannotDouble() ?? Stash;

        await _service.ActAsync(
            new ActionRequest { Action = "Double" }, _alice, new ItemEventRouterResponse());

        for (var guard = 0; guard < 20; guard++)
        {
            var view = (await _service.StateAsync(_alice)).SharedTable!;

            if (view.ActiveSeat is null)
            {
                break;
            }

            await _service.ActAsync(
                new ActionRequest { Action = "Stand" }, _alice, new ItemEventRouterResponse());
        }

        var box = (await _service.StateAsync(_alice)).SharedTable!.Seats[0];

        Assert.Equal(
            opening - box.TotalWagered + box.TotalReturned,
            _bank.GetBalance(_alice, Wallet.Roubles));

        Assert.Null(_escrow.Get(_alice));
    }

    /// <summary>
    /// A SPLIT that arrives when splitting is not legal must cost nothing either. Same
    /// method, same charge, and it was equally unrefunded.
    /// </summary>
    [Fact]
    public async Task ASplitTheEngineRefusesCostsNothing()
    {
        await ADealtHandThatCannotDouble();

        var before = _bank.GetBalance(_alice, Wallet.Roubles);
        var heldBefore = _escrow.Get(_alice)?.Amount ?? 0;

        var refused = await _service.ActAsync(
            new ActionRequest { Action = "Split" }, _alice, new ItemEventRouterResponse());

        Assert.False(refused.Ok);
        Assert.Equal(before, _bank.GetBalance(_alice, Wallet.Roubles));
        Assert.Equal(heldBefore, _escrow.Get(_alice)?.Amount ?? 0);
    }

    /// <summary>
    /// A DOUBLE the player genuinely cannot afford is refused and takes nothing.
    ///
    /// The other half of the same method. This path was always correct, and it is asserted
    /// so that a future change to the refund cannot break it by handing back a charge that
    /// never happened.
    /// </summary>
    [Fact]
    public async Task ADoubleTheyCannotAffordTakesNothing()
    {
        await _service.OpenAsync(
            new OpenTableRequest { Seats = 2, Wallet = nameof(Wallet.Roubles) },
            _alice,
            new ItemEventRouterResponse());

        await _service.BetAsync(
            new DealRequest { Wager = Stake, Wallet = nameof(Wallet.Roubles) },
            _alice,
            new ItemEventRouterResponse());

        var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

        if (dealt.SharedTable!.ActiveSeat != 0
            || !dealt.SharedTable.Seats[0].AvailableActions.Contains(PlayerAction.Double))
        {
            return;
        }

        // Everything gone but the stake already in the box.
        _bank.SetBalance(Wallet.Roubles, 0);

        var refused = await _service.ActAsync(
            new ActionRequest { Action = "Double" }, _alice, new ItemEventRouterResponse());

        Assert.False(refused.Ok);
        Assert.Contains("needs another", refused.Error);
        Assert.Equal(0, _bank.GetBalance(_alice, Wallet.Roubles));

        // The original bet is still recorded. A refusal must not disturb it.
        Assert.Equal(Stake, _escrow.Get(_alice)?.Amount);
    }

    /// <summary>
    /// Plays rounds until one leaves the seat holding three cards and still to act -- which
    /// is exactly when the engine stops allowing a double.
    ///
    /// **It keeps dealing rather than giving up**, and fails loudly if it never gets there.
    /// The first version returned null on an uncooperative deal and the tests quietly
    /// returned, which means a run where the shoe never obliged would have reported four
    /// passes having asserted nothing at all. A test that only sometimes exercises its
    /// subject is the same trap as one that cannot fail.
    ///
    /// The shoe is random and cannot be seeded through the service, so this is bounded
    /// rather than deterministic. A natural or a bust on the first hit is common; forty
    /// rounds without a single three-card live hand is not something the deck does.
    ///
    /// Returns the balance as the interesting round began, which is what the books have to
    /// be reconciled against afterwards.
    /// </summary>
    private async Task<int?> ADealtHandThatCannotDouble()
    {
        await _service.OpenAsync(
            new OpenTableRequest { Seats = 2, Wallet = nameof(Wallet.Roubles) },
            _alice,
            new ItemEventRouterResponse());

        for (var round = 0; round < 40; round++)
        {
            var opening = _bank.GetBalance(_alice, Wallet.Roubles);

            var bet = await _service.BetAsync(
                new DealRequest { Wager = Stake, Wallet = nameof(Wallet.Roubles) },
                _alice,
                new ItemEventRouterResponse());

            if (!bet.Ok)
            {
                break;
            }

            var dealt = await _service.DealAsync(_alice, new ItemEventRouterResponse());

            if (dealt.SharedTable!.ActiveSeat == 0)
            {
                var hit = await _service.ActAsync(
                    new ActionRequest { Action = "Hit" }, _alice, new ItemEventRouterResponse());

                var view = hit.SharedTable!;

                if (view.ActiveSeat == 0
                    && !view.Seats[0].AvailableActions.Contains(PlayerAction.Double))
                {
                    return opening;
                }
            }

            // This round is no good. Play it out so the next one can be dealt.
            for (var guard = 0; guard < 20; guard++)
            {
                var view = (await _service.StateAsync(_alice)).SharedTable!;

                if (view.ActiveSeat is null)
                {
                    break;
                }

                await _service.ActAsync(
                    new ActionRequest { Action = "Stand" }, _alice, new ItemEventRouterResponse());
            }
        }

        Assert.Fail(
            "Forty rounds and never a live hand of three cards. Either the deck is not "
            + "behaving or the helper stopped reaching the state these tests are about -- "
            + "either way they were asserting nothing.");

        return null;
    }
}
