# Money races that exist today, before any shared table -- 2026-09-07

Found while scoping shared-table concurrency. **These are not shared-table problems.** They are in
the shipping code now, and two humans on one Fika server can already reach some of them.

Scope note: the SPT-side mechanics below were decompiled and measured against the **4.0.13** install
at `C:\SPT`. The repo currently targets 4.1.2/`~4.1.3`, which is not installed here and could not be
checked; the architecture is believed unchanged, but that is an assumption, not a measurement.

## SPT does not serialize requests

SPT 4.0.13's pipeline is plain Kestrel + ASP.NET Core middleware with **no serialization** --
handlers do run simultaneously. So every "one profile at a time" assumption in the money code is
resting on the player only having one client, not on anything the server enforces.

Worse, there is a **captive-dependency** problem in stock SPT: the Singleton `HttpServer`
constructor-injects the Scoped `IHttpListener`, so the whole router chain -- including every mod
`[Injectable]`, which defaults to **Scoped** -- is captured as **one process-wide instance**.
Verified empirically on net9/ASP.NET Core in Production. `EventOutputHolder._outputStore` is a plain
`Dictionary` and the mod writes all its money changes into it.

That last race is **not the mod's fault**: `EventOutputHolder` is captured in stock SPT with no mod
loaded, so two Fika players moving items already race on that dictionary. The mod's contribution is a
*third* writer -- the static routes -- operating outside the item-event `GetOutput`/`Clone`/`Reset`
lifecycle.

## Three defects in the mod's own money path

1. **A refund that duplicates money.** Four copies (one per table) of
   `escrow.Get() -> bank.Credit() -> escrow.Release()`. Two concurrent requests for the *same*
   session both read the escrow row, both credit, and the money is paid twice.
   `BlackjackService.cs:236-240` has a guard that returns null when a round is live
   (`tables.Has(sessionId) && Phase == RoundPhase.PlayerTurn`), which narrows the window --
   both racers still pass it when no live round exists, so the race stands.

2. **A check-then-act debit.** `GetBalance` then `RemoveItemByCount`. Nothing holds between them.

3. **Blackjack's escrow accumulate silently loses increments.** `existing.Amount += amount` --
   read-modify-write on a shared row.

All four `MoneyInvariantTests` are strictly sequential: one session, zero threads. They cannot catch
any of this. Per `CLAUDE.md`'s own rule -- write the invariant tests before the settlement -- the
concurrent invariants have not been written at all.

## Two incidental corrections to the code's own comments

- `Bank.TryDebit` hand-rolls a walk over money stacks, justified by a comment about `PaymentService`
  being rouble-only. That is true of `PayMoney` and `GiveProfileMoney`, but **false of
  `PaymentService` as a whole**: `AddPaymentToOutput(PmcData, MongoId currencyTpl, double, MongoId, ItemEventRouterResponse)`
  is public, currency-agnostic, and does exactly what `Bank.TryDebit` does by hand -- sorted stacks,
  smallest first, locked stacks skipped, stash prioritised. Switching to it would not fix the race
  (it is equally check-then-act), but the stated reason for the hand-rolled version does not hold.
- `SlotMachine.Server` has **no `TableStore`**; the machine is a field on `SlotService`
  (`new(random.Create())`), and `SlotService` is plain `[Injectable]` = Scoped = captured, so **one
  `Machine` is shared by every session**. Harmless today -- `RandomSource.Create()` returns
  `Random.Shared`, which is thread-safe, and `Machine` holds no other mutable state -- but it is
  cross-session shared state that nothing documents.

  (Slots does have per-session state: `StatsStore` and `Escrow`, both singletons, `stats-slots.json`
  persisted under a write lock.)

## Where a lock would have to go

Per **table instance**, not per profile and not global: a shared table serializes on the table, and
two different tables should not block each other. The escrow row and the engine state have to move
under the same lock, or the refund race just reappears between them.

Fixing these is independent of the shared-table work and does not need it. It is also a prerequisite
for it: a shared table multiplies every one of these windows by the number of seated players.
