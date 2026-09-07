using System.Text.Json;

namespace Poker.Game.Tests;

/// <summary>
/// Two people at one table: where they sit, who is allowed to see what, and whose
/// turn it is.
///
/// The privacy tests here are the ones that matter. Everything else in this file is
/// arithmetic that a compiler error would have caught eventually; a view that carries
/// somebody else's hole cards is silent, correct-looking, and worth money to whoever
/// reads the network traffic.
/// </summary>
public class SharedTableTests
{
    private sealed class Bot(Func<PokerContext, HoldemDecision> decide) : IPokerAgent
    {
        public HoldemDecision Decide(PokerContext context) => decide(context);
    }

    private static HoldemRules Blinds => new() { SmallBlind = 25, BigBlind = 50, BuyIn = 5_000 };

    private static IPokerAgent Passive => new Bot(context =>
        context.Options.Moves.Contains(HoldemMove.Check) ? HoldemDecision.Check : HoldemDecision.Call);

    /// <summary>Four seats with people in 0 and 2, so a bot sits between them and after them.</summary>
    private static HoldemTable Shared(params IPokerAgent[] agents) =>
        new(Blinds, seats: 4, rng: new Random(5), agents: agents, humanSeats: [0, 2]);

    /// <summary>What one seat is holding, in the form the view puts on the wire.</summary>
    private static List<string> Codes(HoldemTable table, int seat) =>
        table.Seats[seat].Cards.Select(card => card.Code).ToList();

    [Fact]
    public void PeopleSitWhereTheyAreToldAndEveryOtherChairGetsABot()
    {
        var table = Shared(Passive, Passive);

        Assert.Equal(new[] { 0, 2 }, table.HumanSeats);
        Assert.True(table.Seats[0].IsPlayer);
        Assert.False(table.Seats[1].IsPlayer);
        Assert.True(table.Seats[2].IsPlayer);
        Assert.False(table.Seats[3].IsPlayer);
    }

    [Fact]
    public void ATableNobodyChoseTheSeatsForStillPutsThePersonInSeatZero()
    {
        // The default is the whole compatibility story: every caller written before a
        // table could seat two people passes nothing, and has to get what it always
        // got.
        var table = new HoldemTable(Blinds, seats: 3, rng: new Random(5), agents: [Passive, Passive]);

        Assert.Equal(new[] { HoldemTable.PlayerSeatIndex }, table.HumanSeats);
        Assert.Same(table.Seats[0], table.Player);
        Assert.False(table.Seats[1].IsPlayer);
        Assert.False(table.Seats[2].IsPlayer);
    }

    [Fact]
    public void TheAgentCountIsCheckedAgainstTheChairsNobodyIsSittingIn()
    {
        // Two people at a four-seat table leaves two bots. One short, one over and none
        // at all are the same mistake, and none of them may reach a deal.
        Assert.Throws<ArgumentException>(() =>
            new HoldemTable(Blinds, seats: 4, agents: [Passive], humanSeats: [0, 2]));

        Assert.Throws<ArgumentException>(() =>
            new HoldemTable(Blinds, seats: 4, agents: [Passive, Passive, Passive], humanSeats: [0, 2]));

        // No agents at all used to be tolerated: the table dealt, posted the blinds and
        // then died with a KeyNotFoundException the first time a bot was asked.
        Assert.Throws<ArgumentException>(() => new HoldemTable(Blinds, seats: 4, humanSeats: [0, 2]));

        var table = new HoldemTable(Blinds, seats: 4, agents: [Passive, Passive], humanSeats: [0, 2]);
        Assert.Equal(4, table.Seats.Count);

        // And it bottoms out honestly: two people heads-up need no agents at all.
        var headsUp = new HoldemTable(Blinds, seats: 2, humanSeats: [0, 1]);
        Assert.All(headsUp.Seats, seat => Assert.True(seat.IsPlayer));
    }

    [Fact]
    public void AHumanSeatHasToBeARealSeatAndOnlyOnePersonMayClaimIt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HoldemTable(Blinds, seats: 3, agents: [Passive, Passive], humanSeats: [3]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HoldemTable(Blinds, seats: 3, agents: [Passive, Passive], humanSeats: [-1]));

