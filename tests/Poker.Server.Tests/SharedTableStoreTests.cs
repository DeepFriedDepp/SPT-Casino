using Poker.Game;
using SPTarkov.Server.Core.Models.Common;

namespace Poker.Server.Tests;

/// <summary>
/// The store behind shared tables, and the one invariant that guards money:
/// **a player is at one table at a time.**
///
/// That sounds like bookkeeping and is not. Sitting down takes a buy-in out of a real
/// stash, so a player who ends up seated twice has paid twice -- and the second seat is
/// at a table nothing will ever cash them out of, because every lookup finds only one.
/// A double-clicked "join" is the ordinary way to reach it.
/// </summary>
public class SharedTableStoreTests
{
    private readonly SharedTableStore _store = new();

    private static SharedTable Table(string id = "t1") =>
        new()
        {
            Id = id,
            HostName = "Jonas",
            Table = new HoldemTable(seats: 2, agents: [new Folds()]),
            Agents = [],
            Characters = [],
            Seats = new Dictionary<int, SharedSeat>
            {
                [0] = new() { Index = 0, Kind = SeatKind.Human, Name = "Jonas", SessionId = null },
                [1] = new() { Index = 1, Kind = SeatKind.Bot, Name = "Bot" },
            },
            BuyIn = 2_000_000,
            BigBlind = 20_000,
            Wallet = Wallet.Roubles,
            OpenedAtUtc = 0,
        };

    [Fact]
    public void APlayerCanClaimAPlaceOnce()
    {
        var session = new MongoId();
        _store.Add(Table());

        Assert.True(_store.TryClaim(session, "t1"));
        Assert.False(_store.TryClaim(session, "t1"));
    }

    [Fact]
    public void ClaimingSomewhereElseWhileSeatedIsRefused()
    {
        var session = new MongoId();
        _store.Add(Table("t1"));
        _store.Add(Table("t2"));

        Assert.True(_store.TryClaim(session, "t1"));

        // The second table would take a second buy-in for a seat nothing cashes out.
        Assert.False(_store.TryClaim(session, "t2"));
        Assert.Equal("t1", _store.For(session)?.Id);
    }

    [Fact]
    public void ReleasingLetsThemSitSomewhereElse()
    {
        var session = new MongoId();
        _store.Add(Table("t1"));
        _store.Add(Table("t2"));

        Assert.True(_store.TryClaim(session, "t1"));
        _store.Release(session);

        Assert.True(_store.TryClaim(session, "t2"));
        Assert.Equal("t2", _store.For(session)?.Id);
    }

    /// <summary>
    /// THE ONE THAT MATTERS: two joins arriving together seat the player once.
    ///
    /// This is why `TryClaim` is an atomic `TryAdd` and not a read followed by a write.
    /// A check-then-act here is precisely the defect the money work spent a day removing
    /// from the other tables, and it would cost a whole buy-in rather than a blind.
    ///
    /// The interleaving is forced rather than hoped for: both tasks wait on one
    /// `TaskCompletionSource` so neither can finish before the other starts. Without
    /// that, the first would complete before the second began -- the store is fast and
    /// synchronous -- and the test would pass on a broken implementation.
    /// </summary>
    [Fact]
    public async Task TwoJoinsArrivingTogetherSeatThePlayerOnce()
    {
        var session = new MongoId();
        _store.Add(Table());

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var won = 0;

        async Task Join()
        {
            await go.Task;

            if (_store.TryClaim(session, "t1"))
            {
                Interlocked.Increment(ref won);
            }
        }

        var both = Task.WhenAll(Join(), Join());
        go.SetResult();
        await both.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref won));
    }

    [Fact]
    public void ForgettingATableFreesEveryoneAtIt()
    {
        var alice = new MongoId();
        var bob = new MongoId();

        var table = Table();
        table.Seats[0] = new SharedSeat
        {
            Index = 0, Kind = SeatKind.Human, Name = "Alice", SessionId = alice.ToString(),
        };
        table.Seats[1] = new SharedSeat
        {
            Index = 1, Kind = SeatKind.Human, Name = "Bob", SessionId = bob.ToString(),
        };

        _store.Seed(table, alice, bob);
        Assert.NotNull(_store.For(alice));
        Assert.NotNull(_store.For(bob));

        _store.Remove("t1");

        // Both are free to sit somewhere else, rather than stranded at a table that is
        // gone -- which would lock them out of poker until the server restarted.
        Assert.Null(_store.For(alice));
        Assert.Null(_store.For(bob));
        Assert.True(_store.TryClaim(alice, "t2"));
    }

    [Fact]
    public void ATableKnowsWhichSeatAPlayerIsIn()
    {
        var alice = new MongoId();
        var table = Table();
        table.Seats[1] = new SharedSeat
        {
            Index = 1, Kind = SeatKind.Human, Name = "Alice", SessionId = alice.ToString(),
        };

        Assert.Equal(1, table.SeatOf(alice)?.Index);
        Assert.Null(table.SeatOf(new MongoId()));
    }

    /// <summary>An agent that always folds. Enough to satisfy the engine's seat maths.</summary>
    private sealed class Folds : IPokerAgent
    {
        public HoldemDecision Decide(PokerContext context) => new(HoldemMove.Fold);

        public void HandEnded(HandOutcome outcome)
        {
        }
    }
}
