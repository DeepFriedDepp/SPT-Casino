namespace Farkle.Game;

/// <summary>
/// The things a set of dice can be worth points for.
///
/// One value per row of the scoring table. A set of dice is usually worth exactly one
/// of the six-dice rows OR a sum of the others -- a three of a kind beside a lone five,
/// say -- and <see cref="Scored.Parts"/> lists which.
/// </summary>
public enum Combination
{
    /// <summary>Single 1s, 100 each. Only ever one or two of them; three is a kind.</summary>
    Ones,

    /// <summary>Single 5s, 50 each. Only ever one or two of them; three is a kind.</summary>
    Fives,

    /// <summary>Face times 100, except that three 1s are 1000 and not 100.</summary>
    ThreeOfAKind,

    /// <summary>1000, whatever the face.</summary>
    FourOfAKind,

    /// <summary>2000, whatever the face.</summary>
    FiveOfAKind,

    /// <summary>3000, whatever the face.</summary>
    SixOfAKind,

    /// <summary>One of each face. 1500.</summary>
    Straight,

    /// <summary>Six dice in three pairs, a four of a kind and a pair included. 1500.</summary>
    ThreePairs,

    /// <summary>Six dice in two threes of a kind. 2500.</summary>
    TwoTriplets,
}

/// <summary>
/// What a set of dice is worth.
/// </summary>
/// <param name="Points">The total. Zero is a farkle if the set is a whole roll.</param>
/// <param name="Parts">What made the total up, lowest face first.</param>
/// <param name="DeadDice">
/// How many of the dice contributed nothing -- a 2, 3, 4 or 6 with fewer than three of
/// its face, when the set is not one of the six-dice combinations.
/// </param>
public sealed record Scored(int Points, IReadOnlyList<Combination> Parts, int DeadDice)
{
    /// <summary>Whether anything at all scored.</summary>
    public bool Scores => Points > 0;

    /// <summary>
    /// Whether every die in the set earns its place.
    ///
    /// This is the test for a selection a player wants to set aside. The lenient
    /// question -- does anything here score -- is the right one for a whole roll,
    /// because a roll is a farkle only when NOTHING scores. It is the wrong one for a
    /// keep: a player who sets aside a 1 and a 4 is trying to bank a die that scored
    /// nothing, and letting them would shrink the dice they have left to roll for no
    /// return, which is legal in no rule set anybody plays.
    /// </summary>
    public bool EveryDieCounts => Points > 0 && DeadDice == 0;

    public static Scored Nothing(int dice) => new(0, [], dice);
}

/// <summary>
/// The scoring table, and the arithmetic over it.
///
/// ## Written fresh, checked against a reference
///
/// Six public Farkle repos were looked at before this table. None carries a licence, so
/// none of their code is in here; this was written from the rules and then checked,
/// combination by combination, against the one whose scoring was found to be correct
/// -- see `docs/memory/2026-09-11-farkle-reference-not-source.md`. Where that reference
/// and a rule sheet disagreed, the rule sheet won; there were no such places.
///
/// ## The one decision in here that is not a rule
///
/// A six-dice set can read two ways. `1 1 1 5 5 5` is two triplets at 2500 and it is
/// also three 1s plus three 5s at 1500. `4 4 4 4 2 2` is three pairs at 1500 and it is
/// also a four of a kind at 1000 beside two dead dice. `1 1 1 1 1 1` is six of a kind at
/// 3000 and it is also three pairs at 1500.
///
/// The set is worth the BETTER of its two readings, always. No rule sheet says
/// otherwise and every one that mentions it says this. So the scorer works both out
/// and keeps the larger; it never has to decide which combination a set "is".
/// </summary>
public static class Scoring
{
    public const int SingleOne = 100;
    public const int SingleFive = 50;
    public const int ThreeOnes = 1000;
    public const int FourOfAKind = 1000;
    public const int FiveOfAKind = 2000;
    public const int SixOfAKind = 3000;
    public const int Straight = 1500;
    public const int ThreePairs = 1500;
    public const int TwoTriplets = 2500;

    /// <summary>Three of any face but 1. Three 1s are <see cref="ThreeOnes"/>.</summary>
    public static int ThreeOfAKind(int face) => face == 1 ? ThreeOnes : face * 100;

    /// <summary>
    /// What a set of one to six dice is worth.
    /// </summary>
    /// <exception cref="ArgumentException">No dice, more than six, or a value no die shows.</exception>
    public static Scored Of(IReadOnlyList<int> dice)
    {
        if (dice.Count is < 1 or > Dice.InPlay)
        {
            throw new ArgumentException($"A set is one to six dice, not {dice.Count}.", nameof(dice));
        }

        if (!Dice.AreFaces(dice))
        {
            throw new ArgumentException(
                $"[{string.Join(" ", dice)}] holds a value no die shows. That is a bug upstream, not a roll.",
                nameof(dice));
        }

        var counts = new int[Dice.Faces + 1];

        foreach (var die in dice)
        {
            counts[die]++;
        }

        var byFace = ByFace(counts);
        var wholeSet = dice.Count == Dice.InPlay ? WholeSet(counts) : null;

        return wholeSet is not null && wholeSet.Points > byFace.Points ? wholeSet : byFace;
    }

    /// <summary>A roll that scores nothing at all. The turn's points are gone.</summary>
    public static bool IsFarkle(IReadOnlyList<int> roll) => !Of(roll).Scores;

