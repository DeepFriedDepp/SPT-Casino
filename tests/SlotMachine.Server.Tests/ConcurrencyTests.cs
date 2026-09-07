using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace SlotMachine.Server.Tests;

/// <summary>
/// Two requests for one player, arriving at once.
///
/// ## Why this is reachable
///
/// A single stock client cannot do this to itself: every call in `SlotsApi.cs` blocks on
/// `RequestHandler.PostJson` on Unity's main thread, so one player is serial by
/// construction. It takes two clients on one profile, a Fika setup, or
/// `scripts/slots/smoke.ps1` running while the game is open.
///
/// It is worse here than at the other tables, because this machine has the one request
/// a player sends without meaning to spend anything -- **ping** -- and ping is not the
/// read it looks like. It refunds a stranded stake, and the stake it can find is the one
/// the pull in progress wrote down a line before it took the money. Opening the panel
/// during a pull handed that stake back and let the pull play for free.
///
/// ## How the interleaving is forced, and why not with a Barrier
///
/// The obvious tool is a `Barrier(2)` inside the bank: hold the first racer until the
/// second arrives. That works only on the BROKEN code. Once the gate is in, the second
/// racer never reaches the bank at all -- it is parked on the session -- so a hard
/// barrier would deadlock the very test that is meant to prove the fix.
///
/// So <see cref="RaceProbe"/> waits with a timeout instead. It also announces the
/// moment the first racer is *inside* the window, which is what makes these
/// deterministic rather than hopeful: the second request is not launched until the
/// stake has left the stash and escrow is holding it. Ungated the second racer arrives
/// and the window closes early; gated it never arrives and the window simply expires.
/// Neither path can hang.
///
/// A plain `Task.WhenAll` would prove nothing: every fake completes synchronously, so
/// the first racer runs to completion before the second starts and the race never
/// happens.
/// </summary>
public class ConcurrencyTests
{
    private static readonly MongoId Session = new("6a9b474574813708e8fc3ce5");

    private const int Stake = 10_000;

    private const int Rich = 500_000_000;

