using Blackjack.Game;

namespace Blackjack.Tests;

/// <summary>
/// Several people at one table: their own hands and their own wagers, against one
/// dealer hand out of one shoe.
///
/// Every table here is built on a stacked shoe, so the deal order is the assertion
/// as much as the outcome is. Cards go out the way a dealer puts them out -- one to
/// each box in turn, the dealer's upcard, a second to each box, then the hole card
/// -- which is why the sequences below read seat 0, seat 1, dealer, seat 0, seat 1,
/// dealer.
/// </summary>
public class SharedTableTests
{
    private const int Wager = 10_000;

    /// <summary>
    /// A table whose every box has somebody standing at it, dealing a known
    /// sequence. Occupying them all up front keeps the tests about the round rather
    /// than about seating.
    /// </summary>
    private static BlackjackTable Shared(string cards, int seats = 2, Rules? rules = null) =>
        new(rules ?? new Rules(),
            Shoe.Stacked(cards.Split(' ').Select(Card.Parse)),
            seats,
            Enumerable.Range(0, seats).ToList());

    [Fact]
    public void BothSeatsAreDealtAndBothSettleAgainstOneDealerHand()
    {
        // Seat 0 K/9 = 19, seat 1 9/8 = 17, dealer 7/K = 17 and must stand.
        var table = Shared("KS 9C 7D 9H 8D KH");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        table.StartRound();

        table.Stand(0);
        var view = table.Stand(1);

        Assert.Equal(RoundPhase.Settled, view.Phase);
        Assert.True(view.Seats[0].IsInRound);
        Assert.True(view.Seats[1].IsInRound);

        // One dealer hand, and it is the only one either seat could have played
        // against -- 17 is what beats seat 1 to a push and loses to seat 0. Separate
        // dealers could not produce both results from the same two cards.
        Assert.Equal(["7D", "KH"], view.Dealer.Cards);
        Assert.Equal(17, view.Dealer.Value);
        Assert.Equal(HandOutcome.Win, view.Seats[0].Hands[0].Outcome);
        Assert.Equal(HandOutcome.Push, view.Seats[1].Hands[0].Outcome);
    }

    [Fact]
    public void TheShoeIsSharedSoOneSeatsCardsAreGoneForTheOther()
    {
        var table = Shared("KS 9C 7D 9H 8D KH 2S 3S 4S 5S");
        var before = table.ViewTable().ShoeRemaining;

        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        var view = table.StartRound();

        // Seat 0 holds the first and fourth cards, not the first and second: the
        // second went to seat 1. Its own shoe would have dealt it KS and 9C.
        Assert.Equal(["KS", "9H"], view.Seats[0].Hands[0].Cards);
        Assert.Equal(["9C", "8D"], view.Seats[1].Hands[0].Cards);

        // Two boxes and a dealer is six cards out of the one shoe, not four.
        Assert.Equal(6, before - view.ShoeRemaining);

        var solo = Shared("KS 9C 7D 9H 8D KH 2S 3S 4S 5S", seats: 1);
        var soloBefore = solo.ViewTable().ShoeRemaining;
        solo.PlaceBet(0, Wager);

        Assert.Equal(4, soloBefore - solo.StartRound().ShoeRemaining);
    }

