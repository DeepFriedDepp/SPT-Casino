using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace Poker.Server;

/// <summary>Which table to sit down at.</summary>
public record JoinRequest : IRequestData
{
    public string Table { get; set; } = string.Empty;
}

/// <summary>Asking what is open. Carries nothing.</summary>
public record TablesRequest : IRequestData;

/// <summary>What the lobby is handed.</summary>
public record TablesResponse
{
    public bool Ok { get; init; } = true;

    public IReadOnlyList<SharedTableSummary> Tables { get; init; } = [];

    /// <summary>The table this player is already at, if any, so the lobby can say so.</summary>
    public string? YourTable { get; init; }
}

/// <summary>
/// HTTP adapter for shared tables.
///
/// Beside <see cref="PokerCallbacks"/> rather than inside it, because these are a
/// different set of routes over a different service and mixing them would make every
/// method have to say which kind of table it meant. The single-player routes keep
/// working exactly as they did.
///
/// Holds no game logic, same as its neighbour: everything worth testing lives one layer
/// down where it is reachable without a running server.
/// </summary>
[Injectable]
public class SharedPokerCallbacks(
    HttpResponseUtil httpResponseUtil,
    SharedPokerService service,
    SharedTableStore store,
    EventOutputHolder eventOutputHolder,
    PokerLog log)
{
    public ValueTask<string> Tables(TablesRequest info, MongoId sessionId)
    {
        log.Detail($"-> tables [{sessionId}]");

        return new ValueTask<string>(httpResponseUtil.NoBody(new TablesResponse
        {
            Tables = service.List(),
            YourTable = store.For(sessionId)?.Id,
        }));
    }

    public async ValueTask<string> Open(SitRequest info, MongoId sessionId)
    {
        log.Info(
            $"-> open shared table [{sessionId}] -- {info.Seats} seats, "
            + $"{info.BuyIn} {info.Wallet}, blind {info.BigBlind}");

        return Respond(await service.CreateAsync(info, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Join(JoinRequest info, MongoId sessionId)
    {
        log.Info($"-> join {info.Table} [{sessionId}]");

        return Respond(await service.JoinAsync(info.Table, sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Leave(LeaveRequest info, MongoId sessionId)
    {
        log.Info($"-> leave shared table [{sessionId}]");

        return Respond(await service.LeaveAsync(sessionId, Output(sessionId)));
    }

    public async ValueTask<string> Deal(DealRequest info, MongoId sessionId)
    {
        log.Detail($"-> deal (shared) [{sessionId}]");

        return Respond(await service.DealAsync(sessionId));
    }

    public async ValueTask<string> Act(ActRequest info, MongoId sessionId)
    {
        log.Detail($"-> act (shared) [{sessionId}] {info.Move}");

        return Respond(await service.ActAsync(info, sessionId));
    }

    public async ValueTask<string> State(StateRequest info, MongoId sessionId)
    {
        log.Detail($"-> state (shared) [{sessionId}]");

        return Respond(await service.StateAsync(sessionId));
    }

    /// <summary>
    /// An output to hang item changes on, exactly as the single-player callbacks do.
    ///
    /// The buy-in and the cash-out move real currency, and a static route cannot update
    /// the running game's stash on its own -- so the response carries the change record
    /// and the client asks for it with a `PokerSync` item event.
    /// </summary>
    private ItemEventRouterResponse Output(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);

    private string Respond(PokerResponse response)
    {
        if (!response.Ok && response.Error is not null)
        {
            log.Detail($"<- refused: {response.Error}");
        }

        return httpResponseUtil.NoBody(response);
    }
}
