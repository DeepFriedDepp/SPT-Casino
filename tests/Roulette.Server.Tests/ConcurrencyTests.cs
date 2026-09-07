using Casino.Server;
using Roulette.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Roulette.Server.Tests;

/// <summary>
/// Two requests for one player, arriving at once.
///
/// ## Why this is reachable, and why it is not paranoia
///
/// A single stock client cannot do this to itself: every call in `RouletteApi.cs` blocks
/// on `RequestHandler.PostJson` on Unity's main thread, so one player is serial by
/// construction. It takes two clients on one profile, a Fika group, or
/// `scripts/roulette/smoke.ps1` running while the game is open -- and the smoke script
/// exists precisely to be run against a live server, so this is a prerequisite for the
/// shared-table work rather than a precaution.
///
/// ## Why Roulette is the worst of the four
///
/// Blackjack and Poker settle against a hand that is already dealt. Roulette settles
/// against the cloth, and the cloth is a plain `List&lt;Bet&gt;` that `Place`, `Remove`
/// and `Clear` all write. `SpinCoreAsync` reads `table.Staked` into a local, debits
/// exactly that, and only then calls `table.Spin()` -- which sums the list *again*. A
/// `Place` that lands in that window is settled and paid for and never charged for.
///
/// It is not a rounding error and it is not probabilistic. The size of the theft is
/// whatever the racer puts on the cloth.
///
/// ## How the interleaving is forced, and why not with a Barrier
///
/// The obvious tool is a `Barrier(2)`: hold the spin until the racer arrives. That works
/// only on the BROKEN code. Once the gate is in, the racer never reaches the table at
/// all -- it is parked on the session -- so a hard barrier would deadlock the very test
/// meant to prove the fix.
///
/// So <see cref="SpinProbe"/> waits with a timeout instead. It differs from Poker's
/// probe in one way that matters here: the racer is released by the *test*, after its
/// whole call has returned, rather than by the racer's own first touch of the bank.
/// Poker's second racer does the same work as the first, so "it reached the bank" is a
/// good enough place to resume from. Here the racer's damage is done by
/// `table.Place`, several lines *after* its only bank call -- resuming on that call
/// would race the spin against the placement and make the outcome a coin toss instead
/// of a proof.
///
/// Ungated, the racer lands and the window closes behind it. Gated, the window simply
/// expires and the spin carries on alone. The assertions are the same either way -- what
/// the wallet moved, against what the engine settled on -- and neither path can hang.
///
/// A plain `Task.WhenAll` would prove nothing: every fake completes synchronously, so
/// the first racer runs to completion before the second starts and the race never
/// happens.
///
/// ## The wheel is pinned
///
/// <see cref="Seed"/> lands the ball in 11, which is black. That is not decoration: it
/// is what makes the headline failure a specific, quotable number rather than "the
/// balance was wrong sometimes".
/// </summary>
public class ConcurrencyTests
{
    private static readonly MongoId Session = new("6a9b474574813708e8fc3ce5");

    /// <summary>The first spin off this seed is 11 black. Pinned, and asserted below.</summary>
    private const int Seed = 20260905;

    private const int Roubles = 20_000_000;

    /// <summary>What the player actually put on the cloth and expects to pay for.</summary>
    private const int Stake = 100_000;

    /// <summary>What the racer slips on behind them, on the colour that comes in.</summary>
    private const int Slipped = 900_000;

