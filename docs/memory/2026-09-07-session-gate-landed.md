# The session gate, landed across all four tables -- 2026-09-07

Follows `2026-09-07-money-races-verified.md`, which is the spec this was built from, and
supersedes its "order of work" list -- steps 1 to 4 are done.

| | |
| --- | --- |
| Tests | **495 passing**, up from 480 |
| Server half | 5 projects, 0 errors, 0 warnings |
| Client | builds against `C:\SPT` |
| Gate points | Blackjack 8, Roulette 7, Poker 6, Slots 3 |

## What was built

`src/Casino.Server/SessionGate.cs` -- **one** `[Injectable(InjectionType.Singleton)]`
keyed by session, shared by all four tables. Not one per table: `Bank` mutates the same
`pmcData.Inventory.Items` list whichever table the player is at, so per-table gates would
leave a blackjack deal racing a roulette spin unserialised, which is inventory corruption
rather than a miscount.

Every public service method is now a thin gated wrapper over an ungated private `*Core`.
That split is the mitigation for non-reentrancy: with the bodies private there is nothing
gated left for a gated method to call.

Three design choices, each with a test behind it:

- **The wait is bounded** (30s, then a loud `TimeoutException`). Unbounded, any leaked
  handle wedges that session forever -- *including the refund paths*, which is the worst
  thing to jam.
- **The permit returns on exception**, so one failed request cannot lock a player out
  until restart.
- **`EnterAsync` returns `ValueTask<Releaser>`, not `Task`.** `Task` implements
  `IDisposable`, so with a `Task` the line `using var _ = gate.EnterAsync(id);` -- the
  correct line with `await` dropped -- would compile, take no permit, and run unguarded
  while looking right. Verified rather than asserted: that exact line gives
  `error CS1674: 'ValueTask<SessionGate.Releaser>': type used in a using statement must
  implement 'System.IDisposable'`.

`Ping` and `Stats` are gated too, though they move no money. `bank.GetBalance` LINQ-walks
`pmcData.Inventory.Items` while a gated debit structurally modifies it, and
`StatsStore.Get` hands out a live object the JSON serialiser then walks. Neither is a
safe read. Neither can deadlock, because neither calls anything gated.

## Escrow rows are replaced, never edited

Two defects, both fixed, both proven first by a deterministic single-threaded test:

- **Blackjack** did `existing.Amount += amount` -- a non-atomic read-modify-write on an
  object `Get` hands out live. It *loses* money: the row under-records what was taken, so
  a crash refunds less than the player paid.
- **Poker** did two separate field writes, and `RefundAbandoned` reads `Wallet` to pick
  the currency -- so a reader catching the row between them could refund a **rouble stack
  as dollars**.

`tests/Blackjack.Server.Tests/EscrowSharingTests.cs` pins the property that makes both
possible, against the **real** store, with no threads: `Assert.NotSame` on two reads, and
that an earlier reader's row does not change underneath it. Failed first with
`Values are the same instance` and `Expected: 10000, Actual: 20000`.

## Every concurrency test was proven to fail without the gate

Not taken on trust. I disabled the gate in all three fanned-out tables myself and re-ran:

```
Blackjack   2 failed   Assert.Single() Failure: The collection contained 2 items
Roulette    2 failed   Assert.Null() Failure: Value is not null / Values differ
Slots       3 failed   Assert.Equal() Failure: Values differ  (x3)
Poker       2 failed   Expected: 1  Actual: 2
```

Then restored and confirmed green. This mattered: two independent reviewers had written
**stress** tests for this code that passed 7/7 and 20/20 **on the broken version**,
because `Escrow.Flush` takes a lock and writes a file on every call and serialises the
racers by accident.

**Forcing the race needs a timeout, not a `Barrier(2)`.** Once the gate is in, the second
racer never arrives and a hard barrier deadlocks the very test meant to prove the fix. The
`RaceProbe` in each `ConcurrencyTests.cs` waits with a bounded timeout instead, so the
same assertion works gated and ungated and neither path hangs.

## Notes from the rollout

**Blackjack's overload trap was real.** Three of its entry points are convenience
overloads that delegated to fuller ones; gating both halves of a pair is a self-deadlock.
It was resolved by deleting the delegation -- both overloads are now gated wrappers over
one shared core. The alternative (leave the 2-arg ungated) was rejected for a good
reason worth keeping: **every one of the 65 pre-existing tests drove exactly that
overload**, so the ungated path would have been the most-exercised and least-proven code
in the suite.

**Slots' `GetAwaiter().GetResult()` is gone.** `Ping` refunds stranded stakes, so it had
to be gated -- and a blocking wait inside a gated section starves the pool thread the gate
holder needs to resume. It is properly async now, and no `.Result` / `.Wait()` /
`GetAwaiter().GetResult()` exists anywhere in the server half.

**One proposed fix was correctly *not* built.** An analysis claimed Slots' shared
`Machine` races on a `System.Random`. Traced by hand: `IRandomSource.Create()` is
`public Random Create() => Random.Shared;` (`SlotMachine.Server/Escrow.cs:202`), and
`Random.Shared` is thread-safe; the `?? new Random()` fallback only fires in tests and the
console tool. No `lock(_machine)` was added, so no contention was introduced for nothing.

## Still to do

1. **The ordering fixes.** The partial-debit window and the crash windows (Blackjack and
   Poker debit before writing escrow; Roulette and Slots record before debiting). Both
   fixes originally proposed for these were **wrong** -- one was a silent no-op that
   minted money, the other destroyed the deal stake on double/split. Each needs its own
   design. See `2026-09-07-money-races-verified.md`.
2. `StatsStore.Get` should return a copy. Gating closes the same-session half; the
   cross-session half -- `Save` serialising the whole map while another thread mutates a
   `PlayerStats` inside it -- is only closed by copying.
3. **Nothing has been run inside a real SPT server.** Everything here is compile-and-unit-test.
