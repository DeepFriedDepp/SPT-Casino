using Blackjack.Game;
using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// Two requests at once against one shared table.
///
/// **SPT 4.0.13 does not serialise requests.** Two requests for one profile really do run
/// a mod's handlers at the same time, and a shared table adds a second thing worth
/// racing: two different people, arriving together, at one table.
///
/// ## How the interleaving is forced, and why not with a Barrier
///
/// The obvious tool is a `Barrier(2)` inside the bank -- hold the first racer until the
/// second arrives. It works exactly once: as soon as the gate is in, the second racer is
/// parked behind it and never arrives, so the barrier deadlocks and the test hangs rather
/// than passing. A hang tells nobody anything.
///
/// So <see cref="RaceProbe"/> waits with a TIMEOUT instead. Ungated, the second racer
/// lands inside the window and releases the first at once. Gated, it is parked and the
/// wait simply expires. The same assertion works either way and neither path hangs.
///
/// ## `Task.Run`, not a bare `Task.WhenAll`
///
/// `Task.WhenAll(f(), g())` evaluates its arguments left to right, and every fake here
/// completes synchronously -- so `f()` runs to the end before `g()` is called and the two
/// never overlap. Written that way, these tests passed with **both gates deleted**, which
/// is the worst possible outcome: a green test standing over a real defect. `Task.Run`
/// puts each racer on its own thread and is not optional.
///
/// ## What each of these actually proves
///
/// Every test below was run against a build with its guard removed. Two of them still
/// passed, and their remarks say so rather than claiming a proof they do not have -- a
/// test that cannot fail is worth less than no test, because it will later be read as
/// evidence. (Two independent reviewers wrote stress tests for this codebase that passed
/// 7/7 and 20/20 against code that was wrong: `Escrow.Flush` takes a lock and writes a
/// file on every call, which serialises the racers by accident. See CLAUDE.md.)
///
/// | test | guard removed | result |
/// | --- | --- | --- |
/// | double-clicked bet | both gates on BetAsync | FAILS -- two debits |
/// | double-clicked open | `TryClaim` | FAILS -- two tables |
/// | settling vs standing up | lock order reversed | FAILS -- hangs 10s |
/// | two people, one box | table gate on JoinAsync | passes anyway |
/// | leaving vs settling | -- | outcome only |
/// </summary>
public class SharedConcurrencyTests
{
    private const int Stake = 50_000;

    private const int Stash = 1_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedBlackjackStore _store = new();
    private readonly TableStore _solo = new();
    private readonly FakeOutputs _outputs = new();

    private static OpenTableRequest Open(int seats = 4) =>
        new() { Seats = seats, Wallet = nameof(Wallet.Roubles) };

    private static DealRequest Bet() =>
        new() { Wager = Stake, Wallet = nameof(Wallet.Roubles) };

    private SharedBlackjackService Build(IBank bank) => new(
        bank,
        new TableGate(),
        new SessionGate(),
        _store,
        _profiles,
        _escrow,
        _solo,
        _outputs,
        new CasinoSocket(new QuietLogger<CasinoSocket>()));

    /// <summary>
    /// A double-clicked BET must take one stake, not two.
    ///
    /// The check that stops it -- "your bet is already in the box" -- is a read followed
    /// by a debit followed by a write, with nothing holding the gap. Ungated, both racers
    /// read an empty box.
    ///
    /// **Proven to fail** with BOTH gates removed from BetAsync:
    /// `Assert.Single() Failure: The collection contained 2 items --
    /// [(Roubles, 50000), (Roubles, 50000)]`. The player is 100,000 down on a table
    /// showing a 50,000 bet.
    ///
    /// Either gate alone closes it, and that was checked in both directions rather than
    /// assumed. They are both there for different reasons: the TABLE gate because a bet is
    /// a write to a table other people are reading, and the SESSION gate because the same
    /// profile is reachable from roulette and slots, which know nothing about this table.
    /// </summary>
    [Fact]
    public async Task ADoubleClickedBetTakesOneStake()
    {
        var bank = new FakeBank();
        var probe = new RaceProbe(bank);
        var service = Build(probe);

        bank.SetBalance(Wallet.Roubles, Stash);

        Assert.True((await service.OpenAsync(Open(), _alice, new ItemEventRouterResponse())).Ok);

        probe.Arm();

        await Task.WhenAll(
            Task.Run(() => service.BetAsync(Bet(), _alice, new ItemEventRouterResponse())),
            Task.Run(() => service.BetAsync(Bet(), _alice, new ItemEventRouterResponse())));

        Assert.Single(bank.Debits);
        Assert.Equal(Stash - Stake, bank.GetBalance(_alice, Wallet.Roubles));

        var view = (await service.StateAsync(_alice)).SharedTable!;

        Assert.Equal(Stake, view.Seats[0].PendingBet);
        Assert.Equal(Stake, _escrow.Get(_alice)?.Amount);
    }

