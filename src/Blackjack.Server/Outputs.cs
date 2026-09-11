using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;

namespace Blackjack.Server;

/// <summary>
/// <see cref="IOutputs"/> over SPT's own holder, which is where every callback in this mod
/// already gets a response from. Nothing here but the seam.
/// </summary>
[Injectable]
public class Outputs(EventOutputHolder eventOutputHolder) : IOutputs
{
    public ItemEventRouterResponse For(MongoId sessionId) => eventOutputHolder.GetOutput(sessionId);
}
