using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Services;

namespace SlotMachine.Server;

/// <summary>
/// Reads what the player has, and moves it.
///
/// Ported from Poker's, which was ported from Blackjack's. It walks item stacks
/// directly rather than going through `PaymentService`, because both of that service's
/// entry points derive the currency from a trader and neither can settle anything
/// denominated in dollars or euros.
///
/// Three lessons arrive with it, each paid for once already:
///
/// - **`AddItemToStash` can decline an item without throwing.** A full stash silently
///   swallows a payout, so the balance is compared either side of every move against
///   what was intended and the shortfall posted as mail rather than lost.
/// - **The response must come from `EventOutputHolder.GetOutput`.** A hand-built
///   `ItemEventRouterResponse` initialises nothing and `RemoveItemByCount` reaches
///   straight into `output.ProfileChanges[sessionId]`, so it throws *after* the items
///   have already gone. On Blackjack that surfaced as "not enough roubles" while the
///   stake had left the stash.
/// - **A debit that half-succeeds is the worst case**, so it says how much may already
///   be missing rather than reporting a bare failure.
///
/// The one thing roulette does harder than its siblings is splitting a payout. A
/// straight-up win returns 36 times the stake, so a 1,000,000 chip on a number pays
/// 36,000,000 -- thirty-six maximum rouble stacks -- where a poker cash-out was
/// usually one or two.
/// </summary>
[Injectable]
public class Bank(
    InventoryHelper inventoryHelper,
    ItemHelper itemHelper,
    ProfileHelper profileHelper,
    MailSendService mailSendService,
    SlotLog log) : IBank
{
    /// <summary>
    /// How long a mailed payout waits to be collected. Long, because the message
    /// only exists when the stash was too full to take the winnings back -- expiring
    /// it would destroy the very payout this is rescuing.
    /// </summary>
    private const long MailStorageSeconds = 90L * 24 * 60 * 60;

    /// <summary>
    /// Total of every stack of this currency the profile holds. Counts money in
    /// containers as well as loose in the stash, which is what a player would call
    /// their balance.
    /// </summary>
    public int GetBalance(MongoId sessionId, Wallet wallet)
    {
        var pmcData = profileHelper.GetPmcProfile(sessionId);

        if (pmcData is null)
        {
            log.Error($"GetBalance: no PMC profile for session '{sessionId}'.");
            return 0;
        }

        return StacksOf(pmcData, WalletInfo.For(wallet).Tpl).Sum(item => item.GetItemStackSize());
    }

    /// <summary>
    /// Clamped to at least one. A limit of zero -- which a careless item mod can
    /// produce -- would make a payout's splitting loop take zero each pass and never
    /// terminate, hanging a server thread rather than failing.
    /// </summary>
    public int MaxStackSize(Wallet wallet)
    {
        var declared = itemHelper.GetItem(WalletInfo.For(wallet).Tpl).Value?.Properties?.StackMaxSize;

        if (declared is null)
        {
            return int.MaxValue;
        }

        if (declared < 1)
        {
            log.Error(
                $"{wallet} reports a maximum stack of {declared}, which cannot be honoured. "
                + "Treating it as 1 -- an item mod has set something impossible.");
            return 1;
        }

        return (int)declared;
    }

    /// <summary>
    /// Takes the stake when the wheel turns. Returns false without touching
    /// anything if the player is short -- the caller must not spin a wheel it
    /// cannot pay out on.
    /// </summary>
    public bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        var pmcData = profileHelper.GetPmcProfile(sessionId);

        if (pmcData is null)
        {
            log.Error($"TryDebit: no PMC profile for session '{sessionId}'.");
            return false;
        }

        if (amount <= 0)
        {
            return false;
        }

        var tpl = WalletInfo.For(wallet).Tpl;
        var before = GetBalance(sessionId, wallet);

        if (before < amount)
        {
            log.Detail($"debit refused: wanted {amount:N0} {wallet}, player has {before:N0}.");
            return false;
        }

        var remaining = amount;

        // Smallest stacks first, so the stash ends up with fewer loose piles rather
        // than more.
        var stacks = StacksOf(pmcData, tpl).OrderBy(item => item.GetItemStackSize()).ToList();
        log.Detail($"debit {amount:N0} {wallet} across {stacks.Count} stack(s), balance {before:N0}");

        foreach (var stack in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(remaining, stack.GetItemStackSize());

            try
            {
                inventoryHelper.RemoveItemByCount(pmcData, stack.Id, take, sessionId, output);
            }
            catch (Exception ex)
            {
                log.Error(
                    $"RemoveItemByCount threw taking {take:N0} from stack {stack.Id} of "
                    + $"{amount:N0} {wallet}.",
                    ex);

                PutBackWhatLeft(sessionId, wallet, before, output);
                return false;
            }

            remaining -= take;
        }

        var after = GetBalance(sessionId, wallet);
        log.Detail($"debit done: {wallet} {before:N0} -> {after:N0} (expected {before - amount:N0})");

        if (remaining != 0)
        {
            // The stacks ran out before the amount did, even though GetBalance said
            // there was enough. Whatever was taken has to go back for the same reason
            // as the catch above.
            log.Error(
                $"debit came up {remaining:N0} {wallet} short of {amount:N0} with no stacks left.");

            PutBackWhatLeft(sessionId, wallet, before, output);
            return false;
        }

        if (after != before - amount)
        {
            // The arithmetic disagreeing with the stash is the most valuable signal
            // there is: InventoryHelper did something other than what was asked, and
            // every balance shown from here on is suspect.
            log.Error($"debit mismatch: {wallet} is {after:N0} but should be {before - amount:N0}.");
        }

        return true;
    }


    /// <summary>
    /// Puts back whatever left the stash before a debit gave up.
    ///
    /// ## Why a failed debit cannot just return false
    ///
    /// A debit is a loop over stacks, and there is no transaction under it --
    /// `RemoveItemByCount` mutates the profile one stack at a time. So a debit that
    /// fails partway has already taken some of the money.
    ///
    /// **Every caller treats `false` as "nothing moved".** All four tables return
    /// before writing their escrow row, and Roulette and Slots explicitly release the
    /// row they had already written. So the money is gone from the stash with *nothing
    /// on disk that records it* -- the lazy refund on next contact finds no row, and
    /// the player has no route to recovery and no way to know they are owed anything.
    /// Measured during analysis at 20,000-40,000 per occurrence.
    ///
    /// Putting it back restores the contract the callers already assume, which is why
    /// this needs no change at any call site.
    ///
    /// ## Why it recomputes instead of trusting the loop
    ///
    /// `amount - remaining` is what the loop *believes* it took, and the throwing call
    /// is exactly the one whose outcome is unknown -- `RemoveItemByCount` may have
    /// decremented a stack before failing. Reading the balance back is ground truth and
    /// costs one call.
    ///
    /// ## What this does NOT cover
    ///
    /// If the put-back itself fails, the money really is gone and all this can do is say
    /// so loudly. `Credit` posts mail when the stash will not take the items, so the
    /// realistic failure is SPT itself being in a bad state -- by which point the log is
    /// the useful artefact.
    ///
    /// **Not covered by a test.** `Bank` takes concrete `InventoryHelper` and
    /// `ProfileHelper`, whose constructors need a real config server, so the failure
    /// cannot be injected without a running SPT. This path wants watching the first time
    /// a debit genuinely fails on a live server.
    /// </summary>
    private void PutBackWhatLeft(
        MongoId sessionId,
        Wallet wallet,
        int balanceBefore,
        ItemEventRouterResponse output)
    {
        var lost = balanceBefore - GetBalance(sessionId, wallet);

        if (lost <= 0)
        {
            return;
        }

        log.Error($"the debit failed partway -- putting {lost:N0} {wallet} back.");

        try
        {
            Credit(sessionId, wallet, lost, output);
        }
        catch (Exception ex)
        {
            log.Error(
                $"could not put back {lost:N0} {wallet} after a failed debit. That money has "
                + "left the player's stash and is not recorded anywhere.",
                ex);
        }
    }
    /// <summary>Pays the return back into the stash, respecting the stack limit.</summary>
    public void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output)
    {
        var pmcData = profileHelper.GetPmcProfile(sessionId);

        if (pmcData is null)
        {
            log.Error($"Credit: no PMC profile for session '{sessionId}' -- {amount:N0} {wallet} not paid.");
            return;
        }

        if (amount <= 0)
        {
            return;
        }

        var tpl = WalletInfo.For(wallet).Tpl;
        var before = GetBalance(sessionId, wallet);

        // One oversized stack would be rejected by the client, so the payout is split
        // before it is handed over. A straight-up win pays 36 to 1, so this splits far
        // more often here than it ever did at a poker table.
        var maxStack = MaxStackSize(wallet);
        var remaining = amount;
        var stacksMade = 0;

        log.Detail($"credit {amount:N0} {wallet} (max stack {maxStack:N0}), balance {before:N0}");

        while (remaining > 0)
        {
            var size = Math.Min(remaining, maxStack);

            try
            {
                inventoryHelper.AddItemToStash(
                    sessionId,
                    new AddItemDirectRequest
                    {
                        ItemWithModsToAdd =
                        [
                            new Item
                            {
                                Id = new MongoId(),
                                Template = tpl,
                                Upd = new Upd { StackObjectsCount = size },
                            },
                        ],
                        FoundInRaid = false,
                        UseSortingTable = true,
                    },
                    pmcData,
                    output);
            }
            catch (Exception ex)
            {
                // Losing a payout is the worst outcome available, so this is loud and
                // says exactly how much never made it.
                log.Error($"AddItemToStash threw paying {size:N0} {wallet}. {remaining:N0} unpaid. {ex.Message}");
                return;
            }

            remaining -= size;
            stacksMade++;
        }

        var after = GetBalance(sessionId, wallet);
        log.Detail($"credit done: {wallet} {before:N0} -> {after:N0} in {stacksMade} stack(s)");

        // AddItemToStash can decline to place an item **without throwing** -- a full
        // stash is the usual reason. Detecting that is not enough on its own: the
        // winnings would simply be gone. Whatever failed to land is posted instead.
        var shortfall = before + amount - after;

        if (shortfall > 0)
        {
            log.Error(
                $"credit shortfall: {wallet} is {after:N0} but should be {before + amount:N0}. "
                + $"Posting the missing {shortfall:N0} instead -- a full stash would explain this.");

            PayByMail(sessionId, wallet, shortfall);
        }
    }

    /// <summary>
    /// Last resort for a payout the stash would not take. Mail holds the items until
    /// the player makes room and SPT's own notification tells them it is waiting, so
    /// nothing is lost and nothing needs a popup of our own.
    /// </summary>
    private void PayByMail(MongoId sessionId, Wallet wallet, int amount)
    {
        var tpl = WalletInfo.For(wallet).Tpl;
        var maxStack = MaxStackSize(wallet);
        var items = new List<Item>();
        var remaining = amount;

        while (remaining > 0)
        {
            var size = Math.Min(remaining, maxStack);

            items.Add(new Item
            {
                Id = new MongoId(),
                Template = tpl,
                Upd = new Upd { StackObjectsCount = size },
            });

            remaining -= size;
        }

        try
        {
            mailSendService.SendSystemMessageToPlayer(
                sessionId,
                $"Your winnings would not fit in your stash. {amount:N0} {WalletInfo.For(wallet).Label} attached.",
                items,
                MailStorageSeconds,
                null);

            log.Info($"posted {amount:N0} {wallet} to the player -- collect it from messages.");
        }
        catch (Exception ex)
        {
            // Nothing left to fall back on, so this is the loudest line in the mod.
            log.Error($"could not post {amount:N0} {wallet}. THE PLAYER HAS LOST THIS PAYOUT. {ex.Message}");
        }
    }

    /// <summary>
    /// Every stack of this currency **that is in the stash** -- loose, or nested inside a
    /// container that is itself in the stash.
    ///
    /// Deliberately excludes pockets, the secure container, backpack and rig. Matching on
    /// template alone drew a stake from whichever stack turned up first, and
    /// `Inventory.Items` is in no particular order, so a bet could empty gear the player
    /// was about to carry into a raid. See <see cref="Casino.Server.StashScope"/>.
    /// </summary>
    private static IEnumerable<Item> StacksOf(PmcData pmcData, MongoId tpl) =>
        Casino.Server.StashScope.InStash(
            pmcData,
            pmcData.Inventory?.Items?.Where(item => item.Template == tpl) ?? []);
}
