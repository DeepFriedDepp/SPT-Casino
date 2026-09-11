# Farkle -- working notes for Claude

The fifth table in **SPT Casino**, and the first to be started after the casino had one
door. Six dice; set aside what scores, roll the rest or bank; roll nothing and the turn's
points are gone. **Human opponent OR a bot, never both** -- decided at table creation,
which is the one thing that makes this table smaller than shared poker or shared
blackjack rather than larger.

**It is not a game yet.** Read "Current state" before anything else in this file. The
scoring is written and tested, the server answers a ping, the lobby has a tile, and the
panel says plainly that there is nothing to play. Phase 2 -- the actual game -- is
deliberately unwritten until the four decisions under "Open decisions" have answers.

The work order is the source for what "done" means; `docs/memory/2026-09-11-farkle-phase1.md`
is what Phase 0 and 1 found, and `docs/memory/2026-09-11-farkle-reference-not-source.md`
is why no line of any public Farkle repo is in here.

**Update "Current state" when you finish a piece of work.**

---

## The rules this table plays

The scoring table is the work order's, unchanged, and it lives in one place:
`Farkle.Game.Scoring`. The panel prints what the server sends and the server sends
`Scoring.Table`, so the numbers cannot drift.

| Combination | Points |
| --- | --- |
| Single 1 | 100 |
| Single 5 | 50 |
| Three 1s | 1000 |
| Three 2s to 6s | face x 100 |
| Four of a kind | 1000 |
| Five of a kind | 2000 |
| Six of a kind | 3000 |
| 1 to 6 straight | 1500 |
| Three pairs, four-and-a-pair included | 1500 |
| Two triplets | 2500 |

Two things in the code are decisions rather than rows, and both are documented on
`Scoring`:

- **A six-dice set is worth the better of its two readings.** `1 1 1 5 5 5` is two
  triplets at 2500, not three 1s plus three 5s at 1500. `4 4 4 4 2 2` is three pairs at
  1500, not a four at 1000 beside two dead dice. Six 1s is 3000, not 1500. No rule sheet
  says otherwise; the scorer works both out and keeps the larger.
- **A roll is judged leniently and a keep strictly.** A roll is a farkle only when
  *nothing* scores (`Scored.Scores`). A keep must have every die earning its place
  (`Scored.EveryDieCounts`), because a player who sets aside a dead 4 with three 2s is
  shrinking their next roll for no return, which no rule set allows. The reference repo
  has one lenient predicate for both; this one splits them.

Not in the table, on purpose: no short straights (`1 2 3 4 5` is 150 and three dead
dice), no doubling for a second farkle, no penalty for three farkles running. Those are
house rules and none of them is in the work order.

### The odds, computed rather than measured

`Farkle.Game.Odds` enumerates every one of the 6^n outcomes through the scorer. Same
principle as the slot machine's return: the Monte Carlo is a check on the formula, not
the source of the number.

| Dice | Farkle chance | Mean best keep | Break-even turn score |
| --- | --- | --- | --- |
| 1 | 66.67% (4/6) | 25.0 | 38 |
| 2 | 44.44% (16/36) | 50.0 | 112 |
| 3 | 27.78% (60/216) | 86.8 | 312 |
| 4 | 15.74% (204/1296) | 143.5 | 912 |
| 5 | 7.72% (600/7776) | 225.8 | 2,926 |
| 6 | 2.31% (1080/46656) | 424.5 | 18,340 |

The fractions are the ones every Farkle strategy text quotes, and `OddsTests` pins all
six to twelve places. **That is a second, independent check on the scoring table**: a
scorer that mis-scored any combination would move at least one count.

"Mean best keep" is what a roll of N dice adds on average if every point on the table is
taken, a farkle counting as zero. "Break-even" is mean divided by farkle chance: the turn
score above which one more roll of N dice loses points on average. Below it, rolling is
+EV on the raw numbers. Read the sixth row as "always roll a fresh six" and the first as
"never roll one die", since the smallest turn score is 50.

## Where the RNG lives

