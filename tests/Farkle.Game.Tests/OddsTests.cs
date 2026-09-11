namespace Farkle.Game.Tests;

/// <summary>
/// The exact figures, pinned to their fractions. These are the numbers every Farkle
/// strategy text quotes, so if the scorer ever disagrees with them the scorer is wrong
/// -- which makes this a second, independent check on <see cref="Scoring"/> as much
/// as a test of <see cref="Odds"/>.
/// </summary>
public class OddsTests
{
    [Theory]
    [InlineData(1, 4, 6)]
    [InlineData(2, 16, 36)]
    [InlineData(3, 60, 216)]
    [InlineData(4, 204, 1_296)]
    [InlineData(5, 600, 7_776)]
    [InlineData(6, 1_080, 46_656)]
    public void TheFarkleChanceIsTheKnownFraction(int dice, int farkles, int outcomes) =>
        Assert.Equal((double)farkles / outcomes, Odds.FarkleChance(dice), precision: 12);

    [Fact]
    public void MoreDiceFarkleLess()
    {
        for (var dice = 2; dice <= 6; dice++)
        {
            Assert.True(Odds.FarkleChance(dice) < Odds.FarkleChance(dice - 1));
        }
    }

    /// <summary>
    /// One die: a 1 a sixth of the time, a 5 a sixth of the time. (100 + 50) / 6 = 25.
    /// Worked by hand, so it checks the enumeration rather than trusting it.
    /// </summary>
    [Fact]
    public void OneDieIsWorthTwentyFiveOnAverage() =>
        Assert.Equal(25.0, Odds.MeanBestKeep(1), precision: 9);

    /// <summary>
    /// Two dice, by hand: 1,1 = 200; 5,5 = 100; 1,5 either way = 150 twice; a lone 1
    /// with a dead die, 8 ways, = 100; a lone 5 with a dead die, 8 ways, = 50.
    /// (200 + 100 + 300 + 800 + 400) / 36 = 50.
    /// </summary>
    [Fact]
    public void TwoDiceAreWorthFiftyOnAverage() =>
        Assert.Equal(50.0, Odds.MeanBestKeep(2), precision: 9);

    [Fact]
    public void MoreDiceAreWorthMore()
    {
        for (var dice = 2; dice <= 6; dice++)
        {
            Assert.True(Odds.MeanBestKeep(dice) > Odds.MeanBestKeep(dice - 1));
        }
    }

    /// <summary>
    /// Sanity on the break-even line: with one die you lose two thirds of the time for
    /// 25 points on average, so rolling it is only worth it under 37.5 points -- which
    /// is to say never, since the smallest turn score is 50. With six dice the line is
    /// in the tens of thousands, which is to say always roll a fresh six.
    /// </summary>
    [Fact]
    public void TheBreakEvenLineIsWhereItShouldBe()
    {
        Assert.Equal(37.5, Odds.BreakEven(1), precision: 9);
        Assert.True(Odds.BreakEven(6) > 10_000);

        for (var dice = 2; dice <= 6; dice++)
        {
            Assert.True(Odds.BreakEven(dice) > Odds.BreakEven(dice - 1));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RefusesAnImpossibleCount(int dice) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Odds.FarkleChance(dice));
}
