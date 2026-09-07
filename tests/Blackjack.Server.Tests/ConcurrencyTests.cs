using Blackjack.Game;
using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// Two requests for one player, arriving at once.
///
/// ## Why this is reachable, and why it is not paranoia
///
/// A single stock client cannot do this to itself: every call in `BlackjackApi.cs`
/// blocks on `RequestHandler.PostJson` on Unity's main thread, so one player is serial
/// by construction. It takes two clients on one profile, a Fika setup, or
/// `scripts/blackjack/smoke.ps1` running while the game is open. SPT 4.0.13's pipeline
/// is plain Kestrel with no per-profile serialisation of its own, so when a second
/// request does arrive it really does run the route handler alongside the first.
///
/// ## How the interleaving is forced, and why not with a Barrier
///
/// The obvious tool is a `Barrier(2)` inside the bank: hold the first racer until the
/// second arrives. That works only on the BROKEN code. Once the gate is in, the second
/// racer never reaches the bank at all -- it is parked on the session -- so a hard
/// barrier would deadlock the very test that is supposed to prove the fix.
///
/// So <see cref="RaceProbe"/> waits with a timeout instead: the first caller into the
/// bank announces itself and gives the second a fixed window to show up. Ungated, the
/// second arrives and both move money. Gated, the window simply expires and the first
/// carries on alone. The assertion is the same either way -- how many times money
/// moved -- and neither path can hang.
///
/// A plain `Task.WhenAll` would prove nothing here: every fake completes synchronously,
/// so the first racer runs to completion before the second starts and the race never
/// happens.
/// </summary>
public class ConcurrencyTests
{
    private const int Wager = 10_000;

    /// <summary>What <see cref="FakeBank"/> seeds a rouble balance at.</summary>
    private const int Roubles = 1_000_000;

    private readonly MongoId _session = new();
    private readonly FakeBank _bank = new();
    private readonly SessionGate _gate = new();
    private readonly FakeProfiles _profiles = new();
    private readonly FakeStats _stats = new();
    private readonly FakeEscrow _escrow = new();
    private readonly TableStore _tables = new();

    /// <summary>
    /// Two deals at once must take one wager, and neither may throw.
    ///
    /// Both halves matter, because ungated this fails in two separate ways at once.
    /// Both racers clear the "a round is already in progress" guard -- the phase has
    /// not changed yet, because neither has dealt -- both debit a full wager, and both
    /// add it to escrow. Then the loser of the last few instructions calls
    /// <c>Table.Deal</c> on a table already in <see cref="RoundPhase.PlayerTurn"/> and
    /// takes an <see cref="InvalidOperationException"/> that nothing in
    /// <c>DealCoreAsync</c> catches: it leaves the route as a 500, *after* the second
    /// wager has gone. So the player is charged twice, plays once, and sees an error
    /// that gives them no reason to think they are owed anything.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDealsTakeOneWager()
    {
        var probe = new RaceProbe(_bank);

        // Player K/5 = 15 against dealer K/7 = 17, so the round stops at PlayerTurn
        // rather than settling -- which is what makes the second deal illegal.
        var service = WithDeal(probe, "KS KH 5D 7C");

        probe.Arm();

        var faults = await Task.WhenAll(
            Task.Run(() => Attempt(() => service.DealAsync(Bet(), _session, new ItemEventRouterResponse()))),
            Task.Run(() => Attempt(() => service.DealAsync(Bet(), _session, new ItemEventRouterResponse()))));

        // A refused deal is a `BlackjackResponse` with Ok false. An exception is not a
        // refusal -- it is money already taken and a request that never answered.
        Assert.All(faults, fault => Assert.True(fault is null, $"A deal threw rather than being refused: {fault}"));

        // Exactly one wager left the stash.
        Assert.Single(_bank.Debits);
        Assert.Equal(Roubles - Wager, _bank.GetBalance(_session, Wallet.Roubles));

        // And escrow holds the wager that was actually taken, so a crash here refunds
        // the right amount rather than one of two.
        var held = _escrow.Get(_session);
        Assert.NotNull(held);
        Assert.Equal(Wager, held.Amount);
    }

