using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace Blackjack.Server;

/// <summary>Asking what is open. Carries nothing.</summary>
public record TablesRequest : IRequestData;

/// <summary>What the lobby is handed.</summary>
public record TablesResponse
{
    public bool Ok { get; init; } = true;

    public IReadOnlyList<SharedBlackjackSummary> Tables { get; init; } = [];

    /// <summary>The table this player is already at, if any, so the lobby can say so.</summary>
    public string? YourTable { get; init; }
}

/// <summary>Standing up. Carries nothing -- there is only one table to leave.</summary>
public record LeaveTableRequest : IRequestData;

/// <summary>Starting the round. Carries nothing; the bets are already in the boxes.</summary>
public record SharedDealRequest : IRequestData;

/// <summary>
/// HTTP adapter for shared blackjack tables.
///
/// Beside <see cref="BlackjackCallbacks"/> rather than inside it, for the same reason
/// poker's is: a different set of routes over a different service, and mixing them would
/// make every method have to say which kind of table it meant. The single-player routes
/// keep working exactly as they did.
///
/// Holds no game logic. Everything worth testing lives one layer down, where it is
/// reachable without a running server.
/// </summary>
[Injectable]
public class SharedBlackjackCallbacks(
    HttpResponseUtil httpResponseUtil,
    SharedBlackjackService service,
    SharedBlackjackStore store,
    EventOutputHolder eventOutputHolder,
    BlackjackLog log)
{
    public ValueTask<string> Tables(TablesRequest info, MongoId sessionId)
    {
        log.Detail($"-> shared tables [{sessionId}]");

        return new ValueTask<string>(httpResponseUtil.NoBody(new TablesResponse
        {
            Tables = service.List(),
            YourTable = store.For(sessionId)?.Id,
        }));
    }

    public async ValueTask<string> Open(OpenTableRequest info, MongoId sessionId)
    {
        log.Info($"-> open shared blackjack [{sessionId}] -- {info.Seats} boxes, {info.Wallet}");

        return Respond(await service.OpenAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Join(JoinTableRequest info, MongoId sessionId)
    {
        log.Info($"-> join shared blackjack {info.TableId} [{sessionId}]");

        return Respond(await service.JoinAsync(info.TableId, sessionId));
    }

    public async ValueTask<string> Leave(LeaveTableRequest info, MongoId sessionId)
    {
        log.Info($"-> leave shared blackjack [{sessionId}]");

        return Respond(await service.LeaveAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Bet(DealRequest info, MongoId sessionId)
    {
        log.Detail($"-> bet {info.Wager} (shared) [{sessionId}]");

        return Respond(await service.BetAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Deal(SharedDealRequest info, MongoId sessionId)
    {
        log.Detail($"-> deal (shared) [{sessionId}]");

        return Respond(await service.DealAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Act(ActionRequest info, MongoId sessionId)
    {
        log.Detail($"-> act (shared) [{sessionId}] {info.Action}");

        return Respond(await service.ActAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> State(StateRequest info, MongoId sessionId)
    {
        log.Detail($"-> state (shared) [{sessionId}]");

        return Respond(await service.StateAsync(sessionId));
    }

    /// <summary>
    /// An output to hang item changes on, exactly as the single-player callbacks do.
    ///
    /// Bets and payouts move real currency, and a static route cannot update the running
    /// game's stash on its own -- so the response carries the change record and the client
    /// asks for it with a `BlackjackSync` item event.
    /// </summary>
    private ItemEventRouterResponse Output(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);

    private string Respond(BlackjackResponse response)
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
