using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace Farkle.Server;

/// <summary>
/// HTTP adapter. Serialises what <see cref="SharedFarkleService"/> decided and surfaces
/// anything worth seeing to the server console.
///
/// Deliberately holds no game logic -- everything worth testing lives one layer down,
/// where it is reachable without a running server.
/// </summary>
[Injectable]
public class FarkleCallbacks(
    HttpResponseUtil httpResponseUtil,
    SharedFarkleService service,
    SharedFarkleStore store,
    EventOutputHolder eventOutputHolder,
    FarkleLog log)
{
    public async ValueTask<string> Ping(PingRequest info, MongoId sessionId)
    {
        var response = await service.PingAsync(sessionId, Output(sessionId));

        log.Info($"ping from session '{response.SessionId}' -- profile {(response.HasProfile ? "found" : "NOT FOUND")}"
            + (response.HasProfile ? $", {response.Balance:N0} roubles" : string.Empty));

        if (!response.HasProfile)
        {
            log.Error("no profile for that session. If the id above is blank, the session cookie did not resolve.");
        }

        if (response.Note is not null)
        {
            log.Info(response.Note);
        }

        return httpResponseUtil.NoBody(response);
    }

    public ValueTask<string> Tables(TablesRequest info, MongoId sessionId)
    {
        log.Detail($"-> tables [{sessionId}]");

        return new ValueTask<string>(httpResponseUtil.NoBody(new TablesResponse
        {
            Tables = service.List(),
            YourTable = store.For(sessionId)?.Id,
        }));
    }

    public async ValueTask<string> Open(OpenTableRequest info, MongoId sessionId)
    {
        log.Info($"-> open [{sessionId}] -- {info.Stake:N0} roubles, {(info.VsBot ? $"vs bot '{info.Bot}'" : "for a friend")}");

        return Respond(await service.OpenAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Join(JoinTableRequest info, MongoId sessionId)
    {
        log.Info($"-> join {info.TableId} [{sessionId}]");

        return Respond(await service.JoinAsync(info.TableId, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Leave(LeaveTableRequest info, MongoId sessionId)
    {
        log.Info($"-> leave [{sessionId}]");

        return Respond(await service.LeaveAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Roll(RollRequest info, MongoId sessionId)
    {
        log.Detail($"-> roll [{sessionId}]");

        return Respond(await service.RollAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Keep(KeepRequest info, MongoId sessionId)
    {
        log.Detail($"-> keep [{string.Join(",", info.Indices)}] [{sessionId}]");

        return Respond(await service.KeepAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Bank(BankRequest info, MongoId sessionId)
    {
        log.Detail($"-> bank [{sessionId}]");

        return Respond(await service.BankAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> State(StateRequest info, MongoId sessionId)
    {
        log.Detail($"-> state [{sessionId}]");

        return Respond(await service.StateAsync(sessionId, Output(sessionId)));
    }

    /// <summary>
    /// The response the bank writes its change records into. **From `EventOutputHolder`,
    /// never from `new`.** A hand-built one initialises nothing and `RemoveItemByCount`
    /// reaches straight into `output.ProfileChanges[sessionId]`, so it throws after the
    /// items are already gone.
    /// </summary>
    private ItemEventRouterResponse Output(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);

    private string Respond(FarkleResponse response)
    {
        if (!response.Ok && response.Error is not null)
        {
            log.Detail($"<- refused: {response.Error}");
        }

        if (response.Note is not null)
        {
            log.Info(response.Note);
        }

        return httpResponseUtil.NoBody(response);
    }
}