**The server rolls. The client is handed faces.** `Farkle.Game.Dice.Roll(Random, count)` is
the only place a face is decided; it uses `Next(1, 7)` and its remarks say why the 7 is
not a typo. Three of the six public Farkle repos have a die that cannot show a six
because they passed 6 as an exclusive upper bound, and Unity's `Random.Range(int, int)`
has the same exclusive bound -- **whoever writes the dice animation must not roll the
display die that way either.** `DiceTests.AllSixFacesComeUp` is the test that would have
caught all three.

There is no shared RNG utility on the server side today. Roulette and Slots each carry an
identical `IRandomSource` / `RandomSource` (`Random.Shared`) pair; Poker and Blackjack use
`new Random(...)` inline. Farkle's would be the third copy, and `CLAUDE.md`'s rule is to
extract at the second. **When Phase 2 adds the roll route, put `IRandomSource` in
`Casino.Server` beside `Gates.cs` and have Farkle take it from there.** Not done yet
because nothing in `Farkle.Server` rolls and an injected dependency nothing uses is noise.

## The bot

Written as a design, not built. It is here because the work order asked for the same
level of thought poker's bots got and because the thing not to do is easy to state.

### What was looked at, and what it settled

`david-acm/farkle`'s `MachinePlayer.cs` was read in full before this was written. It is
54 lines and **contains no continue-or-bank decision at all** -- it enumerates every
subset of a roll and keeps the highest-scoring one, to drive that project's UI tests. So
it settles only that a bot needs the legal-keeps enumeration first, which
`Scoring.Keeps` and `Scoring.BestKeep` provide by position.

The weakest of the six reference repos decides with one line: keep rolling while the turn
score is under 1,500. That bot is a lookup table with one entry and a player spots it in
three turns. Do not build it.

### Why greedy max-keep is the wrong default

`BestKeep` takes every point on the table. That is not how the game is played well: with
`2 2 2 5 4 6` a strong player often keeps the three 2s (200) and rolls **three** dice,
not the 2s and the 5 (250) and rolls two. The 50 points cost a die, and a die is worth
more than 50 in expectation on almost every turn (see the table: three dice farkle 28%,
two dice 44%).

So the bot's keep choice and its continue decision are one decision, not two. For every
legal keep, the bot weighs "bank now with this" against "roll the remaining dice with this
banked into the turn", and the second term depends on how many dice remain. That is the
whole shape.

### The design: one procedure, several sets of numbers

Exactly poker's arrangement and for exactly poker's reason -- a seat that decides by its
own logic cannot be debugged, and eight separate procedures cannot be blended. One
procedure, dials.

The procedure, per roll:

1. **Enumerate the legal keeps** (`Scoring.Keeps`). On a farkle there are none; the turn
   is over and the bot has no decision.
2. **Hot dice are not a choice.** If a keep uses every die, take it and roll six fresh.
   Everybody does; the break-even for six dice is 18,000.
3. **For each keep, compute two values.** *Bank*: turn score plus the keep's points.
   *Roll*: the same, times the chance the remaining dice do not farkle, plus the mean
   best keep of those dice -- the crude one-step expectation, both figures from `Odds`.
4. **Bend the roll value by the dials**, then take the keep and action with the highest
   value.
5. **Apply the endgame override** before acting, see below.

The dials, each 0 to 1, with what they do to step 3:

- **Risk** -- scales how much the farkle chance is discounted. At 0.5 the bot uses the
  true odds; toward 1 it rolls when the raw numbers say bank, toward 0 it banks early.
  This is the dial that produces "keeps rolling on 800 with two dice" and "banks 350".
- **Greed** -- how strongly it prefers keeping fewer dice to roll more. High greed leaves
  the lone 5 on the table; low greed sweeps up every point it sees.
- **Patience** -- a floor on the turn score it will bank below, expressed as a multiple
  of the 500 opening threshold. Some players will not stop for 300; some stop the
  moment they clear the line.
- **Steadiness** -- how little the game state reaches them. See mood, below.

