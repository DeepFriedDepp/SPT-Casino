# The money races, verified -- and why no fix has been written yet

Three independent analyses, each adversarially critiqued by a second agent that re-read every citation
and re-ran the experiments. **All three critiques returned NEEDS_WORK.** The analysis is sound; the
proposed fixes are not, and two of them move money in the wrong direction.

Nothing here is implemented. This is the spec to implement *from*, after the corrections below.

## The citations are good

My standing worry was that the analysis agents read the tree while it was being rebased. **Resolved:**
each critic independently re-read every `file:line` and confirmed the code says what was claimed.
One went further and decompiled `InventoryHelper` from the live `C:\SPT\SPT\SPTarkov.Server.Core.dll`
with Mono.Cecil to check the three load-bearing SPT claims. All correct.

## Severity, stated honestly

**A single stock client cannot trigger any of this alone.** `SlotApi.cs:60-66` and its siblings all block
on `RequestHandler.PostJson` on Unity's main thread, so one player is serial by construction. Reaching
these needs **two clients on one profile, a Fika setup, or a `scripts/*/smoke.ps1` running while the game
is open.**

That is not reassuring, it is the schedule: the shared-table work puts several humans on one server on
purpose. This is a prerequisite, exactly as decided.

## The mechanism is settled

**One per-session async gate (`SemaphoreSlim(1,1)`), taken at the service entry points.** Reproduced end
to end in a scratch project: ungated, two racers credit a stranded stake twice, 20/20 runs; gated, once,
20/20; no hangs either way.

`SemaphoreSlim` is forced rather than chosen -- Slots and Roulette both `await profiles.SaveAsync` in the
middle of a refund (`SlotService.cs:203`, `RouletteService.cs:324`), so a monitor cannot span it. Lock
ordering is clean and acyclic: gate -> `Escrow._writeLock` -> `SaveServer.saveLocks`, never the reverse,
and nothing in SPT calls back into a casino service. Per-session rather than global is right, and SPT's
`SaveServer` already holds a per-profile semaphore, so the gate adds no queueing SPT does not impose.

### Four corrections to it

1. **One gate, not four.** The specs proposed a `SessionGate` per table. Wrong: the resource being
   protected is the **profile**, not the table. `Bank.TryDebit` walks and `InventoryHelper.RemoveItemByCount`
   mutates the same live `pmcData.Inventory.Items` -- a plain `List<Item>` -- whichever table you are at.
   Four gates leave a blackjack deal racing a roulette spin on one profile completely unserialised, which
   is **inventory corruption, not a miscount**. And two people sharing a profile are *more* likely to pick
   different tables than the same one. It must be one `Casino.Server.SessionGate`,
   `[Injectable(InjectionType.Singleton)]`, injected into all four services. Feasible: all four
   `.Server.csproj` already reference `Casino.Server`, and one folder means one loaded copy.

2. **Bound the wait.** `WaitAsync()` with no timeout means any leaked handle or double-acquire wedges that
   session **permanently -- including the refund paths that give the player their money back**. The code
   already contains a sync-over-async call (`SlotService.cs:42`) inside what would become a critical
   section. A bounded wait that logs loudly and refuses is safer than an unbounded one.

3. **Do not hand back a `Task`.** `using var _ = await gate.EnterAsync(id);` is correct, but
   `using var _ = gate.EnterAsync(id);` -- the same line with `await` dropped -- **also compiles**, because
   `Task` implements `IDisposable`. It disposes the task, takes no permit, and the method runs unserialised
   while looking right. Return a bespoke non-`Task` awaitable.

4. **Gate `Ping` and `Stats` too.** The specs left them out as "pure reads". They are not.
   `Bank.StacksOf` LINQ-walks `pmcData.Inventory.Items` while a gated `TryDebit` structurally modifies it
   -> `InvalidOperationException: Collection was modified` on the request thread. And `StatsStore.Get`
   returns the **live** `PlayerStats`, so the JSON serialiser walks `ByCurrency` while a pull is inside
   `PlayerStats.Record` adding a key. Both are 500s. Gating them is cheap -- they move no money, so they
   cannot deadlock. `StatsStore.Get` should also return a copy, which is the only thing that closes the
   cross-session half.

## Three proposed fixes that must NOT be implemented

- **The Blackjack partial-debit fix is a no-op that mints money.** It proposed
  `escrow.Hold(sessionId, wallet, taken - request.Wager)`. That value is negative by construction, and
  `Hold` opens with `if (amount <= 0) return;`. The call does nothing, the row keeps the full wager while
  only `taken` left the stash, and the next refund credits the difference into existence. Blackjack's
  `Hold` is accumulate-only; reducing needs a new store operation.
