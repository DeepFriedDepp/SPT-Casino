namespace Farkle.Game.Tests;

public class DiceTests
{
    /// <summary>
    /// Every face comes up, six included. Three of the six reference repos have a die
    /// that cannot show a six because they passed 6 as an exclusive upper bound; this
    /// is the test that would have caught them.
    /// </summary>
    [Fact]
    public void AllSixFacesComeUp()
    {
        var rng = new Random(2026);
        var seen = new HashSet<int>();

        for (var i = 0; i < 200; i++)
        {
            foreach (var face in Dice.Roll(rng, 6))
            {
                seen.Add(face);
            }
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], seen.OrderBy(f => f).ToList());
    }

    [Fact]
    public void NothingOutsideOneToSixEverComesUp()
    {
        var rng = new Random(7);

        for (var i = 0; i < 5000; i++)
        {
            Assert.All(Dice.Roll(rng, 6), face => Assert.InRange(face, 1, 6));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void RollsAsManyDiceAsAsked(int count) => Assert.Equal(count, Dice.Roll(new Random(1), count).Length);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void RefusesAnImpossibleCount(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Dice.Roll(new Random(1), count));

    [Fact]
    public void ASeededRollIsRepeatable()
    {
        var first = Dice.Roll(new Random(99), 6);
        var second = Dice.Roll(new Random(99), 6);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Over enough rolls no face is far from a sixth of them. A weak test of fairness,
    /// but a six that never shows would fail it by a mile and so would a die stuck
    /// on 1 to 5.
    /// </summary>
    [Fact]
    public void FacesAreRoughlyEven()
    {
        var rng = new Random(31337);
        var counts = new int[7];
        const int rolls = 60_000;

        for (var i = 0; i < rolls; i++)
        {
            counts[Dice.Roll(rng, 1)[0]]++;
        }

        foreach (var face in Dice.Every)
        {
            Assert.InRange((double)counts[face], rolls / 6 * 0.9, rolls / 6 * 1.1);
        }
    }
}