Named characters are landmarks on those dials, not separate code -- poker's cast is the
model. Three or four that measurably differ beat seven where two are the same person.
**Measure them the way poker's are measured**: over a few thousand turns, record how
often each banks when facing a decision at 300, 600, 1000, 2000 with 2, 3, 4 dice left.
If two characters' tables look alike, one of them is not a character.

### The endgame, which is what gives a threshold bot away

A bot with a fixed stopping rule plays the last turn of a race exactly like the first,
and that is the tell. Two overrides, both computed from the score, both unarguable:

- **The opponent has banked past the target** and this is the last turn. There is no
  bank below what wins. Roll until the turn score covers the gap or the dice farkle;
  the dials do not apply.
- **The opponent is within one strong turn of the target** (say 1,500 or less to go).
  Banking a small turn now hands them the game; the bot's Risk is pushed up in
  proportion to how close they are.

And one that is a rule of the game rather than a strategy: the **opening threshold**. A
player is not on the board until a single turn banks 500 or more; before that, every
turn is roll-until-500-or-farkle and the bot has no decision. That rule is decision #3's
to confirm.

### Mood

Cheap and worth it, exactly as poker found: a mood from -1 to +1 that moves with results
-- a farkle on a 1,200 turn pushes one way, a hot-dice run the other -- decays toward
level, and bends Risk. Steadiness is how little of it reaches the dials. A Gambler who has
just farkled twice presses; a Rock who has just farkled twice banks 350. A player watches
that happen.

Do not log it as "mood". Log the decision and its reason through `IGameLog` -- "rolled 3
dice at 650 (roll 703 > bank 650, risk 0.7)" -- so the console tool can print why the
seat did what it did. A seat that silently does things is untestable and unwatchable.

### Timing

The engine hands the client a thinking time per decision. It already knows the right one:
the bank and roll values from step 3 are close exactly when a person would hesitate, so
the delay is a function of their gap. That one detail beats any amount of random delay,
and poker's notes say the same.

## The multiplayer half, confirmed against the code

The work order inferred from Farkle's rules that shared blackjack, not shared poker, is
the template. **Confirmed from `SharedBlackjackService.cs` rather than taken as settled:**

- **One view, one broadcast.** `SharedBlackjackService.PushAsync` serialises one
  `BlackjackMessage` and sends it to `table.Sessions`. Poker builds a view per seat
  because hole cards are secret. Every Farkle die is on the table for both players, so
  there is nothing to filter and one object goes to everybody. Farkle is the blackjack
  shape.
- **The store is the same shape.** `SharedBlackjackStore` is `Casino.Server.TableClaims`
  plus an in-memory dictionary of tables, with the escrow rows per session as the only
  thing that survives a restart. One `TableClaims` instance per game, not per casino --
  a player may be at a poker table and a Farkle table at once, as they may be at poker
  and blackjack.
- **Lock order is not negotiable.** `TableGate` outer, `SessionGate` inner. Every public
  service method is a gated wrapper over an ungated `*Core`. Read
  `src/Casino.Server/Gates.cs` before writing a line of `SharedFarkleService`.
- **`CasinoSocket`, `Host.Pushed` and `CasinoSocketClient` need no change.** The envelope
  already carries which table a message is from.

Two places Farkle differs from blackjack, found by reading rather than guessing:

- **Simpler: the quiet seat.** Blackjack has no fold, so a seat that goes quiet needs
  `AdvancePastTheAbsentAsync` to play the round past it. Farkle has a natural answer:
  a seat that goes quiet on its turn **banks what it has and passes.** It loses nothing
  it had not already put at risk and the engine needs no special state for it.
- **Harder: a match is a race.** Blackjack rounds are independent and an empty chair is
  fine. Farkle needs exactly two seated players -- human and human, or human and bot --
  before the first roll, and a player who stands up mid-match **forfeits**. That needs
  saying in the rules the panel shows, because it is real money if decision #2 says so.

## Open decisions

Not decided here. They are the repo owner's, and Phase 2 is not written until they are.
Recommendations follow each, with the reason; disagree with any of them and the design
above still holds.

