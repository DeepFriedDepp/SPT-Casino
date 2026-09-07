using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server;

/// <summary>Action names the client sends. Namespaced so they cannot collide with EFT's own.</summary>
public static class BlackjackActions
{
    public const string Deal = "BlackjackDeal";

    public const string Play = "BlackjackPlay";

    /// <summary>
    /// Does nothing. Exists so the client has something harmless to send when it
    /// needs the profile changes SPT has been holding for it -- see
    /// <see cref="BlackjackItemEventCallbacks.Sync"/>.
    /// </summary>
    public const string Sync = "BlackjackSync";

    /// <summary>
    /// Teaches SPT's one global body converter how to build these three. Without it
    /// every one of them throws while the request is still being deserialized, before
    /// the router is reached -- <see cref="Casino.Server.ItemEventActions"/> has the
    /// whole story. Called from <see cref="Startup"/>, and <c>Sync</c> is registered
    /// too even though it carries nothing: the converter rejects the *name*.
    /// </summary>
    public static void Register()
    {
        Casino.Server.ItemEventActions.Register<BlackjackDealAction>(Deal);
        Casino.Server.ItemEventActions.Register<BlackjackPlayAction>(Play);
        Casino.Server.ItemEventActions.Register<BlackjackSyncAction>(Sync);
    }
}

/// <summary>
/// The transport the game client uses.
///
/// These arrive on the same endpoint EFT already uses for moving items, so the reply
/// carries the ProfileChanges the client applies to its own inventory. That is the
/// whole point: money moved through a plain static route lands in the profile but
/// leaves the client's stash view stale until it reloads.
///
/// The static routes in <see cref="BlackjackRouter"/> stay alongside this. They are
/// how the mod is tested with curl and no game attached, and they discard the change
/// record because nothing is listening for it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public sealed class BlackjackItemEventRouter(BlackjackItemEventCallbacks callbacks)
    : ItemEventRouterDefinition
{
    /// <summary>
    /// 4.0.13 has no <c>ItemRouteAction&lt;T&gt;</c> to declare a payload type on, so
    /// the router dispatches on the action name itself. The body is already the right
    /// record by the time it arrives -- <see cref="BlackjackActions.Register"/> told
    /// SPT's converter how to build it -- so this is a cast, not a re-parse.
    /// </summary>
    private static T Typed<T>(BaseInteractionRequestData body)
        where T : BaseInteractionRequestData =>
        Casino.Server.ItemEventActions.Typed<T>(body);

    protected override IEnumerable<HandledRoute> GetHandledRoutes() =>
    [
        new HandledRoute(BlackjackActions.Deal, false),
        new HandledRoute(BlackjackActions.Play, false),
        new HandledRoute(BlackjackActions.Sync, false),
    ];

    protected override async ValueTask<ItemEventRouterResponse> HandleItemEventInternal(
        string url,
        PmcData pmcData,
        BaseInteractionRequestData body,
        MongoId sessionID,
        ItemEventRouterResponse output) =>
        body.Action switch
        {
            BlackjackActions.Deal =>
                await callbacks.Deal(Typed<BlackjackDealAction>(body), sessionID, output),
            BlackjackActions.Play =>
                await callbacks.Play(Typed<BlackjackPlayAction>(body), sessionID, output),
            _ => callbacks.Sync(sessionID, output),
        };
}
