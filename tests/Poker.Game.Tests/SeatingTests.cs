namespace Poker.Game.Tests;

/// <summary>
/// People arriving at and leaving a table that is already running.
///
/// This is what a shared table needs and <see cref="HoldemTable.Reseat"/> is not: Reseat
/// buys a BROKE seat back in and refuses one that still has chips, because it exists for
/// a bust-out. Somebody joining a running table is the opposite -- the chair is occupied
/// and playing fine, and the point is to change who is in it.
/// </summary>
public class SeatingTests
{
    private static HoldemTable Table(int seats = 4, params int[] humans)
    {
        var people = humans.Length == 0 ? [0] : humans.ToList();
        var agents = Enumerable.Range(0, seats - people.Count)
            .Select(_ => (IPokerAgent)new Folds())
            .ToList();

        return new HoldemTable(seats: seats, agents: agents, humanSeats: people);
    }

    [Fact]
    public void APersonTakesOverABotsChair()
    {
        var table = Table(seats: 4, humans: 0);

        table.TakeSeat(2, chips: 5_000, name: "Friend");

        Assert.Equal([0, 2], table.HumanSeats);
        Assert.True(table.Seats[2].IsPlayer);
        Assert.Equal("Friend", table.Seats[2].Name);
        Assert.Equal(5_000, table.Seats[2].Stack);

        // Seats 1 and 3 are still bots and still have their agents -- proven by the
        // table being able to run a hand, which needs one per bot seat.
        table.StartHand();
        Assert.NotEqual(HoldemStreet.Idle, table.Street);
    }

    /// <summary>
    /// The agent goes when the person arrives.
    ///
    /// If it stayed, the seat would have both a person and a bot deciding for it, and
    /// whichever ran first would act on the other's behalf. That is a wrong bet with
    /// somebody's real money behind it, so it is worth a test of its own rather than
    /// being implied by the seat's flag.
    /// </summary>
    [Fact]
    public void TheBotStopsDecidingForASeatSomebodyIsSittingIn()
    {
        var table = Table(seats: 3, humans: 0);
        table.TakeSeat(1, chips: 5_000, name: "Friend");

        table.StartHand();

        // Only seat 2 is a bot now. The table must be waiting on a person -- if the
        // departed agent were still wired in it would have acted for seat 1 already.
        var stops = 0;
        while (table.ActorSeat is { } actor && stops++ < 20)
        {
            if (!table.Seats[actor].IsPlayer)
            {
                Assert.Fail($"seat {actor} acted as a bot but a person is sitting in it");
            }

            if (actor is 0 or 1)
            {
                table.Act(actor, new HoldemDecision(HoldemMove.Fold));
            }
        }

        Assert.True(stops > 0);
    }

    [Fact]
    public void NobodySitsDownInTheMiddleOfAHand()
    {
        var table = Table(seats: 4, humans: 0);
        table.StartHand();

        var refused = Assert.Throws<InvalidOperationException>(
            () => table.TakeSeat(2, chips: 5_000, name: "Friend"));

        Assert.Contains("middle of a hand", refused.Message);
    }

    [Fact]
    public void TwoPeopleCannotShareAChair()
    {
        var table = Table(seats: 4, humans: 0);
        table.TakeSeat(2, chips: 5_000, name: "Friend");

        Assert.Throws<InvalidOperationException>(
            () => table.TakeSeat(2, chips: 5_000, name: "Gatecrasher"));
    }

    [Fact]
    public void ASeatOffTheTableIsRefused()
    {
        var table = Table(seats: 4, humans: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.TakeSeat(9, 5_000, "Nobody"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => table.VacateSeat(9, new Folds(), "Bot", 5_000));
    }

    /// <summary>
    /// Standing up leaves the chair in play with a bot in it, rather than emptying it.
    ///
    /// A five-handed table that silently becomes four-handed when somebody leaves is a
    /// different game for everybody still sitting at it -- the blinds come round faster
    /// and the hand values move. Nobody asked for that when their friend logged off.
    /// </summary>
    [Fact]
    public void StandingUpLeavesABotInTheChair()
    {
        var table = Table(seats: 4, humans: 0);
        table.TakeSeat(2, chips: 5_000, name: "Friend");

        table.VacateSeat(2, new Folds(), "Bot", chips: 5_000);

        Assert.Equal([0], table.HumanSeats);
        Assert.False(table.Seats[2].IsPlayer);
        Assert.Equal(4, table.Seats.Count);

        // Still deals, so the replacement agent really is wired in.
        table.StartHand();
        Assert.NotEqual(HoldemStreet.Idle, table.Street);
    }

    /// <summary>
    /// The last person cannot stand up and leave a table of bots playing itself.
    ///
    /// Refused here rather than tolerated because the engine has no idea what should
    /// happen next -- closing the table is a decision about somebody's money, and it
    /// belongs to the caller that took the buy-in.
    /// </summary>
    [Fact]
    public void TheLastPersonCannotLeaveABotsOnlyTableBehind()
    {
        var table = Table(seats: 4, humans: 0);

        var refused = Assert.Throws<InvalidOperationException>(
            () => table.VacateSeat(0, new Folds(), "Bot", 5_000));

        Assert.Contains("last person", refused.Message);
    }

    /// <summary>
    /// Somebody who has just sat down sees their own cards and nobody else's.
    ///
    /// The privacy rule is tested thoroughly elsewhere; what this pins is that ARRIVING
    /// through TakeSeat gets you the same treatment as being dealt in at construction.
    /// A join that quietly left the newcomer outside the per-viewer filter would be the
    /// easiest way to reintroduce the bug the filter exists for.
    /// </summary>
    [Fact]
    public void SomebodyWhoJustSatDownSeesOnlyTheirOwnCards()
    {
        var table = Table(seats: 4, humans: 0);
        table.TakeSeat(2, chips: 5_000, name: "Friend");
        table.StartHand();

        var mine = System.Text.Json.JsonSerializer.Serialize(HoldemView.From(table, 2));
        var theirs = table.Seats[0].Cards.Select(card => card.Code).ToList();

        Assert.NotEmpty(theirs);

        foreach (var code in theirs)
        {
            Assert.DoesNotContain($"\"{code}\"", mine);
        }

        foreach (var code in table.Seats[2].Cards.Select(card => card.Code))
        {
            Assert.Contains($"\"{code}\"", mine);
        }
    }

    private sealed class Folds : IPokerAgent
    {
        public HoldemDecision Decide(PokerContext context) => new(HoldemMove.Fold);

        public void HandEnded(HandOutcome outcome)
        {
        }
    }
}
