# Farkle Phase 0 and 1: what was built, what was found, what is still open -- 2026-09-11

The work order is the Farkle work order (fifth table, human OR bot, never both). Phase 0
and Phase 1 are done; Phase 2 is deliberately not started. This is the findings file; the
design that follows from it lives in `docs/farkle.md`.

## Phase 0, as built

| Piece | State | Verified by |
| --- | --- | --- |
| `src/Farkle.Game` | Scoring, dice, odds, game log. No turn or match state yet | 81 xunit tests, all green |
| `tests/Farkle.Game.Tests` | Scoring against the reference's cases plus the ones it lacks; odds pinned to fractions | `dotnet test`, 81/81 |
| `src/Farkle.Server` | Ping only, gated. No bank, no escrow | Release build, 0 warnings, against `SPTarkov.Server.Core` 4.0.13 |
| `src/Farkle.Client` | A panel that fetches and prints the scoring sheet and odds, and says the game is not built | Compiled into `Casino.Client` -- see below |
| `Casino.Client` | Fifth entry in `Games.All`, fifth shim, two `<Compile Include>` lines | Build |
| `scripts/casino/pack.ps1` | `Farkle` in `$tables` and `$configs` | Read; not run against `C:\SPT` |
| `SPT-Casino.slnx` | `/Farkle/` folder | Parses (`--` only in the comment delimiters) |

**Two corrections to the work order, from the code:**

- It asks to "register `Farkle.Server`'s `[Injectable]` types in `Casino.Server/Startup.cs`".
  There is no such list. `Casino.Server.Startup` prints a banner and nothing else; SPT's
  `ModLoader.LoadMod` loads every `.dll` in the folder and `RegisterSptServicesAsync`
  registers every `[Injectable]` it finds. Dropping `Farkle.Server.dll` beside the others is
  the registration. The pack script is what makes that happen, so that is what changed.
- It asks for "a task-bar tab in `Casino.Client` next to Blackjack/Poker/Roulette/Slots".
  The casino has one tab and a lobby; a table is a line in `Games.All` and the lobby draws
  a tile for it. `CasinoLobby.BuildTiles` centres N tiles from `Games.Count`, so a fifth is
  five 300-wide tiles and four 36 gaps = 1644 of a 1920 reference width. It fits.

**A sixth `SptVersion` site.** `CLAUDE.md` said five places say `~4.0.13`. `Farkle.Server/
TableInfo.cs` is the sixth. Move one, move all six.

**Tile art, drawn rather than hand-made.** There was no Python on this box when this was
first written, so the lobby fell back to a drawn diamond pip. Python 3.14 and Pillow were
installed the same day and `tools/draw-farkle-tile.py` now renders `tile-farkle.png` at
the other tiles' 320x320 -- two dice, a 1 and a 5, weathered to match. Regenerable, which
none of the other four tiles are.

## Phase 1A -- scoring

Written fresh; checked line by line against `ScoreCalculator.cs` as an answer key, never
copied. See `2026-09-11-farkle-reference-not-source.md` for the licence reasoning.

The table is the one in the work order, unchanged. Two things in it are decisions rather
than rules and are documented on `Scoring`:

- **A six-dice set is worth the better of its two readings.** `1 1 1 5 5 5` is 2500, not
  1500; `4 4 4 4 2 2` is 1500, not 1000; six 1s is 3000, not 1500.
- **A roll is judged leniently and a keep strictly.** `2 2 2 4` scores 200 as a roll and
  may not be set aside whole, because the 4 is dead.

**Independent confirmation.** `Odds.FarkleChance(n)` enumerates all 6^n outcomes through
the scorer, and `OddsTests` pins it to the fractions every Farkle strategy text quotes:
4/6, 16/36, 60/216, 204/1296, 600/7776, 1080/46656. All six match to twelve places. A
scorer that mis-scored any combination would move at least one of those counts, so this
is a second check on the table that owes nothing to the reference repo.

## Phase 1B -- where the RNG lives

**No shared RNG utility exists on the server side.** Checked with a grep over
`src/*.Server` and `src/*.Game`:

