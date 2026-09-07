# One escrow row, two tables -- a mint in shipped poker

**2026-09-07.** Found while building shared blackjack. It is in the poker people are
already playing.

## The shape

`escrow-poker.json` and `escrow-blackjack.json` hold **one row per session**. That was
correct while a player could only ever be at their own table. Shared tables gave a
session a second place to owe money from, and nothing noticed.

Both services have a `RefundAbandoned` / `RefundAbandonedStake` that runs on **sit, on
deal, and on state** -- which is to say, on opening the panel. Each decides whether a
stake is orphaned by asking one question:

```csharp
if (tables.Get(sessionId) is not null) return null;   // poker
if (tables.Has(sessionId) && ...Phase == PlayerTurn) return null;   // blackjack
```

`tables` there is the **private** store. A stake belonging to a shared table fails that
test, so it is handed back as an orphan.

## What it costs

Sit at a shared poker table, then open the solo poker panel. Proved by test, with the
guard removed:

```
Assert.Equal() Failure: Values differ
Expected: 18000000
Actual:   20000000
```

Two million roubles back in the stash with the chips still on the table -- and standing
up then pays the stack out a second time. Blackjack is the same, smaller only because a
bet is smaller than a buy-in:

```
Expected: 950000
Actual:   1000000
```

No exploit knowledge needed. Opening the other panel is the whole of it.

## The fix, and why it is two-sided

The rule is the one the game already implies: **you are at one table.**

- `PokerService` / `BlackjackService` refuse the solo table to somebody at a shared one,
  **and** `RefundAbandoned` returns null when they are -- the second matters more, because
  it is the one that runs on `State`.
- `SharedPokerService` / `SharedBlackjackService` refuse a seat while escrow holds
  anything, with two messages: "stand up from your own table first" when it is live, and
  "open it to collect first" when it is a leftover from a server that died mid-hand.

Either half alone leaves the opposite order open, so both are there.

## Tests

`tests/Poker.Server.Tests/SharedTableIsolationTests.cs` and the blackjack twin. Five each,
and **every one was run with its guard deleted first** -- three of five fail on the poker
side, three of five on blackjack. The two that pass either way are the ones asserting the
guard does NOT fire for an ordinary player, which is the point of having them.

Money assertions are ordered **before** the `Ok` flag deliberately. `Ok` being false is how
the guard happens to be built; the balance not moving is the defect. Asserting money first
means a future failure message names the mint rather than a flag.

## What is still open

A player may still be at a poker table and a blackjack table at once, and that stays
allowed -- the escrow files are separate and per game, so there is no collision. See
`Casino.Server.TableClaims` for why the claim is per game rather than casino-wide.
