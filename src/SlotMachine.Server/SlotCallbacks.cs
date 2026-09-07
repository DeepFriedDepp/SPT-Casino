using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace SlotMachine.Server;

/// <summary>
/// HTTP adapter. Serialises what <see cref="SlotService"/> decided and surfaces
/// anything worth seeing to the server console.
///
/// Deliberately holds no game logic -- everything worth testing lives one layer down,
/// where it is reachable without a running server.
/// </summary>
[Injectable]
public class SlotCallbacks(
    HttpResponseUtil httpResponseUtil,
    SlotService service,
    EventOutputHolder eventOutputHolder,
    SlotLog log)
{
    public async ValueTask<string> Ping(PingRequest info, MongoId sessionId)
    {
        var response = await service.PingAsync(sessionId, Output(sessionId));

        log.Info(
            $"ping from session '{response.SessionId}' -- profile {(response.HasProfile ? "found" : "NOT FOUND")}"
            + (response.HasProfile
                ? $", {string.Join(", ", response.Balances.Select(b => $"{b.Key} {b.Value:N0}"))}"
                : string.Empty));

        if (!response.HasProfile)
        {
            log.Error("no profile for that session. If the id above is blank, the session cookie did not resolve.");
        }

        return httpResponseUtil.NoBody(response);
    }

    public async ValueTask<string> Pull(PullRequest info, MongoId sessionId)
    {
        log.Detail($"pull [{sessionId}] -- {info.Stake:N0} {info.Wallet}");

        var response = await service.PullAsync(info, sessionId, Output(sessionId));

        if (!response.Ok)
        {
            log.Info($"refused: {response.Error}");
        }

        return httpResponseUtil.NoBody(response);
    }

    /// <summary>
    /// The lifetime record. Unlike Ping and Pull this comes back as the stats object
    /// itself rather than wrapped in a response, because nothing about it can fail
    /// in a way the player needs telling about.
    ///
    /// The serialisation below is exactly the walk that used to throw: it happens out
    /// here, after the session's gate has been given up. What it is handed is a copy
    /// taken under that gate rather than the live record. See
    /// <see cref="PlayerStats.Snapshot"/>.
    /// </summary>
    public async ValueTask<string> Stats(StatsRequest info, MongoId sessionId)
    {
        log.Detail($"stats [{sessionId}]");

        return httpResponseUtil.NoBody(await service.Stats(sessionId));
    }

    /// <summary>
    /// The response the bank writes its change records into.
    ///
    /// **From `EventOutputHolder`, never from `new`.** A hand-built one initialises
    /// nothing and `RemoveItemByCount` reaches straight into
    /// `output.ProfileChanges[sessionId]`, so it throws after the items are already
    /// gone. On Blackjack that surfaced as "not enough roubles" while the stake had
    /// left the stash.
    /// </summary>
    private ItemEventRouterResponse Output(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);
}