- Roulette and Slots each carry an identical `IRandomSource` / `RandomSource`
  (`Random.Shared`) pair in `Abstractions.cs` and `Escrow.cs`.
- Poker builds `new Random(seed)` inline in both services; Blackjack passes null and lets
  `Shoe` fall back to `new Random()`.

So Farkle would be the **third** copy of `IRandomSource`. `CLAUDE.md`'s rule is to extract at
the second case, and the second case is already there. **Recommendation:** when Phase 2
adds Farkle's roll route, move `IRandomSource` + `RandomSource` into `Casino.Server` beside
`Gates.cs`, have Farkle take it from there, and leave Roulette's and Slots' copies alone
until somebody is in those files for another reason. Not done now, because nothing in
`Farkle.Server` rolls yet and an injected dependency nothing uses is noise.

The principle is already in place: `Farkle.Game.Dice.Roll(Random, count)` is the only
place a face is decided, the client is handed faces, and the client's spin animation is a
display concern -- `Dice.cs` remarks say so and name the Unity `Random.Range` trap.

## Phase 1C -- the AI opponent

`MachinePlayer.cs` read in full (54 lines). **It contains no continue-or-bank decision.** It
is a greedy best-subset keep chooser for the project's UI tests. Not a template; see the
reference file. What it did settle is that the bot needs the legal-keeps enumeration first,
which `Scoring.Keeps` provides.

The design that replaces a hardcoded threshold is in `docs/farkle.md`, "The bot". In
brief: the exact break-even line per dice count, computed by `Odds.BreakEven`, is the
reference a personality bends around with dials in the poker style -- one procedure,
several sets of numbers, never several procedures -- plus an endgame term, because a
threshold bot with no idea the opponent is at 9,600 is the tell.

## Phase 1D -- the multiplayer plumbing

**Confirmed from the code, not inferred from the rules:** shared blackjack is the right
template and shared poker is not.

- `SharedBlackjackService.PushAsync` serialises **one** `BlackjackMessage` and broadcasts it
  to `table.Sessions`. Poker builds a view per seat. Farkle has no concealed information --
  both players see every die -- so it is the blackjack shape: one view, one push.
- `SharedBlackjackStore` is `TableClaims` + a `ConcurrentDictionary` of tables, in memory,
  with the escrow rows per session as the thing that survives a restart. That is exactly
  Farkle's need, once decision #2 says whether there are escrow rows at all.
- Lock order is unchanged and non-negotiable: `TableGate` outer, `SessionGate` inner,
  every public method a gated wrapper over an ungated `*Core`.
- **One place Farkle is simpler than blackjack:** blackjack has no fold, so a quiet seat
  needs `AdvancePastTheAbsentAsync` to play the round on without it. Farkle has a natural
  equivalent -- a seat that goes quiet on its turn **banks** whatever it has and passes.
  That loses it nothing it had not already risked and needs no engine special case.
- **One place it is harder:** blackjack's rounds are independent, so an empty chair is
  fine. Farkle is a race to a target, so a table needs exactly two seated players (or one
  and a bot) before the first roll, and a player who leaves mid-match forfeits. That is a
  Phase 2 rule, recorded here so it is not discovered mid-build.

`CasinoSocket`, `Host.Pushed` and the client's `CasinoSocketClient` need no change: the
envelope already carries which table a message is from.

## The four open decisions, with recommendations

Recorded in `docs/farkle.md` under "Open decisions"; not decided here, because they are the
repo owner's to make. The recommendations, in one line each:

1. **Two players.** It is what the infrastructure has been proven against, and a Farkle
   table is a two-seat object anyway (one human plus one human, or one human plus a bot).
2. **Stakes as a fixed match wager, per player, escrowed at sit-down, winner takes both.**
   Blackjack's bet-then-resolve shape, one round long; nothing pooled in real currency
   until settlement, so the per-session money code stays correct.
3. **Race to 10,000 with a 500 opening threshold.** 10,000 is the majority rule set; 5,000
   is a short game and a short game with money on it feels like a coin flip.
4. **A dial-driven bot around the computed break-even, with an endgame term.** Designed
   in `docs/farkle.md`; not built.

Phase 2 starts when those four have answers.
