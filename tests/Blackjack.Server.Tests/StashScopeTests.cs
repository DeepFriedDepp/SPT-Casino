using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Blackjack.Server.Tests;

/// <summary>
/// What the tables are allowed to spend.
///
/// ## The defect this was written against
///
/// Every table's `Bank.StacksOf` matched currency by **template alone**, across the whole
/// profile. A 500,000 rouble stack in the secure container carries the same template as
/// one in the stash, and `PmcData.Inventory.Items` is in no particular order -- so a bet
/// drew from whichever turned up first, which in practice meant equipment before the
/// stash.
///
/// A player about to run Labs with their roubles in a Gamma, placing a bet at the casino,
/// could have that Gamma emptied instead. The table has no business touching gear.
///
/// Backported from upstream `fd505e4`, which found and fixed it on the 4.1.x line. These
/// tests are ours: the fix arrived without any.
/// </summary>
public class StashScopeTests
{
    private static readonly MongoId Stash = new();
    private static readonly MongoId Equipment = new();
    private static readonly MongoId Roubles = new();

    /// <summary>Money put away is spendable. That is the whole point of the rule.</summary>
    [Fact]
    public void ALooseStackInTheStashIsInScope()
    {
        var loose = Item(Stash);
        var profile = Profile(loose);

        Assert.True(StashScope.IsInStash(profile, loose));
    }

    /// <summary>
    /// **The one that matters.** A stack in the secure container is not spendable, however
    /// much the template matches.
    /// </summary>
    [Fact]
    public void AStackInEquipmentIsNotInScope()
    {
        var carried = Item(Equipment);
        var profile = Profile(carried);

        Assert.False(StashScope.IsInStash(profile, carried));
    }

    /// <summary>
    /// Nested containers count, as long as the chain ends at the stash. Money in a money
    /// case in the stash is money in the stash, and a rule that refused it would tell a
    /// tidy player they are broke.
    /// </summary>
    [Fact]
    public void AStackInsideAContainerInTheStashIsInScope()
    {
        var moneyCase = Item(Stash);
        var inside = Item(moneyCase.Id);
        var profile = Profile(moneyCase, inside);

        Assert.True(StashScope.IsInStash(profile, inside));
    }

    /// <summary>And nesting does not launder equipment into scope.</summary>
    [Fact]
    public void AStackInsideAContainerInEquipmentIsNotInScope()
    {
        var rig = Item(Equipment);
        var inside = Item(rig.Id);
        var profile = Profile(rig, inside);

        Assert.False(StashScope.IsInStash(profile, inside));
    }

    /// <summary>
    /// A parent that is not in the profile stops the walk. A corrupt profile is not this
    /// method's problem, but it must not be allowed to make one look spendable.
    /// </summary>
    [Fact]
    public void ADanglingParentIsNotInScope()
    {
        var orphan = Item(new MongoId());
        var profile = Profile(orphan);

        Assert.False(StashScope.IsInStash(profile, orphan));
    }

    /// <summary>
    /// A cycle terminates rather than spinning forever holding the session gate.
    ///
    /// Asserted with a timeout because the failure mode is a hang, and a hung test tells
    /// nobody anything -- the same reason the concurrency tests do not use a Barrier.
    /// </summary>
    [Fact]
    public void ACycleTerminates()
    {
        var first = Item(Stash);
        var second = Item(first.Id);

        // Tie the knot: first's parent becomes second, so neither reaches the stash.
        first.ParentId = second.Id.ToString();

        var profile = Profile(first, second);
        var walked = Task.Run(() => StashScope.IsInStash(profile, first));

        Assert.True(walked.Wait(TimeSpan.FromSeconds(5)), "The parent walk did not terminate on a cycle.");
        Assert.False(walked.Result);
    }

    /// <summary>
    /// The sequence form agrees with the single form, item for item.
    ///
    /// <see cref="StashScope.InStash"/> exists only to build the parent index once instead
    /// of once per item, and an optimisation that quietly answers differently is worse
    /// than the cost it saves.
    /// </summary>
    [Fact]
    public void TheSequenceFormAgreesWithTheSingleForm()
    {
        var inStash = Item(Stash);
        var carried = Item(Equipment);
        var nested = Item(inStash.Id);
        var orphan = Item(new MongoId());

        var profile = Profile(inStash, carried, nested, orphan);
        var all = new[] { inStash, carried, nested, orphan };

        var bySequence = StashScope.InStash(profile, all).ToList();
        var bySingle = all.Where(item => StashScope.IsInStash(profile, item)).ToList();

        Assert.Equal(bySingle, bySequence);

        // And it is the answer we actually want, not merely a consistent one.
        Assert.Equal(new[] { inStash, nested }, bySequence);
    }

    /// <summary>A profile with no inventory answers no, rather than throwing.</summary>
    [Fact]
    public void AnEmptyProfileIsNotInScope()
    {
        var stray = Item(Stash);

        Assert.False(StashScope.IsInStash(new PmcData(), stray));
        Assert.Empty(StashScope.InStash(new PmcData(), [stray]));
    }

    private static Item Item(MongoId parent) =>
        new() { Id = new MongoId(), ParentId = parent.ToString(), Template = Roubles };

    private static PmcData Profile(params Item[] items) =>
        new()
        {
            Inventory = new BotBaseInventory
            {
                Stash = Stash,
                Equipment = Equipment,
                Items = [.. items],
            },
        };
}
