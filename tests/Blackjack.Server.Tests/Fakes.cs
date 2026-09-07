using Blackjack.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// A wallet in memory. Records every movement so a test can assert not just the
/// final balance but that money moved the expected number of times -- a double
/// charged twice and a double charged once both end on the same balance if the
/// payout is also wrong.
/// </summary>
internal sealed class FakeBank : IBank
{
    /// <summary>
    /// Every method below is atomic, and that is load-bearing rather than tidy.
    ///
    /// The concurrency tests drive two threads through a *service*, and the defects
    /// they are looking for are composite -- `escrow.Get` then `bank.Credit` then
    /// `escrow.Release`, with the window sitting *between* the calls. Locking each
    /// call individually leaves every one of those windows exactly as wide as it is
    /// in production, so nothing under test is hidden.
    ///
    /// What it does remove is the fake's own ability to invent failures. Without it,
    /// `_balances` and the movement lists are plain collections written from two
    /// threads, so a concurrent test could fail with a corrupted `Dictionary` or an
    /// `InvalidOperationException` that has nothing whatever to do with the race it
    /// was written for -- and, far worse, `Debits.Add` losing an entry would make
    /// `Assert.Equal(1, bank.Debits.Count)` **pass on today's broken code**. A green
    /// test over a real defect is the one outcome worth going out of the way to avoid.
    ///
    /// The exposed lists are still only safe to *read* once the racing tasks have been
    /// awaited. Every test does that.
    /// </summary>
    private readonly object _lock = new();

    private readonly Dictionary<Wallet, int> _balances =
        Enum.GetValues<Wallet>().ToDictionary(w => w, w => w switch
        {
            Wallet.Roubles => 1_000_000,
            Wallet.Dollars or Wallet.Euros => 10_000,
            _ => 0,
        });

    /// <summary>No stack limit in the fakes -- splitting is the real Bank's problem.</summary>
    public int MaxStackSize(Wallet wallet) => int.MaxValue;

    internal List<(Wallet Wallet, int Amount)> Debits { get; } = [];

    internal List<(Wallet Wallet, int Amount)> Credits { get; } = [];

    /// <summary>
    /// Every response instance handed to this bank. The whole reason the parameter
    /// exists is that it reaches the client, so a test can check it was not swapped
    /// for a throwaway on the way down.
    /// </summary>
    internal List<ItemEventRouterResponse> Outputs { get; } = [];

    /// <summary>Forces TryDebit to fail, simulating money vanishing mid-round.</summary>
    internal bool RefuseDebits { get; set; }

    internal void SetBalance(Wallet wallet, int amount)
    {
        lock (_lock)
        {
            _balances[wallet] = amount;
        }
    }

    public int GetBalance(MongoId sessionId, Wallet wallet)
    {
        lock (_lock)
        {
            return _balances[wallet];
        }
    }

    public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        lock (_lock)
        {
            if (RefuseDebits || amount <= 0 || _balances[wallet] < amount)
            {
                return false;
            }

            _balances[wallet] -= amount;
            Debits.Add((wallet, amount));
            Outputs.Add(output);
            return true;
        }
    }

    public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        lock (_lock)
        {
            if (amount <= 0)
            {
                return;
            }

            _balances[wallet] += amount;
            Credits.Add((wallet, amount));
            Outputs.Add(output);
        }
    }
}

internal sealed class FakeProfiles : IProfileGateway
{
    private int _saves;

    internal bool Exists { get; set; } = true;

    /// <summary>Interlocked because `Saves++` from two threads silently loses one.</summary>
    internal int Saves => Volatile.Read(ref _saves);

    public bool HasProfile(MongoId sessionId) => Exists;

    public Task SaveAsync(MongoId sessionId)
    {
        Interlocked.Increment(ref _saves);
        return Task.CompletedTask;
    }
}

internal sealed class FakeStats : IStatsStore
{
    private readonly object _lock = new();

    private readonly Dictionary<string, PlayerStats> _stats = [];

    private int _saves;

    internal int Saves => Volatile.Read(ref _saves);

    /// <summary>
    /// Hands back the live object, exactly as the real <c>StatsStore</c> does. That is
    /// deliberate -- it is the shape under test, not an accident of the fake -- so a
    /// test that wants to prove the sharing is real can do so here as well as against
    /// the real store. Only the dictionary itself is protected.
    /// </summary>
    public PlayerStats Get(MongoId sessionId)
    {
        lock (_lock)
        {
            var key = sessionId.ToString();
            if (!_stats.TryGetValue(key, out var stats))
            {
                stats = new PlayerStats();
                _stats[key] = stats;
            }

            return stats;
        }
    }

    public void Save(MongoId sessionId, PlayerStats stats)
    {
        lock (_lock)
        {
            _stats[sessionId.ToString()] = stats;
        }

        Interlocked.Increment(ref _saves);
    }
}

internal sealed class FakeEscrow : IEscrowStore
{
    private readonly object _lock = new();

    private readonly Dictionary<string, OutstandingStake> _held = [];

    private int _releases;

    /// <summary>
    /// Releases that actually removed a row. A second concurrent release finds nothing
    /// and does not count -- which is how a test tells "both racers refunded" from
    /// "both racers tried".
    /// </summary>
    internal int Releases => Volatile.Read(ref _releases);

    public OutstandingStake? Get(MongoId sessionId)
    {
        lock (_lock)
        {
            return _held.TryGetValue(sessionId.ToString(), out var s) ? s : null;
        }
    }

    /// <summary>
    /// Accumulates in place, like the real Blackjack store. The `+=` is inside the
    /// lock here, so this fake does NOT reproduce the lost-update defect at
    /// `Blackjack.Server/Escrow.cs:126` -- that one is proved against the real store,
    /// deterministically, by asserting `Get` hands back the same instance twice.
    /// </summary>
    public void Hold(MongoId sessionId, Wallet wallet, int amount)
    {
        lock (_lock)
        {
            if (amount <= 0)
            {
                return;
            }

            var key = sessionId.ToString();
            if (_held.TryGetValue(key, out var existing))
            {
                existing.Amount += amount;
                return;
            }

            _held[key] = new OutstandingStake { Wallet = wallet.ToString(), Amount = amount };
        }
    }

    public void Release(MongoId sessionId)
    {
        bool removed;

        lock (_lock)
        {
            removed = _held.Remove(sessionId.ToString());
        }

        if (removed)
        {
            Interlocked.Increment(ref _releases);
        }
    }
}
