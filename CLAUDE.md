# SPT-Casino -- working notes for Claude

**SPT Casino** is one mod: a single task-bar tab that opens a lobby, and five tables
behind it -- **Blackjack**, **Poker**, **Roulette**, **Slots** and **Farkle**, the last
of which is a scoring sheet and a health check so far, not a game. It was three
separate mods until 2026-09-05, and the seams are still visible on purpose.

**One folder each side.** `BepInEx/plugins/Casino` and `SPT_Runtime/user/mods/Casino`.
The server folder holds eleven assemblies -- a metadata one plus a `.Server` and a
`.Game` per table -- and SPT is perfectly happy with that. See "One folder, seven
assemblies", which was written when there were seven and is true of any number.

**Read the per-mod notes as well as this file.** This one holds only what is true of
all four; everything specific lives next door and is much longer:

| Table | Notes | State |
| --- | --- | --- |
| Blackjack | `docs/blackjack.md` | Roubles and five other wallets. **Shared tables** since casino 1.3.0 |
| Poker | `docs/poker.md` | Plays for roubles. **Shared tables** since casino 1.2.0 |
| Roulette | `docs/roulette.md` | Plays for roubles as of 2026-09-05. Ships inside the casino |
| Slots | `docs/slots.md` | Roubles, dollars or euros. Shipped in casino 1.1.0, with AUTO and SPEED |
| Farkle | `docs/farkle.md` | **Not a game yet.** Scoring engine tested, ping route, a panel that says so. Phase 2 waits on four decisions |

`docs/blackjack-readme.md` is Blackjack's public README, kept because it was the
repo's front page before the merge.

---

## How the casino is put together

```
src/Casino.Client/        the only plugin. Tab, lobby, welcome card, escape key
src/Casino.Shared/        one copy of what every table draws with. No project of its own
src/<Table>.Client/       each table's panel and views. NOT shipped as plugins
src/Casino.Server/        the one AbstractModMetadata, and the legacy-data finder
src/<Table>.Server/       each table's server code. No metadata of its own any more
src/<Table>.Game/         the rules, no SPT types, unit tested
tests/<Table>.*.Tests/
tools/<Table>.Console/    a harness that plays the game in a terminal
scripts/casino/pack.ps1   builds and installs the whole thing
scripts/<table>/          the per-table server pack and smoke scripts
docs/<table>.md           that table's working notes
```

**`Casino.Client` compiles the five tables in rather than owning them.** The panels
are listed as `<Compile Include="..\Roulette.Client\...">` in its project file and are
edited where they live. Not a line of them changed at the merge, which was possible
only because no panel ever referenced the task bar, the menu icon or the escape key.
The one thing they did reach for -- a log and a MonoBehaviour to start coroutines on --
is `src/Casino.Client/Shims.cs`, which stands in under the three old plugin names.

The `.Client` projects still build on their own and still produce plugins. **Do
not ship those.** They are the editing surface, and `scripts/casino/pack.ps1` retires
their installed folders when it installs the casino, because four tabs and four Harmony
patches on one method is the likeliest way an upgrade goes wrong.

### Adding a table

Write the panel, implement `ICasinoGame` in `Games.cs` (three properties, three
methods: Name, Pip, Blurb, IsOpen, Open, Close), and add a line to `Games.All`. No
second tab, no second GUID, no second plugin. The lobby and the escape key pick it up
without being told.

### The layers, which matter more than they look

| Canvas | Sorting order |
| --- | --- |
| Lobby | 2900 |
| Welcome card | 2950 |
| The tables | 30000 |

Everything covers the lobby. That is what makes the transitions work: bring the lobby
up **solid underneath** whatever is on screen, then fade that away. Fading the lobby
*in* after removing the thing above it leaves frames where only the game's menu is
drawn, which is exactly the flash that had to be fixed once already. `CasinoLobby.Show`
takes an `instant` flag for this.

## What the tables share, and where it lives

`src/Casino.Shared` holds one copy of everything every table draws with:

| File | Was |
| --- | --- |
| `Textures.cs` | identical in all three |
| `CardView.cs` | identical in Blackjack and Poker |
| `ChipView.cs` | Roulette's was a strict superset of Poker's, zero lines lost |
| `ProfileSync.cs` | identical but for the sync action, which is now a parameter |
| `Host.cs` | new: the two things the shared code needs from its host |

