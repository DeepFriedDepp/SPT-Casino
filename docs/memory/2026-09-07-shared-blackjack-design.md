# Shared blackjack, and how it differs from shared poker -- 2026-09-07

Poker's shared table came first and works. This is the second one, and the useful part of
writing it down is the places where copying poker would be **wrong** rather than merely
wasteful.

Poker's design is in `2026-09-07-shared-table-design.md`; the plumbing it produced --
`TableGate`, `CasinoSocket`, `Host.Pushed`, the claim index -- is reused unchanged.

## The three real differences

### 1. There is no hidden information between players

**In blackjack every player's cards are face up. That is the game.** The only hidden card
is the dealer's hole card, and it is hidden from everybody equally --
`BlackjackTable.View` already does exactly that, and its comment already says why.

So there is **one view for the whole table** and no per-seat filtering.

This is worth stating loudly because Poker's hardest requirement was per-viewer views,
and carrying that habit across would be work that makes the game *wrong*: a blackjack
table where you cannot see the other players' hands is not a blackjack table, it is two
people playing alone in the same room.

The push shape follows: **one message, broadcast**, where poker needs N messages. The
opposite of its sibling, for a good reason.

### 2. Sitting a round out is a legitimate state

Poker's disconnect problem is sharp: four other seats are waiting on somebody who has
gone, and the only honest answer is to fold them and carry on.

Blackjack has no fold. A seat that has not bet when the round starts is simply **not in
that round** -- dealt nothing, settling nothing -- which is an ordinary thing a real
player does at a real table. So a quiet or disconnected seat needs no special handling
at all: it stops betting and the table goes on without it.

That makes the disconnect story strictly easier here, and it is the one place shared
blackjack is *simpler* than shared poker rather than harder.

### 3. Seats had to be built from nothing

Poker was already a seated game. `HoldemTable` had five chairs with one human in seat 0,
so letting more than one be human was mostly parameterisation.

`BlackjackTable` has no seats. Its `_hands` list is **one player's split hands**, not
players, and `Deal(int wager)` takes a single wager. The seat concept is new, and that
is where the work is.

## What makes it worth playing

**One shoe and one dealer hand.** Cards deplete for everybody, and every seat settles
against the same dealer upcard.

If the seats had separate shoes and separate dealers they would not be at a table
together -- they would be side by side, which is what the mod already does today and
which nobody would call a shared table. Watching somebody bust while you stand on 19
against a six is the entire social point.

## What is reused, unchanged

- **`Casino.Server.TableGate`**, and its lock order: table gate OUTER, session gate INNER,
  never the reverse. Blackjack needs it for the same reason poker does -- settling a round
  touches the table and several players' profiles together.
- **`CasinoSocket`** and the one-socket-per-player design. The envelope already carries
  which table a message came from, which is exactly why it was put there.
- **`Casino.Shared.Host.Pushed`**, the seam a panel subscribes to.
- **The money model**: each player's wager leaves their own stash and their own return is
  paid back to them. No shared pot of real currency. This is what let poker's shared table
  keep the existing per-session money code correct, and it is unchanged here.

## Two things deliberately kept separate from poker

**A player may be at one shared poker table and one shared blackjack table at once.** The
claim index is per game, not casino-wide. It could be either; per game is chosen because
escrow is already per game -- `escrow-poker.json` and `escrow-blackjack.json` are separate
files keyed by session -- so the money model already assumes a player can owe and be owed
at both. Making the claim casino-wide would be a stricter rule than the money underneath
it needs.

**No shared-table code was hoisted into a common base before the second one existed.**
Poker's `SharedTableStore` and `SharedPokerService` are not generic. That is deliberate:
this repo's oldest habit is four copies of `Bank.cs` drifting apart, and the cure for it
is not a premature abstraction over a single example. Once blackjack's server layer is
written, whatever the two genuinely share gets extracted -- with two real cases to shape
it rather than one guess.

## Not doing

- Insurance, and side bets. Neither exists in the single-player game either.
- Splitting the dealer between seats. One dealer, one hand.
- Blackjack bots. Poker needs them because a two-handed hold'em table is a different game;
  blackjack against the dealer plays identically whether one seat or five are occupied, so
  an empty chair can just be empty.
