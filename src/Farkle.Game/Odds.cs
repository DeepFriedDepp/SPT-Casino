namespace Farkle.Game;

/// <summary>
/// The numbers the continue-or-bank decision turns on, computed rather than measured.
///
/// Same principle as the slot machine's return: there are at most 46,656 ways six dice
/// can land, so every figure here is an exact enumeration over all of them, and a
/// Monte Carlo run is a check on the formula rather than the source of the number.
///
/// Written now, ahead of the bot, because the bot's design depends on these being
/// known -- see `docs/farkle.md`. A bot built on a guessed threshold is the one thing
/// across all six reference repos that was not worth imitating.
/// </summary>
public static class Odds
{
    /// <summary>
    /// The chance a roll of this many dice scores nothing.
    ///
    /// Exact, by counting. The well-known figures this reproduces: two thirds for one
    /// die, 44.4% for two, 27.8% for three, 15.7% for four, 7.7% for five and 2.3% for
    /// six -- the test pins every one to its fraction.
    /// </summary>
    public static double FarkleChance(int dice)
    {
        var (farkles, outcomes) = Enumerate(dice, roll => Scoring.IsFarkle(roll) ? 1 : 0);

        return (double)farkles / outcomes;
    }

    /// <summary>
    /// What a roll of this many dice is worth on average if every point on the table is
    /// taken, a farkle counting as zero.
    ///
    /// The crude expected gain of one more roll. Crude because taking every point is
    /// not the best keep and because it ignores what the kept dice do to the NEXT
    /// roll, but it is the honest first number: against `turnScore x FarkleChance` it
    /// says whether a roll is worth making at all.
    /// </summary>
    public static double MeanBestKeep(int dice)
    {
        var (points, outcomes) = Enumerate(dice, roll => Scoring.BestKeep(roll)?.Value.Points ?? 0);

        return (double)points / outcomes;
    }

    /// <summary>
    /// The turn score above which one more roll of this many dice loses points on
    /// average: the point where what the roll expects to add equals what it expects to
    /// lose. Below it rolling is +EV on the raw numbers; above it banking is.
    ///
    /// **A bot that used exactly this would be a bad bot.** It is the break-even line,
    /// and a player who always stops precisely there is as predictable as one who
    /// always stops at 300. It is the reference the dials bend around.
    /// </summary>
    public static double BreakEven(int dice)
    {
        var farkle = FarkleChance(dice);

        return MeanBestKeep(dice) / farkle;
    }

    /// <summary>Walks every outcome of <paramref name="dice"/> dice, summing a measure.</summary>
    private static (long Total, long Outcomes) Enumerate(int dice, Func<int[], int> measure)
    {
        if (dice is < 1 or > Dice.InPlay)
        {
            throw new ArgumentOutOfRangeException(nameof(dice), dice, "One to six dice.");
        }

        var roll = new int[dice];
        var outcomes = 0L;
        var total = 0L;

        for (var i = 0; i < dice; i++)
        {
            roll[i] = 1;
        }

        while (true)
        {
            total += measure(roll);
            outcomes++;

            // Odometer: advance the last die, carrying into the one before it.
            var position = dice - 1;

            while (position >= 0)
            {
                if (roll[position] < Dice.Faces)
                {
                    roll[position]++;
                    break;
                }

                roll[position] = 1;
                position--;
            }

            if (position < 0)
            {
                return (total, outcomes);
            }
        }
    }
}