        // A duplicate would otherwise read as two people and leave the table one agent
        // short of a seat, which is a KeyNotFoundException three streets later.
        Assert.Throws<ArgumentException>(() =>
            new HoldemTable(Blinds, seats: 3, agents: [Passive], humanSeats: [1, 1]));

        // Enough agents for every chair, and still refused: a table with nobody at it
        // would deal hands forever and never wait for anyone.
        Assert.Throws<ArgumentException>(() =>
            new HoldemTable(Blinds, seats: 3, agents: [Passive, Passive, Passive], humanSeats: []));
    }

    [Fact]
    public void NamesFollowTheBotsAndThePeopleRatherThanTheSeatNumbers()
    {
        var table = new HoldemTable(
            Blinds,
            seats: 4,
            agents: [Passive, Passive],
            names: ["Kilo", "Lima"],
            humanSeats: [0, 2],
            humanNames: new Dictionary<int, string> { [2] = "Nikita" });

        Assert.Equal("You", table.Seats[0].Name);
        Assert.Equal("Kilo", table.Seats[1].Name);
        Assert.Equal("Nikita", table.Seats[2].Name);

        // The second name goes to the second *bot*, which is seat 3. Indexed by seat it
        // would have run off the end of the list and this chair would be "Seat 3".
        Assert.Equal("Lima", table.Seats[3].Name);
    }

    [Fact]
    public void AViewNeverCarriesAnybodyElsesHoleCards()
    {
        var table = Shared(Passive, Passive);
        table.StartHand();

        var mine = Codes(table, 0);
        var theirs = Codes(table, 2);

        var view = HoldemView.From(table, 0);

        Assert.Equal(mine, view.Seats[0].Cards);
        Assert.Empty(view.Seats[2].Cards);
        Assert.Equal(0, view.ViewerSeat);

        // Asserted against the serialised form, because that is what the integration
        // step sends. A card that is "hidden" but still somewhere in the object -- on
        // another property, in a name, in something added later -- is a card the client
        // has, and a structural assertion on Seats[2].Cards would never see it.
        //
        // Matched with the quotes on, so a code like "2C" cannot collide with a run of
        // digits that happens to be followed by a C: a card only ever reaches the wire
        // as a whole JSON string.
        var json = JsonSerializer.Serialize(view);

        foreach (var code in theirs)
        {
            Assert.DoesNotContain($"\"{code}\"", json);
        }

        foreach (var code in mine)
        {
            Assert.Contains($"\"{code}\"", json);
        }

        // The other direction is the half that catches a view built for "the human
        // seat" rather than for a viewer: with one person seated the two are the same
        // view, and every single-player test passes either way.
        var opposite = JsonSerializer.Serialize(HoldemView.From(table, 2));

        foreach (var code in mine)
        {
            Assert.DoesNotContain($"\"{code}\"", opposite);
        }

        foreach (var code in theirs)
        {
            Assert.Contains($"\"{code}\"", opposite);
        }
    }

    [Fact]
    public void AViewOnlyOffersItsOwnViewerAMove()
    {
        // Somebody else's options are not merely useless to a client: MaxRaiseTo is
        // their stack, and AwaitingPlayer would have both clients drawing a live
        // prompt for one seat's turn.
        var table = Shared(Passive, Passive);
        table.StartHand();

        Assert.True(table.IsTurnFor(0));

        var mine = HoldemView.From(table, 0);
        var theirs = HoldemView.From(table, 2);

        Assert.True(mine.AwaitingPlayer);
        Assert.NotNull(mine.Options);

        Assert.False(theirs.AwaitingPlayer);
        Assert.Null(theirs.Options);

        // Both are still told whose turn it is -- that is public at a real table.
        Assert.Equal(0, mine.ActorSeat);
        Assert.Equal(0, theirs.ActorSeat);
    }

    [Fact]
    public void AShowdownShowsEveryLiveHandToEverybody()
    {
        var table = Shared(Passive, Passive);
        table.StartHand();

        while (table.ActorSeat is int actor)
        {
            var options = table.Options();
            table.Act(actor, options.Moves.Contains(HoldemMove.Check) ? HoldemDecision.Check : HoldemDecision.Call);
        }

        Assert.Equal(HoldemStreet.Showdown, table.Street);
        Assert.All(table.Seats, seat => Assert.NotNull(seat.Hand));

        // Nobody folded, so every hand was shown -- to both people, including each
        // other's. Privacy ends where the showdown begins, and a per-viewer view that
        // forgot that would leave two people unable to see why they lost.
        foreach (var viewer in new[] { 0, 2 })
        {
            var view = HoldemView.From(table, viewer);

            foreach (var seat in table.Seats)
            {
                Assert.Equal(Codes(table, seat.Index), view.Seats[seat.Index].Cards);
            }
        }
    }

    [Fact]
    public void TheTurnRunsThroughThePeopleAndTheBotsInOneOrder()
    {
        // Four seats, people at 0 and 2. The button starts on 0, so the blinds are
        // seats 1 and 2 and the seat after the big blind opens: bot 3, person 0, bot 1,
        // person 2.
        //
        // Recorded from both sides -- each bot writes down the seat it was asked about,
        // the test writes down each person's -- so the assertion is on one rotation
        // rather than on the people alone. A table that asked its people in the right
        // order while letting a bot act out of turn would pass a people-only test, and
        // recording the seat off the context rather than off a captured number pins the
        // agents to the chairs they were meant for.
        var order = new List<int>();

        IPokerAgent Watcher() => new Bot(context =>
        {
            order.Add(context.Seat.Index);

            return context.Options.Moves.Contains(HoldemMove.Check) ? HoldemDecision.Check : HoldemDecision.Call;
        });

        var table = Shared(Watcher(), Watcher());
        table.StartHand();

        while (table.ActorSeat is int actor && order.Count < 4)
        {
            order.Add(actor);

            var options = table.Options();
            table.Act(actor, options.Moves.Contains(HoldemMove.Check) ? HoldemDecision.Check : HoldemDecision.Call);
        }

        Assert.Equal(new[] { 3, 0, 1, 2 }, order.Take(4));
    }

    [Fact]
    public void ASeatCannotActWhenItIsSomebodyElsesTurn()
    {
        // The stale-action case, and the reason Act takes a seat at all. Two clients
        // means two messages in flight, and the one that arrives after the table has
        // moved on has to be refused rather than applied to whoever is up now.
        var table = Shared(Passive, Passive);
        table.StartHand();

        Assert.True(table.IsTurnFor(0));
        Assert.False(table.IsTurnFor(2));

        Assert.Throws<InvalidOperationException>(() => table.Act(2, HoldemDecision.Fold));
        Assert.False(table.Seats[2].Folded);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Act(9, HoldemDecision.Fold));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsTurnFor(9));
    }

    [Fact]
    public void TheOneHumanShorthandsRefuseATableWithTwoPeopleAtIt()
    {
        // Loudly, rather than answering about seat 0. Every one of these is asked about
        // somebody's chips or somebody's cards, and a plausible wrong answer is how one
        // person gets paid the other's stack -- or shown the other's hand.
        var table = Shared(Passive, Passive);
        table.StartHand();

        Assert.Throws<InvalidOperationException>(() => _ = table.Player);
        Assert.Throws<InvalidOperationException>(() => table.Act(HoldemDecision.Call));
        Assert.Throws<InvalidOperationException>(() => HoldemView.Of(table));

        // The seat-aware forms answer instead, and the hand goes on.
        table.Act(0, HoldemDecision.Call);
        Assert.Equal(50, table.Seats[0].CommittedThisStreet);
    }

    [Fact]
    public void TheOneHumanTableSeesExactlyWhatItSawBefore()
    {
        var table = new HoldemTable(Blinds, seats: 3, rng: new Random(5), agents: [Passive, Passive]);
        table.StartHand();

        // Compared as JSON rather than as records: the view holds lists, and record
        // equality compares those by reference, so two separately built snapshots of
        // one table are never equal however identical their contents. The wire form is
        // the thing that has to match anyway.
        Assert.Equal(
            JsonSerializer.Serialize(HoldemView.From(table, HoldemTable.PlayerSeatIndex)),
            JsonSerializer.Serialize(HoldemView.Of(table)));

        Assert.Equal(table.AwaitingPlayer, table.IsTurnFor(HoldemTable.PlayerSeatIndex));
    }
}
