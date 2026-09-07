# Two humans at one poker table -- the design -- 2026-09-07

Written before the integration, so the decisions are decisions rather than whatever fell
out of the code. Foundations for it (multi-human engine, WebSocket transport) are being
built separately; this is how they get joined up.

Transport reasoning is in `2026-09-07-phase1-multiplayer-transport.md`; concurrency in
`2026-09-07-session-gate-landed.md`.

## What "sit at the same table with my friend" means here

Two humans in the same hand, seeing one board, betting into one pot, taking turns --
with bots filling the seats nobody took. Not two people playing side by side, which is
what the mod does today: `TableStore` is keyed by `sessionId`, so two people get two
entirely separate tables and cannot see each other at all.

Bots filling the rest is the deliberate default. Heads-up hold'em is a different and
much sharper game than five-handed, and "me and a friend" should not silently change
what the table is. Five seats stay five seats; the humans take two of them.

## The lock order, which is the thing most likely to go wrong

A shared table has **two** kinds of state, and they need different units of exclusion:

| State | Belongs to | Guarded by |
| --- | --- | --- |
| Profile, stash, escrow row | one player | `Casino.Server.SessionGate`, keyed by session |
| Engine, seats, pot, whose turn | the table | a new per-table lock |

Both get taken on the same call. Dealing a hand touches the table AND takes money from a
profile, and those cannot be separated -- the whole point of the existing money code is
that you do not deal a hand you cannot collect on.

**So the rule is: the table lock is always the OUTER one, and a session gate is never
held while reaching for a table lock.**

Written down because the obvious alternative deadlocks. Player A takes their session gate
then wants the table; player B holds the table and wants A's session gate to charge them
a blind. Neither moves, both wedge for 30 seconds, and the failure surfaces as "poker
stopped working" long after the code that caused it.

Nothing else in the casino takes a table lock, so a player at a shared table who also has
blackjack open in a second client is safe: blackjack takes only a session gate, and no
path anywhere goes gate-then-table.

The per-table lock must be bounded the same way `SessionGate` is, and for the same
reason: a wedged table is worse than a failed request, because the money for a live hand
is already in escrow.

## Which seats can see which cards

`HoldemView.From` already filters hole cards -- it just filters on `IsPlayer`, which
means "the one human". Once there are two, filtering on that shows each human the other's
cards.

**The view is built per viewer, and another seat's cards must be absent from the object
rather than flagged hidden.** This is the one requirement in the whole feature where the
obvious shortcut is a security hole: a client that receives data it is told not to draw
still has the data, and the mod cannot stop somebody reading it out of the response. So
the server sends each player a different object.

That also decides the push shape: a table update is **N messages, one per seated human**,
not one broadcast. Broadcasting one object to everyone is exactly the mistake.

## How two friends find each other

A lobby list, not auto-join and not a code to type. `sit` currently creates a table
immediately; it grows two neighbours:

- **list** -- open shared tables on this server: who is sitting, stakes, free seats.
- **join** -- take a free seat at one, paying that table's buy-in from your own stash.

Creating stays what `sit` already does, with a flag for whether the table is private
(yours alone, exactly as now) or open for others to join. **Single-player must keep
working unchanged and stay the default** -- it is what everyone has today, and a shared
table that breaks the solo one is a bad trade.

## Money stays per player

Nothing about escrow changes shape. Each human's buy-in leaves their own stash and is
recorded against their own session; each human's cash-out pays their own stack back.
There is no shared pot of real currency -- the pot is chips, and chips only become
currency when a player stands up.

That is what makes the existing per-session money code still correct: it was written for
one profile at a time, and one profile at a time is still exactly what it sees.

## Somebody will alt-F4 mid-hand

They will, and their stack is real money sitting in escrow.

- The hand cannot stop and wait. Four other seats are in it, and a table that hangs
  because one player closed the game is worse than one that plays on.
- So a seat that goes quiet **folds after a timeout**, and the hand continues.
- Their chips stay theirs. The stack stays in escrow under their session, and they get it
  back the same way an interrupted single-player table already gives it back -- on their
  next contact, or by leaving.
- Folding costs them what they had already committed that hand and nothing more, which is
  the same outcome as folding on purpose.

Handing the seat to a bot was the other candidate and is rejected: a bot playing a real
person's real money, making bets they did not authorise, is not a thing to do quietly.
Folding loses them the blind. A bot could lose them the stack.

## What this does NOT do

- No cross-server play. Everyone must already be on the one SPT server, which is what
  Fika gets you. Fika is not otherwise involved -- see the transport note for why it
  cannot be.
- No spectating.
- No rebuys mid-hand.
- Blackjack, Roulette and Slots stay single-player. Nothing here forces that; Poker is
  the pilot because it is the hard case, and the rest can follow if it works.
