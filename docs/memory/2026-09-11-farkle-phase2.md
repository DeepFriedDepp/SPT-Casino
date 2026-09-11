# Farkle Phase 2: it plays -- 2026-09-11

The four open decisions were confirmed by the repo owner exactly as recommended in
`docs/farkle.md`, with one addition: the forfeit gets its own named rule and its own money
test rather than being an inference. This is what landed the same day, and what was
verified rather than assumed.

## Built

| Piece | What | Verified by |
| --- | --- | --- |
| `Farkle.Game.FarkleMatch` | Two seats, turns, the 500 threshold, hot dice, the last turn after 10,000, the tie rule, `Yield`, `Forfeit` | 121 engine tests green (was 81) |
| `Farkle.Game.FarkleBot` | One procedure, four dials, four characters, endgame overrides, mood | Tests pin the overrides and the ordering between characters, not the numbers |
| `Farkle.Server.SharedFarkleService` | Nine routes, table gate outer / session gate inner, settlement, the forfeit rule, quiet and absent seats, stranded refunds | 24 server tests green |
| `Farkle.Server.Bank` / `Escrow` | Fifth copies, from the slot machine's; `escrow-farkle.json` | Money invariants |
| `Casino.Server.RandomSource` | `IRandomSource` extracted at the third case | Build |
| `Farkle.Client.FarklePanel` | Lobby, stake, bot picker, match, dice you tap, replay of the other seat's turn, push plus poll | `Casino.Client` Release build, 0 warnings |
| `tools/Farkle.Console` | Round-robin measurement of the cast, and a narrated match | Run; table below |

**Not done: no live run.** Nothing has been inside a running SPT server. Every other table's
first live run found something.

## The concurrency test, proven to fail without the gate

`CLAUDE.md`: "A concurrency test must be proven to fail without the gate. Disable the
`gate.EnterAsync` line, watch it fail, quote the message, restore it." Done, twice, by
script: `tables.EnterAsync` in `LeaveAsync` and `sessions.EnterAsync` in the refund branch
of `LeaveCoreAsync` replaced with nothing, then the one test run:

```
Failed Farkle.Server.Tests.ConcurrencyTests.TwoLeavesOfAnUnjoinedTableRefundOnce [51 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
```

Two refunds of one stake from a double-clicked LEAVE. Gates restored: `Passed! - Failed: 0,
Passed: 24`. The probe waits with a 300ms timeout, not a `Barrier`, for the reason poker's
and blackjack's do -- gated, the second racer never arrives, and a barrier would hang.

## The forfeit rule, as a money test

`AForfeitPaysBothStakesToTheRemainingPlayer`: Alice opens, Bob joins, Alice rolls, Bob
leaves. Alice is at stash + stake, Bob at stash - stake, the bank's net movement is zero,
both escrow rows are gone, the match reads `Finished` / `Forfeit` with Alice the winner,
and Bob's leave response says where his stake went. The same rule, applied by the clock:
`TheAbsentSeatForfeitsAndTheOtherIsPaid`, at five minutes silent.

The other side of it, also tested: leaving a table nobody joined is not a forfeit and
refunds the stake; leaving a bot table mid-match loses the stake to the house with no
credit anywhere.

## The cast, measured

300 matches per pair, seed 1. Bank rate when the character had the choice, by dice it would
roll on with:

| | 1 | 2 | 3 | 4 | 5 | all | avg bank | farkle% | wins |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Kolya | 91% | 95% | 95% | 13% | 2% | 54% | 649 | 16% | 51% |
| Sveta | 94% | 43% | 26% | 5% | 1% | 27% | 899 | 40% | 51% |
| Timur | 94% | 87% | 34% | 8% | 2% | 42% | 747 | 26% | 52% |
| Vanya | 50% | 33% | 20% | 2% | 0% | 18% | 1,194 | 57% | 46% |

It took three runs to get here, and the middle one went backwards: taking Timur's
patience off to make him "stop the moment he clears the line" made him Kolya's twin at
94% / 95% with three dice left. **Patience is what separates a small-banker from a
stopper**, and **Greed shows in which dice are kept, not in the bank rate** -- the harness
cannot see it. Both are now written beside the numbers in `FarkleBot.cs`. Exact dial values
remain open, per the decision; these are measured starting points.

## Things found on the way

- The engine asks keep and bank as two steps; the bot answers them as one. The table
  carries a `BotWillBank` flag between the two, read when the match comes back to
  `Rolling` for the bot's seat.
- A join is the only touch a waiting table gets from anybody but its host, so it is where a
  host who opened a table and closed the game is noticed: the table goes, the host is
  refunded, the joiner has paid nothing. Tested.
- `FakeRandom` must hand back ONE `Random`, not a fresh `Random(seed)` per call: a fresh
  one makes every six-dice roll of a match identical, and a match where both players farkle
  the same way every turn never ends.
- SPT's `ISptLogger<T>` on 4.0.13 has `Log(LogLevel, string, LogTextColor?,
  LogBackgroundColor?, Exception?)`, `IsLogEnabled` and `DumpAndStop`, with `LogLevel` in
  `SPTarkov.Server.Core.Models.Spt.Logging`. The compiler says so if a fake drifts.
