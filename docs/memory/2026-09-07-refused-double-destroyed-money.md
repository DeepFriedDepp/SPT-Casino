# A refused DOUBLE destroyed the stake

**2026-09-07.** Found by an adversarial audit of `SharedBlackjackService`, hours after it
shipped in casino 1.3.0. Never released to anyone but this machine.

## The defect

`ChargeDoubleAsync` debits before telling the engine, which is the right way round --
the engine deals a card on a double, and refusing after that is a card the player got for
nothing. The cost of that ordering is that a refusal on the OTHER side has to hand the
money back. It did not:

```csharp
catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
{
    return View(table, sessionId) with { Ok = false, Error = ex.Message };   // and the money?
}
```

The stake was debited, held in escrow, and then released at settlement against a hand that
had never been staked that much. Gone.

**Reachable by accident.** A double stops being legal the moment a hand has three cards, so
a stale view, a double-clicked button or a retried request all arrive as a double that
cannot happen. The audit's probe dealt two hundred rounds until one hit the state:

```
balance before DOUBLE:              900,000
balance right after refused DOUBLE: 800,000
escrow after refused DOUBLE:        200,000
seat TotalWagered: 100,000, TotalReturned: 200,000
final balance: 1,000,000, honest balance would be 1,100,000
```

Split had it too, through the same method.

## The fix, both ways round

- The action is checked against the engine's own `AvailableActions(seat)` **before a rouble
  moves**. That is the same list the panel draws its buttons from.
- Anything the engine refuses anyway is refunded, and escrow is **rewritten to the seat's
  actual `TotalWagered`** rather than released -- releasing would drop the original bet's
  record too, and that bet is still live.

## The solo table had it right, and differently

`BlackjackService.ActCoreAsync` checks affordability, lets the engine act, then debits
`view.TotalWagered - session.Staked` -- computed from **what the engine actually did**. An
engine refusal costs nothing because the debit never happens.

That design has no refund path at all and is the better shape. Its own accepted risk is the
opposite one: it can deal the card and then fail to collect, which it reports as a Warning
("the player is now playing a stake they did not pay").

The shared table took the other trade without taking on the obligation that comes with it.
**When translating one money path into another, the trade-off is part of what you are
copying** -- collect-first buys you "no free cards" and owes you a refund; act-first buys
you "no refunds" and owes you a warning. Picking one and implementing neither is how this
happened.

## Tests

`tests/Blackjack.Server.Tests/DoubleAndSplitMoneyTests.cs`. All three fail with the fixes
removed, each by exactly the destroyed 100,000.

The helper **plays rounds until it reaches the state and `Assert.Fail`s if it never does**.
Its first version returned null on an uncooperative deal and the tests quietly returned --
which would have reported four passes having asserted nothing. A test that only sometimes
exercises its subject is the same trap as one that cannot fail.