- **The escrow-first flip destroys money on double/split.** At `BlackjackService.cs:156-158` a round is
  already live with the deal's stake held. Under the proposed order, a legitimate `TryDebit` refusal
  (player broke -- the *ordinary* case) reaches `escrow.Release`, which **deletes the whole row including
  the deal stake that really was taken**. Today's failure path touches escrow at all. Pure regression.
- **The stress/hammer tests are worthless.** One critic ran the spec's exact scenario seven times on
  known-broken code: clean every time. Another got 20/20 passes. `Flush`'s lock plus a real file write
  serialises the threads so thoroughly the window never opens. **A test that passes on broken code proves
  nothing.** Use the deterministic identity test instead -- `Assert.NotSame(first, second)` on the row
  handed out by `Get`, which fails today, reliably, with no threads at all.

**And make every fake thread-safe before writing a single concurrent test.** `FakeEscrow`, `FakeStats` and
`FakeProfiles` store into plain `Dictionary`, and the counters are non-atomic `++`. Left alone, the tests
produce nondeterministic dictionary corruption unrelated to the defect under test -- or worse, a lost
increment makes `Assert.Equal(1, bank.Credits)` **pass on today's broken code**.

## One proposed fix that is unnecessary

**Slots' shared `Machine` needs no lock.** One analysis claimed `Machine._rng` is a plain `System.Random`
shared across every session. Traced by hand: `IRandomSource.Create()` is
`public Random Create() => Random.Shared;` (`SlotMachine.Server/Escrow.cs:202`), and `Random.Shared` is
thread-safe. `Machine`'s `?? new Random()` fallback only fires when constructed with null, which is tests
and the console tool, not the server path. Two of the three agents got this right; the third assumed from
the field's declared type without following the call. Dropping it avoids adding contention for nothing.

## The race inventory

Reachability matters more than the count. **The specs mostly analysed races that need a prior crash --
a stranded escrow row refunded twice. The reachable form needs nothing to have gone wrong: a refund
racing a stake that is still live.** That reframing came from the critics, and it is the single most
important correction in this document, because none of the originally proposed tests covered it.

**Verified by execution:**

| What | Where | Effect |
| --- | --- | --- |
| `Place` lands after `table.Staked` is read | `RouletteService.cs:219` | **1,700,000 minted against a 100,000 debit.** Deterministic, repeatable, scales with whatever the racer adds |
| Two concurrent Sits take two buy-ins, keep one table | `PokerService.cs:110` | **2,000,000 destroyed**, nothing on disk knows it existed |
| A `State` refunds a buy-in still being paid | `PokerService.cs:119` vs `:363` | Free fully-funded 2,000,000 table; `Leave` later credits the stack again |
| Two concurrent Leaves cash out one stack | `PokerService.cs:308` | Entire stack duplicated. Needs no crash |
| A `State` refunds a **live** spin | `RouletteService.cs:232` vs `:304` | Free roll. In Slots, merely opening the panel during a pull mints the stake |
| Stranded refund pays twice | all four tables | One extra copy per racer |
| Two concurrent Deals | `BlackjackService.cs:88` | Uncaught `InvalidOperationException` **and** a lost wager |
| `existing.Amount += amount` | `Blackjack/Escrow.cs:126` | Under-records escrow -> **under-refunds**. The failure nobody reports |

**Analysed, not executed:** settlement paying winnings twice (`BlackjackService.cs:263`,
`RouletteService.cs:276`) -- reachable in *ordinary play*, which makes it more valuable than the refund
cases; `session.Staked` read-modify-write across `ActAsync`; Poker's torn `Wallet` field, which can refund
a rouble stack **as dollars**; crash-window ordering inconsistent across tables (Blackjack/Poker debit
before escrow, so a crash destroys the stake; Roulette/Slots record before debiting, so a crash mints one).

## What the gate cannot fix, and must not pretend to

`EventOutputHolder._outputStore` is a plain `Dictionary` in stock SPT, racy **across** sessions, and the
callbacks build the output *before* the service is invoked -- so both racers touch it whatever the gate
does. The mod cannot reach it. Say so plainly rather than appearing to cover it. The one honest benefit:
both racers write into the same `ItemEventRouterResponse`, and the gate does serialise the mod's own
writes into it.

## Order of work

1. Make the fakes thread-safe. Nothing else is trustworthy until this is done.
2. Write the deterministic failing tests -- identity tests first, then the live-stake races. **Confirm each
   one fails before writing any fix.** Per `CLAUDE.md`'s own rule.
3. `Casino.Server.SessionGate`: one singleton, bounded wait, non-`Task` awaitable.
4. Take it at every service entry point including `Ping` and `Stats`, in all four tables.
5. `StatsStore.Get` returns a copy.
6. Only then the ordering fixes (partial debit, crash windows) -- each needs its own design, and the two
   proposed above are wrong.
