namespace Farkle.Game.Tests;

/// <summary>
/// The rules of a match, one at a time. Dice are forced through a stacked
/// <see cref="Random"/> so every test says which faces came up.
/// </summary>
public class MatchTests
{
    private static FarkleMatch Seated(FarkleRules? rules = null, IGameLog? log = null)
    {
        var match = new FarkleMatch(rules, log);
        match.Sit(0, "Alice");
        match.Sit(1, "Bob");
        return match;
    }

    // ---- seating ---------------------------------------------------------------------

    [Fact]
    public void NothingHappensUntilBothChairsAreTaken()
    {
        var match = new FarkleMatch();
        match.Sit(0, "Alice");

        Assert.Equal(Phase.WaitingForOpponent, match.Phase);
        Assert.Throws<InvalidOperationException>(() => match.RollDice(new Random(1)));

        match.Sit(1, "Bob", isBot: true);

        Assert.Equal(Phase.Rolling, match.Phase);
        Assert.Equal(0, match.CurrentSeat);
        Assert.True(match.Seats[1].IsBot);
        Assert.Equal(1, match.TurnNumber);
    }

    [Fact]
    public void AChairCannotBeTakenTwice()
    {
        var match = new FarkleMatch();
        match.Sit(0, "Alice");

        Assert.Throws<InvalidOperationException>(() => match.Sit(0, "Bob"));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.Sit(2, "Carol"));
    }

    [Fact]
    public void NobodySitsDownOnceTheMatchHasStarted()
    {
        var match = Seated();

        Assert.Throws<InvalidOperationException>(() => match.Sit(1, "Carol"));
    }

    // ---- the turn --------------------------------------------------------------------

    [Fact]
    public void AScoringRollWaitsToBeChosenFrom()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 2, 3, 4, 6, 6));

        Assert.Equal(Phase.Choosing, match.Phase);
        Assert.Equal([1, 2, 3, 4, 6, 6], match.Roll);
        Assert.Single(match.LegalKeeps);
        Assert.Equal([0], match.LegalKeeps[0].Indices);
        Assert.Throws<InvalidOperationException>(() => match.RollDice(new Random(1)));
        Assert.Throws<InvalidOperationException>(() => match.Bank());
    }

    [Fact]
    public void AFarkleEndsTheTurnWithNothing()
    {
        var match = Seated();
        match.RollDice(Stacked(2, 2, 3, 3, 4, 6));

        Assert.Equal(1, match.CurrentSeat);
        Assert.Equal(Phase.Rolling, match.Phase);
        Assert.Equal(0, match.Seats[0].Score);
        Assert.Equal(6, match.DiceInHand);
        Assert.Contains(match.LastTurn, e => e.Kind == TurnEventKind.Farkled && e.Seat == 0);
    }

    [Fact]
    public void KeepingAddsToTheTurnAndTakesDiceOutOfHand()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 5, 2, 3, 4, 6));

        var scored = match.KeepDice([0, 1]);

        Assert.Equal(150, scored.Points);
        Assert.Equal(150, match.TurnScore);
        Assert.Equal(4, match.DiceInHand);
        Assert.Equal([1, 5], match.SetAside);
        Assert.Equal(Phase.Rolling, match.Phase);
        Assert.Empty(match.Roll);
    }

    [Fact]
    public void ADeadDieCannotBeSetAside()
    {
        var match = Seated();
        match.RollDice(Stacked(2, 2, 2, 4, 5, 6));

        var ex = Assert.Throws<InvalidOperationException>(() => match.KeepDice([0, 1, 2, 3]));
        Assert.Contains("scores nothing", ex.Message);

        // The table is untouched by a refused keep.
        Assert.Equal(Phase.Choosing, match.Phase);
        Assert.Equal(0, match.TurnScore);
        Assert.Equal(6, match.DiceInHand);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 6 })]
    [InlineData(new[] { -1 })]
    public void ANonsenseKeepIsRefused(int[] indices)
    {
        var match = Seated();
        match.RollDice(Stacked(1, 1, 1, 5, 5, 5));

        Assert.Throws<InvalidOperationException>(() => match.KeepDice(indices));
    }

    [Fact]
    public void HotDiceGiveAllSixBack()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 1, 1, 5, 5, 5));
        match.KeepDice([0, 1, 2, 3, 4, 5]);

        Assert.Equal(2500, match.TurnScore);
        Assert.Equal(6, match.DiceInHand);
        Assert.Equal(Phase.Rolling, match.Phase);
        Assert.Contains(match.Turn, e => e.Kind == TurnEventKind.HotDice);
    }

    // ---- banking and the threshold ---------------------------------------------------

    [Fact]
    public void NothingUnderTheThresholdCanBeBankedBeforeTheBoard()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 5, 2, 3, 4, 6));
        match.KeepDice([0, 1]);

        Assert.False(match.CanBank);
        var ex = Assert.Throws<InvalidOperationException>(match.Bank);
        Assert.Contains("500", ex.Message);
        Assert.Equal(Phase.Rolling, match.Phase);
    }

    [Fact]
    public void TheThresholdOnceClearedIsClearedForGood()
    {
        var match = Seated();

        // Alice: 600 in one turn, on the board.
        match.RollDice(Stacked(6, 6, 6, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        Assert.True(match.CanBank);
        match.Bank();

        Assert.True(match.Seats[0].OnBoard);
        Assert.Equal(600, match.Seats[0].Score);
        Assert.Equal(1, match.CurrentSeat);

        // Bob farkles straight away.
        match.RollDice(Stacked(2, 2, 3, 3, 4, 6));
        Assert.Equal(0, match.CurrentSeat);

        // Alice may now bank 50.
        match.RollDice(Stacked(5, 2, 3, 4, 6, 6));
        match.KeepDice([0]);
        Assert.True(match.CanBank);
        match.Bank();
        Assert.Equal(650, match.Seats[0].Score);
    }

    [Fact]
    public void BankingIsRefusedWhileDiceAreShowing()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));

        Assert.Throws<InvalidOperationException>(match.Bank);
    }

    // ---- the end ---------------------------------------------------------------------

    [Fact]
    public void PassingTheTargetGivesTheOtherSeatOneLastTurn()
    {
        var match = Seated(new FarkleRules(Target: 1000, OpeningThreshold: 500));

        // Alice banks 1000 straight off.
        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        Assert.Equal(Phase.Rolling, match.Phase);
        Assert.Equal(1, match.FinalTurnFor);
        Assert.Equal(1, match.CurrentSeat);
        Assert.Null(match.Winner);

        // Bob's last turn: 600, not enough.
        match.RollDice(Stacked(6, 6, 6, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        Assert.Equal(Phase.Finished, match.Phase);
        Assert.Equal(0, match.Winner);
        Assert.Equal(Ending.ReachedTarget, match.Ending);
        Assert.Contains(match.LastTurn, e => e.Kind == TurnEventKind.Won && e.Seat == 0);
    }

    [Fact]
    public void TheLastTurnCanStealTheMatch()
    {
        var match = Seated(new FarkleRules(Target: 1000, OpeningThreshold: 500));

        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        // Bob: two triplets, 2500, and wins.
        match.RollDice(Stacked(2, 2, 2, 5, 5, 5));
        match.KeepDice([0, 1, 2, 3, 4, 5]);
        match.Bank();

        Assert.Equal(Phase.Finished, match.Phase);
        Assert.Equal(1, match.Winner);
    }

    [Fact]
    public void ATieGoesToTheSeatThatSetTheMark()
    {
        var match = Seated(new FarkleRules(Target: 1000, OpeningThreshold: 500));

        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        Assert.Equal(1000, match.Seats[0].Score);
        Assert.Equal(1000, match.Seats[1].Score);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void AFarkleOnTheLastTurnEndsIt()
    {
        var match = Seated(new FarkleRules(Target: 1000, OpeningThreshold: 500));

        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        match.RollDice(Stacked(2, 2, 3, 3, 4, 6));

        Assert.Equal(Phase.Finished, match.Phase);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void NothingMovesAfterTheEnd()
    {
        var match = Seated(new FarkleRules(Target: 1000, OpeningThreshold: 500));
        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();
        match.RollDice(Stacked(2, 2, 3, 3, 4, 6));

        Assert.Throws<InvalidOperationException>(() => match.RollDice(new Random(1)));
        Assert.Throws<InvalidOperationException>(match.Bank);
        Assert.False(match.CanBank);
    }

    // ---- the two rules the server leans on -------------------------------------------

    [Fact]
    public void YieldBanksWhenItMayAndLosesTheTurnWhenItMayNot()
    {
        var match = Seated();

        // 150 before the board: yielding loses it.
        match.RollDice(Stacked(1, 5, 2, 3, 4, 6));
        match.KeepDice([0, 1]);
        match.Yield();

        Assert.Equal(0, match.Seats[0].Score);
        Assert.Equal(1, match.CurrentSeat);
        Assert.Contains(match.LastTurn, e => e.Kind == TurnEventKind.Yielded && e.Points == 150);

        // Bob at 600: yielding banks it.
        match.RollDice(Stacked(6, 6, 6, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Yield();

        Assert.Equal(600, match.Seats[1].Score);
        Assert.True(match.Seats[1].OnBoard);
        Assert.Equal(0, match.CurrentSeat);
    }

    [Fact]
    public void YieldWhileDiceAreShowingLosesTheTurn()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.Yield();

        Assert.Equal(1, match.CurrentSeat);
        Assert.Equal(0, match.Seats[0].Score);
    }

    [Fact]
    public void StandingUpForfeitsToTheOtherSeat()
    {
        var match = Seated();
        match.RollDice(Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        match.Forfeit(0);

        Assert.Equal(Phase.Finished, match.Phase);
        Assert.Equal(1, match.Winner);
        Assert.Equal(Ending.Forfeit, match.Ending);
        Assert.Contains(match.LastTurn, e => e.Kind == TurnEventKind.Forfeited && e.Seat == 0);
    }

    [Fact]
    public void ThereIsNothingToForfeitBeforeTheMatchStarts()
    {
        var match = new FarkleMatch();
        match.Sit(0, "Alice");

        Assert.Throws<InvalidOperationException>(() => match.Forfeit(0));
    }

    // ---- the view and the log ------------------------------------------------------

    [Fact]
    public void TheViewCarriesEverythingTheClientDraws()
    {
        var match = Seated();
        match.RollDice(Stacked(2, 2, 2, 5, 4, 6));

        var view = MatchView.From(match);

        Assert.Equal("Choosing", view.Phase);
        Assert.Equal([2, 2, 2, 5, 4, 6], view.Roll);
        Assert.Equal(2, view.Seats.Count);
        Assert.Equal("Alice", view.Seats[0].Name);
        Assert.Equal(10_000, view.Target);
        Assert.Equal(500, view.OpeningThreshold);
        Assert.Contains(view.Keeps, k => k.Indices.SequenceEqual([0, 1, 2, 3]) && k.Points == 250);
        Assert.DoesNotContain(view.Keeps, k => k.Indices.Contains(4));
        Assert.Equal("None", view.Ending);
    }

    [Fact]
    public void TheLogSaysWhatHappened()
    {
        var log = new ListGameLog();
        var match = Seated(log: log);
        match.RollDice(Stacked(6, 6, 6, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        Assert.True(log.Mentions("rolls first"), log.ToString());
        Assert.True(log.Mentions("set aside 6 6 6 for 600"), log.ToString());
        Assert.True(log.Mentions("banks 600"), log.ToString());
    }

    [Fact]
    public void RulesAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FarkleMatch(new FarkleRules(Target: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FarkleMatch(new FarkleRules(Target: 100, OpeningThreshold: 500)));
    }

    /// <summary>A Random that deals these faces in this order. What "stacked deck" means for dice.</summary>
    internal static Random Stacked(params int[] faces) => new StackedRandom(faces);

    private sealed class StackedRandom(int[] faces) : Random
    {
        private int _next;

        public override int Next(int minValue, int maxValue)
        {
            if (_next >= faces.Length)
            {
                throw new InvalidOperationException("The stacked dice ran out.");
            }

            var face = faces[_next++];

            if (face < minValue || face >= maxValue)
            {
                throw new InvalidOperationException($"Stacked face {face} is outside [{minValue}, {maxValue}).");
            }

            return face;
        }
    }
}