    /// <summary>
    /// Two people reaching for the last free box get one box between them.
    ///
    /// Nothing is debited on a blackjack join, so the cost of getting this wrong is not
    /// money: it is two people sharing one box, each acting on the other's cards.
    ///
    /// **This one does NOT prove the gate, and says so.** With the table gate removed from
    /// JoinAsync it still passes -- the engine refuses the second `TakeSeat` on its own
    /// (`BlackjackSeat.IsOccupied`), and the remaining window is too narrow for two threads
    /// to land in reliably. The probe cannot widen it either: it hooks `IBank`, and a
    /// blackjack join touches no money.
    ///
    /// It is kept as an OUTCOME test -- one seat, one occupant, and the loser free to go
    /// elsewhere -- which is worth asserting even though it is not evidence about the lock.
    /// Anything stronger would need a seam inside the engine, and putting one there to
    /// serve a test would be paying in production code for a proof.
    /// </summary>
    [Fact]
    public async Task TwoPeopleCannotTakeTheSameBox()
    {
        var bank = new FakeBank();
        var service = Build(bank);

        bank.SetBalance(Wallet.Roubles, Stash);

        // Two boxes: the host's, and exactly one free.
        Assert.True((await service.OpenAsync(Open(seats: 2), _alice, new ItemEventRouterResponse())).Ok);

        var id = _store.All.Single().Id;
        var carol = new MongoId();

        var results = await Task.WhenAll(
            Task.Run(() => service.JoinAsync(id, _bob)),
            Task.Run(() => service.JoinAsync(id, carol)));

        Assert.Single(results, r => r.Ok);

        var view = (await service.StateAsync(_alice)).SharedTable!;

        Assert.Equal(2, view.Seats.Count(seat => seat.IsOccupied));

        // And the one who was turned away is not left claimed at a table they never
        // reached -- that is the stuck-player shape, and it survives until a restart.
        var refused = _bob;
        if (results[0].Ok)
        {
            refused = carol;
        }

        Assert.True((await service.OpenAsync(Open(), refused, new ItemEventRouterResponse())).Ok);
    }

    /// <summary>
    /// One person double-clicking OPEN gets one table, not two.
    ///
    /// A table that does not exist yet cannot be gated, so the claim on the PLAYER is the
    /// whole of the protection here.
    ///
    /// **Proven to fail** with `TryClaim`'s result ignored: `Assert.Single() Failure: The
    /// collection contained 2 matching items` -- two tables, one player, seated at both and
    /// reachable from neither.
    /// </summary>
    [Fact]
    public async Task ADoubleClickedOpenMakesOneTable()
    {
        var bank = new FakeBank();
        var service = Build(bank);

        bank.SetBalance(Wallet.Roubles, Stash);

        var results = await Task.WhenAll(
            Task.Run(() => service.OpenAsync(Open(), _alice, new ItemEventRouterResponse())),
            Task.Run(() => service.OpenAsync(Open(), _alice, new ItemEventRouterResponse())));

        Assert.Single(results, r => r.Ok);
        Assert.Single(_store.All);
    }

    /// <summary>
    /// THE lock-order test. Settling while somebody stands up must not deadlock.
    ///
    /// ## The cycle this rules out
    ///
    /// Settlement is the only place that takes a session gate it was not handed -- it pays
    /// every seat in the round, so it holds the TABLE and reaches for a PLAYER. Standing up
    /// needs both as well. If those two ever take them in different orders:
    ///
    ///   * the settling request holds the table and waits for the player's gate
    ///   * the leaving request holds the player's gate and waits for the table
    ///
    /// and neither ever moves. That is why TableGate is the OUTER lock everywhere, without
    /// exception, and this is the test that notices when it stops being.
    ///
    /// One player is enough and is deliberate: a single stand settles the round inside the
    /// Act call, so the settlement is guaranteed to happen rather than depending on what
    /// was dealt. Two players and nobody acting would leave the round in PlayerTurn and
    /// the settlement -- the whole point -- would never run.
    ///
    /// **Proven to hang.** With LeaveAsync's two `using` lines swapped to session-then-
    /// table, the order this codebase forbids, this test stops dead and fails on the
    /// timeout below with the message it carries. Restored, it finishes in milliseconds.
    /// The `Task.WhenAny` is what turns a hang into a failure somebody can act on instead
    /// of a test run that never ends.
    /// </summary>
    [Fact]
    public async Task SettlingWhileStandingUpDoesNotDeadlock()
    {
        var bank = new FakeBank();
        var service = Build(bank);

        bank.SetBalance(Wallet.Roubles, Stash);

        Assert.True((await service.OpenAsync(Open(), _alice, new ItemEventRouterResponse())).Ok);
        Assert.True((await service.BetAsync(Bet(), _alice, new ItemEventRouterResponse())).Ok);
        Assert.True((await service.DealAsync(_alice, new ItemEventRouterResponse())).Ok);

        // Standing settles the round -- the dealer plays out and every seat is paid, which
        // is the call that takes a session gate while holding the table.
        var work = Task.WhenAll(
            Task.Run(() => service.ActAsync(
                new ActionRequest { Action = "Stand" }, _alice, new ItemEventRouterResponse())),
            Task.Run(() => service.LeaveAsync(_alice, new ItemEventRouterResponse())),
            Task.Run(() => service.StateAsync(_alice)));

        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            ReferenceEquals(finished, work),
            "Settling deadlocked against standing up. Check the lock order in "
            + "SharedBlackjackService: TableGate is the OUTER lock and SessionGate the inner, "
            + "and EVERY path must take them in that order. Settlement holds the table and "
            + "reaches for a player, so any path that does the reverse closes the cycle.");

