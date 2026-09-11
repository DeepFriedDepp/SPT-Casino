namespace Farkle.Game;

/// <summary>
/// Rolling. The one place a die's face is decided.
///
/// **The result is decided on the server and nowhere else.** The client is handed the
/// faces and animates towards them; a client that rolled its own dice would be a
/// client that could roll its own two triplets. Same rule as the wheel and the reels.
/// </summary>
public static class Dice
{
    public const int Faces = 6;

    /// <summary>How many dice are in play at the start of a turn, and after hot dice.</summary>
    public const int InPlay = 6;

    /// <summary>
    /// Rolls <paramref name="count"/> dice.
    ///
    /// `Next(1, 7)`, and the 7 is deliberate: <see cref="Random.Next(int, int)"/> takes
    /// an EXCLUSIVE upper bound. Three of the Farkle repos looked at for this table call
    /// their engine's range function with 6 as the upper bound and so have a die that
    /// can never show a six. Unity's `Random.Range(int, int)` is exclusive too, so the
    /// same mistake is available on the client side of this mod and must not be made
    /// there either -- see <see cref="Every"/>, which the tests use to prove all six
    /// faces come up.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than one or more than six dice.</exception>
    public static int[] Roll(Random rng, int count)
    {
        if (count is < 1 or > InPlay)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A roll is one to six dice.");
        }

        var faces = new int[count];

        for (var i = 0; i < count; i++)
        {
            faces[i] = rng.Next(1, Faces + 1);
        }

        return faces;
    }

    /// <summary>The faces a die can show, in order. For the tests and for enumeration.</summary>
    public static IEnumerable<int> Every => Enumerable.Range(1, Faces);

    /// <summary>
    /// Whether every value is a face a die can show. The engine refuses anything else
    /// rather than scoring it as nothing, because a 0 or a 7 arriving at the scorer is
    /// a bug upstream, not a die.
    /// </summary>
    public static bool AreFaces(IReadOnlyList<int> dice)
    {
        foreach (var die in dice)
        {
            if (die is < 1 or > Faces)
            {
                return false;
            }
        }

        return true;
    }
}
