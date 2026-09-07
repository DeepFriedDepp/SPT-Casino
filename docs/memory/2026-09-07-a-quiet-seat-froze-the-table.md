# A quiet seat froze the table, in both games

**2026-09-07.** Found in blackjack while building shared tables, then confirmed in the
shipped poker that two people had already played.

## The shape

The engine runs its own bots, so only a **human** seat needs a request to act. When that
person's game closes, nobody sends one -- and the table waits on them for as long as the
server runs. Standing up is refused mid-hand ("Finish the hand first" / "Finish the round
first"), so everybody else is stuck behind that seat with their money committed and no way
to reach it.

`LastSeenUtc` was written on every request, in both games, and **read nowhere**.

`SharedPokerService.PushAsync` even carried a comment saying "the table plays on and their
seat times out", describing behaviour that did not exist. That is worse than saying
nothing: the next person to read it stops looking.

Only a restart ended it -- and only by discarding the hand. The money did come back (the
tables are in memory and the escrow rows are not, so the stakes refund as orphans), but
nobody should have to restart a server because their friend's game crashed.

## The fix, and why it differs per game

Ninety seconds of silence, then the table plays on without that seat. The same number in
both, deliberately: two tables in one casino that time out differently is a difference
nobody could explain to a player.

**What the seat does differs, and both follow their own game.**

| | action | what it costs the absent player |
| --- | --- | --- |
| Blackjack | **Stand** | nothing. They keep the hand and are paid whatever it wins |
| Poker | **Fold** | what they had already put in -- the ordinary rule for leaving |

Blackjack has no fold, and standing is the only action that is always legal and never
spends money. Hold'em does have one, and it is the only move legal from every position
without putting more in -- checking is illegal the moment there is a bet to answer, and
calling would spend an absent player's chips on a hand nobody is playing.

## Driven by requests, not a timer

A background sweep would need its own gate discipline and would move money with nobody's
request behind it. The panel asks for the table whenever it draws, so somebody still
sitting there is the clock. Both are called after the table gate and before any session
gate -- blackjack's can settle a round and so takes session gates inside; poker's only
rearranges chips and needs none, which is why blackjack's is async and poker's is not.

## Tests

`tests/Blackjack.Server.Tests/SharedTableIntegrationTests.cs` and
`tests/Poker.Server.Tests/AbsentSeatTests.cs`. Both prove the freeze **and** the escape:
the turn moving is only half of it, since the other half is that the players left behind
could not stand up.

Each pair fails with the timeout disabled. Each also carries a test that a player who is
merely thinking keeps their turn -- a rule that also cuts off present players is worse than
the bug it replaces, because it throws away a live hand for real money on a table somebody
is looking at.
