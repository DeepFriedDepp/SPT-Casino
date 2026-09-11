using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace Farkle.Server;

/// <summary>
/// Registers the table's HTTP surface. One route so far, on `/farkle/*` like the
/// other four tables own `/blackjack/*`, `/poker/*`, `/roulette/*` and `/slots/*`.
///
/// Plain static paths, so the whole thing can be exercised with a script against a
/// running server and no game client attached.
///
/// No item-event router yet. That exists on the other tables so the running game's
/// stash can be told money moved; until decision #2 says Farkle moves money there is
/// nothing to tell it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public class FarkleRouter(JsonUtil jsonUtil, FarkleCallbacks callbacks)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PingRequest>(
                "/farkle/ping",
                async (url, info, sessionId, output) =>
                    await callbacks.Ping(info, sessionId)),
        ]);