**`Host` is the whole seam.** These files used to reach for their own table's plugin
by name, which is most of why they could not simply be shared, and there were only
ever two such reaches: where the art is, and where to log. Both are set once at
startup, and both tolerate never being set -- a shared file that throws because a host
forgot to introduce itself would be worse than the duplication it replaced.

The `.Client` projects compile the shared files too, so each still builds on its
own. `Casino.Client` compiles them once alongside the five panels.

Verified against the built assembly rather than assumed: `Casino.Client.dll` now
carries exactly one `Textures`, one `ProfileSync`, one `CardView` and one `ChipView`.
Before the extraction it shipped three, three, two and two.

**Still duplicated, on the server side**: `Bank.cs`, `Escrow.cs`, `Abstractions.cs`,
`ProfileGateway.cs`, `TableStore.cs` and `Wallets.cs` exist four times. Those are
four tables loaded into one server process, so the duplication is real but
harmless in a way the client's was not. See "Merging the servers".

**Blackjack is the one that drifts**: it is the oldest and improvements made while
writing the other two were never carried back. On 2026-09-05 that cost a real bug --
`InRaid` was wrong in every copy at once, and every casino tab greyed out for
the rest of the session after a visit to the hideout. That class of fault is what the
extraction was for.

## `dotnet` on this box is not the `dotnet` you want

**This branch targets SPT 4.0.13, which is net9.0.** There is no .NET 10 SDK on this
machine and none is needed. The one first on PATH is `C:\Program Files\dotnet\dotnet.exe`
and carries only 6.0 and 8.0; the SDK you want is user-local:

```
C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe --list-sdks   # 9.0.317
```

Use that full path for any `dotnet build`, `dotnet test` or `dotnet run` on the server
half, or put its directory ahead of `C:\Program Files\dotnet` on PATH -- the pack
scripts shell out to plain `dotnet`. The `.Client` projects are net472 and build under
the 8.0 SDK that is already on PATH, which is why the client half needs no special
treatment.

```
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' build src\Casino.Server\Casino.Server.csproj -c Release
dotnet build src\Casino.Client\Casino.Client.csproj -c Release "-p:SPTPath=C:\SPT"
scripts/casino/pack.ps1 -InstallPath 'C:\SPT'
```

`C:\SPT` is the real install here: SPT 4.0.13, EFT `0.16.9.40087`, Fika 2.3.5, read out
of the binaries rather than a folder name. It is a live, heavily-modded game, not a test
rig -- treat it as read-only unless installing is the actual task.

**The version gate is loud, not silent.** `CLAUDE.md` said for a long time that a mod
rejected by `SptVersion` "loads nothing and logs nothing". That is wrong, and it was
checked against 4.0.13's own `ModValidator`: `ValidateCoreAssemblyReference` runs at
line 24, *before* the semver check at line 136, and a mod compiled against the wrong
`SPTarkov.Server.Core` dies there on an **unhandled throw that takes every other server
mod down with it**. Silence at startup is not the gate. A wall of red is.

**`PluginValidator` does not exist in 4.0.13.** It arrived in 4.1.3. The note elsewhere
about a plugin built against a 4.0 install being rejected outright describes the 4.1.x
line only.

`tools/Blackjack.Installer` is deliberately outside the solution: it embeds a
`payload.zip` that `tools/build-installer.py` generates, so from a clean checkout it
fails with CS1566.

**`.slnx` files are XML, so a `--` inside a comment is a parse error.** This has broken
the build twice; both times the comment was written in this repo's own house style.

## The things that are true of every table

**SPT 4.x server mods are C#, not TypeScript.** The `mod.ts` / `package.json` /
tsyringe world ended at 3.x and most guides online still describe it. On this branch
server mods are **net9.0** class libraries referencing `SPTarkov.Server.Core` **4.0.13**,
with an `AbstractModMetadata` record in place of `package.json`. (On the 4.1.x line
those are net10.0, `IModMetadata` is an interface, and the NuGet ids are `SPTushonka.*`
from 4.1.3 -- see `docs/memory/2026-09-07-spt-renamed-to-sptushonka.md`.)

**`SptVersion` is a hard load gate.** All six places say `~4.0.13` -- `ModMetadata.cs`
and each of the five `TableInfo.cs`. Move one and you must move all six.

**The plugin is compiled against the game, not just against SPT.** 4.1.3's
`PluginValidator` reads a plugin's references to `spt-*` and compares Major.Minor to
the running server, so a plugin built against a 4.0 install is rejected outright. Pass
`-p:SPTPath=...` through PowerShell, not Bash: a backslash path gets mangled on the way
and every reference silently fails to resolve.

