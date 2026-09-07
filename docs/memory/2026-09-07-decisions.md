# Decisions taken, and what they rule out -- 2026-09-07

Taken by the repo owner after Phase 1, on the evidence in the other files here. Recorded because a fresh
session will otherwise re-open all three.

## 1. Stay on SPT 4.0.13. One source tree, no 4.1.x branch.

The work order started from the opposite recommendation -- target 4.1.x, treat 4.0.13 as a separate
track -- and the evidence inverted it. That reasoning rested on two things that did not survive contact:

- **"Fika needs 4.1.x."** Fika cannot carry a shared casino table at *any* version. Its net manager is
  created by a Harmony prefix on the raid preparer and destroyed at raid teardown; all ten of its modding
  events are raid-lifecycle. The casino lives in the menu. See
  `2026-09-07-phase1-multiplayer-transport.md`.
- **"The deobfuscated 4.1.x client is easier to patch."** Measured: the client half needed **two
  identifier renames**. See `2026-09-07-phase1-4013-backport.md`.

Against that, 4.1.x costs a .NET 10 SDK this box does not have, a different EFT client build, a clean SPT
install -- discarding a working ~45-mod Fika setup -- and following a project three weeks into a
trademark-forced rename onto new NuGet ids. See `2026-09-07-spt-renamed-to-sptushonka.md`.

**Multi-targeting was rejected** with it: it doubles the verification surface across two client
generations, and the 4.1.x half could not be built or tested on this machine anyway.

## 2. Poker is the shared-multiplayer pilot.

This one went **against** the recommendation, deliberately. The code says the slice sizes are
Slots < Roulette < Blackjack < Poker, and Poker is not merely the largest -- it is the only table with
hidden per-seat information, turn order, an inter-player pot, and an escrow holding a live moving stack
rather than a stake for the length of one call. Roulette was recommended precisely because it has none of
those.

So the pilot carries the hard problems rather than deferring them, and three consequences follow that
would not have applied to Roulette:

- **Hand privacy becomes a first-class requirement immediately.** Another seat's hole cards must never go
  over the wire, which means per-seat filtering wherever state is broadcast -- not hiding on the client.
- **Human/bot seat mixing stops being deferrable.** It was open decision #5 in the work order, explicitly
  "not blocking for a Roulette pilot". It blocks now. Today `HoldemTable.PlayerSeatIndex = 0` and a
  per-seat `bool HoldemSeat.IsPlayer` bake human-ness in at seat 0; **a human in seat 2 has no
  representation at all**.
- **The concurrency model has to be right first**, because a pot with several humans acting in turn is
  where a weak one shows.

Trap for whoever builds it: `HoldemTable.cs:81` reads `if (agents is not null && agents.Count != seats - 1)`,
and `agents` defaults to null on both constructors. A table can be built with no agents and the ctor
throws nothing -- it dies later with `KeyNotFoundException` at `HoldemTable.cs:503`. Do not design against
a guard that is not there.

## 3. Fix the money races before any multiplayer work.

They are in the shipping code now and two Fika players can already reach some of them --
`2026-09-07-money-concurrency-defects.md`. A shared table multiplies every window by the number of seated
players, so this is a prerequisite rather than a parallel task.

**One ordering note.** "Money first" could not be taken literally: nothing in the server half compiled on
this box until it was retargeted, so no money test could run. The mechanical backport therefore landed
first, as infrastructure -- see `2026-09-07-backport-landed.md` -- and the money work follows it, still
well ahead of any multiplayer code.

## The transport, which was not a decision so much as a finding

SPT's own `IWebSocketConnectionHandler`. Not Fika, not SignalR, not long-polling. Two working precedents
are already installed on this machine -- SharedHideout (which references Fika nowhere) and Fika's own
server, which uses it for its out-of-raid messaging rather than its own P2P transport. Detail and the two
traps in `2026-09-07-phase1-multiplayer-transport.md`.
