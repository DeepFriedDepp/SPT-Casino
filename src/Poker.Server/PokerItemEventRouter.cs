using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Routers;

namespace Poker.Server;

/// <summary>Action names the client sends. Namespaced so they cannot collide with EFT's own.</summary>
public static class PokerActions
{
    public const string Sit = "PokerSit";

    public const string Deal = "PokerDeal";

    public const string Act = "PokerAct";

    public const string Leave = "PokerLeave";

    /// <summary>
    /// Does nothing to the game. Exists so the client has something harmless to send
    /// when it needs the profile changes SPT has been holding for it -- see
    /// <see cref="PokerItemEventCallbacks.Sync"/>.
    /// </summary>
    public const string Sync = "PokerSync";

    /// <summary>
    /// Teaches SPT's one global body converter how to build these five. Without it
    /// every one of them throws while the request is still being deserialized, before
    /// the router is reached -- <see cref="Casino.Server.ItemEventActions"/> has the
    /// whole story. Called from <see cref="Startup"/>, and the payload-less ones are
    /// registered too: the converter rejects the *name*, not the shape.
    /// </summary>
    public static void Register()
    {
        Casino.Server.ItemEventActions.Register<PokerSitAction>(Sit);
        Casino.Server.ItemEventActions.Register<PokerDealAction>(Deal);
        Casino.Server.ItemEventActions.Register<PokerActAction>(Act);
        Casino.Server.ItemEventActions.Register<PokerLeaveAction>(Leave);
        Casino.Server.ItemEventActions.Register<PokerSyncAction>(Sync);
    }
}

/// <summary>
/// The transport the game client uses, and the reason a stash stays in step.
///
/// These arrive on the same endpoint EFT already uses for moving items, so the reply
/// carries the `ProfileChanges` the client applies to its own inventory. That is the
/// whole point: currency moved through a plain static route lands in the profile but
/// leaves the stash on screen stale until a reload, which reads to a player as the mod
/// eating their winnings.
///
/// The static routes in <see cref="PokerRouter"/> stay alongside this. They are how
/// the mod is exercised with a script and no game attached, and they discard the
/// change record because nothing is listening for it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader)]
public sealed class PokerItemEventRouter(PokerItemEventCallbacks callbacks)
    : ItemEventRouterDefinition
{
    /// <summary>
    /// 4.0.13 has no <c>ItemRouteAction&lt;T&gt;</c> to declare a payload type on, so
    /// the router dispatches on the action name itself. The body is already the right
    /// record by the time it arrives -- <see cref="PokerActions.Register"/> told SPT's
    /// converter how to build it -- so this is a cast, not a re-parse.
    /// </summary>
    private static T Typed<T>(BaseInteractionRequestData body)
        where T : BaseInteractionRequestData =>
        Casino.Server.ItemEventActions.Typed<T>(body);

    protected override IEnumerable<HandledRoute> GetHandledRoutes() =>
    [
        new HandledRoute(PokerActions.Sit, false),
        new HandledRoute(PokerActions.Deal, false),
        new HandledRoute(PokerActions.Act, false),
        new HandledRoute(PokerActions.Leave, false),
        new HandledRoute(PokerActions.Sync, false),
    ];

    protected override async ValueTask<ItemEventRouterResponse> HandleItemEventInternal(
        string url,
        PmcData pmcData,
        BaseInteractionRequestData body,
        MongoId sessionID,
        ItemEventRouterResponse output) =>
        body.Action switch
        {
            PokerActions.Sit => await callbacks.Sit(Typed<PokerSitAction>(body), sessionID, output),
            PokerActions.Deal => await callbacks.Deal(Typed<PokerDealAction>(body), sessionID, output),
            PokerActions.Act => await callbacks.Act(Typed<PokerActAction>(body), sessionID, output),
            PokerActions.Leave => await callbacks.Leave(Typed<PokerLeaveAction>(body), sessionID, output),
            _ => await callbacks.Sync(sessionID, output),
        };
}
