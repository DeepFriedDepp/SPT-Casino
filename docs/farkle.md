# Farkle -- working notes for Claude

The fifth table in **SPT Casino**, and the first to be started after the casino had one
door. Six dice; set aside what scores, roll the rest or bank; roll nothing and the turn's
points are gone. **Human opponent OR a bot, never both** -- decided at table creation,
which is the one thing that makes this table smaller than shared poker or shared
blackjack rather than larger.

**It plays, for real roubles, as of 2026-09-11.** Read "Current state" before anything
else in this file. Two seats, a race to 10,000 with a 500 opening threshold, a fixed
stake each and the winner paid both -- against a friend over the casino socket, or
against one of four measured bot characters.

`docs/memory/2026-09-11-farkle-phase1.md` is what the investigation found,
`2026-09-11-farkle-reference-not-source.md` is why no line of any public Farkle repo is in
here, and `2026-09-11-farkle-phase2.md` is what landed when the four decisions were taken.

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

On the server, Farkle takes `Casino.Server.IRandomSource` -- extracted into
`src/Casino.Server/RandomSource.cs` with the roll route, because Roulette and Slots
already carried identical copies and `CLAUDE.md`'s rule is to extract at the second case.
Those two copies are left in place until somebody is in those files for another reason;
they are in different namespaces and nothing collides. The tests hand the service a
`FakeRandom` that returns **one** seeded `Random` across every call -- a fresh
`Random(seed)` per roll makes every six-dice roll identical and a match that never ends.

## The bot

Built as designed below, in `Farkle.Game.FarkleBot`, and measured with
`tools/Farkle.Console` -- see "The cast, measured". The design is kept here in full
because it is the reasoning the numbers hang off, and because the thing not to do is easy
to state.

The server plays a bot's whole turn inside the request that ended the human's, and the
panel replays it from the view's `LastTurn` events. The engine asks keep and bank as two
steps; the bot answers them as one, so `FarkleTable.BotWillBank` carries the answer from
the keep to the next `Rolling` phase.

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

## The four decisions, taken 2026-09-11

All four recommendations were confirmed as written by the repo owner. Recorded here so a
fresh session does not reopen them.

1. **Two players.** A table is a two-seat object: human and human, or human and one bot.
   Free-for-all Farkle with more seats is a real variant and is out of scope -- it needs
   its own lobby, turn ring and forfeit design.
2. **A fixed match wager, per player, in roubles, escrowed at sit-down; the winner is
   paid both.** 10,000 to 1,000,000 a seat. **The forfeit rule**: standing up mid-match
   pays both stakes to the player who stayed -- its own named rule in
   `SharedFarkleService` and its own case in `MoneyInvariantTests`, not an inference.
   Leaving a table nobody joined is not a forfeit; the stake comes straight back. Against
   the house the human's stake is real and the bot's is notional: a win pays two stakes,
   a loss pays nothing, and leaving mid-match loses the stake to the house.
3. **Race to 10,000, 500 opening threshold.** The first seat past 10,000 does not win on
   the spot: the other gets one last turn, and a tie goes to the seat that set the mark.
4. **The dial-driven bot**, as designed above, measured with `tools/Farkle.Console`.

**Still open, deliberately:** the exact dial values per character. The cast below is a
measured starting point, not a finished one.

## The cast, measured

`dotnet run --project tools/Farkle.Console -- --matches 300` plays every pair 300 times
and prints how often each character banks **when it had the choice** -- on the board, a
keep made, dice still in hand -- by how many dice it would roll on with. 2026-09-11,
seed 1:

| | 1 die | 2 | 3 | 4 | 5 | all | avg bank | farkle% | wins |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Kolya (Rock) | 91% | 95% | 95% | 13% | 2% | 54% | 649 | 16% | 51% |
| Sveta (Grinder) | 94% | 43% | 26% | 5% | 1% | 27% | 899 | 40% | 51% |
| Timur (Tourist) | 94% | 87% | 34% | 8% | 2% | 42% | 747 | 26% | 52% |
| Vanya (Gambler) | 50% | 33% | 20% | 2% | 0% | 18% | 1,194 | 57% | 46% |

Two things the first two runs taught, both now in `FarkleBot.cs` beside the numbers:

- **Patience is what separates a small-banker from a stopper.** The first tuning took
  Timur's patience off to make him "stop the moment he clears the line", and he became
  Kolya's twin -- 94% and 95% with three dice left. Put back, they separate at 34% and 95%.
- **Greed shows in which dice are kept, not in the bank rate.** Timur sweeps the lone 5
  where Sveta leaves it to roll one more die; the bank table cannot see that. If Greed is
  ever retuned, add a keep-choice column to the harness first.

The characters win between 46% and 52% of their matches against each other, which is
the right shape: none of them is a bad player, they are different players. A character
whose row matches another's is one character with two names -- widen a dial and measure
again.

## Verifying

```
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' test tests\Farkle.Game.Tests\Farkle.Game.Tests.csproj
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' test tests\Farkle.Server.Tests\Farkle.Server.Tests.csproj
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project tools\Farkle.Console -- --matches 300
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project tools\Farkle.Console -- --watch --a Kolya --b Vanya
dotnet build src\Casino.Client\Casino.Client.csproj -c Release "-p:SPTPath=C:\SPT"
```

**The concurrency test is proven to fail without the gate**, as `CLAUDE.md` requires. With
the `tables.EnterAsync` line in `LeaveAsync` and the `sessions.EnterAsync` line in the
refund branch of `LeaveCoreAsync` replaced by nothing, a double-clicked LEAVE on an
unjoined table refunds the stake twice and `TwoLeavesOfAnUnjoinedTableRefundOnce` fails
with `Expected: 1, Actual: 2` on the credit count. Restored, 24/24. The run is quoted in
`docs/memory/2026-09-11-farkle-phase2.md`.

In the game: the lobby's fifth tile opens the table. The lobby half lists tables waiting
for an opponent, takes a stake, and offers OPEN A TABLE FOR A FRIEND or PLAY THE HOUSE
against a named regular. Sitting down takes the stake at once and tells the running game
so the counter agrees with the server. In a match, tap dice to pick a keep -- only dice
the server says may be kept light up -- then SET ASIDE, then ROLL or BANK. The other
seat's turn is replayed event by event rather than appearing as a score change. LEAVE
mid-match is the forfeit.

## Current state

**2026-09-11 -- Phase 2 landed. It plays. Not yet run inside a live SPT server.**

| Piece | State |
| --- | --- |
| `src/Farkle.Game` | `Scoring`, `Dice`, `Odds`, `FarkleMatch` (seats, turns, threshold, last turn, tie, yield, forfeit), `MatchView`, `FarkleBot` with four characters |
| `tests/Farkle.Game.Tests` | 121 tests, green: scoring, odds, match rules, bot overrides and character separation |
| `src/Farkle.Server` | `SharedFarkleService` / `Store` / `Callbacks` / `Router`, `Bank`, `Escrow` (`escrow-farkle.json`), `FarkleSync` item event. Nine routes on `/farkle/*` |
| `tests/Farkle.Server.Tests` | 24 tests, green: the money invariants including the forfeit rule, stranded refunds, the quiet and absent seats, and a concurrency probe proven to fail ungated |
| `src/Casino.Server/RandomSource.cs` | The shared `IRandomSource`, extracted at the third case. Roulette's and Slots' copies untouched |
| `src/Farkle.Client` | The whole panel: lobby, stake, bot picker, match with clickable dice, opponent-turn replay, socket push plus a 4s poll. Compiled into `Casino.Client`, builds clean |
| `tools/Farkle.Console` | The measurement harness and a narrated match |
| `scripts/casino/pack.ps1` | Knows the fifth table. **Not run against a real install yet** |

### What has not happened

- **No live run.** Nothing here has been inside a running SPT server: not the routes,
  not the item-event registration, not a push. Every other table's first live run found
  something; expect this one to. `docs/memory/2026-09-07-first-live-run.md` is the shape
  of what one run proves.
- **The bot's thinking time is not used yet.** `BotDecision.Seconds` is computed and
  logged; the panel replays a bot turn at fixed pauses. Wiring the two together is the
  next thing that makes the bot feel like a person.
- **Dial values are starting points.** Measured once. Re-measure after any change.
- Dice faces on the table are drawn from `Textures.RoundedBox`; there is no table art.