        await work;

        // And having survived it, the books still balance -- a deadlock avoided by dropping
        // a lock would pass the assertion above and lose money here.
        var seat = (await service.StateAsync(_alice)).SharedTable?.Seats[0];

        if (seat is not null)
        {
            Assert.Equal(
                Stash - seat.TotalWagered + seat.TotalReturned,
                bank.GetBalance(_alice, Wallet.Roubles));
        }
    }

    /// <summary>
    /// Leaving and settling at once must not pay a stake twice.
    ///
    /// Standing up refunds an undealt bet; settling returns a dealt one. Both credit and
    /// both release the same single escrow row, so the two racing is the shape that pays
    /// one stake down two paths.
    /// </summary>
    [Fact]
    public async Task LeavingWhileTheTableSettlesPaysOneStakeOnce()
    {
        var bank = new FakeBank();
        var probe = new RaceProbe(bank);
        var service = Build(probe);

        bank.SetBalance(Wallet.Roubles, Stash);

        Assert.True((await service.OpenAsync(Open(), _alice, new ItemEventRouterResponse())).Ok);
        Assert.True((await service.JoinAsync(_store.All.Single().Id, _bob)).Ok);

        Assert.True((await service.BetAsync(Bet(), _bob, new ItemEventRouterResponse())).Ok);

        probe.Arm();

        // Alice deals -- which settles nothing for Bob only if he did not bet, and he did.
        // Meanwhile Bob tries to stand up and take his bet back.
        await Task.WhenAll(
            Task.Run(() => service.DealAsync(_alice, new ItemEventRouterResponse())),
            Task.Run(() => service.LeaveAsync(_bob, new ItemEventRouterResponse())));

        // **Both orders are legitimate, and they end in different places** -- which is why
        // this asserts the invariant rather than an outcome.
        //
        // If the leave won, Bob took his undealt bet back and owes nothing. If the deal
        // won, his hand is live, the leave was refused with "Finish the round first", and
        // his stake is STILL correctly in escrow. An assertion that escrow must be empty
        // would be asserting which racer won.
        //
        // What must hold either way: he is owed his stake exactly ONCE, down one path or
        // the other, never both.
        var held = _escrow.Get(_bob);

        if (held is not null)
        {
            Assert.Equal(Stake, held.Amount);
        }

        Assert.True(
            bank.GetBalance(_bob, Wallet.Roubles) <= Stash + Stake,
            $"Bob ended on {bank.GetBalance(_bob, Wallet.Roubles):N0} from a {Stash:N0} stash "
            + $"and one {Stake:N0} bet. A blackjack win pays at most 2x, so anything above "
            + $"{Stash + Stake:N0} is a stake paid down two paths.");

        // A refund and a settlement both credit. Exactly one of them may have happened, so
        // Bob has been credited at most once -- twice is the stake paid twice.
        Assert.True(
            bank.Credits.Count <= 1,
            $"Bob was credited {bank.Credits.Count} times for one bet: "
            + string.Join(", ", bank.Credits.Select(c => $"{c.Amount:N0} {c.Wallet}")));

        // And the two records agree. Whatever left the table's books reached his stash.
        var moved = bank.GetBalance(_bob, Wallet.Roubles) - (Stash - Stake);

        Assert.Equal(bank.Credits.Sum(c => c.Amount), moved);
    }

    /// <summary>
    /// Widens the window between a read and the write that depends on it, WITHOUT
    /// deadlocking once the gate closes it.
    ///
    /// The first racer to arrive waits for the second with a TIMEOUT. Ungated, the second
    /// lands here and releases it at once. Gated, the second never arrives -- it is parked
    /// on the session -- and the wait expires instead of hanging. See the class remarks
    /// for why a `Barrier(2)` cannot do this job.
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
                _second.Task.Wait(TimeSpan.FromSeconds(1));
            }
            else
            {
                _second.TrySetResult();
            }
        }
    }
}
