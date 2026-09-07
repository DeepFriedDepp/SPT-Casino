# The 4.0.13 backport, as landed -- 2026-09-07

Branch `spt-4.0.13`, based on upstream `3acfbd0` (**SPT Casino 1.1.0**). Decision behind it:
`2026-09-07-spt-renamed-to-sptushonka.md` and `2026-09-07-phase1-multiplayer-transport.md`.

> **Rebased 2026-09-07.** This work was first built on `c0fcc3b`, which the work order said was identical
> to upstream. It was not -- the fork was **three commits behind**, missing SPT Casino **1.1.0**
> (AUTO and SPEED for the slot machine). The branch has been moved onto `3acfbd0`. `c0fcc3b` was a clean
> ancestor, so this was a fast-forward of the base with one small conflict, and nothing was lost. See the
> correction at the top of `2026-09-06-phase0-verification.md`.
>
> **The 1.1.0 client code compiles against 4.0.13 unchanged** -- `SlotPanel.cs` gained 232 lines of AUTO
> and SPEED written against the deobfuscated 4.1.x client, and it needed no adjustment at all.
> `Casino.Client.dll` went 173,568 -> 176,128 bytes. That is a second, independent data point for how
> narrow this mod's game-API surface is.
>
> **One upstream bug inherited and fixed here.** The 1.1.0 release bumped the version in
> `ModMetadata.cs`, `CasinoPlugin.cs`, `pack.ps1` and `Banner.cs`'s doc comment, but **missed
> `Casino.Server.csproj`**, which still said `1.0.1` -- directly above its own comment reading
> "Must match ModMetadata.Version. Two places, and they have to agree." So upstream's `Casino.Server.dll`
> carries an assembly version of 1.0.1 while the mod reports itself as 1.1.0. Corrected on this branch;
> worth reporting upstream.

## Where it stands

| | |
| --- | --- |
| Server half | 5 projects, **0 errors, 0 warnings** against real 4.0.13 assemblies |
| `.Game` + tests + tools | 14 projects, clean |
| Tests | **480 passed, 0 failed**, all 8 suites |
| Client half | `Casino.Client.dll` builds against `C:\SPT`, 173,568 bytes |

Build with the user-local SDK -- there is no .NET 10 on this box and none is needed any more:

```
& 'C:\Users\Jonasty\AppData\Local\Microsoft\dotnet\dotnet.exe' build src\Casino.Server\Casino.Server.csproj -c Release
dotnet build src\Casino.Client\Casino.Client.csproj -c Release "-p:SPTPath=C:\SPT"   # net472, the 8.0 SDK is fine
```

**Not yet run inside a server.** Everything below compiles and unit-tests; nothing has been loaded into
SPT. DI registration, route dispatch, `OnLoad` ordering and the JSON handler path are all unexercised.

## What changed, by kind

**Retarget** -- `net10.0` -> `net9.0` on all 19 non-client projects, `SPTarkov.*` `4.1.2` -> `4.0.13`.
Zero errors came from the framework change itself; the four `.Game` projects and their tests needed
nothing but the TFM string. Everything else was the API delta, and most of it was namespaces 4.1 added:
`Helpers.Items`/`Helpers.Profile` -> `Helpers`, `Services.Commerce` -> `Services`.

**Shims** -- `IModMetadata` -> `AbstractModMetadata`, `OnLoadAsync(CancellationToken)` -> `OnLoad()`,
`RouteAction<T>`'s 5-arg lambda -> 4-arg returning `ValueTask<string>`, `OnLoadOrder.PostLoad` ->
`PostSptModLoader`. `OnLoadOrder` is a static class of `int` consts in 4.0.13, not an enum -- which is why
`PostSptModLoader + 1` is legal.

**The item-event transport** -- rewritten, and this is the part that had to be got right rather than
merely compiled. `src/Casino.Server/ItemEventActions.cs` is new and carries the full explanation. The
short version: 4.0.13 has no `ItemRouteAction<T>` to hang a payload type on, and its one global
`BaseInteractionRequestDataConverter` *throws* on an action name it does not recognise -- inside
`JsonUtil.Deserialize`, called from `StaticRouter`, **before any router is consulted**. A router that
digs the payload back out of `body.ExtensionData` is therefore unreachable code. That was the shape the
investigation first produced; it compiled and was dead, proven by running the real 4.0.13 assembly and
getting `Unhandled action type BlackjackSync` for all ten of this mod's actions while EFT's own `Move`
bound fine.