    [Fact]
    public void ASeatThatDidNotBetIsDealtNothingAndSettlesNothing()
    {
        // Three boxes, the middle one quiet. Seat 0 K/9 = 19, seat 2 9/8 = 17,
        // dealer 7/K = 17. The stack carries two spare cards so that a table which
        // wrongly dealt to all three would run out of assertions before it ran out
        // of shoe -- a "Shoe exhausted" is not proof of anything this test claims.
        var table = Shared("KS 9C 7D 9H 8D KH 2S 3S", seats: 3);
        var before = table.ViewTable().ShoeRemaining;

        table.PlaceBet(0, Wager);
        table.PlaceBet(2, Wager);
        var dealt = table.StartRound();

        // Dealt nothing, and it took nothing from the shoe: six cards for two boxes
        // and a dealer, not eight.
        Assert.Empty(dealt.Seats[1].Hands);
        Assert.False(dealt.Seats[1].IsInRound);
        Assert.Equal(6, before - dealt.ShoeRemaining);

        // The turn order skips it too -- seat 2 follows seat 0 directly.
        Assert.Equal(0, dealt.ActiveSeat);
        Assert.Equal(2, table.Stand(0).ActiveSeat);

        var view = table.Stand(2);
        Assert.Equal(RoundPhase.Settled, view.Phase);

        // Settles nothing: an empty stake and an empty return, while the two who did
        // bet played a perfectly ordinary round against the one dealer hand.
        Assert.Equal(0, view.Seats[1].TotalWagered);
        Assert.Equal(0, view.Seats[1].TotalReturned);
        Assert.Equal(HandOutcome.Win, view.Seats[0].Hands[0].Outcome);
        Assert.Equal(HandOutcome.Push, view.Seats[2].Hands[0].Outcome);
    }

    [Fact]
    public void TurnOrderRunsSeatZeroThenSeatOneThenTheDealer()
    {
        var table = Shared("KS 9C 7D 9H 8D KH");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        var dealt = table.StartRound();

        Assert.Equal(0, dealt.ActiveSeat);
        Assert.True(table.IsTurnFor(0));
        Assert.False(table.IsTurnFor(1));

        // Seat 1 cannot act out of turn, and is not offered anything to act with.
        Assert.Empty(table.AvailableActions(1));
        var early = Assert.Throws<InvalidOperationException>(() => table.Hit(1));
        Assert.Contains("seat 0's turn", early.Message);

        var afterFirst = table.Stand(0);
        Assert.Equal(1, afterFirst.ActiveSeat);
        Assert.NotEmpty(table.AvailableActions(1));

        // Seat 0 is finished and cannot come back for a second go.
        Assert.Empty(table.AvailableActions(0));
        Assert.Throws<InvalidOperationException>(() => table.Hit(0));

        var afterSecond = table.Stand(1);

        // The dealer plays once, after the last seat, and the round is over.
        Assert.Null(afterSecond.ActiveSeat);
        Assert.Equal(RoundPhase.Settled, afterSecond.Phase);
        Assert.Equal(2, afterSecond.Dealer.Cards.Count);
    }

    [Fact]
    public void ASplitAtSeatZeroDoesNotDisturbSeatOne()
    {
        // Seat 0 8/8, seat 1 9/8 = 17, dealer 7/K = 17. The split draws a three and
        // a two out of the shared shoe.
        var table = Shared("8S 9C 7D 8D 8H KH 3C 2S");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        table.StartRound();

        var split = table.Split(0);

        // The two extra cards came out of the shoe, not out of seat 1's hand.
        Assert.Equal(2, split.Seats[0].Hands.Count);
        Assert.Single(split.Seats[1].Hands);
        Assert.Equal(["9C", "8H"], split.Seats[1].Hands[0].Cards);

        // The turn stays with seat 0 until both of its hands are done.
        Assert.Equal(0, split.ActiveSeat);
        Assert.Throws<InvalidOperationException>(() => table.Stand(1));

        var afterFirstHand = table.Stand(0);
        Assert.Equal(0, afterFirstHand.ActiveSeat);
        Assert.Equal(1, afterFirstHand.Seats[0].ActiveHandIndex);

        var afterSecondHand = table.Stand(0);
        Assert.Equal(1, afterSecondHand.ActiveSeat);

        var view = table.Stand(1);

        // Seat 0 lost both halves of its split; seat 1 pushed its 17 regardless.
        Assert.Equal(HandOutcome.Lose, view.Seats[0].Hands[0].Outcome);
        Assert.Equal(HandOutcome.Lose, view.Seats[0].Hands[1].Outcome);
        Assert.Equal(HandOutcome.Push, view.Seats[1].Hands[0].Outcome);
        Assert.Equal(Wager, view.Seats[1].TotalReturned);
    }