    /// <summary>
    /// Two "open the panel" requests at once must give an abandoned stake back once.
    ///
    /// <c>RefundAbandonedStake</c> is a read-then-act across three calls -- `escrow.Get`,
    /// `bank.Credit`, `escrow.Release` -- with nothing holding the gap. Ungated, both
    /// racers read the same outstanding row before either releases it and both pay it
    /// out, so the stranded round hands back twice what was staked. That is currency
    /// created from nothing, on the one request a player makes without meaning to spend
    /// anything.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentStateReadsRefundAnAbandonedStakeOnce()
    {
        var probe = new RaceProbe(_bank);
        var service = WithDeal(probe, "KS KH 5D 7C");

        // Unarmed for the setup: only the racing pair below should be slowed down.
        await service.DealAsync(Bet(), _session, new ItemEventRouterResponse());
        Assert.Single(_bank.Debits);
        Assert.NotNull(_escrow.Get(_session));

        // The server restarts. Tables are in memory and vanish; escrow is on disk and
        // does not. That is exactly the state the refund exists for.
        _tables.Clear(_session);

        probe.Arm();

        var responses = await Task.WhenAll(
            Task.Run(() => service.State(_session, new ItemEventRouterResponse())),
            Task.Run(() => service.State(_session, new ItemEventRouterResponse())));

        // The stake came back exactly once. Ungated this is `Roubles + Wager`.
        Assert.Single(_bank.Credits);
        Assert.Equal((Wallet.Roubles, Wager), _bank.Credits[0]);
        Assert.Equal(Roubles, _bank.GetBalance(_session, Wallet.Roubles));

        // And the row was retired once, not "removed" twice.
        Assert.Equal(1, _escrow.Releases);
        Assert.Null(_escrow.Get(_session));

        // Exactly one of the two replies explains the refund. Two notes would tell the
        // player their money came back twice, which -- ungated -- it did.
        Assert.Equal(1, responses.Count(response => response.Note is not null));
    }

    private BlackjackService WithDeal(IBank bank, string cards)
    {
        _tables.Seed(_session, new BlackjackTable(
            new Rules { MinBet = 1, MaxBet = int.MaxValue },
            Shoe.Stacked(cards.Split(' ').Select(Card.Parse))));

        return new BlackjackService(bank, _gate, _profiles, _tables, _stats, _escrow, new SharedBlackjackStore());
    }

    private static DealRequest Bet() => new() { Wager = Wager, Wallet = nameof(Wallet.Roubles) };

    /// <summary>
    /// Runs one racer and hands back what it threw, if anything.
    ///
    /// `Task.WhenAll` would surface only the first exception and abandon the rest of
    /// the assertions, and the money counts are worth seeing even when a racer blew up.
    /// </summary>
    private static async Task<Exception?> Attempt(Func<Task> work)
    {
        try
        {
            await work();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// A bank that holds the first caller into a money movement open for a moment, so
    /// a second caller has a chance to arrive inside the window.
    ///
    /// Bounded rather than a barrier, so the gated path -- where the second caller
    /// never arrives -- finishes instead of deadlocking. See the class remarks.
    /// </summary>
    private sealed class RaceProbe(FakeBank inner) : IBank
    {
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrivals;

        private volatile bool _armed;

        /// <summary>Starts widening the window. Setup calls should not be slowed down.</summary>
        internal void Arm() => _armed = true;

        public int GetBalance(MongoId sessionId, Wallet wallet) => inner.GetBalance(sessionId, wallet);

        public int MaxStackSize(Wallet wallet) => inner.MaxStackSize(wallet);

        public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            Stall();
            return inner.TryDebit(sessionId, wallet, amount, output);
        }

        public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            Stall();
            inner.Credit(sessionId, wallet, amount, output);
        }

        private void Stall()
        {
            if (!_armed)
            {
                return;
            }

            if (Interlocked.Increment(ref _arrivals) == 1)
            {
                // Ungated, the second racer lands here and releases us at once. Gated,
                // it is parked on the session and this simply times out.
                _second.Task.Wait(TimeSpan.FromSeconds(1));
            }
            else
            {
                _second.TrySetResult();
            }
        }
    }
}
