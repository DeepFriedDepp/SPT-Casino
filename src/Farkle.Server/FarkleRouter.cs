using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Farkle.Server;

/// <summary>
/// Registers the table's HTTP surface on `/farkle/*`, like the other four tables own
/// `/blackjack/*`, `/poker/*`, `/roulette/*` and `/slots/*`.
///
/// Plain static paths, so the whole thing can be exercised with a script against a
/// running server and no game client attached. **Every action is a request.** The
/// socket only pushes downward -- see `Casino.Server.CasinoSocket`.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class FarkleRouter(JsonUtil jsonUtil, FarkleCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PingRequest>("/farkle/ping", async (url, info, sessionId, output) => await callbacks.Ping(info, sessionId)),
            new RouteAction<TablesRequest>("/farkle/tables", async (url, info, sessionId, output) => await callbacks.Tables(info, sessionId)),
            new RouteAction<OpenTableRequest>("/farkle/open", async (url, info, sessionId, output) => await callbacks.Open(info, sessionId)),
            new RouteAction<JoinTableRequest>("/farkle/join", async (url, info, sessionId, output) => await callbacks.Join(info, sessionId)),
            new RouteAction<LeaveTableRequest>("/farkle/leave", async (url, info, sessionId, output) => await callbacks.Leave(info, sessionId)),
            new RouteAction<RollRequest>("/farkle/roll", async (url, info, sessionId, output) => await callbacks.Roll(info, sessionId)),
            new RouteAction<KeepRequest>("/farkle/keep", async (url, info, sessionId, output) => await callbacks.Keep(info, sessionId)),
            new RouteAction<BankRequest>("/farkle/bank", async (url, info, sessionId, output) => await callbacks.Bank(info, sessionId)),
            new RouteAction<StateRequest>("/farkle/state", async (url, info, sessionId, output) => await callbacks.State(info, sessionId)),
        ]);
