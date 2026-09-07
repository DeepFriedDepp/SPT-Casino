using Casino.Server;
using Poker.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Poker.Server.Tests;

/// <summary>
/// Two requests for one player, arriving at once.
///
/// ## Why this is reachable, and why it is not paranoia
///
/// A single stock client cannot do this to itself: every call in `PokerApi.cs` blocks
/// on `RequestHandler.PostJson` on Unity's main thread, so one player is serial by
/// construction. It takes two clients on one profile, a Fika group, or
/// `scripts/poker/smoke.ps1` running while the game is open -- and a shared Poker table
/// is several humans on one server on purpose, which is why this had to be closed
/// before that work rather than after it.
///
/// ## How the interleaving is forced, and why not with a Barrier
///
/// The obvious tool is a `Barrier(2)` inside the bank: hold the first racer until the
/// second arrives. That works only on the BROKEN code. Once the gate is in, the second
/// racer never reaches the bank at all -- it is waiting on the session -- so a hard
/// barrier would deadlock the very test that is supposed to prove the fix.
///
/// So <see cref="RaceProbe"/> waits with a timeout instead: the first caller into
/// `Credit` announces itself and gives the second a fixed window to show up. Ungated,
/// the second arrives and both pay out. Gated, the window simply expires and the first
/// carries on alone. The assertion is the same either way -- how many times the money
/// moved -- and neither path can hang.
///
/// A plain `Task.WhenAll` would prove nothing here: every fake completes synchronously,
/// so the first racer runs to completion before the second starts and the race never
/// happens.
/// </summary>
public class ConcurrencyTests
{
    private static readonly MongoId Session = new("6a8cd3a7e0b8272790f41285");

    private const int BuyIn = 2_000_000;

    private const int Roubles = 20_000_000;

    /// <summary>
    /// Two "stand up" requests at once must cash the stack out once, not twice.
    ///
    /// Ungated this pays the player's entire stack a second time -- verified by
    /// execution during the analysis -- and it needs no crash, no restart and no
    /// stranded escrow row. It is the ordinary path, which makes it the most reachable
    /// money-creating defect in the table.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentLeavesCashOutTheStackOnce()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Roubles);

        var probe = new RaceProbe(bank);
        var tables = new TableStore();
        var escrow = new FakeEscrow();

        var service = new PokerService(
            probe,
            new SessionGate(),
            new FakeProfiles(),
            tables,
            escrow,
            new FakeNames(),
            new SilentLog());

        var sat = await service.SitAsync(
            new SitRequest { Seats = 4, BuyIn = BuyIn, BigBlind = 20_000, Seed = 1 },
            Session,
            new ItemEventRouterResponse());

        Assert.True(sat.Ok);

        var afterBuyIn = bank.GetBalance(Session, Wallet.Roubles);
        Assert.Equal(Roubles - BuyIn, afterBuyIn);

        probe.Arm();

        await Task.WhenAll(
            Task.Run(() => service.LeaveAsync(Session, new ItemEventRouterResponse())),
            Task.Run(() => service.LeaveAsync(Session, new ItemEventRouterResponse())));

        // The stack came back exactly once. Ungated this is `afterBuyIn + 2 * stack`.
        Assert.Equal(1, bank.Credits);
        Assert.Equal(Roubles, bank.GetBalance(Session, Wallet.Roubles));

        // And the escrow row was retired once, not "removed" twice.
        Assert.Equal(1, escrow.Releases);
        Assert.Null(escrow.Get(Session));
    }

    /// <summary>
    /// Two "sit down" requests at once must take one buy-in, not two.
    ///
    /// This one runs the other way and is worse for the player: ungated, both racers
    /// debit a full buy-in, both record escrow (the second REPLACING the first, since
    /// Poker's escrow records rather than accumulates), and only one table survives.
    /// The second buy-in is destroyed outright, with nothing on disk that knows it ever
    /// existed -- so the lazy refund on next contact cannot give it back.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentSitsTakeOneBuyIn()
    {
        var bank = new FakeBank();
        bank.Seed(Wallet.Roubles, Roubles);

        var probe = new RaceProbe(bank);
        var tables = new TableStore();
        var escrow = new FakeEscrow();

        var service = new PokerService(
            probe,
            new SessionGate(),
            new FakeProfiles(),
            tables,
            escrow,
            new FakeNames(),
            new SilentLog());

        probe.Arm();

        static SitRequest Request() =>
            new() { Seats = 4, BuyIn = BuyIn, BigBlind = 20_000, Seed = 1 };

        await Task.WhenAll(
            Task.Run(() => service.SitAsync(Request(), Session, new ItemEventRouterResponse())),
            Task.Run(() => service.SitAsync(Request(), Session, new ItemEventRouterResponse())));

        // Exactly one buy-in left the stash...
        Assert.Equal(1, bank.Debits);
        Assert.Equal(Roubles - BuyIn, bank.GetBalance(Session, Wallet.Roubles));

        // ...and escrow records the buy-in that was actually taken, so a crash here
        // refunds the right amount rather than one of two.
        var owed = escrow.Get(Session);
        Assert.NotNull(owed);
        Assert.Equal(BuyIn, owed.Chips);
    }

    /// <summary>
    /// A bank that holds the first caller into <see cref="Credit"/> open for a moment,
    /// so a second caller has a chance to arrive inside the window.
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
}