**Request bodies are matched case-sensitively, and PascalCase.** Lowercase keys bind
nothing and every field takes its default, which is how a 100,000 stake arrives as 0
while looking like it bound correctly.

**A destroyed Unity object is not null to a plain reference check.** Comfort's
`Singleton<T>.Instantiated` is `ldsfld; box; ldnull; cgt.un` -- a raw comparison, so it
reports a torn-down world as present. Use Unity's `==` on the instance. And note the
hideout is a `GameWorld` too: `HideoutGameWorld : ClientLocalGameWorld :
ClientGameWorld : GameWorld`.

## Where the money is

**All three tables move real roubles**, through escrow and the profile. There is no
chip balance and nothing to cash out: a stake leaves the stash when it is committed and
the return is paid straight back in, with anything the stash will not take posted as
mail.

Roulette's is the newest and the most carefully checked: 13 money tests written
**before** the settlement they check, then mutation-tested against eight deliberate
faults, all eight caught. `Payouts` and `Bet.Covers` were mutation-tested separately --
21 faults, and the two that survived the first pass were both tests **counting** the
numbers a bet covers instead of reading them. When adding a bet, assert *which* numbers
it covers, not how many.

**Write `MoneyInvariantTests` before the settlement, not after.** An end-of-run balance
check misses errors that cancel, and a settlement written first gets tests shaped around
what it already does rather than around what it owes.

### One escrow row per session, so one table per game

`escrow-<table>.json` holds **one** `OutstandingStake`/`OutstandingStack` per session.
That was correct while a player could only be at their own table, and shared tables gave
a session a second place to owe from.

It is not a miscount if you get it wrong. Both `RefundAbandoned` methods decide a stake is
orphaned by asking whether the **private** store has anything for that player, so a live
shared stake fails the test and is handed back — and one of the places that runs is
`State`, which is to say **opening the panel**. Proved by test, guard removed: 2,000,000
roubles back in the stash with the chips still on the felt, then paid out again on
standing up.

**So: a player is at one poker table, and one blackjack table.** Enforced from both sides
— the solo service refuses somebody at a shared table *and* stops refunding their stake;
the shared service refuses a seat while escrow holds anything. Either half alone leaves
the opposite order open. Poker and blackjack at the same time is still fine: the escrow
files are separate, which is also why `Casino.Server.TableClaims` is one instance per
game rather than one for the casino.

See `docs/memory/2026-09-07-one-escrow-row-two-tables.md`.

### One player, one thing at a time

**Every public service method takes `Casino.Server.SessionGate` and then calls an ungated
private `*Core`.** Read `src/Casino.Server/SessionGate.cs` before changing anything on a
service -- the reasoning is all in there. The short version:

- SPT 4.0.13 does **not** serialise requests. Two requests for one profile really do run
  a mod's handlers at once, and nearly every money defect here is a composite
  read-then-act with nothing holding the gap.
- The gate is keyed by **session** and there is exactly **one instance** for the whole
  casino, not one per table. The resource being protected is the profile: `Bank` mutates
  the same `pmcData.Inventory.Items` list whichever table you are at, so per-table gates
  would leave a blackjack deal racing a roulette spin unserialised -- inventory
  corruption, not a miscount.
- **It is not reentrant.** That is why the bodies are private `*Core` methods: a gated
  method calling another gated method for the same session deadlocks until the timeout.
  If you add an entry point, gate the wrapper and call cores from it.
- **`Ping` and `Stats` are gated too**, though they move no money. `bank.GetBalance`
  walks `pmcData.Inventory.Items` while a debit modifies it, and `StatsStore.Get` hands
  out a live object the serialiser then walks. Neither is a safe read.
- **Never** use `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` to dodge making a
  caller async. A blocking wait inside a gated section starves the pool thread the gate
  holder needs to resume.

**A concurrency test must be proven to fail without the gate.** Disable the
`gate.EnterAsync` line, watch it fail, quote the message, restore it. Two independent
reviewers wrote stress tests for this code that passed 7/7 and 20/20 **on the broken
version** -- `Escrow.Flush` takes a lock and writes a file on every call, which
serialises the racers by accident. A test that passes either way is worse than none,
because it will later be read as proof the defect is gone.

