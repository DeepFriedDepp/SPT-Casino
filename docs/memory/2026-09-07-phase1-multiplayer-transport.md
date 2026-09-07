# Phase 1B: the transport question is settled, and the answer is not Fika -- 2026-09-07

## Fika cannot carry this. Not at 2.3.5, not at 2.4.x, not at any version.

Fika **does** have a real, documented, third-party packet API --
`Fika.Core.Modding.FikaEventDispatcher.SubscribeEvent` -> `FikaNetworkManagerCreatedEvent` ->
`IFikaNetworkManager.RegisterPacket` / `SendData`, documented at `wiki.project-fika.com/modding-fika`
and present in the installed 2.3.5 binary. It is simply **raid-scoped, structurally**:

- The net manager is created in exactly **one** place in the entire 594-file source --
  a Harmony prefix on `TarkovApplication`'s raid preparer -- and destroyed at raid `GameWorld`
  teardown.
- **All ten** Fika modding events are raid-lifecycle events. There is no menu, hideout, or lobby event.
- Every one of the eleven Fika-interop mods installed on this box gates on
  `FikaNetworkManagerCreatedEvent` before touching the network.

The casino's tables live in the menu and the hideout. **There is no hook to use.** This is not a
version problem, so it is not fixed by moving to a newer Fika or a newer SPT.

Two further reasons not to build on it even where it would work:

- `FikaEventDispatcher.UnsubscribeEvent<T>` is **broken**: it does `OnFikaEvent -= e => {...}`,
  subtracting a freshly-allocated lambda that can never reference-equal the one `SubscribeEvent`
  added. Handlers can never be removed and every subscription leaks for the process lifetime.
  Confirmed present in the installed 2.3.5 DLL.
- The out-of-raid channels Fika *does* have are sealed: `FikaRequestHandler`'s 27 fixed `/fika/*`
  routes, a websocket with private handlers and a fixed `EFikaNotification` enum, and a presence API
  with a fixed schema. (The presence API's `Location`/`MatchId` are strings and could technically be
  abused as a two-field broadcast channel. Nobody sane would.)

**Fika remains a prerequisite for the scenario, not a dependency of the code.** It is what puts two
humans on one SPT server at the same time. The casino just should not link against it.

## The answer: SPT's own WebSocket, `IWebSocketConnectionHandler`

A class marked `[Injectable]` implementing
`SPTarkov.Server.Core.Servers.Ws.IWebSocketConnectionHandler` gets its own hook URL and its own raw
WebSocket endpoint. `WebSocketServer` is DI-injected with `IEnumerable<IWebSocketConnectionHandler>`
and dispatches by hook URL, so **`[Injectable]` alone is sufficient** -- no registration call.
`HttpServer.HandleRequest` checks WebSocket handlers before anything else and `app.UseWebSockets()`
is already on globally.

**There are two working precedents on this machine, and one is a direct template:**

- **SharedHideout** -- the closest analogue to what the casino needs -- uses this and **references
  Fika nowhere at all**. Its server csproj is `net9.0` with `SPTarkov.*` at **`4.0.13`**: an exact
  match for this box, buildable with the local 9.0.317 SDK.
- **Fika's own server** ships three `IWebSocketConnectionHandler` implementations. Fika itself does
  its out-of-raid cross-player messaging this way rather than over its own P2P transport.

Both halves were compiled against the real on-disk 4.0.13 assemblies: **0 errors each**. On the
client, both `System.Net.WebSockets.ClientWebSocket` and `websocket-sharp.dll` ship in the game's
Managed folder, and SPT already blanket-patches Mono TLS validation so a self-signed `wss://` works.

### Two traps, both found the hard way

1. **`OnConnection`'s third parameter is not a session id**, despite being named `sessionIdContext`.
   `WebSocketServer` sets it to `DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")` -- a per-connection
   timestamp. Keying clients by it cannot address a player, and two connections in the same
   millisecond collide. **Fika does it correctly**: it reads `context.Request.Headers.Authorization`
   and recovers the session id from that. Do the same.
2. There is also a built-in `SptWebSocketConnectionHandler` (public) exposing
   `SendMessageToAll(WsNotificationEvent)`, `SendMessage(MongoId sessionID, ...)` and
   `IsWebSocketConnected(MongoId)`. Worth trying **before** standing up a custom hook URL --
   the game client is already connected to that socket. Its `NotificationEventType` enum is closed
   (73 values, no extension member), which is the likely reason it will not stretch far enough.

## The two options that were ruled out

**SignalR: reachable, but not worth it.** The first probe reported it unreachable; that was
**refuted**. `SPTWeb.InitializeSptBlazor` calls `AddRazorComponents().AddInteractiveServerComponents()`
pre-`Build()`, which transitively calls `AddSignalR()` -- so the full SignalR service graph is
registered on **every** SPT 4.0.13 server. And because SPT's `InjectAll` registers a mod's
`[Injectable]` against every non-`System` interface it implements, a mod can contribute an
`IStartupFilter` and map a hub. That was demonstrated end to end against SPT's real pre-`Build` call
order: `MapHub<TestHub>("/casino/hub")` succeeded and `POST /casino/hub/negotiate` returned 200 with
a real transport list. It works -- it is just more machinery than the WebSocket handler, with no
precedent on this stack and a client-side SignalR dependency to drag into a `net472` Unity plugin.

**Long-polling: the worst option.** SPT's client HTTP layer blocks the calling thread and has a hard
100 s timeout with a 4x retry storm on expiry.

## Pilot table: Roulette. Confirmed from the code.

Ordering by size of a shared-table slice: **Slots < Roulette < Blackjack < Poker**, which matches the
work order's guess for the three it named.

Poker is not merely harder, it is a different order of magnitude -- the only table with hidden
per-seat information, turn order, an inter-player pot, and an escrow holding a **live moving stack**
rather than a stake for the length of one call. Its five seats are
`PokerPanel.TableSeats = 5` (a client constant) and human-ness is baked into the engine as
`HoldemTable.PlayerSeatIndex = 0` plus a per-seat `bool HoldemSeat.IsPlayer`. **A human in seat 2 has
no representation today.**

Note a trap for any shared-table work on Poker: `HoldemTable.cs:81` reads
`if (agents is not null && agents.Count != seats - 1)`, and `agents` defaults to null on both
constructors -- so a table can be built with no agents at all and the ctor throws nothing. It dies
later with `KeyNotFoundException` at `HoldemTable.cs:503`. The invariant is **not** ctor-enforced;
do not design against a guard that is not there.

## What does not exist yet, anywhere in the tree

All four tables are strictly single-session, server-authoritative request/response games keyed on the
SPT `MongoId sessionId`. There is no table identity, no join/leave for anyone but the owner, no push
or polling channel, and no locking around engine state. Greps for `TableId`/`RoomId`/`LobbyId`/
`GameId`/`matchId` return nothing.

The repo is not co-op-*unaware*, though: nine sites across the client plugins and docs already handle
the Fika case where "the player is not the one who decides when the raid starts -- they can be pulled
in from the lobby with the table still open". So it knows a Fika host exists; it just has no concept
of a second human **at a table**.
