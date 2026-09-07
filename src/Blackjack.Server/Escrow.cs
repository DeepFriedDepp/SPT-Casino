using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Blackjack.Server;

/// <summary>A stake taken from a player whose round has not settled.</summary>
public class OutstandingStake
{
    public string Wallet { get; set; } = nameof(Server.Wallet.Roubles);

    public int Amount { get; set; }

    public long TakenAtUtc { get; set; }
}

/// <summary>
/// Records money that has left the player but not yet been settled.
///
/// The table itself is in memory on purpose -- a half-played hand should not survive
/// a restart. The stake is a different matter: it is debited from the profile and
/// written to disk immediately, so without this a crash mid-hand takes the player's
/// money and leaves no hand to win it back with. Anything found outstanding is
/// refunded the next time that player is seen.
///
/// The same record covers items held in a bet container once valuables are staked
/// through EFT's grid, which has exactly this failure mode.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class EscrowStore : IEscrowStore
{
    private const string FileName = "escrow-blackjack.json";

    /// <summary>What this file was called when this table was its own mod.</summary>
    private const string LegacyFileName = "escrow.json";

    private readonly ISptLogger<EscrowStore> _logger;
    private readonly FileUtil _fileUtil;
    private readonly JsonUtil _jsonUtil;
    private readonly string _path;
    private readonly Lock _writeLock = new();
    private readonly ConcurrentDictionary<string, OutstandingStake> _held;

    public EscrowStore(
        ISptLogger<EscrowStore> logger,
        FileUtil fileUtil,
        JsonUtil jsonUtil,
        ModHelper modHelper)
    {
        _logger = logger;
        _fileUtil = fileUtil;
        _jsonUtil = jsonUtil;

        var folder = System.IO.Path.Combine(
            modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()),
            "data");

        _fileUtil.CreateDirectory(folder);
        _path = System.IO.Path.Combine(folder, FileName);

        // Named per table because all three now share one mod folder, and all three
        // used to call this escrow.json. One file with three writers would have been
        // three tables overwriting each other's record of money they owe.
        //
        // Which means the old file is somewhere this would never look, so it is
        // imported once. See Casino.Server.LegacyData.
        _held = Load();

        if (_held.IsEmpty)
        {
            var carried = Casino.Server.LegacyData.Find(
                modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()),
                "Blackjack",
                LegacyFileName);

            if (carried is not null)
            {
                _held = Load(carried);

                if (!_held.IsEmpty)
                {
                    _logger.Info(
                        $"[Blackjack] carried {_held.Count} unfinished session(s) over from {carried}.");
                    Flush();
                }
            }
        }

        if (!_held.IsEmpty)
        {
            _logger.Info($"Blackjack: {_held.Count} unsettled stake(s) carried over -- each is refunded on next contact.");
        }
    }

    public int Outstanding => _held.Count;

    public OutstandingStake? Get(MongoId sessionId) =>
        _held.TryGetValue(sessionId.ToString(), out var stake) ? stake : null;

    /// <summary>
    /// Adds to whatever is already held. Doubling and splitting raise the stake after
    /// the fact, so this accumulates rather than replaces.
    ///
    /// ## The update delegate publishes a NEW row rather than editing the stored one
    ///
    /// It used to be `existing.Amount += amount; return existing;`, and that was wrong
    /// in two ways that compound.
    ///
    /// `+=` on a field is a read, an add and a write, not one operation, so two callers
    /// accumulating at once could both read the same value and the second write would
    /// erase the first. That direction **loses** money: the row under-records what was
    /// actually taken, and a refund after a crash then pays back less than the player
    /// paid. Under-refunding is the failure nobody reports, because the player has no
    /// way to see what the file said.
    ///
    /// Worse, `Get` hands out the store's own object, so a caller that has read the row
    /// and is about to refund it watches the amount change underneath it. That is the
    /// property `EscrowSharingTests` pins down, deterministically and without threads --
    /// stress tests were tried and passed on the broken code, because `Flush` takes a
    /// lock and writes a file on every call and that serialises the callers by accident.
    ///
    /// A pure delegate fixes both. `AddOrUpdate` re-runs it when its compare-and-swap
    /// loses, so an accumulation that races another is recomputed against the value that
    /// actually won rather than against a stale one -- and because the stored reference
    /// is replaced rather than edited, nobody else's row moves under them.
    ///
    /// Roulette and Slots already published fresh rows. This is Blackjack catching up,
    /// which is the drift `CLAUDE.md` warns about, on the money path.
    /// </summary>
    public void Hold(MongoId sessionId, Wallet wallet, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var key = sessionId.ToString();
        _held.AddOrUpdate(
            key,
            _ => new OutstandingStake
            {
                Wallet = wallet.ToString(),
                Amount = amount,
                TakenAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
            (_, existing) => new OutstandingStake
            {
                // The wallet and the moment of taking belong to the stake that opened
                // the row; only the amount accumulates.
                Wallet = existing.Wallet,
                Amount = existing.Amount + amount,
                TakenAtUtc = existing.TakenAtUtc,
            });

        Flush();
    }

    public void Release(MongoId sessionId)
    {
        if (_held.TryRemove(sessionId.ToString(), out _))
        {
            Flush();
        }
    }

    private void Flush()
    {
        lock (_writeLock)
        {
            try
            {
                var json = _jsonUtil.Serialize(_held, true);

                if (json is null)
                {
                    // Writing nothing would truncate the file and lose every stake it
                    // was holding, which is worse than failing to write at all.
                    _logger.Error($"Blackjack: the outstanding stakes would not serialise -- {_path} left as it was.");
                    return;
                }

                _fileUtil.WriteFile(_path, json);
            }
            catch (Exception ex)
            {
                // Worth shouting about: a stake that cannot be recorded is a stake that
                // cannot be refunded if the server goes down before the hand ends.
                _logger.Error($"Blackjack: could not record the outstanding stake at {_path} -- {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the record. <paramref name="from"/> is how the old file gets imported
    /// when the tables moved into one folder; everything else reads the live one.
    /// </summary>
    private ConcurrentDictionary<string, OutstandingStake> Load(string? from = null)
    {
        var source = from ?? _path;

        if (!_fileUtil.FileExists(source))
        {
            return new ConcurrentDictionary<string, OutstandingStake>();
        }

        try
        {
            var loaded = _jsonUtil.Deserialize<Dictionary<string, OutstandingStake>>(_fileUtil.ReadFile(source));
            return new ConcurrentDictionary<string, OutstandingStake>(loaded ?? []);
        }
        catch (Exception ex)
        {
            _logger.Error($"Blackjack: escrow file at {source} is unreadable -- {ex.Message}");
            return new ConcurrentDictionary<string, OutstandingStake>();
        }
    }
}
