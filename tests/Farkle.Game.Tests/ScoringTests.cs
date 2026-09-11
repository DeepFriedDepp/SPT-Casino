namespace Farkle.Game.Tests;

/// <summary>
/// The scoring table, one row at a time, and then the places two rows meet.
///
/// The expected values were checked against the reference scorer's own test cases
/// (`david-acm/farkle`, `ScoreCalculatorShould.cs`) rather than against memory. Where
/// this file has a case that one does not -- the four-and-a-pair, the six-of-a-kind
/// that is also three pairs, the dead-die rule -- the expectation is from the rule
/// sheet and is marked.
/// </summary>
public class ScoringTests
{
    // ---- one row each ---------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 1 }, 100)]
    [InlineData(new[] { 5 }, 50)]
    [InlineData(new[] { 1, 1 }, 200)]
    [InlineData(new[] { 5, 5 }, 100)]
    [InlineData(new[] { 1, 5 }, 150)]
    [InlineData(new[] { 1, 5, 1 }, 250)]
    public void SinglesAreAHundredAndFifty(int[] dice, int expected) =>
        Assert.Equal(expected, Scoring.Of(dice).Points);

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 200)]
    [InlineData(3, 300)]
    [InlineData(4, 400)]
    [InlineData(5, 500)]
    [InlineData(6, 600)]
    public void ThreeOfAKindIsFaceTimesAHundredExceptOnes(int face, int expected)
    {
        var scored = Scoring.Of([face, face, face]);

        Assert.Equal(expected, scored.Points);
        Assert.Equal([Combination.ThreeOfAKind], scored.Parts);
    }

    /// <summary>
    /// Four, five and six of a kind are flat, whatever the face. This is the tier the
    /// `ericfnsf/Farkle` scorer does not have at all, which is one of the two reasons
    /// its numbers were not used.
    /// </summary>
    [Theory]
    [InlineData(4, 1000, Combination.FourOfAKind)]
    [InlineData(5, 2000, Combination.FiveOfAKind)]
    [InlineData(6, 3000, Combination.SixOfAKind)]
    public void FourFiveAndSixOfAKindAreFlat(int count, int expected, Combination kind)
    {
        foreach (var face in Dice.Every)
        {
            var scored = Scoring.Of(Enumerable.Repeat(face, count).ToList());

            Assert.Equal(expected, scored.Points);
            Assert.Equal([kind], scored.Parts);
        }
    }

    [Fact]
    public void AStraightIsFifteenHundredInAnyOrder()
    {
        var scored = Scoring.Of([4, 1, 6, 3, 5, 2]);

        Assert.Equal(1500, scored.Points);
        Assert.Equal([Combination.Straight], scored.Parts);
    }

    [Fact]
    public void ThreeDistinctPairsAreFifteenHundred()
    {
        var scored = Scoring.Of([2, 2, 4, 4, 6, 6]);

        Assert.Equal(1500, scored.Points);
        Assert.Equal([Combination.ThreePairs], scored.Parts);
    }

    [Fact]
    public void TwoTripletsAreTwentyFiveHundred()
    {
        var scored = Scoring.Of([2, 2, 2, 5, 5, 5]);

        Assert.Equal(2500, scored.Points);
        Assert.Equal([Combination.TwoTriplets], scored.Parts);
    }

    // ---- where rows meet ------------------------------------------------------------

    /// <summary>
    /// Rule sheet, not the reference: a four of a kind beside a pair is three pairs, and
    /// at 1500 that beats the 1000 the four is worth on its own.
    /// </summary>
    [Theory]
    [InlineData(new[] { 4, 4, 4, 4, 2, 2 })]
    [InlineData(new[] { 1, 1, 1, 1, 5, 5 })]
    [InlineData(new[] { 6, 6, 3, 3, 6, 6 })]
    public void FourOfAKindWithAPairIsThreePairs(int[] dice)
    {
        var scored = Scoring.Of(dice);

        Assert.Equal(1500, scored.Points);
        Assert.Equal([Combination.ThreePairs], scored.Parts);
        Assert.Equal(0, scored.DeadDice);
    }

    /// <summary>Six of a kind is also three pairs. It is worth the higher of the two.</summary>
    [Fact]
    public void SixOfAKindOutscoresItsOwnThreePairs()
    {
        var scored = Scoring.Of([2, 2, 2, 2, 2, 2]);

        Assert.Equal(3000, scored.Points);
        Assert.Equal([Combination.SixOfAKind], scored.Parts);
    }

    /// <summary>
    /// Three 1s and three 5s read as 1000 + 500 face by face and as two triplets at
    /// 2500 whole. The player gets the 2500.
    /// </summary>
    [Fact]
    public void TwoTripletsBeatTheSumOfTheirKinds()
    {
        Assert.Equal(2500, Scoring.Of([1, 1, 1, 5, 5, 5]).Points);
        Assert.Equal(2500, Scoring.Of([1, 5, 1, 5, 1, 5]).Points);
    }

    /// <summary>
    /// A set can hold more than one thing. The parts add, lowest face first, and a
    /// three of a kind beside a scoring single is the ordinary case.
    /// </summary>
    [Theory]
    [InlineData(new[] { 2, 2, 2, 5, 5 }, 300, new[] { Combination.ThreeOfAKind, Combination.Fives })]
    [InlineData(new[] { 2, 2, 2, 5 }, 250, new[] { Combination.ThreeOfAKind, Combination.Fives })]
    [InlineData(new[] { 3, 3, 3, 1 }, 400, new[] { Combination.Ones, Combination.ThreeOfAKind })]
    [InlineData(new[] { 1, 1, 1, 5, 5 }, 1100, new[] { Combination.ThreeOfAKind, Combination.Fives })]
    [InlineData(new[] { 4, 4, 4, 4, 1 }, 1100, new[] { Combination.Ones, Combination.FourOfAKind })]
    [InlineData(new[] { 6, 6, 6, 1, 5 }, 750, new[] { Combination.Ones, Combination.Fives, Combination.ThreeOfAKind })]
    public void PartsAdd(int[] dice, int expected, Combination[] parts)
    {
        var scored = Scoring.Of(dice);

        Assert.Equal(expected, scored.Points);
        Assert.Equal(parts, scored.Parts);
    }

    /// <summary>
    /// The only five-dice set that is a whole-set combination does not exist: two
    /// triplets, three pairs and a straight all need six. So `1 2 3 4 5` is 150, not a
    /// short straight, whatever some house rules say.
    /// </summary>
    [Fact]
    public void ThereAreNoShortStraights()
    {
        var scored = Scoring.Of([1, 2, 3, 4, 5]);

        Assert.Equal(150, scored.Points);
        Assert.Equal(3, scored.DeadDice);
    }

    // ---- nothing --------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 2, 3 })]
    [InlineData(new[] { 2, 2 })]
    [InlineData(new[] { 2, 3, 4, 6 })]
    [InlineData(new[] { 2, 2, 3, 3, 4, 6 })]
    [InlineData(new[] { 3, 3, 4, 4, 6, 2 })]
    public void NothingScoringIsAFarkle(int[] roll)
    {
        Assert.True(Scoring.IsFarkle(roll));

        var scored = Scoring.Of(roll);

        Assert.Equal(0, scored.Points);
        Assert.Empty(scored.Parts);
        Assert.Equal(roll.Length, scored.DeadDice);
    }

    [Theory]
    [InlineData(new[] { 2, 2, 2 })]
    [InlineData(new[] { 2, 2, 4, 4, 6, 6 })]
    [InlineData(new[] { 2, 2, 2, 4 })]
    [InlineData(new[] { 5, 2, 3 })]
    public void AnythingScoringIsNotAFarkle(int[] roll) => Assert.False(Scoring.IsFarkle(roll));

    // ---- dead dice, and what may be kept --------------------------------------------

    /// <summary>
    /// A roll is judged leniently -- one scoring die saves it -- but a keep is not. The
    /// 4 in `2 2 2 4` scores nothing, so the set scores and yet may not be set aside.
    /// </summary>
    [Fact]
    public void ADeadDieMakesASetUnkeepableButNotWorthless()
    {
        var scored = Scoring.Of([2, 2, 2, 4]);

        Assert.Equal(200, scored.Points);
        Assert.Equal(1, scored.DeadDice);
        Assert.True(scored.Scores);
        Assert.False(scored.EveryDieCounts);
    }

    [Fact]
    public void KeepsAreByPositionSoTwinsAreTwoDice()
    {
        var keeps = Scoring.Keeps([1, 2, 1, 3, 4, 6]);

        // Either 1 alone, or both. Nothing else in that roll scores.
        var sets = keeps.Select(k => string.Join(",", k.Indices)).OrderBy(s => s).ToList();

        Assert.Equal(["0", "0,2", "2"], sets);
        Assert.All(keeps, k => Assert.True(k.Value.EveryDieCounts));
    }

    [Fact]
    public void KeepsNeverIncludeADeadDie()
    {
        var keeps = Scoring.Keeps([2, 2, 2, 4, 5, 6]);

        foreach (var keep in keeps)
        {
            Assert.DoesNotContain(3, keep.Indices); // the 4
            Assert.DoesNotContain(5, keep.Indices); // the 6
        }

        Assert.Contains(keeps, k => k.Indices.SequenceEqual([0, 1, 2, 4]) && k.Value.Points == 250);
    }

    [Theory]
    [InlineData(new[] { 1, 1, 1, 2, 3, 4 }, new[] { 1, 1, 1 })]
    [InlineData(new[] { 1, 5, 2, 3, 4, 4 }, new[] { 1, 5 })]
    [InlineData(new[] { 2, 2, 4, 4, 6, 6 }, new[] { 2, 2, 4, 4, 6, 6 })]
    [InlineData(new[] { 1, 1, 1, 5, 5, 5 }, new[] { 1, 1, 1, 5, 5, 5 })]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6 }, new[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(new[] { 2, 2, 2, 5, 5, 4 }, new[] { 2, 2, 2, 5, 5 })]
    [InlineData(new[] { 3, 3, 3, 1, 2, 6 }, new[] { 1, 3, 3, 3 })]
    [InlineData(new[] { 4, 4, 4, 4, 2, 2 }, new[] { 2, 2, 4, 4, 4, 4 })]
    public void BestKeepTakesEveryPointOnTheTable(int[] roll, int[] expectedFaces)
    {
        var best = Scoring.BestKeep(roll);

        Assert.NotNull(best);

        var kept = best.Indices.Select(i => roll[i]).OrderBy(f => f).ToList();

        Assert.Equal(expectedFaces.OrderBy(f => f).ToList(), kept);
    }

    [Fact]
    public void BestKeepIsNullOnAFarkle() => Assert.Null(Scoring.BestKeep([2, 2, 3, 3, 4, 6]));

    // ---- refusals -------------------------------------------------------------------

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 1, 1, 1, 1, 1, 1 })]
    public void RefusesTheWrongNumberOfDice(int[] dice) =>
        Assert.Throws<ArgumentException>(() => Scoring.Of(dice));

    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 7 })]
    [InlineData(new[] { 1, 2, 9 })]
    public void RefusesAValueNoDieShows(int[] dice) =>
        Assert.Throws<ArgumentException>(() => Scoring.Of(dice));

    /// <summary>The printed table agrees with the constants it is printed from.</summary>
    [Fact]
    public void ThePrintedTableMatchesTheCode()
    {
        var table = Scoring.Table.ToDictionary(row => row.Combination, row => row.Points);

        Assert.Equal("100", table["Single 1"]);
        Assert.Equal("50", table["Single 5"]);
        Assert.Equal("1000", table["Three 1s"]);
        Assert.Equal("1000", table["Four of a kind"]);
        Assert.Equal("2000", table["Five of a kind"]);
        Assert.Equal("3000", table["Six of a kind"]);
        Assert.Equal("1500", table["1 to 6 straight"]);
        Assert.Equal("1500", table["Three pairs"]);
        Assert.Equal("2500", table["Two triplets"]);
        Assert.Equal(10, table.Count);
    }
}