    [Fact]
    public void EachSeatsMoneyIsItsOwnAndNeitherSeatMovesTheOthers()
    {
        // Seat 0 K/9 = 19, seat 1 5/6 = 11 which it doubles into a two for 13.
        // Dealer 7/K = 17: seat 0 wins, seat 1 loses twice as much as it first put up.
        var table = Shared("KS 5C 7D 9H 6D KH 2H");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, 25_000);
        table.StartRound();

        var afterSeatZero = table.Stand(0);
        Assert.Equal(Wager, afterSeatZero.Seats[0].TotalWagered);
        Assert.Equal(25_000, afterSeatZero.Seats[1].TotalWagered);

        // Seat 1 doubling puts 25,000 more of its own money up. Seat 0 has already
        // stood and must not have moved by a rouble.
        var afterDouble = table.Double(1);
        Assert.Equal(Wager, afterDouble.Seats[0].TotalWagered);
        Assert.Equal(50_000, afterDouble.Seats[1].TotalWagered);

        var view = afterDouble;
        Assert.Equal(RoundPhase.Settled, view.Phase);

        Assert.Equal(Wager, view.Seats[0].TotalWagered);
        Assert.Equal(20_000, view.Seats[0].TotalReturned);
        Assert.Equal(10_000, view.Seats[0].Net);

