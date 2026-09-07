# Shared blackjack, and the fake that was hiding the money

**2026-09-07.** Blackjack now has the same shared tables poker got, and building them
turned up two things worth writing down.

## Why blackjack's is a broadcast where poker's is not

**Every player's cards are face up. That is the game.** The only concealed card is the
dealer's hole card, which `BlackjackTable.ViewTable` hides from everybody equally. So
there is ONE `TableView` and it goes to the whole table unchanged.

Poker needs a different `HoldemView` per seat because hole cards are secret. Carrying
that habit here would have been work that made the game wrong -- a blackjack table where
you cannot see the other hands is not a blackjack table, it is people playing alone in
the same room. `tests/Blackjack.Server.Tests/SharedTableIntegrationTests.cs` asserts the
*opposite* of poker's privacy test: every card of every seat must be present in the
serialised JSON.

## The other real differences

- **A seat costs nothing.** No buy-in, no stack -- a blackjack wager is taken and settled
  inside one round, so between rounds a box owes nothing and is owed nothing. That is why
  joining takes no money and why `PushAsync` needs no per-viewer filtering.
- **Betting and dealing come apart.** Alone, "deal" means bet and deal at once. At a
  shared table the money goes in the box first, every box gets its chance, and then
  **anybody seated** may start the round -- a table only its host could deal stops dead
  the moment that person walks away.
- **The turn is a seat, not a phase.** `Phase == "PlayerTurn"` is true while somebody else
  is playing. The panel's buttons hang off `ActiveSeat == YourSeat`; reading the phase
  alone puts HIT in front of a player whose turn it is not.
- **Real nicknames from the first commit.** The engine's fallback for an unnamed occupied
  seat is the word "You", which is a relationship rather than a name -- it reaches
  everybody unchanged and each player sees their friend labelled as themselves. That
  shipped in poker and somebody hit it at a live table. `NameFor` falls back to "Box n".

## The fake bank kept one wallet for the whole table

Both `FakeBank`s -- poker's and blackjack's -- held `Dictionary<Wallet, int>`, with the
session id ignored. Correct while every test used one player, and quietly wrong the
moment two sat down: Alice's stake moved the same number Bob's balance was read off.

It surfaced as blackjack's first two-player test expecting 950,000 and being handed
900,000. Both fakes are keyed by `(session, wallet)` now, with `Seed`/`SetBalance` still
setting an opening balance for everybody so the hundred single-player tests are untouched.

**Two poker assertions had the bug baked into their expected values** and had to be
corrected, not just re-run:

- `Assert.Equal(Stash - 2 * BuyIn, GetBalance(alice))` -- two buy-ins off one purse. Now
  `Stash - BuyIn` each, and Bob's is asserted too.
- After Bob stands up with an unplayed stack, `Stash - BuyIn` was Alice's number wearing
  Bob's name. He is back to the full `Stash`.

The service was right both times. The test was measuring something else and passing.

That is the same failure mode as the concurrency stress tests that passed 7/7 on broken
code: **a fake that cannot express the defect makes a green test into evidence.** When a
test grows a second actor, check what the fakes think an actor is.
