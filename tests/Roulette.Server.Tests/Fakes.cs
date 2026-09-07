using Roulette.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Roulette.Server.Tests;

/// <summary>
/// A stash that is just a dictionary.
///
/// The reason the money tests need no server at all. It behaves the way the real bank
/// does at the boundaries that matter -- refusing a debit it cannot cover, never going
/// negative -- so a service that satisfies this one is doing the arithmetic right,
/// whatever `InventoryHelper` then does with it.
/// </summary>
public sealed class FakeBank : IBank
{
    /// <summary>
    /// Every method below is atomic, and that is load-bearing rather than tidy.
    ///
    /// The concurrency tests drive two threads through a *service*, and the defects
    /// they look for are composite -- `escrow.Get` then `bank.Credit` then
    /// `escrow.Release`, and `table.Staked` read at one line and settled at another,
    /// with the window sitting *between* the calls. Locking each call individually
    /// leaves every one of those windows exactly as wide as it is in production, so
    /// nothing under test is hidden.
    ///
    /// What it removes is this fake's own ability to invent failures: without it,
    /// `_balances` and `Movements` are plain collections written from two threads, and
    /// a lost `Debits++` would make `Assert.Equal(1, bank.Debits)` **pass on today's
    /// broken code**. A green test over a real defect is the outcome worth going out of
    /// the way to avoid.
    ///
    /// `Movements` and `Moved` are safe to read once the racing tasks have been awaited.
    /// </summary>
    private readonly object _lock = new();

    private readonly Dictionary<Wallet, int> _balances = new();

    private int _debits;

    private int _credits;

    private int _refusedDebits;

    /// <summary>Every move, in order. What the invariant test measures.</summary>
    public List<(Wallet Wallet, int Amount)> Movements { get; } = [];

    public int Debits => Volatile.Read(ref _debits);

    public int Credits => Volatile.Read(ref _credits);

    /// <summary>Set when a debit was refused, so a test can tell a refusal from a bug.</summary>
    public int RefusedDebits => Volatile.Read(ref _refusedDebits);

    /// <summary>The net of every movement. Zero means the player is exactly where they started.</summary>
    public int Moved
    {
        get
        {
            lock (_lock)
            {
                return Movements.Sum(m => m.Amount);
            }
        }
    }

    public void Seed(Wallet wallet, int amount)
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
            return _balances.GetValueOrDefault(wallet);
        }
    }

    public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        lock (_lock)
        {
            var balance = _balances.GetValueOrDefault(wallet);

            if (amount <= 0 || balance < amount)
            {
                _refusedDebits++;
                return false;
            }

            _balances[wallet] = balance - amount;
            Movements.Add((wallet, -amount));
            _debits++;

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

            _balances[wallet] = _balances.GetValueOrDefault(wallet) + amount;
            Movements.Add((wallet, amount));
            _credits++;
        }
    }

    /// <summary>Roubles stack to a million on a stock server; dollars and euros to 50,000.</summary>
    public int MaxStackSize(Wallet wallet) => wallet switch
    {
        Wallet.Roubles => 1_000_000,
        _ => 50_000,
    };
}

public sealed class FakeProfiles : IProfileGateway
{
    private int _saves;

    public bool Exists { get; set; } = true;

    /// <summary>Interlocked because `Saves++` from two threads silently loses one.</summary>
    public int Saves => Volatile.Read(ref _saves);

    public bool HasProfile(MongoId sessionId) => Exists;

    public Task SaveAsync(MongoId sessionId)
    {
        Interlocked.Increment(ref _saves);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Escrow without a file behind it.
///
/// Records what was taken for a spin and not yet returned. Unlike Poker's, which
/// tracks a live stack that moves every hand, there is nothing to update here: the
/// window between the debit and the credit contains no other event.
/// </summary>
public sealed class FakeEscrow : IEscrowStore
{
    private readonly object _lock = new();

    private readonly Dictionary<string, OutstandingStake> _held = new();

    private int _releases;

    /// <summary>Every value ever recorded, so a test can see when it was written.</summary>
    public List<int> Recorded { get; } = [];

    /// <summary>
    /// Releases that actually removed a row. A second concurrent release finds nothing
    /// and does not count -- which is how a test tells "both racers refunded" from
    /// "both racers tried".
    /// </summary>
    public int Releases => Volatile.Read(ref _releases);

    public OutstandingStake? Get(MongoId sessionId)
    {
        lock (_lock)
        {
            return _held.GetValueOrDefault(sessionId.ToString());
        }
    }

    public void Record(MongoId sessionId, Wallet wallet, int amount)
    {
        lock (_lock)
        {
            _held[sessionId.ToString()] = new OutstandingStake
            {
                Wallet = wallet.ToString(),
                Amount = amount,
                TakenAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            Recorded.Add(amount);
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

    /// <summary>
    /// Plants a stake as though a server had died holding it. What the record on disk
    /// would look like to the next session, with no table and no bets to go with it.
    /// </summary>
    public void Strand(MongoId sessionId, Wallet wallet, int amount)
    {
        lock (_lock)
        {
            _held[sessionId.ToString()] = new OutstandingStake
            {
                Wallet = wallet.ToString(),
                Amount = amount,
                TakenAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
    }
}

/// <summary>A wheel that lands where the test says. Seeded, so a run is repeatable.</summary>
public sealed class FakeRandom(int seed) : IRandomSource
{
    public Random Create() => new(seed);
}

/// <summary>A log that says nothing, so a test run is readable.</summary>
public sealed class QuietLog : IRouletteLog
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
