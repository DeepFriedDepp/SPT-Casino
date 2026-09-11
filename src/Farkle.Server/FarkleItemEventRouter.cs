using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;

namespace Farkle.Server;

/// <summary>Action names the client sends. Namespaced so they cannot collide with EFT's own.</summary>
public static class FarkleActions
{
    /// <summary>
    /// Does nothing to the game. Exists so the client has something harmless to send when
    /// it needs the profile changes SPT has been holding for it.
    /// </summary>
    public const string Sync = "FarkleSync";

    /// <summary>
    /// Teaches SPT's one global body converter how to build this. It carries nothing and
    /// still has to be registered: on 4.0.13 the converter rejects an unrecognised action
    /// NAME while deserialising, before any router is reached. See
    /// <see cref="Casino.Server.ItemEventActions"/>. Called from <see cref="Startup"/>.
    /// </summary>
    public static void Register() => Casino.Server.ItemEventActions.Register<FarkleSyncAction>(Sync);
}

/// <summary>
/// The one action this table puts on EFT's own item-event endpoint.
///
/// Static routes cannot update the running game's inventory: currency moved through one
/// lands in the profile and leaves the stash on screen stale until a reload, which reads
/// to a player as the mod eating their money. An item-event reply carries the
/// `ProfileChanges` the client applies to its own inventory, so the client sends this
/// when it wants them -- after sitting down, after leaving, after the match settles.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public sealed class FarkleItemEventRouter(FarkleItemEventCallbacks callbacks) : ItemEventRouterDefinition
{
    protected override IEnumerable<HandledRoute> GetHandledRoutes() => [new HandledRoute(FarkleActions.Sync, false)];

    protected override async ValueTask<ItemEventRouterResponse> HandleItemEventInternal(
        string url,
        PmcData pmcData,
        BaseInteractionRequestData body,
        MongoId sessionID,
        ItemEventRouterResponse output) =>
        await callbacks.Sync(sessionID, output);
}

/// <summary>
/// Answers the sync action. The body is not the point and the client ignores it; what
/// matters is that this is an item-event response at all. It is also a ping, so it gives
/// back a stake stranded by a server that died mid-match, and it has an output to hang
/// that on.
/// </summary>
[Injectable]
public class FarkleItemEventCallbacks(SharedFarkleService service, FarkleLog log)
{
    public async Task<ItemEventRouterResponse> Sync(MongoId sessionId, ItemEventRouterResponse output)
    {
        log.Detail($"sync (item event) [{sessionId}]");

        var response = await service.PingAsync(sessionId, output);

        if (response.Note is not null)
        {
            log.Info(response.Note);
        }

        return output;
    }
}
