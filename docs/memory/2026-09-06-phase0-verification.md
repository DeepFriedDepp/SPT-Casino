# Phase 0 verification -- 2026-09-06

Checked at `c0fcc3b` (`main`, identical to upstream). Toolchain and install facts live in
`2026-09-06-dev-box.md`.

## Folder layout: two folders, not three. No regression.

**The report of three install folders does not reproduce.** Verified two ways.

`scripts/casino/pack.ps1` stages exactly two destinations and nothing else:

```
$pluginDir = Join-Path $stage 'BepInEx\plugins\Casino'
$modDir    = Join-Path $stage 'SPT_Runtime\user\mods\Casino'
```

and the shipped `releases/casino/SPT_CasinoV1.0.1.zip`, extracted and enumerated in full,
contains 82 entries under exactly those two roots.

The three-folder sighting is almost certainly the **per-table release zips**, which do still
each install to their own folder and are still in the tree:
`releases/blackjack/*.zip`, `releases/poker/*.zip`, `releases/roulette/*.zip`. Installing
any two of those alongside the casino gives a folder each -- and, worse, a task-bar tab and
a Harmony patch each, which is exactly what `pack.ps1 -InstallPath` retires on purpose.

Nothing to fix here. If three folders show up again, ask which zip was installed.

## CORRECTED 2026-09-07 -- the zip below was never the current release

**The section below is right about the artefact and wrong about the conclusion it drew**, and the reason
matters more than the finding: **the fork was three commits behind upstream and I did not check.**

The work order stated the fork was "currently identical to upstream JoelHauser/SPT-Casino at `c0fcc3b`".
It was not. Upstream `main` was at `3acfbd0`, three commits ahead, with `c0fcc3b` a clean ancestor:

```
eaf0bce  The startup line drops the game list and just gives the version   (tag 1.1.0)
2e69d73  AUTO and SPEED for the slot machine, and 1.1.0 to ship them
3acfbd0  Put slots on the mod page
```

Upstream had already cut **1.1.0**, and `releases/casino/SPT_CasinoV1.1.0.zip` ships **all four tables**:
88 entries, nine server-side assemblies plus `Casino.Client.dll`, four configs including
`slots.config.json`, and `tile-slotmachine.png`. Exactly the nine `CLAUDE.md` describes.

`SPT_CasinoV1.0.1.zip` really does hold only three tables -- that part is accurate -- but it was never the
current release, only the newest zip *visible from a stale base*. **The work order's claim that the
release carries all four tables was correct**, and I contradicted it from three commits behind.

The lesson, since it cost real work: **`git ls-remote` the upstream before trusting any claim that a fork
is current.** One command, and it would have caught this before a backport was built on the wrong base.
The `spt-4.0.13` branch has since been moved onto `3acfbd0` -- see `2026-09-07-backport-landed.md`.

The two-folder finding above is unaffected: the 1.1.0 zip has the same two roots.

## Superseded: `SPT_CasinoV1.0.1.zip` has three tables, not four

The work order states the release zip holds "all four tables' server DLLs together under it".
**It does not.** Enumerated from the archive:

```
Blackjack.Game.dll  Blackjack.Server.dll
Poker.Game.dll      Poker.Server.dll
Roulette.Game.dll   Roulette.Server.dll
Casino.Server.dll
Casino.Client.dll
blackjack.config.json  poker.config.json  roulette.config.json
```

Seven server-side assemblies, three tables. Searching the archive for `lot` returns **no
entries at all** -- no `SlotMachine.Server.dll`, no `SlotMachine.Game.dll`, no
`slots.config.json`, and no slots tile in the plugin folder.

This is a stale artefact, not a packer bug. `pack.ps1` at HEAD has
`$tables = @('Blackjack','Poker','Roulette','SlotMachine')` and would `throw "no
SlotMachine.Server.dll under any src\*\bin\Release"` rather than quietly ship three. So the
zip was packed by an earlier three-table version of the script and never re-cut.
`CLAUDE.md` already says the server folder should hold **nine** assemblies (metadata + four
`.Server` + four `.Game`); the zip has seven.

Anyone reasoning about the current release from that zip is reasoning about the old one.
It needs re-cutting before it goes anywhere -- and the version inside it would still say
1.0.1, which is the trap.

## The Phase 0 build could not be run, for a toolchain reason

The work order asks for a clean `dotnet build` plus `pack.ps1` from current `main`. **Not
possible on this box**: 19 of 25 projects are `net10.0` and no .NET 10 SDK is installed
(see `2026-09-06-dev-box.md`). `pack.ps1` shells out to plain `dotnet`, so it fails at its
first `dotnet build` regardless of the SPT path passed.

The layout conclusions above therefore rest on the packer's source and on the shipped
artefact rather than on a fresh build. Both agree, and they are independent of each other,
so the two-folder finding is solid. The stale-zip finding would only be strengthened by a
build -- a successful pack would produce nine assemblies and prove the zip out of date.

## Smaller things noticed while looking

- `src/SlotMachine.Client/SlotMachine.Client.csproj` exists on disk and its sources are
  compiled into `Casino.Client`, but the project is **not listed in `SPT-Casino.slnx`**.
  The other four `.Client` projects are. So the slots panel builds only as part of the
  casino plugin, never on its own -- unlike every other table.
- `SPT-Casino.slnx` lists 23 projects. `CLAUDE.md` says `dotnet build SPT-Casino.slnx` is
  "18 projects, clean". Stale by five.
- The `.slnx` comments still say "all three mods", "three tables", and for SlotMachine
  "No client yet". All stale since the slots table landed.
- `SptVersion` is `~4.1.3` in five places: `src/Casino.Server/ModMetadata.cs:49` and each
  `src/<Table>.Server/TableInfo.cs:24` (`SlotMachine`'s is at line 16). Any 4.0.13 work has
  to move all five, and the gate fails silently when it is wrong.
