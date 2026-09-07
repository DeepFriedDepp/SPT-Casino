using Blackjack.Game;
using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// One player, one blackjack table.
///
/// ## The defect these were written against
///
/// **There is one escrow row per session, and shared tables gave a session two places
/// to owe money from.** `escrow-blackjack.json` is keyed by session and holds a single
/// <see cref="OutstandingStake"/>; that was correct while a player could only ever be at
/// their own table, and stopped being correct the moment they could also be at somebody
/// else's.
///
/// What it costs is not a miscount. `RefundAbandonedStake` runs on **every** private deal
/// and on every private State -- which is to say, on opening the panel -- and its test for
/// "is this stake orphaned" is whether the PRIVATE table has a round in progress. A stake
/// belonging to a shared table fails that test, so it is handed back as an abandoned one:
/// the player is credited money they have not lost, the row is dropped, and the shared
/// round then settles and pays them again. Money out of nothing, reachable by opening the
/// solo table while sitting at a shared one.
///
/// So the rule is the one the game already implies -- **you are at one table** -- and it is
/// enforced from both sides, because either one alone leaves the other order open.
/// </summary>
public class SharedTableIsolationTests
{
    private static readonly Rules House = new();

    /// <summary>
    /// The mint, in the order a player would stumble into it: sit at a shared table, put a
    /// bet in the box, then open the private table.
    ///
    /// Fails without the guard with a credit of 50,000 the player never lost.
    /// </summary>
    [Fact]
    public async Task OpeningThePrivateTableDoesNotRefundASharedTablesBet()
    {
        var kit = new Kit();
        var session = new MongoId();

        await kit.SitAndBet(session, 50_000);

        var creditsBefore = kit.Bank.Credits.Count;
        var balanceBefore = kit.Bank.GetBalance(session, Wallet.Roubles);

        var state = await kit.Solo.State(session, new ItemEventRouterResponse());

        // The money assertions come first deliberately. `Ok` being false is how the guard
        // happens to be implemented; the balance not moving is the thing that matters, and
        // asserting it first means the failure message names the mint rather than the flag.
        Assert.Equal(balanceBefore, kit.Bank.GetBalance(session, Wallet.Roubles));
        Assert.Equal(creditsBefore, kit.Bank.Credits.Count);

        // And the shared table still knows it is owed. A refund that took the row would
        // leave the round to settle against nothing.
        Assert.Equal(50_000, kit.Escrow.Get(session)?.Amount);

        Assert.False(state.Ok);
    }

    /// <summary>
    /// The same defect through the deal route rather than the state route. Worth its own
    /// test because the two call the refund from different places and only one of them
    /// would be noticed by a player.
    /// </summary>
    [Fact]
    public async Task DealingPrivatelyIsRefusedWhileSeatedAtASharedTable()
    {
        var kit = new Kit();
        var session = new MongoId();

        await kit.SitAndBet(session, 50_000);

        var creditsBefore = kit.Bank.Credits.Count;
        var debitsBefore = kit.Bank.Debits.Count;

        var deal = await kit.Solo.DealAsync(
            new DealRequest { Wallet = nameof(Wallet.Roubles), Wager = 10_000 },
            session,
            new ItemEventRouterResponse());

        Assert.False(deal.Ok);
        Assert.Contains("shared table", deal.Error);
        Assert.Equal(creditsBefore, kit.Bank.Credits.Count);
        Assert.Equal(debitsBefore, kit.Bank.Debits.Count);
        Assert.Equal(50_000, kit.Escrow.Get(session)?.Amount);
    }