1. **Player count for human mode.** *Recommend two.* It is what the transport has been
   proven against (`2026-09-07-first-live-run.md` is a two-player match), and a
   human-or-bot table is a two-seat object either way. Free-for-all Farkle with more
   seats is a real variant; it is also a lobby, a turn ring and a forfeit rule for N
   players, none of which exists yet for any table.
2. **Stakes.** *Recommend a fixed match wager, per player, in roubles, escrowed at
   sit-down; the winner is paid both.* This is blackjack's bet-then-resolve shape
   stretched over one match rather than one round, and it keeps every existing money
   rule intact: each player's wager leaves their own stash, is recorded against their
   own session in `escrow-farkle.json`, and nothing is pooled in real currency until
   settlement. A no-money score race is the other honest answer and removes `Bank.cs`
   and `Escrow.cs` from this table entirely -- but it also makes Farkle the one table in
   the casino where nothing is at stake, and the casino is built around the stake.
   Whichever way this goes, **write `MoneyInvariantTests` before the settlement**, as
   Roulette did.
3. **Win condition.** *Recommend a race to 10,000 with a 500 opening threshold.* 10,000
   is the majority rule set and the one two of the three reference repos use; 5,000 is a
   short game, and a short game with money on it is closer to a coin flip than a game.
   The threshold matters more than it looks: without it the first turn is a free 50, and
   with it the bot's first-turn logic is a rule rather than a decision.
4. **AI opponent.** *Recommend the design above.* Dial-driven around the computed
   break-even line, with the endgame override and mood. Build the measurement harness
   (a console tool, like `tools/Poker.Console`) at the same time as the bot, not after;
   poker's characters all had to be widened after measuring and Farkle's will too.

## Verifying

```
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' test tests\Farkle.Game.Tests\Farkle.Game.Tests.csproj
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' build src\Farkle.Server\Farkle.Server.csproj -c Release
dotnet build src\Casino.Client\Casino.Client.csproj -c Release "-p:SPTPath=C:\SPT"
```

In the game: the lobby shows a fifth tile, FARKLE, two dice showing a 1 and a 5. That
tile is the one piece of art in the casino that is drawn by a script rather than by hand
-- `python tools/draw-farkle-tile.py` regenerates `src/Casino.Client/assets/tile-farkle.png`
at the other tiles' 320x320, so it can be changed without an artist. It needs Pillow.
Opening the tile fetches `/farkle/ping` and prints the scoring sheet
and the six farkle odds from the server. If the panel says the server is not answering,
`Farkle.Server.dll` is not in `user/mods/Casino` -- `scripts/casino/pack.ps1` puts it
there.

## Current state

**2026-09-11 -- Phase 0 and Phase 1 done. Phase 2 not started.**

| Piece | State |
| --- | --- |
| `src/Farkle.Game` | `Scoring`, `Dice`, `Odds`, `IGameLog`. No turn, seat or match state |
| `tests/Farkle.Game.Tests` | 81 tests, green. Scoring against the reference's cases plus the ones it lacks; odds pinned to the known fractions |
| `src/Farkle.Server` | `/farkle/ping`, gated, returns the scoring table and odds. **No bank, no escrow, no item-event action, no RNG** -- all wait on decision #2 |
| `src/Farkle.Client` | A panel that prints what ping returns and says the game is not built. Compiled into `Casino.Client` |
| `Casino.Client` | Fifth `Games.All` entry, fifth shim, builds clean against `C:\SPT` |
| `scripts/casino/pack.ps1` | Knows the fifth table. **Not run against a real install yet** |
| Art | `tile-farkle.png`, drawn by `tools/draw-farkle-tile.py`. No table art yet; the panel is procedural |

### Open items

- Answer the four decisions above; then Phase 2.
- Dice faces for the table itself, when there is a table. The tile script's `die()` is
  the obvious starting point, and the display die must not use `Random.Range(1, 6)`.
- When the roll route lands: extract `IRandomSource` into `Casino.Server` (see "Where the
  RNG lives") rather than adding a third copy.
- `Farkle.Server.Tests` does not exist yet. There is nothing to test that `Farkle.Game.Tests`
  does not already cover; it appears with the first route that moves state.