The supported way is better than the workaround: `BaseInteractionRequestDataConverter` has a public static
`RegisterModDataHandler(string action, Func<string, BaseInteractionRequestData>)`. So each table's
`*Actions.Register()` teaches SPT to build its own records, called first thing in that table's
`Startup.OnLoad` -- **ahead of the verbose early-return**, because a table with its banner switched off
still has to bind its own bodies. The typed records are untouched; the routers cast rather than re-parse.
Deserialization borrows `JsonUtil.JsonSerializerOptionsNoIndent`, SPT's own options, so casing behaves
exactly as 4.1.x did.

All ten actions are registered, including the four payload-less `*Sync` ones: the converter rejects the
*name*, not the shape.

Worth knowing for later: only the `*Sync` actions are actually live. The shipping clients play over the
static routes -- `PokerApi.cs` posts all six, `BlackjackApi.cs` likewise -- and the only item event any
client sends is `ProfileSync.Request("<Table>Sync")`. `PokerSit/Deal/Act/Leave` and `BlackjackDeal/Play`
are registered but dead in the client. The class doc in `RouletteItemEventRouter.cs` claiming "Poker and
Blackjack put their whole game on the item-event transport" is stale.

**Spectre.Console removed.** SPT 4.0.13 ships no Spectre anywhere. `Casino.Server/Startup.OnLoad` calls
`Banner.Rainbow`, so a 4.0.13 server's first act would have been a `FileNotFoundException` -- and adding a
`PackageReference` fixes only the compile, because `CopyLocalLockFileAssemblies=false` and the pack
scripts' allowlist mean the DLL would ship nowhere. `Banner.cs` now cycles `ConsoleColor`, which needs no
dependency and renders in a console with no VT processing (raw ANSI was the other candidate and fails
worse -- escape codes print as literal garbage).

`Palette.cs` is **deleted**. It was Spectre-only and entirely dead: nothing called `Palette.Next()`. The
four `*Log.cs` doc comments pointing at it were already lying -- `Banner` uses a flat
`LogTextColor.Cyan`, not a shared cycle -- and now say what the code does.

**The version gates moved** -- `~4.1.3` -> `~4.0.13` in all five places: `Casino.Server/ModMetadata.cs`
and each `<Table>.Server/TableInfo.cs`.

## A real bug the backport surfaced

Five `CS8604` warnings appeared that 4.1.2 never showed: `JsonUtil.Serialize` returns `string?` in
4.0.13 while `FileUtil.WriteFile` takes a non-nullable `string`.

That is not cosmetic. Poker, Roulette and Slots already guard it -- they null-check and **return without
writing**, because writing nothing truncates the escrow file and loses every stake it was holding.
**Blackjack did not**, and neither did either `StatsStore`. Exactly the drift `CLAUDE.md` warns about, and
on the money path. All five sites now match the newer tables' pattern.

## Corrections to `CLAUDE.md` carried into the code comments

- **`PluginValidator` does not exist in 4.0.13** (added 4.1.3).
- **A version-gate rejection is loud and total.** `ValidateCoreAssemblyReference` runs at
  `ModValidator.cs:24`, before the semver check at line 136; a mod built against 4.1.2 on a 4.0.13 server
  dies on an unhandled throw and **takes every other server mod with it**. "Loads nothing and logs
  nothing" was wrong, and it is now corrected in `ModMetadata.cs` and the three table `Startup.cs` docs.

## Left to do

1. **The money races** -- being specified now. See `2026-09-07-money-concurrency-defects.md`.
2. Load it into the real server and watch it boot. Nothing here is runtime-verified.
3. No test covers the rewritten item-event transport. No test constructs any `*ItemEventRouter` or
   exercises `HandleItemEventInternal`; `Poker.Server.Tests/ItemEventTests.cs` drives the *callbacks* with
   already-typed objects, which bypasses precisely the layer that was rewritten.
4. `CLAUDE.md` itself still describes the 4.1.x world and a different dev box.
5. `scripts/casino/pack.ps1` still defaults `-SPTPath` to `H:\SPT4.1.X`.