    /// <summary>
    /// The other order: a private round already has money in escrow, and the player tries
    /// to sit down at a shared table.
    ///
    /// Left open, this is the same collision from the other side -- the shared table's
    /// settlement releases the row, and the private round's stake stops being recoverable.
    /// </summary>
    [Fact]
    public async Task SittingAtASharedTableIsRefusedWhileAPrivateRoundIsUnsettled()
    {
        var kit = new Kit();
        var session = new MongoId();

        // A private round that has taken money and not given it back yet.
        kit.Tables.Seed(session, new BlackjackTable(House, new Random(7)));
        await kit.Solo.DealAsync(
            new DealRequest { Wallet = nameof(Wallet.Roubles), Wager = 10_000 },
            session,
            new ItemEventRouterResponse());

        // A round that settled on the deal -- a natural, or a dealer blackjack -- owes
        // nothing and must not block anybody, so only run the assertion when it did not.
        if (kit.Escrow.Get(session) is null)
        {
            return;
        }

        var open = await kit.Shared.OpenAsync(
            new OpenTableRequest { Seats = 4, Wallet = nameof(Wallet.Roubles) },
            session,
            new ItemEventRouterResponse());

        Assert.False(open.Ok);
        Assert.Contains("finish", open.Error, StringComparison.OrdinalIgnoreCase);

        // Refused before anything was claimed, so they are not stranded.
        Assert.Null(kit.SharedStore.For(session));
    }

    /// <summary>
    /// The guard must not fire for the ordinary player, who has never seen a shared table.
    /// A rule that also refuses the common case is not a fix.
    /// </summary>
    [Fact]
    public async Task APlayerWhoIsNotAtASharedTableDealsNormally()
    {
        var kit = new Kit();
        var session = new MongoId();

        kit.Tables.Seed(session, new BlackjackTable(House, new Random(11)));

        var deal = await kit.Solo.DealAsync(
            new DealRequest { Wallet = nameof(Wallet.Roubles), Wager = 10_000 },
            session,
            new ItemEventRouterResponse());

        Assert.True(deal.Ok, deal.Error);
        Assert.Equal(10_000, deal.Round!.TotalWagered);
    }

    /// <summary>
    /// And once they stand up, the private table is theirs again. A guard that latched
    /// would lock a player out of the solo game for the rest of the server's life.
    /// </summary>
    [Fact]
    public async Task LeavingASharedTableGivesThePrivateTableBack()
    {
        var kit = new Kit();
        var session = new MongoId();

        await kit.SitAndBet(session, 50_000);
        var left = await kit.Shared.LeaveAsync(session, new ItemEventRouterResponse());

        Assert.True(left.Ok, left.Error);

        var deal = await kit.Solo.DealAsync(
            new DealRequest { Wallet = nameof(Wallet.Roubles), Wager = 10_000 },
            session,
            new ItemEventRouterResponse());

        Assert.True(deal.Ok, deal.Error);
    }

    /// <summary>
    /// Both services over one bank, one escrow and one pair of gates -- which is the whole
    /// point: in the server they are two singletons sharing exactly these.
    /// </summary>
    private sealed class Kit
    {
        internal FakeBank Bank { get; } = new();

        internal FakeEscrow Escrow { get; } = new();

        internal FakeProfiles Profiles { get; } = new();

        internal TableStore Tables { get; } = new();

        internal SharedBlackjackStore SharedStore { get; } = new();

        private SessionGate Sessions { get; } = new();

        private TableGate TableGates { get; } = new();

        internal BlackjackService Solo => new(
            Bank, Sessions, Profiles, Tables, new FakeStats(), Escrow, SharedStore);

        internal SharedBlackjackService Shared => new(
            Bank,
            TableGates,
            Sessions,
            SharedStore,
            Profiles,
            Escrow,
            Tables,
            new CasinoSocket(new QuietLogger<CasinoSocket>()));

        /// <summary>Opens a shared table and puts a bet in the box, which is what takes money.</summary>
        internal async Task SitAndBet(MongoId session, int wager)
        {
            var open = await Shared.OpenAsync(
                new OpenTableRequest { Seats = 4, Wallet = nameof(Wallet.Roubles) },
                session,
                new ItemEventRouterResponse());

            Assert.True(open.Ok, open.Error);

            var bet = await Shared.BetAsync(
                new DealRequest { Wallet = nameof(Wallet.Roubles), Wager = wager },
                session,
                new ItemEventRouterResponse());

            Assert.True(bet.Ok, bet.Error);
        }
    }
}
