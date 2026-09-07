# It ran. A whole hand of poker, two people, on a real server -- 2026-09-07

Reported by the repo owner: **a full match of shared poker played with a friend, working
flawlessly.** First time any of this had been inside a running SPT server at all.

Every other file in this folder was written while that was still unknown, and several of
them say so in as many words. This is the correction.

## What that one run actually proves

More than it looks, because the untested part was a chain and the whole chain had to work
for a hand to finish:

- **The mod loads on SPT 4.0.13.** The backport is real, not just a clean compile: DI
  registration, `OnLoad` ordering, route dispatch, the `SptVersion` gate.
- **The server half is in a folder SPT reads.** The install-path fix was right. Had it not
  been, the tab would have opened and every route would have 404'd.
- **The item-event handler registration works.** `BaseInteractionRequestDataConverter`
  accepted the mod's own action types rather than throwing `Unhandled action type` --
  which is what the whole `RegisterModDataHandler` rewrite existed to fix, and which the
  first port would have failed on.
- **The websocket works end to end**, and this was the least proven thing in the release.
  No socket had ever been opened. It means SPT's `WebSocketServer` really does dispatch
  `/casino/ws` to an `[Injectable]` handler, the `Authorization` header survives the
  handshake, and the session recovered from it addresses the right player. Every one of
  those was reasoned from a decompiled assembly and a third-party mod's source.
- **`Pump()` from `Update` delivers**, and the messages arrive on a thread a panel can draw
  from.
- **Two humans really do share one table**: seats, turn order, the pot, and both of them
  seeing the hand move without polling.
- **The money moved correctly** for a whole match, through the gate, on two profiles.

## What it does NOT prove, and is still open

Worth keeping honest -- "it worked" is not "it is proven", and the difference is where the
next bug lives.

- **The hole-card privacy was almost certainly not checked.** Two people playing normally
  each see their own screen; neither would notice the other's cards being on the wire.
  It is asserted by tests against the serialised view and by `shared-smoke.ps1`, but not
  by having played. Run the smoke script if you want that one confirmed by observation.
- **Nobody disconnected mid-hand.** The timeout-fold path, and a stack sitting in escrow
  while its owner is gone, are unexercised.
- **Nobody ran out of money mid-hand**, so `Bank.PutBackWhatLeft` -- the partial-debit
  unwind -- has still never run. It cannot be unit-tested either. If the server log ever
  says "the debit failed partway", that is it working.
- **No concurrency was forced.** Two people playing at human speed is not the same as two
  requests landing in the same millisecond, which is what the session gate is for.
- One match is one shoe's worth of hands, on one machine, with one pair of profiles.

## What this changes about the other notes

These files were written before the run and their closing caveats are now wrong:

- `2026-09-07-backport-landed.md` -- "Nothing has been run inside a real SPT server."
- `2026-09-07-session-gate-landed.md` -- same, under "Still to do".
- `releases/casino/CHANGELOG-1.2.0-SPT4.0.13.md` -- "no socket has ever been opened".

All three now point here. The narrower caveats above stand.