    /// <summary>
    /// Long enough that a hung test says so instead of sitting there. Nothing here is
    /// meant to take anywhere near it.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// **A ping landing inside a pull must not refund the stake that pull is playing.**
    ///
    /// Ungated the sequence is: the pull records the stake in escrow, takes it, and is
    /// still spinning when the ping reaches `RefundStranded`, reads that same live row,
    /// and credits it straight back. The pull then pays out on a stake the player no
    /// longer paid. No crash, no restart, no stranded row -- just the panel being
    /// opened at the wrong moment, which is the most reachable money-minting defect at
    /// this table.
    /// </summary>
    [Fact]
    public async Task APingDuringAPullDoesNotRefundTheStakeInPlay()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Rich);

        var probe = new RaceProbe(bank);
        var escrow = new FakeEscrow();
        var service = Service(probe, escrow, new FakeStats());

        var pull = Task.Run(() => service.PullAsync(Request(), Session, Output()));

        // The stake has gone and escrow is holding it: exactly the window a ping used
        // to walk into.
        await probe.Inside.WaitAsync(Patience);

        var ping = Task.Run(() => service.PingAsync(Session, Output()));

        var reply = await pull;
        await ping;

        Assert.True(reply.Ok, reply.Error);

        var paid = reply.Pull!.Paid;

        // Opening less the stake plus the winnings, and nothing else. Ungated this is
        // opening PLUS the winnings: the stake was handed back while it was being
        // played, so the pull was free.
        Assert.Equal(Rich - Stake + paid, bank.GetBalance(Session, Wallet.Roubles));

        // The stake left the stash exactly once...
        Assert.Equal(1, bank.Debits);

        // ...and the only thing paid back is what the reels paid. Ungated there is one
        // credit more than this: the ping refunding a stake that was still in play.
        Assert.Equal(paid > 0 ? 1 : 0, bank.Credits);

        // And nothing is left owing either way round.
        Assert.Null(escrow.Get(Session));
    }

    /// <summary>
    /// **Two pulls at once cost two stakes.**
    ///
    /// This is the same fault as the one above wearing a different hat, and it needs no
    /// second kind of request: a pull refunds anything stranded *before* it stakes
    /// anything, so the second pull's own refund finds the first pull's live escrow row
    /// and gives that stake back mid-flight. Two pulls, two payouts, one stake paid for.
    ///
    /// It is also why the overwrite this row was written to worry about -- Slots'
    /// `Escrow.Record` REPLACES rather than accumulates, so a second Record on a live
    /// row would discard the first's stake with nothing left on disk that knows it was
    /// taken -- does not show up in the numbers below. It cannot: the refund clears the
    /// row a moment before the second Record lands, having already done something worse
    /// with it. Both close together, and for the same reason, because the gate is what
    /// stops a second pull being inside the first one's window at all.
    ///
    /// The end state is asserted too: two stakes recorded, two rows retired, nothing
    /// held. What escrow wrote down is what the stash was actually charged.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentPullsAreChargedTwoStakes()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Rich);

        var probe = new RaceProbe(bank);
        var escrow = new FakeEscrow();
        var service = Service(probe, escrow, new FakeStats());

        var first = Task.Run(() => service.PullAsync(Request(), Session, Output()));

        await probe.Inside.WaitAsync(Patience);

        var second = Task.Run(() => service.PullAsync(Request(), Session, Output()));

        var replies = await Task.WhenAll(first, second);

        Assert.All(replies, reply => Assert.True(reply.Ok, reply.Error));

        var paid = replies.Sum(reply => reply.Pull!.Paid);

        // Two pulls, two stakes. Ungated the stash is down by one of them, because the
        // second pull's refund gave the first pull's stake back before staking its own.
        Assert.Equal(Rich - (2 * Stake) + paid, bank.GetBalance(Session, Wallet.Roubles));

        Assert.Equal(2, bank.Debits);

        // Nothing was handed back except winnings. Ungated there is one credit more --
        // the second pull refunding the first pull's stake out from under it.
        Assert.Equal(replies.Count(reply => reply.Pull!.Paid > 0), bank.Credits);

        // Each pull wrote its own stake down and retired its own row.
        Assert.Equal([Stake, Stake], escrow.Recorded);
        Assert.Equal(2, escrow.Releases);
        Assert.Null(escrow.Get(Session));
    }

    /// <summary>
    /// **A stats read that arrives during a pull is not a torn read.**
    ///
    /// `IStatsStore.Get` hands out the live record, and the pull writes to it between
    /// taking the stake and releasing the escrow. Ungated a read landing in that window
    /// sees a stash already charged for a pull the lifetime record has never heard of --
    /// and, worse, carries the live object out to the HTTP layer, where the JSON
    /// serialiser walks `ByCurrency` while <see cref="PlayerStats.Record"/> is adding a
    /// key to it. That throws `Collection was modified` onto the request thread.
    ///
    /// The gate closes the first half by making the read wait; the copy
    /// <see cref="SlotService"/> takes under it closes the second, because the walk that
    /// used to happen after the gate was given up now happens inside it.
    /// </summary>
    [Fact]
    public async Task AStatsReadDuringAPullWaitsForItAndComesBackDetached()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Rich);

        var probe = new RaceProbe(bank);
        var stats = new FakeStats();
        var service = Service(probe, new FakeEscrow(), stats);

        var pull = Task.Run(() => service.PullAsync(Request(), Session, Output()));

        // The stake is gone. The record of it has not been written yet -- that happens
        // a few lines later, once the reels have settled.
        await probe.Inside.WaitAsync(Patience);

        var view = await service.Stats(Session);
        var reply = await pull;

        // The read waited for the pull instead of catching it halfway: the stash is
        // down a stake and the record says so. Ungated this is 0, with the money
        // already moved.
        Assert.Equal(1, view.PullsPlayed);
        Assert.Equal(Stake, view.ByCurrency[nameof(Wallet.Roubles)].Wagered);
        Assert.Equal(reply.Pull!.Paid, view.ByCurrency[nameof(Wallet.Roubles)].Returned);

        // And it is a copy. What the HTTP layer serialises after the gate has been
        // given up is something the next pull cannot write to.
        Assert.NotSame(stats.Get(Session), view);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A service wired the way the server wires it, but for the bank.
    ///
    /// The random source is the **production** one -- `Random.Shared`, which is
    /// thread-safe -- rather than the seeded `FakeRandom` the other suites use. Two
    /// threads sharing one `new Random(seed)` is a data race in the fixture itself, and
    /// a test that races the code under test and its own scaffolding at the same time
    /// cannot say which of the two it caught. Nothing here cares where the reels land:
    /// every figure asserted is read back out of the reply.
    /// </summary>
    private static SlotService Service(IBank bank, IEscrowStore escrow, IStatsStore stats) =>
        new(bank,
            new Casino.Server.SessionGate(),
            new FakeProfiles(),
            escrow,
            new RandomSource(),
            stats,
            new QuietLog());

    private static PullRequest Request() =>
        new() { Wallet = nameof(Wallet.Roubles), Stake = Stake };

    private static ItemEventRouterResponse Output() => new();

    /// <summary>
    /// A bank that holds the first racer inside the escrow window, and says when it is
    /// there.
    ///
    /// <see cref="Inside"/> completes the moment a stake has been debited -- which in
    /// <see cref="SlotService"/> is after the escrow row is written and long before it
    /// is released. A test awaits it before launching the second request, so the
    /// interleaving is arranged rather than hoped for.
    ///
    /// The hold is a bounded wait, not a barrier, so the gated path -- where the second
    /// racer never reaches the bank -- finishes instead of deadlocking. See the class
    /// remarks.
    /// </summary>
    private sealed class RaceProbe(FakeBank inner) : IBank
    {
        /// <summary>
        /// How long the first racer holds the window open. Long enough for a second
        /// request to cross a thread-pool hop, short enough that three of these tests
        /// are still a few seconds.
        /// </summary>
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private readonly TaskCompletionSource _inside = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrivals;

        /// <summary>Completes once a stake has been taken and not yet paid back.</summary>
        internal Task Inside => _inside.Task;

        public int GetBalance(MongoId sessionId, Wallet wallet) => inner.GetBalance(sessionId, wallet);

        public int MaxStackSize(Wallet wallet) => inner.MaxStackSize(wallet);

        public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            // Held *after* the debit, so the window really is the one the mod has:
            // escrow written, money gone, nothing paid back yet.
            var taken = inner.TryDebit(sessionId, wallet, amount, output);
            Stall();

            return taken;
        }

        public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            // Ungated, this is where the second racer turns up: a refund of the stake
            // the first racer is still playing.
            Stall();
            inner.Credit(sessionId, wallet, amount, output);
        }

        private void Stall()
        {
            if (Interlocked.Increment(ref _arrivals) == 1)
            {
                _inside.TrySetResult();

                // Ungated, a second racer lands in the bank and releases this at once.
                // Gated, it is parked on the session and this simply times out.
                _second.Task.Wait(Window);
            }
            else
            {
                _second.TrySetResult();
            }
        }
    }
}