        Assert.Equal(50_000, view.Seats[1].TotalWagered);
        Assert.Equal(0, view.Seats[1].TotalReturned);
        Assert.Equal(-50_000, view.Seats[1].Net);
    }

    [Fact]
    public void EachSeatsNaturalPaysAtTheRateItsOwnStakeEarned()
    {
        // Both naturals, dealer 7/8 = 15 and never draws because nothing live is
        // left to beat. Seat 0 staked roubles at 3:2, seat 1 something indivisible
        // at even money -- see Rules.BlackjackPayout.
        var table = Shared("AS AH 7D KS KH 8C");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager, blackjackPayout: 1.0);
        var view = table.StartRound();

        Assert.Equal(RoundPhase.Settled, view.Phase);
        Assert.Equal(HandOutcome.Blackjack, view.Seats[0].Hands[0].Outcome);
        Assert.Equal(HandOutcome.Blackjack, view.Seats[1].Hands[0].Outcome);

        Assert.Equal(25_000, view.Seats[0].TotalReturned);
        Assert.Equal(20_000, view.Seats[1].TotalReturned);
    }

    [Fact]
    public void SeatsAreTakenAndGivenUpBetweenRoundsOnly()
    {
        var table = Shared("KS 9C 7D 9H 8D KH KD 8C 9S 9D", seats: 3);

        // Between rounds, sitting down works.
        table.VacateSeat(1);
        table.VacateSeat(2);
        table.TakeSeat(1, "Bob");
        Assert.True(table.Seats[1].IsOccupied);
        Assert.Equal("Bob", table.Seats[1].Name);

        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        table.StartRound();

        // Mid-round, neither is allowed: a new arrival would be dealt into a round
        // they had not paid into, and a departure would abandon a live hand.
        var arriving = Assert.Throws<InvalidOperationException>(() => table.TakeSeat(2, "Cara"));
        Assert.Contains("middle of a round", arriving.Message);
        Assert.Throws<InvalidOperationException>(() => table.VacateSeat(1));

        table.Stand(0);
        table.Stand(1);

        // Settled, so standing up works again and takes the box out of play.
        table.VacateSeat(1);
        Assert.False(table.Seats[1].IsOccupied);
        Assert.Empty(table.Seats[1].Hands);
        Assert.Throws<InvalidOperationException>(() => table.PlaceBet(1, Wager));

        // The next round skips the empty box entirely: seat 0 K/9 = 19 against
        // dealer 8/9 = 17, four cards, and seat 1 is dealt none of them.
        table.PlaceBet(0, Wager);
        table.StartRound();
        var view = table.Stand(0);

        Assert.Empty(view.Seats[1].Hands);
        Assert.Equal(HandOutcome.Win, view.Seats[0].Hands[0].Outcome);

        // The last person cannot stand up; the caller closes the table instead.
        var last = Assert.Throws<InvalidOperationException>(() => table.VacateSeat(0));
        Assert.Contains("last person", last.Message);
    }

    [Fact]
    public void TheOneSeatShorthandsRefuseToGuessOnceSomebodyElseSitsDown()
    {
        // The single-player API is about "the player". With two of them it would have
        // to pick one, and picking one means hitting somebody else's hand or paying
        // somebody else's winnings.
        var table = Shared("KS 9C 7D 9H 8D KH");

        Assert.Throws<InvalidOperationException>(() => table.Deal(Wager));
        Assert.Throws<InvalidOperationException>(() => table.View());
        Assert.Throws<InvalidOperationException>(() => table.Hit());
        Assert.Throws<InvalidOperationException>(() => { _ = table.TotalWagered; });
        Assert.Throws<InvalidOperationException>(() => { _ = table.TotalReturned; });
        Assert.Throws<InvalidOperationException>(() => { _ = table.ActiveHandIndex; });

        // The shared API answers all of it, and Phase never refuses -- a caller has
        // to be able to ask what the table is doing before it knows who is at it.
        Assert.Equal(RoundPhase.AwaitingBet, table.Phase);
        Assert.Equal(2, table.ViewTable().Seats.Count);
    }

    [Fact]
    public void AOneSeatTableStillDealsExactlyAsItAlwaysDid()
    {
        // The seat-addressed calls and the shorthands are the same round on a table
        // with nobody else at it, down to the card order.
        var shorthand = Shared("KS KH 9D 7C", seats: 1);
        var shorthandView = shorthand.Deal(Wager);

        var addressed = Shared("KS KH 9D 7C", seats: 1);
        addressed.PlaceBet(0, Wager);
        addressed.StartRound();

        Assert.Equal(shorthandView.PlayerHands[0].Cards, addressed.ViewTable().Seats[0].Hands[0].Cards);

        var stood = shorthand.Stand();
        var sharedStood = addressed.Stand(0);

        Assert.Equal(HandOutcome.Win, stood.PlayerHands[0].Outcome);
        Assert.Equal(stood.TotalReturned, sharedStood.Seats[0].TotalReturned);
        Assert.Equal(stood.Dealer.Cards, sharedStood.Dealer.Cards);
    }

    [Fact]
    public void DealingWithNobodyBettingIsRefused()
    {
        var table = Shared("KS 9C 7D 9H 8D KH");

        var nothing = Assert.Throws<InvalidOperationException>(() => table.StartRound());
        Assert.Contains("Nobody has bet", nothing.Message);

        // A bet does not carry over, so a seat that says nothing next round sits that
        // one out rather than being staked again without asking.
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        table.StartRound();
        table.Stand(0);
        var settled = table.Stand(1);

        Assert.Equal(0, settled.Seats[0].PendingBet);
        Assert.Equal(0, settled.Seats[1].PendingBet);
        Assert.Throws<InvalidOperationException>(() => table.StartRound());
    }

    [Fact]
    public void TheHoleCardIsHiddenFromEverySeatAndRevealedToEverySeatAtOnce()
    {
        // The only concealed card in blackjack, hidden from both people equally --
        // which is why there is one view for the table and not one per person.
        var table = Shared("KS 9C 7D 9H 8D KH");
        table.PlaceBet(0, Wager);
        table.PlaceBet(1, Wager);
        var dealt = table.StartRound();

        Assert.Equal(["7D"], dealt.Dealer.Cards);

        table.Stand(0);
        var settled = table.Stand(1);

        Assert.Equal(["7D", "KH"], settled.Dealer.Cards);

        // Everything else is face up the whole way through: both seats' cards are in
        // the same snapshot, unfiltered, from the moment they are dealt.
        Assert.Equal(2, dealt.Seats[0].Hands[0].Cards.Count);
        Assert.Equal(2, dealt.Seats[1].Hands[0].Cards.Count);
    }
}