    /// <summary>
    /// **The headline.** A `Place` that lands during a spin must not be played for free.
    ///
    /// Ungated this is the worst defect in the codebase, and it is deterministic:
    /// `SpinCoreAsync` reads `table.Staked` as 100,000, debits 100,000, and then settles
    /// a cloth of 1,000,000 -- paying 1,800,000 back on a 900,000 black that the wallet
    /// was never charged for. 1,700,000 minted against a 100,000 debit, and the figure
    /// scales with whatever the racer puts down.
    ///
    /// The two assertions that catch it are deliberately different in kind. One compares
    /// the debit to what the engine settled on, so the theft is named at its source. The
    /// other is the ordinary money invariant -- closing balance is opening less the
    /// stake plus the return -- so the same test would still fail if the mechanism moved.
    /// </summary>
    [Fact]
    public async Task APlaceThatLandsDuringASpinIsNotPlayedForFree()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Roubles);

        var probe = new SpinProbe(bank);
        var service = Table(probe);

        // What the player meant to spin for: one straight-up number that is not 11.
        var placed = await service.Place(
            new PlaceRequest { Kind = "Straight", Selection = 7, Amount = Stake }, Session);

        Assert.True(placed.Ok, placed.Error);

        probe.Arm();

        var spinning = Task.Run(() => service.SpinAsync(Session, Output()));

        // The spin has read the cloth and is inside the debit, holding the window open.
        await probe.Reached;

        var slipping = Task.Run(async () =>
        {
            // 900,000 on the colour that is about to come in. Ungated this is on the
            // cloth before `table.Spin()` sums it, and it is settled as though paid for.
            await service.Place(
                new PlaceRequest { Kind = "Black", Selection = 0, Amount = Slipped }, Session);

            probe.Release();
        });

        var spin = await spinning;
        await slipping;

        var last = spin.Table!.Last!;

        // The wheel is pinned, so the racer's colour is known to have come in. If this
        // ever changes the two assertions below stop being the headline and start being
        // a coincidence, so it is asserted rather than assumed.
        Assert.Equal(11, last.Number);
        Assert.Equal(nameof(PocketColour.Black), last.Colour);

        // One debit, and it is the whole of what the engine settled on. Ungated the
        // engine settles 1,000,000 and the wallet is charged 100,000.
        Assert.Equal(1, bank.Debits);

        var debited = -bank.Movements.Single(m => m.Amount < 0).Amount;
        Assert.Equal(last.Staked, debited);

        // And the ordinary invariant: the wallet moved by the return less the stake and
        // by nothing else. Ungated this is 1,700,000 to the good.
        Assert.Equal(Roubles - last.Staked + last.Returned, bank.GetBalance(Session, Wallet.Roubles));
        Assert.Equal(last.Returned - last.Staked, bank.Moved);

        // The cloth that was paid for is the cloth that was spun. The racer's chip
        // arrives after the wheel has turned and is refused by the engine's own phase
        // check, which is the correct answer to "the wheel has turned".
        Assert.Equal(Stake, last.Staked);
    }

    /// <summary>
    /// A `State` arriving mid-spin must not refund the live spin's stake.
    ///
    /// `SpinCoreAsync` records the stake in escrow *before* it debits, on purpose: a
    /// crash between the two leaves a record of money the player is owed. The cost is a
    /// window in which the escrow row describes a spin that is still running, and
    /// `RefundStranded` cannot tell that row from a stranded one -- it checks only
    /// `owed.Amount &lt;= 0`. There is no live-spin guard on this path at all.
    ///
    /// So ungated, a `State` in that window hands the stake straight back, the spin then
    /// pays its return on top, and the escrow release that should have retired the row
    /// finds it already gone. The player pays nothing and keeps the win.
    ///
    /// `State` is not an exotic thing to arrive here either: it is what the panel sends
    /// when it opens, and it is `RouletteSync` on the item-event transport, which the
    /// client fires at the end of every spin animation.
    /// </summary>
    [Fact]
    public async Task AConcurrentStateDoesNotRefundALiveSpin()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Roubles);

        var probe = new SpinProbe(bank);
        var escrow = new FakeEscrow();
        var service = Table(probe, escrow);

        var placed = await service.Place(
            new PlaceRequest { Kind = "Straight", Selection = 7, Amount = Stake }, Session);

        Assert.True(placed.Ok, placed.Error);

        probe.Arm();

        var spinning = Task.Run(() => service.SpinAsync(Session, Output()));

        // Inside the debit, which is after the escrow row was written. That row now
        // describes a spin that has not finished.
        await probe.Reached;

        RouletteResponse? state = null;

        var reading = Task.Run(async () =>
        {
            state = await service.StateAsync(Session, Output());
            probe.Release();
        });

        var spin = await spinning;
        await reading;

        var last = spin.Table!.Last!;

        // Nothing was stranded, so nothing was given back. Ungated this reads
        // "A spin was interrupted before it paid out. 100,000 has been returned."
        Assert.Null(state!.Note);

        // The stake was taken once and the return paid once -- no third movement.
        Assert.Equal(1, bank.Debits);
        Assert.Equal(last.Returned > 0 ? 1 : 0, bank.Credits);
        Assert.Equal(Roubles - last.Staked + last.Returned, bank.GetBalance(Session, Wallet.Roubles));

        // And the row the spin wrote was retired by the spin, exactly once.
        Assert.Equal(1, escrow.Releases);
        Assert.Null(escrow.Get(Session));
    }

    // ------------------------------------------------------------------ helpers

    private static RouletteService Table(IBank bank, FakeEscrow? escrow = null) =>
        new(bank,
            new SessionGate(),
            new FakeProfiles(),
            escrow ?? new FakeEscrow(),
            new TableStore(),
            new FakeRandom(Seed),
            new QuietLog());

    private static ItemEventRouterResponse Output() => new();

    /// <summary>
    /// A bank that holds the spin open inside its debit, so a second request has a
    /// chance to arrive while the cloth and the escrow row are both mid-flight.
    ///
    /// The debit is the right place to stop. It sits after `table.Staked` has been read
    /// into a local and after the escrow row has been written, and before `table.Spin()`
    /// re-sums the cloth -- which is exactly the span both defects live in.
    ///
    /// Bounded rather than a barrier, so the gated path -- where the second caller never
    /// arrives -- finishes instead of deadlocking. See the class remarks.
    /// </summary>
    private sealed class SpinProbe(FakeBank inner) : IBank
    {
        /// <summary>
        /// Long enough for a racer that is going to arrive to have arrived, short enough
        /// that the gated run -- where it never will -- is not a slow test.
        /// </summary>
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrivals;

        private volatile bool _armed;

        /// <summary>Starts widening the window. Setup calls should not be slowed down.</summary>
        internal void Arm() => _armed = true;

        /// <summary>Completes once the spin is inside the debit with the window open.</summary>
        internal Task Reached => _reached.Task;

        /// <summary>Closes the window. Called once the racer's whole call has returned.</summary>
        internal void Release() => _released.TrySetResult();

        public int GetBalance(MongoId sessionId, Wallet wallet) => inner.GetBalance(sessionId, wallet);

        public int MaxStackSize(Wallet wallet) => inner.MaxStackSize(wallet);

        public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            Stall();
            return inner.TryDebit(sessionId, wallet, amount, output);
        }

        public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output) =>
            inner.Credit(sessionId, wallet, amount, output);

        /// <summary>
        /// Only the first debit stalls. A second one would be a defect of its own, and
        /// holding it open would hide that behind a timeout.
        /// </summary>
        private void Stall()
        {
            if (!_armed || Interlocked.Increment(ref _arrivals) != 1)
            {
                return;
            }

            _reached.TrySetResult();

            // Ungated, the racer lands and releases us. Gated, it is parked on the
            // session and this simply times out.
            _released.Task.Wait(Window);
        }
    }
}
