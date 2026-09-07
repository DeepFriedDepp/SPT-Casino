# docs/memory

Findings logged as they were **measured**, not as they were expected.

One file per investigation, named `YYYY-MM-DD-<topic>.md`. Each states what was checked,
the command or artefact it was checked against, and the result. A finding that contradicts
a work order, a README or `CLAUDE.md` says so explicitly and names what it contradicts --
that is the whole point of writing it down.

The convention that matters: **separate what was verified from what was inferred.** A line
that says "no .NET 10 SDK on this box" should be followed by the command that proves it, or
it is worth nothing to the next session.

| File | What it settles |
| --- | --- |
| `2026-09-06-dev-box.md` | What toolchain and what SPT install actually exist on this machine |
| `2026-09-06-phase0-verification.md` | Folder layout, the release zip's real contents, build blockers |
| `2026-09-07-phase1-4013-backport.md` | What the 4.0.13 backport costs. Both halves really compiled |
| `2026-09-07-phase1-multiplayer-transport.md` | Why not Fika, why SPT's own WebSocket, and the pilot table |
| `2026-09-07-money-concurrency-defects.md` | Money races in the shipping code, today, before any shared table |
| `2026-09-07-spt-renamed-to-sptushonka.md` | SPT's rename, the dead NuGet ids, and two `CLAUDE.md` corrections |
| `2026-09-07-backport-landed.md` | What the `spt-4.0.13` branch actually changed, and what is still unverified |
| `2026-09-07-decisions.md` | The three decisions taken, why, and what each rules out |
| `2026-09-07-money-races-verified.md` | The verified race inventory, and the fix designs that must NOT be built |
| `2026-09-07-session-gate-landed.md` | The gate, across all four tables, and how each test was proven to fail without it |
| `2026-09-07-shared-table-design.md` | Two humans at one poker table: lock order, per-seat privacy, and what happens when somebody alt-F4s |
