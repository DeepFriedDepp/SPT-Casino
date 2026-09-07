using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Casino.Server;

/// <summary>
/// One player, one thing at a time, across the whole casino.
///
/// ## What this is for
///
/// SPT 4.0.13's request pipeline is plain Kestrel and ASP.NET Core middleware with no
/// serialisation of its own, so two requests for one profile really do run a mod's route
/// handlers at the same time. Nearly every money defect in this codebase is a composite
/// read-then-act across two or three calls with nothing holding the gap:
/// `escrow.Get` then `bank.Credit` then `escrow.Release` refunds the same stake twice;
/// `GetBalance` then `RemoveItemByCount` takes a stake the player cannot cover; a
/// `Place` landing after `table.Staked` has been read gets played for free.
///
/// A single stock client cannot do this to itself -- every `*Api.cs` call blocks on
/// `RequestHandler.PostJson` on Unity's main thread. It needs two clients on one
/// profile, a Fika setup, or a smoke script running while the game is open. Which is
/// exactly what the shared-table work is for, so this is a prerequisite rather than a
/// precaution.
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
/// That works because all four `.Server` projects reference `Casino.Server` and the
/// one-folder install means SPT loads exactly one copy of this assembly.
///
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
/// cannot. The code already contains one sync-over-async call
/// (`SlotService.Ping` -> `.GetAwaiter().GetResult()`), so this is a live hazard rather
/// than a theoretical one.
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
    /// <summary>
    /// Long enough that no honest operation reaches it -- the slowest thing under the
    /// gate is a profile save -- and short enough that a wedged session surfaces in a
    /// player's session rather than outliving it.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One semaphore per session, and they are never evicted.
    ///
    /// Deliberate: removing one while a caller holds it hands the next caller a
    /// different semaphore and silently un-serialises the session, which is the exact
    /// failure this class exists to prevent. A `SemaphoreSlim` is tiny and the key set
    /// is bounded by the profiles that have ever visited the casino this run.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Waits for this session's turn. Dispose the result to give it up -- always with
    /// <c>using</c>, so an exception inside the body cannot strand the permit.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The session did not come free within <see cref="Timeout"/>. That is a bug in this
    /// mod -- a leaked handle or a reentrant call -- not a busy server, and it says so
    /// rather than hanging.
    /// </exception>
    public async ValueTask<Releaser> EnterAsync(MongoId sessionId, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(sessionId.ToString(), _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"the casino waited {Timeout.TotalSeconds:N0}s for session {sessionId} and gave up. "
                + "Something is holding that session's gate -- a leaked handle, or a gated method "
                + "calling another gated method for the same session. See Casino.Server.SessionGate.");
        }

        return new Releaser(gate);
    }

    /// <summary>
    /// Gives the session back on dispose.
    ///
    /// A struct so the common path allocates nothing, and null-guarded because
    /// <c>default(Releaser)</c> is constructible and would otherwise throw on dispose.
    /// </summary>
    public readonly struct Releaser(SemaphoreSlim? gate) : IDisposable
    {
        public void Dispose() => gate?.Release();
    }
}
