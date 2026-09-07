using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Poker.Server;

/// <summary>
/// Registers the mod's HTTP surface.
///
/// Plain static paths, so the whole thing can be exercised with a script against a
/// running server and no game client attached -- which is the only way any of this
/// gets tested until the BepInEx plugin exists. See `scripts/smoke.ps1`.
///
/// A static route cannot update the client's own inventory model, so when money
/// starts moving these will be joined by item-event actions rather than replaced by
/// them. Two transports, one service.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class PokerRouter(JsonUtil jsonUtil, PokerCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PingRequest>(
                "/poker/ping",
                async (url, info, sessionId, output) =>
                    await callbacks.Ping(info, sessionId)),

            new RouteAction<SitRequest>(
                "/poker/sit",
                async (url, info, sessionId, output) =>
                    await callbacks.Sit(info, sessionId)),

            new RouteAction<DealRequest>(
                "/poker/deal",
                async (url, info, sessionId, output) =>
                    await callbacks.Deal(info, sessionId)),

            new RouteAction<ActRequest>(
                "/poker/act",
                async (url, info, sessionId, output) =>
                    await callbacks.Act(info, sessionId)),

            new RouteAction<StateRequest>(
                "/poker/state",
                async (url, info, sessionId, output) =>
                    await callbacks.State(info, sessionId)),

            new RouteAction<LeaveRequest>(
                "/poker/leave",
                async (url, info, sessionId, output) =>
                    await callbacks.Leave(info, sessionId)),
        ])
{
}

/// <summary>
/// The shared-table half of the HTTP surface.
///
/// A second router rather than more routes on the first, because SPT registers each
/// `StaticRouter` independently and two of them keep the two services from having to
/// know about each other. The paths are namespaced under `/poker/shared/` so nothing a
/// player already has can collide with them.
///
/// **Actions come up here and not over the socket**, deliberately. A socket frame has
/// no <c>SessionGate</c> and no <c>ItemEventRouterResponse</c> behind it, so a bet sent
/// that way would be a bet that never reaches the stash. The socket only ever pushes
/// the table downward; every decision travels as an ordinary request.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class SharedPokerRouter(JsonUtil jsonUtil, SharedPokerCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<TablesRequest>(
                "/poker/shared/tables",
                async (url, info, sessionId, output) =>
                    await callbacks.Tables(info, sessionId)),

            new RouteAction<SitRequest>(
                "/poker/shared/open",
                async (url, info, sessionId, output) =>
                    await callbacks.Open(info, sessionId)),

            new RouteAction<JoinRequest>(
                "/poker/shared/join",
                async (url, info, sessionId, output) =>
                    await callbacks.Join(info, sessionId)),

            new RouteAction<LeaveRequest>(
                "/poker/shared/leave",
                async (url, info, sessionId, output) =>
                    await callbacks.Leave(info, sessionId)),

            new RouteAction<DealRequest>(
                "/poker/shared/deal",
                async (url, info, sessionId, output) =>
                    await callbacks.Deal(info, sessionId)),

            new RouteAction<ActRequest>(
                "/poker/shared/act",
                async (url, info, sessionId, output) =>
                    await callbacks.Act(info, sessionId)),

            new RouteAction<StateRequest>(
                "/poker/shared/state",
                async (url, info, sessionId, output) =>
                    await callbacks.State(info, sessionId)),
        ])
{
}
