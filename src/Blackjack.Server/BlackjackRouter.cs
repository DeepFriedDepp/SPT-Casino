using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Blackjack.Server;

/// <summary>
/// Registers the mod's HTTP surface. Routes are plain static paths, so they can be
/// exercised with curl against a running server without the game client attached.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class BlackjackRouter(JsonUtil jsonUtil, BlackjackCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PingRequest>(
                "/blackjack/ping",
                async (url, info, sessionId, output) =>
                    await callbacks.Ping(info, sessionId)),

            new RouteAction<DealRequest>(
                "/blackjack/deal",
                async (url, info, sessionId, output) =>
                    await callbacks.Deal(info, sessionId)),

            new RouteAction<ActionRequest>(
                "/blackjack/action",
                async (url, info, sessionId, output) =>
                    await callbacks.Act(info, sessionId)),

            new RouteAction<StateRequest>(
                "/blackjack/state",
                async (url, info, sessionId, output) =>
                    await callbacks.State(info, sessionId)),

            new RouteAction<StatsRequest>(
                "/blackjack/stats",
                async (url, info, sessionId, output) =>
                    await callbacks.Stats(info, sessionId)),
        ])
{
}

/// <summary>
/// The shared-table half of the HTTP surface.
///
/// A second router rather than more routes on the first, because SPT registers each
/// <c>StaticRouter</c> independently and two of them keep the two services from having to
/// know about each other. The paths are namespaced under `/blackjack/shared/` so nothing
/// a player already has can collide with them.
///
/// **Actions come up here and not over the socket**, deliberately. A socket frame has no
/// <c>SessionGate</c> and no <c>ItemEventRouterResponse</c> behind it, so a bet sent that
/// way would be a bet that never reaches the stash. The socket only ever pushes the table
/// downward; every decision travels as an ordinary request.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class SharedBlackjackRouter(JsonUtil jsonUtil, SharedBlackjackCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<TablesRequest>(
                "/blackjack/shared/tables",
                async (url, info, sessionId, output) =>
                    await callbacks.Tables(info, sessionId)),

            new RouteAction<OpenTableRequest>(
                "/blackjack/shared/open",
                async (url, info, sessionId, output) =>
                    await callbacks.Open(info, sessionId)),

            new RouteAction<JoinTableRequest>(
                "/blackjack/shared/join",
                async (url, info, sessionId, output) =>
                    await callbacks.Join(info, sessionId)),

            new RouteAction<LeaveTableRequest>(
                "/blackjack/shared/leave",
                async (url, info, sessionId, output) =>
                    await callbacks.Leave(info, sessionId)),

            // Betting and dealing are two routes, unlike the solo table's one `deal`.
            // They have to be: at a shared table the money goes in the box first and
            // every box has to have had its chance before a card is turned.
            new RouteAction<DealRequest>(
                "/blackjack/shared/bet",
                async (url, info, sessionId, output) =>
                    await callbacks.Bet(info, sessionId)),

            new RouteAction<SharedDealRequest>(
                "/blackjack/shared/deal",
                async (url, info, sessionId, output) =>
                    await callbacks.Deal(info, sessionId)),

            new RouteAction<ActionRequest>(
                "/blackjack/shared/action",
                async (url, info, sessionId, output) =>
                    await callbacks.Act(info, sessionId)),

            new RouteAction<StateRequest>(
                "/blackjack/shared/state",
                async (url, info, sessionId, output) =>
                    await callbacks.State(info, sessionId)),
        ])
{
}
