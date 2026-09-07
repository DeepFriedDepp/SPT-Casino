using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Casino.Server;

/// <summary>
/// The mechanism behind <see cref="SessionGate"/> and <see cref="TableGate"/>: one
/// async mutex per key, taken with a bounded wait.
///
/// Shared rather than written twice because this repo has already paid for that mistake
/// once -- `Bank.cs`, `Escrow.cs` and the rest exist four times, and when a bug is found
/// in one it is a bug in all four. See `CLAUDE.md` on drift.
///
/// It is deliberately NOT the public type. The two gates stay distinct so that the lock
/// ORDER can be stated in the type system's terms and checked by reading a signature --
/// see <see cref="TableGate"/>, where getting the order wrong is a deadlock.
/// </summary>
internal sealed class CasinoGate(string what)
{
    /// <summary>
    /// Long enough that no honest operation reaches it -- the slowest thing under a gate
    /// is a profile save -- and short enough that a wedged key surfaces during a player's
    /// session rather than outliving it.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One semaphore per key, and they are never evicted.
    ///
    /// Deliberate: removing one while a caller holds it hands the next caller a
    /// different semaphore and silently un-serialises the key, which is the exact failure
    /// this class exists to prevent. A `SemaphoreSlim` is tiny and the key set is bounded
    /// by the profiles and tables seen this run.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    internal async ValueTask<Releaser> EnterAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"the casino waited {Timeout.TotalSeconds:N0}s for {what} {key} and gave up. "
                + $"Something is holding that {what}'s gate -- a leaked handle, a gated method "
                + "calling another gated method for the same key, or a lock taken out of order. "
                + "See Casino.Server.Gates.");
        }

        return new Releaser(gate);
    }
}

/// <summary>
/// Gives a gate back on dispose.
///
/// A struct so the common path allocates nothing, and null-guarded because
/// <c>default(Releaser)</c> is constructible and would otherwise throw on dispose.
/// </summary>
public readonly struct Releaser(SemaphoreSlim? gate) : IDisposable
{
    public void Dispose() => gate?.Release();
}

/// <summary>
/// One player, one thing at a time, across the whole casino.
///
/// ## What this is for
///
/// SPT 4.0.13's request pipeline is plain Kestrel and ASP.NET Core middleware with no
/// serialisation of its own, so two requests for one profile really do run a mod's route
/// handlers at the same time. Nearly every money defect in this codebase was a composite
/// read-then-act across two or three calls with nothing holding the gap:
/// `escrow.Get` then `bank.Credit` then `escrow.Release` refunded the same stake twice;
/// `GetBalance` then `RemoveItemByCount` took a stake the player could not cover; a
/// `Place` landing after `table.Staked` had been read was played for free.
///
/// A single stock client cannot do this to itself -- every `*Api.cs` call blocks on
/// `RequestHandler.PostJson` on Unity's main thread. It needs two clients on one profile,
/// a Fika setup, or a smoke script running while the game is open.
///
/// ## Why the key is the SESSION and the gate is ONE object
///
/// The tempting design is a gate per table. It is wrong, and not by a little.
///
/// The resource actually being protected is the **profile**, not the table.
/// `Bank.TryDebit` walks `pmcData.Inventory.Items` and `InventoryHelper.RemoveItemByCount`
/// structurally modifies that same `List&lt;Item&gt;` -- whichever table the player is
/// sitting at. Four per-table gates would leave a blackjack deal racing a roulette spin
/// on one profile entirely unserialised, and concurrent mutation of a `List&lt;T&gt;` is
/// inventory corruption, not a miscount. Two people sharing a profile are, if anything,
/// *more* likely to pick different tables than the same one.
///
/// So: one instance, `InjectionType.Singleton`, injected into all four tables' services.
/// Keyed per session rather than global because two different players have no shared
/// state worth serialising, and SPT's own `SaveServer` already holds a per-profile
/// semaphore -- so this adds no queueing SPT does not already impose.
///
/// ## Three things that are easy to get wrong
///
/// **It is NOT reentrant.** A gated method that calls another gated method for the same
/// session deadlocks against itself, permanently. Public entry points take the gate;
/// everything they call must be an ungated private core. If you add a gated method,
/// check what it calls.
///
/// **The wait is bounded on purpose.** An unbounded `WaitAsync()` means any leaked
/// handle or accidental double-acquire wedges that session forever -- *including the
/// refund paths that give the player their money back*, which is the worst possible
/// thing to jam. A request that fails loudly can be retried; a session that hangs
/// cannot.
///
/// **`EnterAsync` deliberately returns a `ValueTask&lt;Releaser&gt;`, not a `Task`.**
/// `Task` implements `IDisposable`, so with a `Task` return the line
/// `using var _ = gate.EnterAsync(id);` -- the correct line with the `await` dropped --
/// would compile, dispose the task, take no permit at all, and run the body
/// unserialised while looking right. `ValueTask&lt;T&gt;` is not `IDisposable`, so that
/// typo is a compile error instead of a silent hole in the money path.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class SessionGate
{
    private readonly CasinoGate _gate = new("session");

    /// <summary>How long a caller waits before the gate gives up and says so.</summary>
    public static TimeSpan Timeout => CasinoGate.Timeout;

    /// <summary>
    /// Waits for this session's turn. Dispose the result to give it up -- always with
    /// <c>using</c>, so an exception inside the body cannot strand the permit.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The session did not come free within <see cref="Timeout"/>. That is a bug in this
    /// mod -- a leaked handle, a reentrant call, or a lock taken out of order -- not a
    /// busy server, and it says so rather than hanging.
    /// </exception>
    public ValueTask<Releaser> EnterAsync(MongoId sessionId, CancellationToken cancellationToken = default) =>
        _gate.EnterAsync(sessionId.ToString(), cancellationToken);
}

