using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Casino.Server;

/// <summary>
/// Decides whether an inventory item is actually put away in the stash, as opposed to
/// worn or carried -- pockets, the secure container, backpack, rig, and anything else
/// hanging off <c>Inventory.Equipment</c> rather than <c>Inventory.Stash</c>.
///
/// Every table's Bank matches currency stacks by template alone, and a stack in the
/// player's pockets carries the same template as one sitting in the stash. Without this
/// check a debit draws from whichever it finds first -- which in practice meant equipment
/// before the stash, since `PmcData.Inventory.Items` lists items in no particular order.
/// **A table has no business emptying gear the player is carrying into a raid**; it may
/// only spend what has been put away.
///
/// Backported from upstream `fd505e4`. The rule and the parent-walk are theirs; what is
/// different here is <see cref="InStash"/>, because the predicate form builds an index of
/// the whole inventory once per item it is asked about. That is only ever called for
/// currency stacks -- the template test short-circuits ahead of it -- so it was not the
/// disaster it looks like, but a player with twenty rouble stacks and a full stash still
/// paid for twenty passes over every item they own on every balance check. Filtering a
/// sequence lets the index be built once.
/// </summary>
public static class StashScope
{
    /// <summary>
    /// The subset of <paramref name="candidates"/> that is in the stash.
    ///
    /// Prefer this to <see cref="IsInStash"/> whenever more than one item is being tested
    /// against the same profile: the parent lookup is built once here and once per call
    /// there.
    /// </summary>
    public static IEnumerable<Item> InStash(PmcData pmcData, IEnumerable<Item> candidates)
    {
        var stash = pmcData.Inventory?.Stash;
        var items = pmcData.Inventory?.Items;

        if (stash is null || items is null || candidates is null)
        {
            return [];
        }

        var stashId = stash.Value.ToString();
        var byId = Index(items);

        return candidates.Where(item => ResolvesToStash(item, stashId, byId));
    }

    /// <summary>
    /// Walks <paramref name="item"/>'s parent chain looking for the stash. True for a
    /// stack resting loose in it, or nested inside a container that is itself in the
    /// stash; false for one in equipment, or for a chain that runs out before reaching
    /// either root.
    /// </summary>
    public static bool IsInStash(PmcData pmcData, Item item)
    {
        var stash = pmcData.Inventory?.Stash;
        var items = pmcData.Inventory?.Items;

        if (stash is null || items is null || item is null)
        {
            return false;
        }

        return ResolvesToStash(item, stash.Value.ToString(), Index(items));
    }

    /// <summary>
    /// Id to item, built once per question rather than once per item asked about.
    ///
    /// Tolerant of a duplicate id rather than throwing: `ToDictionary` would, and a
    /// profile with two items sharing an id is a profile this method should still be able
    /// to answer about. First one wins, which is the same item either way for the purpose
    /// of walking to a parent.
    /// </summary>
    private static Dictionary<string, Item> Index(IEnumerable<Item> items)
    {
        var byId = new Dictionary<string, Item>();

        foreach (var item in items)
        {
            byId.TryAdd(item.Id.ToString(), item);
        }

        return byId;
    }

    private static bool ResolvesToStash(Item item, string stashId, Dictionary<string, Item> byId)
    {
        var current = item;
        var hops = 0;

        while (current.ParentId is not null)
        {
            if (current.ParentId == stashId)
            {
                return true;
            }

            // A cycle or a dangling reference stops here rather than spinning forever --
            // a corrupt profile is not this method's problem to solve.
            if (++hops > byId.Count || !byId.TryGetValue(current.ParentId, out var parent))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }
}