Forcing the race needs care too: a `Barrier(2)` deadlocks once the gate is in, because
the second racer never arrives. See `tests/Poker.Server.Tests/ConcurrencyTests.cs` --
the probe waits with a **timeout** instead, so the same assertion works gated and
ungated and neither path hangs.

**Escrow rows are replaced, never edited in place.** `Get` hands out the store's own
object, so `existing.Amount += amount` both loses updates and mutates a row another
caller is holding. Poker's was worse -- two separate field writes, and `RefundAbandoned`
reads `Wallet` to pick the currency, so a torn row could refund roubles as dollars. All
four stores now publish a fresh `OutstandingStake`/`OutstandingStack` from the
`AddOrUpdate` delegate.

## The two remotes, and which one you may push to

```
origin    https://github.com/DeepFriedDepp/SPT-Casino.git   fetch + push
upstream  https://github.com/JoelHauser/SPT-Casino.git      FETCH ONLY
```

**`origin` is this fork and is the only thing anything is ever pushed to.** `upstream` is
the mod this was forked from, kept as a remote so its fixes can be read and backported --
see `docs/memory/2026-09-11-backport-from-upstream.md`, which also explains why nothing
there cherry-picks.

Its push URL is deliberately set to a string that is not a repository, so `git push
upstream` fails with "does not appear to be a git repository" rather than doing anything.
Leave it that way. Restoring it buys nothing -- we have no write access there, and a
contribution goes as a pull request from `origin`.

## Publishing

**SPT Casino registers as `com.mybutthasarash.sptcasino`, and only the main file has
to declare it.** That is `Casino.Client.dll`, and it does. The three server mods
bundled alongside keep their own GUIDs and that is a valid upload.

Written down because the opposite was believed here for months and is still the
reason `docs/blackjack.md` and `docs/poker.md` needed correcting: the stricter reading
was used, on 2026-09-05, to argue that the release could not go out without merging
the three server mods first. It could. Do not block a release on this again.

```
scripts/casino/pack.ps1 -Zip     # releases/casino/SPT_CasinoV1.1.0.zip
```

## One folder, seven assemblies

Read out of `SPT.Server.dll` rather than guessed, because the shape of the install
depends on it:

- `ModLoader.LoadMods` walks `Directory.GetDirectories("./user/mods/")` and calls
  `LoadMod` once per **folder**.
- `LoadMod` does `new DirectoryInfo(path).GetFiles()`, loads **every** `.dll` it finds,
  and hangs them all off one `SptMod.Assemblies`.
- `RegisterSptServicesAsync` walks that whole list, so every `[Injectable]` in every
  assembly is registered.
- `LoadModMetadata` runs `SingleOrDefault` over the types implementing `IModMetadata`
  and throws **"Duplicate mod metadata found for mod at path"** on the second.

So the rule is: **one folder, one metadata, as many assemblies as you like.** That is
why `Casino.Server` exists and is almost empty, and why the three tables carry a
`TableInfo` with their version on it instead of an `IModMetadata`. Their versions are
still their own -- Blackjack is on 1.1.4 inside a casino on 1.0.0 -- because they
describe the table rather than the download.

**Do not put the parked folder inside `user/mods`.** SPT tries to load every directory
under there, and one holding no assemblies throws `No Assemblies found in path` at
Critical on every boot. That was traded for three folders once already; the install
script parks old mods in `user/_replaced-by-SPT-Casino`, beside `mods` rather than in
it.

### The two collisions one folder creates

**`config.json`** was the same name in all three, so one file would have been read
three times. They are `blackjack.config.json`, `poker.config.json` and
`roulette.config.json` now.

**`escrow.json` was the dangerous one**, and it is the record of money the house owes
a player whose hand or spin was interrupted. Three writers on one path would have been
three tables overwriting each other's. They are `escrow-<table>.json`, and because the
old file is now somewhere the new code would never look, each store imports it once on
first run -- see `Casino.Server.LegacyData`, which checks both where the folder was and
where the install script parks it.

Proven rather than assumed: a record for 4,250,000 was planted in a retired Roulette
folder, the server was restarted, and it arrived in `escrow-roulette.json` under the
new folder. Then it was deleted, because it named a real session and would have paid
out money nobody staked.

## Keeping these notes honest

Each `docs/<table>.md` has a **Current state** section. Update it when a piece of work
finishes. Poker's notes went four commits claiming its server did not exist, and this
file spent a day saying Roulette moved no money after it did; a fresh session reads
those sections first and believes them.