/// <summary>
/// One shared table, one thing at a time.
///
/// A table that several humans sit at has state none of them owns: the engine, the
/// seats, the pot, and whose turn it is. <see cref="SessionGate"/> cannot cover it --
/// that gate is keyed by player, and the whole point here is that two different players
/// touch the same table.
///
/// ## THE LOCK ORDER, WHICH IS THE THING MOST LIKELY TO GO WRONG
///
/// **A table gate is always the OUTER lock. Never reach for one while holding a
/// <see cref="SessionGate"/>.**
///
/// Both get taken on the same call, because dealing a hand touches the table *and* takes
/// money from a profile and those cannot be separated -- the entire point of the money
/// code is that you do not deal a hand you cannot collect on. So the order has to be
/// fixed, and this is it:
///
///     using var table = await tables.EnterAsync(tableId);      // outer
///     using var player = await sessions.EnterAsync(sessionId); // inner
///
/// The other order deadlocks, and the failure is nasty. Player A takes their session gate
/// and wants the table; player B holds the table and wants A's session gate to charge
/// them a blind. Neither moves, both wedge for the full timeout, and it surfaces as
/// "poker stopped working" long after the code that caused it ran.
///
/// Nothing else in the casino takes a table gate, so this is safe against the rest of it:
/// a player at a shared table who also has blackjack open in a second client is fine,
/// because blackjack takes only a session gate and no path anywhere goes gate-then-table.
///
/// Bounded for the same reason the session gate is, and more urgently -- a wedged table
/// is worse than a wedged player, because the money for a live hand is already in escrow
/// and several people are waiting on it.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class TableGate
{
    private readonly CasinoGate _gate = new("table");

    /// <summary>How long a caller waits before the gate gives up and says so.</summary>
    public static TimeSpan Timeout => CasinoGate.Timeout;

    /// <summary>
    /// Waits for this table's turn. **Take this BEFORE any session gate**, never after --
    /// see the type's remarks.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The table did not come free within <see cref="Timeout"/>. The likeliest cause is
    /// the lock order being broken somewhere.
    /// </exception>
    public ValueTask<Releaser> EnterAsync(string tableId, CancellationToken cancellationToken = default) =>
        _gate.EnterAsync(tableId, cancellationToken);
}