    /// <summary>
    /// Every selection of these dice a player would be allowed to set aside, as index
    /// lists into the roll, each with what it is worth.
    ///
    /// By index rather than by face, because two dice showing the same face are two
    /// dice: a roll of `1 1 2 3 4 6` allows keeping either 1 or both, and a client that
    /// taps the third die needs to be told about the third die.
    ///
    /// Only selections where <see cref="Scored.EveryDieCounts"/> -- a keep that includes
    /// a dead die is refused, see that property. Six dice is at most 63 subsets, so this
    /// simply tries them all.
    /// </summary>
    public static IReadOnlyList<Keep> Keeps(IReadOnlyList<int> roll)
    {
        var keeps = new List<Keep>();
        var n = roll.Count;

        for (var mask = 1; mask < 1 << n; mask++)
        {
            var indices = new List<int>(n);
            var faces = new List<int>(n);

            for (var i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    indices.Add(i);
                    faces.Add(roll[i]);
                }
            }

            var scored = Of(faces);

            if (scored.EveryDieCounts)
            {
                keeps.Add(new Keep(indices, scored));
            }
        }

        return keeps;
    }

    /// <summary>
    /// The keep worth the most points, and among equals the one that leaves the most
    /// dice to roll again. Null on a farkle.
    ///
    /// **This is a convenience, not a strategy.** Taking every point on the table is
    /// not always the right play -- a lone 5 beside three 2s is 50 points against a die
    /// you could roll -- and the bot must not be built on this method. See the AI
    /// section of `docs/farkle.md`.
    /// </summary>
    public static Keep? BestKeep(IReadOnlyList<int> roll)
    {
        Keep? best = null;

        foreach (var keep in Keeps(roll))
        {
            if (best is null
                || keep.Value.Points > best.Value.Points
                || (keep.Value.Points == best.Value.Points && keep.Indices.Count < best.Indices.Count))
            {
                best = keep;
            }
        }

        return best;
    }

    /// <summary>
    /// The table as the panel prints it. Sent by the server rather than written into the
    /// client, so the two cannot disagree about what a straight is worth.
    /// </summary>
    public static IReadOnlyList<(string Combination, string Points)> Table { get; } =
    [
        ("Single 1", $"{SingleOne}"),
        ("Single 5", $"{SingleFive}"),
        ("Three 1s", $"{ThreeOnes}"),
        ("Three 2s to 6s", "face x 100"),
        ("Four of a kind", $"{FourOfAKind}"),
        ("Five of a kind", $"{FiveOfAKind}"),
        ("Six of a kind", $"{SixOfAKind}"),
        ("1 to 6 straight", $"{Straight}"),
        ("Three pairs", $"{ThreePairs}"),
        ("Two triplets", $"{TwoTriplets}"),
    ];

    /// <summary>
    /// Each face on its own: its kind if there are three or more, its singles if it is a
    /// 1 or a 5, and nothing -- dead -- otherwise. Parts come out lowest face first.
    /// </summary>
    private static Scored ByFace(int[] counts)
    {
        var parts = new List<Combination>();
        var points = 0;
        var dead = 0;

        for (var face = 1; face <= Dice.Faces; face++)
        {
            var count = counts[face];

            if (count == 0)
            {
                continue;
            }

            if (count >= 3)
            {
                var (worth, kind) = Kind(face, count);
                points += worth;
                parts.Add(kind);
            }
            else if (face == 1)
            {
                points += count * SingleOne;
                parts.Add(Combination.Ones);
            }
            else if (face == 5)
            {
                points += count * SingleFive;
                parts.Add(Combination.Fives);
            }
            else
            {
                dead += count;
            }
        }

        return new Scored(points, parts, dead);
    }

    private static (int Points, Combination Kind) Kind(int face, int count) => count switch
    {
        6 => (SixOfAKind, Combination.SixOfAKind),
        5 => (FiveOfAKind, Combination.FiveOfAKind),
        4 => (FourOfAKind, Combination.FourOfAKind),
        _ => (ThreeOfAKind(face), Combination.ThreeOfAKind),
    };

    /// <summary>
    /// The three readings only six dice can have. A six of a kind is not one of them --
    /// it is a single face and <see cref="ByFace"/> already scores it, higher than the
    /// three pairs it also happens to be.
    /// </summary>
    private static Scored? WholeSet(int[] counts)
    {
        var faces = 0;
        var triplets = 0;
        var allEven = true;

        for (var face = 1; face <= Dice.Faces; face++)
        {
            if (counts[face] == 0)
            {
                continue;
            }

            faces++;
            triplets += counts[face] == 3 ? 1 : 0;
            allEven &= counts[face] % 2 == 0;
        }

        if (triplets == 2)
        {
            return new Scored(TwoTriplets, [Combination.TwoTriplets], 0);
        }

        if (allEven)
        {
            // Every face present an even number of times: three distinct pairs, or a
            // four and a pair, or a six. That is three pairs by every rule sheet that
            // scores them, and the six is outscored by its own kind anyway.
            return new Scored(ThreePairs, [Combination.ThreePairs], 0);
        }

        if (faces == Dice.Faces)
        {
            return new Scored(Straight, [Combination.Straight], 0);
        }

        return null;
    }
}

/// <summary>Dice a player may set aside from a roll, by position, and what they are worth.</summary>
public sealed record Keep(IReadOnlyList<int> Indices, Scored Value);
