using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace Farkle.Server.Tests;

/// <summary>
/// A stash per player that is just a dictionary.
///
/// Per SESSION, unlike the slot machine's fake, because a Farkle match is the one place
/// in the casino where one player's loss is another player's credit and the test has to
/// see both sides. Every method is atomic for the reason the other fakes give: the
/// concurrency probe must not be able to invent a lost increment of its own.
/// </summary>
public sealed class FakeBank : IBank
{
    private readonly object _lock = new();

    private readonly Dictionary<string, int> _balances = new();

    /// <summary>Every move, in order: who, and by how much. What the invariant tests measure.</summary>
    public List<(string Session, int Amount)> Movements { get; } = [];

    public int Debits
    {
        get
        {
            lock (_lock)
            {
                return Movements.Count(m => m.Amount < 0);
            }
        }
    }

    public int Credits
    {
        get
        {
            lock (_lock)
            {
                return Movements.Count(m => m.Amount > 0);
            }
        }
    }

    public int RefusedDebits { get; private set; }

    /// <summary>The net of everything that moved, across every player. Zero is zero-sum.</summary>
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

    public void Seed(MongoId sessionId, int amount)
    {
        lock (_lock)
        {
            _balances[sessionId.ToString()] = amount;
        }
    }

    public int GetBalance(MongoId sessionId, Wallet wallet)
    {
        lock (_lock)
        {
            return _balances.GetValueOrDefault(sessionId.ToString());
        }
    }

    public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        lock (_lock)
        {
            var key = sessionId.ToString();
            var balance = _balances.GetValueOrDefault(key);

            if (amount <= 0 || balance < amount)
            {
                RefusedDebits++;
                return false;
            }

            _balances[key] = balance - amount;
            Movements.Add((key, -amount));

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

            var key = sessionId.ToString();
            _balances[key] = _balances.GetValueOrDefault(key) + amount;
            Movements.Add((key, amount));
        }
    }

    public int MaxStackSize(Wallet wallet) => 1_000_000;
}

public sealed class FakeProfiles : IProfileGateway
{
    private int _saves;

    public bool Exists { get; set; } = true;

    public int Saves => Volatile.Read(ref _saves);

    public Dictionary<string, string> Names { get; } = new();

    public string? NameOf(MongoId sessionId) =>
        Names.TryGetValue(sessionId.ToString(), out var name) ? name : null;

    public bool HasProfile(MongoId sessionId) => Exists;

    public Task SaveAsync(MongoId sessionId)
    {
        Interlocked.Increment(ref _saves);
        return Task.CompletedTask;
    }
}

/// <summary>Escrow without a file behind it.</summary>
public sealed class FakeEscrow : IEscrowStore
{
    private readonly object _lock = new();

    private readonly Dictionary<string, OutstandingStake> _held = new();

    public int Held
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

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
        }
    }

    public void Release(MongoId sessionId)
    {
        lock (_lock)
        {
            _held.Remove(sessionId.ToString());
        }
    }

    /// <summary>Plants a row as though a server had died holding it: a stake, and no table.</summary>
    public void Strand(MongoId sessionId, int amount) => Record(sessionId, Wallet.Roubles, amount);
}

/// <summary>
/// Dice that follow one seeded sequence across every call.
///
/// One `Random` handed back every time rather than a fresh one per call: a fresh
/// `Random(seed)` per roll would make every six-dice roll of a match identical, and a
/// match where both players farkle the same way every turn never ends.
/// </summary>
public sealed class FakeRandom(int seed) : Casino.Server.IRandomSource
{
    private readonly Random _rng = new(seed);

    public Random Create() => _rng;
}

public sealed class FakeOutputs : IOutputs
{
    private readonly Dictionary<string, ItemEventRouterResponse> _bySession = new();

    public ItemEventRouterResponse For(MongoId sessionId)
    {
        lock (_bySession)
        {
            var key = sessionId.ToString();

            if (!_bySession.TryGetValue(key, out var output))
            {
                output = new ItemEventRouterResponse();
                _bySession[key] = output;
            }

            return output;
        }
    }
}

/// <summary>A log that says nothing, so a test run is readable.</summary>
public sealed class QuietLog : IFarkleLog
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
}

/// <summary>For the real <c>CasinoSocket</c>, which wants SPT's logger and has nobody connected.</summary>
public sealed class QuietLogger<T> : ISptLogger<T>
{
    public void LogWithColor(string data, LogTextColor? textColor = null, LogBackgroundColor? backgroundColor = null, Exception? ex = null)
    {
    }

    public void Success(string data, Exception? ex = null)
    {
    }

    public void Error(string data, Exception? ex = null)
    {
    }

    public void Warning(string data, Exception? ex = null)
    {
    }

    public void Info(string data, Exception? ex = null)
    {
    }

    public void Debug(string data, Exception? ex = null)
    {
    }

    public void Critical(string data, Exception? ex = null)
    {
    }

    public void Log(LogLevel level, string data, LogTextColor? textColor = null, LogBackgroundColor? backgroundColor = null, Exception? ex = null)
    {
    }

    public bool IsLogEnabled(LogLevel level) => false;

    public void DumpAndStop()
    {
    }
}
