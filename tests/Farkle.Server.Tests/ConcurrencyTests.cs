using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Farkle.Server.Tests;

/// <summary>
/// Two requests for one player in the same instant, and what the gates do about it.
///
/// **Proven to fail without the gate**, per `CLAUDE.md`: with the `tables.EnterAsync` and
/// `sessions.EnterAsync` lines in `LeaveAsync` / `LeaveCoreAsync` commented out, the test
/// below fails with `Assert.Equal() Failure: Expected: 1, Actual: 2` on the credit count
/// -- two refunds of one stake. Restored, it passes. See
/// `docs/memory/2026-09-11-farkle-phase2.md` for the run.
///
/// ## Why the probe waits with a timeout
///
/// The race has to be forced, because two `Task.Run`s at human speed rarely overlap. The
/// probe below blocks the FIRST racer inside the bank until the second arrives -- and the
/// second only arrives if nothing is serialising them. With the gate in, it never does,
/// so the wait must expire rather than hang; a `Barrier(2)` would deadlock the moment the
/// fix went in. Same shape as poker's and blackjack's probes.
/// </summary>
public class ConcurrencyTests
{
    private const int Stake = 100_000;
    private const int Stash = 1_000_000;

    private readonly MongoId _alice = new();

    /// <summary>
    /// A double-clicked LEAVE on a table nobody joined. Each click is a refund of the whole
    /// stake, and there is exactly one stake.
    /// </summary>
    [Fact]
    public async Task TwoLeavesOfAnUnjoinedTableRefundOnce()
    {
        var inner = new FakeBank();
        inner.Seed(_alice, Stash);

        var probe = new RaceProbe(inner);
        var escrow = new FakeEscrow();
        var store = new SharedFarkleStore();
        var profiles = new FakeProfiles();

        var service = new SharedFarkleService(
            probe,
            new TableGate(),
            new SessionGate(),
            store,
            profiles,
            escrow,
            new FakeOutputs(),
            new CasinoSocket(new QuietLogger<CasinoSocket>()),
            new FakeRandom(1),
            new QuietLog());

        var opened = await service.OpenAsync(new OpenTableRequest { Stake = Stake }, _alice, new ItemEventRouterResponse());
        Assert.True(opened.Ok, opened.Error);

        var first = Task.Run(() => service.LeaveAsync(_alice, new ItemEventRouterResponse()));
        var second = Task.Run(() => service.LeaveAsync(_alice, new ItemEventRouterResponse()));

        await Task.WhenAll(first, second);

        Assert.Equal(1, inner.Credits);
        Assert.Equal(Stash, inner.GetBalance(_alice, Wallet.Roubles));
        Assert.Equal(0, escrow.Held);
        Assert.Empty(store.All);

        // One of the two was told the table was gone, or that they were not at one.
        Assert.Single(new[] { first.Result, second.Result }, r => r.Ok);
    }

    /// <summary>
    /// Holds the first refund open until a second one arrives, or 300ms pass. Under the
    /// gate the second never arrives inside the window, so the first proceeds alone and
    /// the second finds nothing to refund.
    /// </summary>
    private sealed class RaceProbe(FakeBank inner) : IBank
    {
        private readonly SemaphoreSlim _arrived = new(0);

        private int _inside;

        public int GetBalance(MongoId sessionId, Wallet wallet) => inner.GetBalance(sessionId, wallet);

        public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output) =>
            inner.TryDebit(sessionId, wallet, amount, output);

        public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
        {
            var me = Interlocked.Increment(ref _inside);
            _arrived.Release();

            if (me == 1)
            {
                // First in. Wait for a second racer to reach this point -- which only a
                // missing gate allows -- then carry on regardless.
                _arrived.Wait();
                _arrived.Wait(TimeSpan.FromMilliseconds(300));
            }

            inner.Credit(sessionId, wallet, amount, output);
        }

        public int MaxStackSize(Wallet wallet) => inner.MaxStackSize(wallet);
    }
}
