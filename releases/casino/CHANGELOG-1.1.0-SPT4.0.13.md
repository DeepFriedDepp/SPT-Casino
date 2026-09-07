# SPT Casino 1.1.0, for SPT 4.0.13

A build of casino 1.1.0 for the **SPT 4.0.x** line. Same four tables, same slot machine
with AUTO and SPEED. It is not the same download as `SPT_CasinoV1.1.0.zip`, which is the
4.1.x build -- the two differ in target framework, in the version gate, and in which
folder the server half unpacks into, so they are not interchangeable.

**Requires SPT 4.0.13** and EFT `0.16.9.40087`. The gate is `~4.0.13`.

## Installing

Extract over your SPT folder -- the one holding `SPT\` and `EscapeFromTarkov.exe`. The
server half lands in `SPT\user\mods\Casino`, beside your other server mods.

You should see a `[Casino] client loaded` line in `BepInEx/LogOutput.log` and one
`[Casino]` line in the server console.

**If it fails, it fails loudly.** A mod built for the wrong SPT dies on an unhandled
throw during mod validation and takes the other server mods down with it, so a wall of
red naming a version is the version gate; silence is something else.

## What changed from the 4.1.x build

**Ported to 4.0.13.** net10.0 to net9.0 across the tree, `SPTarkov.*` 4.1.2 to 4.0.13,
`IModMetadata` to `AbstractModMetadata`, `OnLoadAsync` to `OnLoad`, and the 4.1-only
`Helpers.Items` / `Helpers.Profile` / `Services.Commerce` namespaces folded back.

**The item-event transport was rewritten.** 4.0.13 has no `ItemRouteAction<T>`; its one
global body converter throws on an action name it does not recognise, before any router
is reached. Each table now registers its own action types through
`BaseInteractionRequestDataConverter.RegisterModDataHandler`, so the typed records
survive untouched.

**The console banner no longer needs Spectre.Console**, which 4.0.13 does not ship. It
would have thrown `FileNotFoundException` on the first boot.

**The client needed two renames.** On this client build `ItemFactory` and
`ItemIconCreator` are `ItemFactoryClass` and `GClass926`. Of the 19 game types the
plugin touches, 17 kept their names.

## Money fixes, which apply to any version

These were found while porting and are worth having regardless of which SPT you run.

**Every request for one player is now serialised.** SPT does not do this for you -- two
requests for one profile really do run a mod's handlers at once, and nearly every money
path here was a read-then-act with nothing holding the gap. Nine tests across the four
tables cover it, and each was proven to fail without the fix. Among the things that
could happen before it, all reproduced rather than theorised:

- Two "stand up" clicks cashed out the same poker stack **twice**.
- Two "sit down" requests took **two buy-ins** and kept one table, destroying the other
  with nothing on disk that knew it existed.
- A bet placed while the roulette wheel was already turning was **played for free** --
  measured at 1,700,000 paid out against a 100,000 stake.
- Opening the slots panel during a pull could refund the stake that was still in play.
- A stranded stake could be refunded twice by two requests arriving together.

**Escrow rows are no longer edited in place.** Blackjack's accumulate could lose part of
a raise, so a crash refunded less than was taken. Poker's wrote two fields separately,
and the refund path reads one of them to choose the currency -- a torn row could pay a
rouble stack back as dollars.

**Lifetime stats are copied before they leave.** The JSON serialiser used to walk the
live record after the request had finished with it, which could throw mid-response.

## Known limits

- Not yet run inside a live server. Everything here is compile-and-unit-test: 496 tests,
  0 errors, 0 warnings.
- The remaining ordering hazards are unfixed and known: a debit interrupted partway
  leaves money gone with no escrow row, and a crash between the debit and the escrow
  write loses or mints one stake depending on the table. Both need their own design --
  two proposed fixes were tried and rejected, one of which silently minted money.
