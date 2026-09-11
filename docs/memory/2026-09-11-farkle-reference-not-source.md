# Farkle: the reference repos are answer keys, not source -- 2026-09-11

Logged before any game code was written, as the work order asked. The question was whether
any of six public Farkle repos could be reused; the answer is that two are worth reading and
none may be copied.

## Verified: neither useful repo carries a licence

Checked against GitHub's API rather than the web page, on 2026-09-11:

```
curl -s https://api.github.com/repos/david-acm/farkle   | grep '"license"'   # "license": null
curl -s https://api.github.com/repos/ericfnsf/Farkle    | grep '"license"'   # "license": null
```

Both trees were also listed (`/git/trees/<branch>?recursive=1`) and hold no `LICENSE`,
`LICENSE.md` or `COPYING` at any depth. `david-acm/farkle` is on `main`; `ericfnsf/Farkle`
is on `master`.

**No licence means all rights reserved.** Public visibility on GitHub grants the right to
view and fork on GitHub, and nothing else. So: read them, learn from them, check against
them -- and write every line of `Farkle.Game` fresh. If either author grants permission
later, note it here and this constraint lifts.

## What each is good for

### `david-acm/farkle` -- `src/HotDice.Shared/Scoring/ScoreCalculator.cs`

(Note the `src/` prefix. The work order's path lacks it and 404s.)

**Correct scoring, and the best answer key available.** Its logic, in its own words:

- A set is worth the **maximum** of two readings: a per-face decomposition, or one of the
  whole-set six-dice combinations. That is exactly how `Farkle.Game.Scoring.Of` is built,
  independently, because it is the only way `1 1 1 5 5 5` comes out at 2500 and
  `4 4 4 4 2 2` at 1500 without special-casing either.
- Three 1s are 1000, cited to its own issue #177. Four/five/six of a kind are flat
  1000/2000/3000, cited to #35. Multi-part keeps sum (#270).
- Three pairs is "every face count even", which covers four-and-a-pair and six-of-a-kind;
  the six is outscored by its own kind under the max rule.
- Its `CanKeep` is deliberately lenient (any die scores), because the same predicate gates
  the farkle check. **`Farkle.Game` splits these**: `Scored.Scores` is the lenient roll
  question and `Scored.EveryDieCounts` is the strict keep question, because a player who
  sets aside a dead die is being allowed to do something no rule set permits.

Its tests (`tests/HotDice.Tests/Scoring/ScoreCalculatorShould.cs`) were used as the check
cases; `tests/Farkle.Game.Tests/ScoringTests.cs` reproduces every one of their expectations
and adds the four-and-a-pair and six-is-also-three-pairs cases they lack.

### `david-acm/farkle` -- `src/HotDice.Shared/Scoring/MachinePlayer.cs`

The work order asked for this to be read before any AI design. Read in full; it is **54
lines and decides nothing about rolling versus banking.** It is a greedy keep chooser: it
enumerates every subset of the roll and keeps the highest-scoring one. There is no
continue-or-bank logic anywhere in the file, no risk model, no awareness of turn score,
game score or opponent. It exists to drive the project's end-to-end UI tests.

So it is not a template for the bot. It is, however, the one tool a bot needs first -- the
set of legal keeps -- and `Scoring.Keeps` / `Scoring.BestKeep` provide the same thing by
position rather than by value. **Greedy max-keep is also the wrong default for a bot**, for
a reason its author would not have cared about: a lone 5 beside three 2s is 50 points
against a die you could still roll. See the AI section of `docs/farkle.md`.

### `ericfnsf/Farkle` -- `FarkleLib`

Worth looking at for how it separates the engine from the host (`IDice`/`IGame`/`IPlayer`
behind a class library, tests per project). Not worth imitating here, because this repo's
four-table `Game`/`Server`/`Client` split is already that shape and is what `Farkle.*`
follows. Its scoring is **wrong** -- three 1s score 100 and there is no four/five/six-of-a-
kind tier -- so no number from it was used.

## The Unity repos, and the die that cannot show a six

FarkleFrenzy, `cimlm1/Farkle` and `Car7er2005/Farkle` all roll with `Random.Range(1, 6)`,
whose upper bound is exclusive in Unity's integer overload. `Farkle.Game.Dice.Roll` uses
`Next(1, 7)` and says why in its remarks; `DiceTests.AllSixFacesComeUp` is the test that
would have caught all three. The same overload exists on the client side of this mod, so
whoever writes the dice animation must not reproduce it there.
