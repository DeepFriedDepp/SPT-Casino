# SPT was renamed. The packages this repo references are a dead end. -- 2026-09-07

This was not in the work order and it changes what "move to 4.1.x" means.

SPT was hit with a BSG trademark notice around **2026-08-12**. The `sp-tarkov` GitHub org is
**archived**; development continued under a renamed org, **SP-Tushonka**, which has shipped 4.1.3
(2026-08-20), 4.1.4 (2026-09-04) and 4.1.5 (2026-09-05). The old hub is gone --
`forge.sp-tarkov.com` returns HTTP 525 and `www.sp-tarkov.com` returns HTTP 410 Gone.

**The latest SPT release is 4.1.5, not 4.1.2.**

## The NuGet ids changed. Verified directly against nuget.org.

```
sptarkov.server.core     97 versions, last three: 4.1.0, 4.1.1, 4.1.2
sptarkov.common          97 versions, last three: 4.1.0, 4.1.1, 4.1.2
sptarkov.di              96 versions, last three: 4.1.0, 4.1.1, 4.1.2

sptushonka.server.core    7 versions, ending: 4.1.3, 4.1.4, 4.1.5
sptushonka.common         7 versions, ending: 4.1.3, 4.1.4, 4.1.5
sptushonka.di             7 versions, ending: 4.1.3, 4.1.4, 4.1.5
```

The assembly inside is still `SPTarkov.Server.Core.dll` and the C# namespace is unchanged, so this
is an id change, not an API change. `SPTushonka.Server.Core` also republished a `4.1.2` under the
new id; `sptushonka.common` and `.di` start at 4.1.3.

## What that means for this repo

The repo is in a spot nobody chose: every `*.Server.csproj` references **`SPTarkov.Server.Core`
4.1.2 -- the last release under the dead id** -- while the `SptVersion` gate declares **`~4.1.3`**,
whose packages only exist under the *new* id. It compiles because the namespace never moved, but the
repo is one release behind a rename it has not noticed, and it cannot pick up 4.1.3+ without
changing package ids.

So "stay on 4.1.x" is not the stable, maintained option it reads as in the work order. It means
following a project that has just been renamed under legal pressure, onto package ids three weeks
old, on a line that shipped two releases in the last four days.

`4.0.13` is genuinely the end of the 4.0 line -- that part of the work order's framing is right.
But "unmaintained 4.0.x versus maintained 4.1.x" is not the choice on offer.

## Two corrections to `CLAUDE.md` that came out of the same check

- **`PluginValidator` does not exist in SPT 4.0.13.** It was added in 4.1.3. Verified against the
  live install and against the absence of the type and its strings from the `4.1.2` source tree.
  `CLAUDE.md` attributes it to "4.1.3's `PluginValidator`", which is right -- but the repo's
  `Blackjack.Client.csproj` comment implies it gates 4.0 plugins generally, and on 4.0.13 there is no
  such gate.
- **A version-gate rejection is loud, and it kills every server mod, not just the offending one.**
  `ValidateCoreAssemblyReference` runs at `ModValidator.cs:24`, before the `SptVersion` semver check
  at line 136. A mod referencing `SPTarkov.Server.Core` 4.1.2 on a 4.0.13 server dies on an unhandled
  throw -- "requires a minimum SPT version of `4.1.2`, but you are running `4.0.13`" -- and never
  reaches the `SptVersion` gate at all. `CLAUDE.md`'s "loads nothing and *logs nothing*" is wrong for
  this path.

## Fika version facts, while they are written down

- Fika **2.3.5** is correctly paired with SPT 4.0.13 (its own csproj: `SPTarkov.*` 4.0.13, `net9.0`,
  `SptVersion ">=4.0.13"`).
- The installed **plugin** is one step behind -- **2.3.9** is the newest for the 4.0.x line, not 2.3.5.
- The installed **server** is current: `v2.3.5` is the last Fika server release on the 4.0.x line.
  Beware a trap here -- `FikaServer.dll` reports version **2.0.9.0** because upstream never bumped a
  stale `<Version>` in its csproj. The real version lives in `FikaModMetadata.cs`. It is not an old
  install.
- Fika's server half is at **`C:\SPT\SPT\user\mods\fika-server\`**, not `C:\SPT\SPT_Runtime\user\mods\`.
- Fika interop DLLs on this box reference Fika.Core versions from **2.0.7 to 2.3.9** against the
  installed 2.3.5, resolved by simple name with `PublicKeyToken=null`. **Fika's mod-facing surface is
  version-loose in practice** -- two installed mods are compiled against a *newer* Fika than the one
  running. Treating Fika API drift as a hard blocker is not supported by what actually runs here.
