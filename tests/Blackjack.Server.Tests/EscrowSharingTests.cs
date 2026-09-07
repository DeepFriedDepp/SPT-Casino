using System.Reflection;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Blackjack.Server.Tests;

/// <summary>
/// The real <see cref="EscrowStore"/>, not the fake, and no threads at all.
///
/// ## Why this is the gate for the whole concurrency effort
///
/// The obvious way to prove a lost update is to hammer the store from many threads and
/// watch the total come out short. That was tried, twice, by two independent reviewers:
/// 64 threads x 200 increments came out **exact, seven runs out of seven**, and a
/// second harness passed 20 out of 20. `Flush` takes a lock and writes a real file on
/// every `Hold`, which serialises the callers so thoroughly the window never opens.
///
/// **A stress test that passes on broken code is worse than no test**, because it will
/// be read later as evidence the defect is gone. So the proof here is deterministic and
/// single-threaded: it does not try to *observe* a lost update, it demonstrates the
/// property that makes one possible -- that <see cref="EscrowStore.Get"/> hands out the
/// store's own live object, and <see cref="EscrowStore.Hold"/> mutates it in place.
///
/// Given that, the interleaving needs no demonstration: two threads running
/// `existing.Amount += amount` against one shared object is a non-atomic
/// read-modify-write, and the `+=` is three operations, not one.
///
/// Both assertions below FAIL on today's code. That is the point of the file.
/// </summary>
public class EscrowSharingTests : IDisposable
{
    private const int Wager = 10_000;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "casino-escrow-tests",
        Guid.NewGuid().ToString("N"));

    private readonly MongoId _session = new();

    private EscrowStore Store()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "data"));

        var files = new FileUtil();
        var json = new JsonUtil([]);

        return new EscrowStore(
            new SilentLogger<EscrowStore>(),
            files,
            json,
            new FixedFolderModHelper(files, json, _folder));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch
        {
            // A temp folder that will not delete is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two holds, two reads: the store hands back the SAME instance both times.
    ///
    /// This is what makes the accumulate at `Escrow.cs:126` a shared-object mutation
    /// rather than a value update, and it is why a per-session gate is the fix rather
    /// than a `ConcurrentDictionary` -- the dictionary is already concurrent, and it is
    /// not the thing being raced. The object inside it is.
    /// </summary>
    [Fact]
    public void HoldHandsOutTheStoresOwnLiveRowRatherThanACopy()
    {
        var store = Store();

        store.Hold(_session, Wallet.Roubles, Wager);
        var first = store.Get(_session);

        store.Hold(_session, Wallet.Roubles, Wager);
        var second = store.Get(_session);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // FAILS TODAY: AddOrUpdate's delegate returns `existing`, so both reads are one object.
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// The consequence, stated as money: a caller holding a reference to the row sees
    /// its amount change underneath it.
    ///
    /// A refund path that has read the row and is about to credit it is doing exactly
    /// this, which is how a concurrent hold makes a refund pay out an amount nobody
    /// decided on.
    /// </summary>
    [Fact]
    public void ASecondHoldMutatesTheRowAnEarlierReaderIsStillHolding()
    {
        var store = Store();

        store.Hold(_session, Wallet.Roubles, Wager);
        var seenByTheFirstReader = store.Get(_session);
        Assert.NotNull(seenByTheFirstReader);
        Assert.Equal(Wager, seenByTheFirstReader.Amount);

        store.Hold(_session, Wallet.Roubles, Wager);

        // FAILS TODAY: the first reader's row is now 20,000. It read 10,000 and never
        // asked for it to change.
        Assert.Equal(Wager, seenByTheFirstReader.Amount);
    }

    /// <summary>Points the store at a temp folder instead of the mod's install folder.</summary>
    private sealed class FixedFolderModHelper(FileUtil fileUtil, JsonUtil jsonUtil, string folder)
        : ModHelper(fileUtil, jsonUtil)
    {
        public override string GetAbsolutePathToModFolder(Assembly modAssembly) => folder;
    }

    /// <summary>Swallows everything, so a test run stays readable.</summary>
    private sealed class SilentLogger<T> : ISptLogger<T>
    {
        public void LogWithColor(
            string data,
            LogTextColor? textColor = null,
            LogBackgroundColor? backgroundColor = null,
            Exception? ex = null)
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

        public void Log(
            LogLevel level,
            string data,
            LogTextColor? textColor = null,
            LogBackgroundColor? backgroundColor = null,
            Exception? ex = null)
        {
        }

        public bool IsLogEnabled(LogLevel level) => false;

        public void DumpAndStop()
        {
        }
    }
}
