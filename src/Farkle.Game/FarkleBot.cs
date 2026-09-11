namespace Farkle.Game;

/// <summary>
/// The four numbers a bot's character is made of, each 0 to 1.
/// </summary>
/// <param name="Risk">
/// How much the farkle chance is discounted. 0.5 plays the true odds; toward 1 the
/// seat rolls when the numbers say bank, toward 0 it banks early.
/// </param>
/// <param name="Greed">
/// How strongly it values dice left to roll. High greed leaves a lone 5 on the table
/// to roll one more die; low greed sweeps up every point it sees.
/// </param>
/// <param name="Patience">
/// A floor under what it will bank, as a share of twice the opening threshold. Some
/// players will not stop for 300; some stop the moment they clear the line.
/// </param>
/// <param name="Steadiness">How little the run of the game reaches it. At 1, nothing does.</param>
public sealed record BotDials(double Risk, double Greed, double Patience, double Steadiness)
{
    public BotDials Clamped() => new(Unit(Risk), Unit(Greed), Unit(Patience), Unit(Steadiness));

    /// <summary>Every dial moved <paramref name="t"/> of the way toward the other's.</summary>
    public BotDials Blend(BotDials other, double t) => new(
        Risk + (other.Risk - Risk) * t,
        Greed + (other.Greed - Greed) * t,
        Patience + (other.Patience - Patience) * t,
        Steadiness + (other.Steadiness - Steadiness) * t);

    private static double Unit(double v) => Math.Clamp(v, 0, 1);
}

/// <summary>
/// A named set of dials. Landmarks on the dials, not separate code: every character runs
/// the one procedure in <see cref="FarkleBot"/> and differs only in these numbers, which
/// is what lets <see cref="Improvise"/> put a seat between two of them.
///
/// **The numbers here are starting points, not measured values.** Poker's cast every one
/// had to be widened after measuring, and these will too. `tools/Farkle.Console` is the
/// harness; the doc's instruction is to measure before trusting any of them.
/// </summary>
public sealed record BotCharacter(string Name, string Blurb, BotDials Dials)
{
    // Measured, not guessed -- see docs/farkle.md, "The cast, measured". Two things the
    // first two runs taught: Patience is what separates a small-banker from a stopper
    // (taking Timur's off made him Kolya's twin at 94% with three dice left), and Greed
    // shows in WHICH dice are kept rather than in the bank rate, so it is not in the bank
    // table at all. Re-measure after any change here.
    public static readonly BotCharacter Rock =
        new("Kolya", "Banks the moment he may. Has never lost 1,000 on a turn and never will.", new BotDials(0.15, 0.30, 0.10, 0.90));

    public static readonly BotCharacter Grinder =
        new("Sveta", "Plays the odds and nothing else.", new BotDials(0.45, 0.50, 0.50, 0.75));

    public static readonly BotCharacter Tourist =
        new("Timur", "Takes every point on the table and stops when it feels like enough.", new BotDials(0.55, 0.10, 0.35, 0.45));

    public static readonly BotCharacter Gambler =
        new("Vanya", "One more roll. Always one more roll.", new BotDials(0.85, 0.75, 0.65, 0.25));

    public static IReadOnlyList<BotCharacter> All { get; } = [Rock, Grinder, Tourist, Gambler];

    /// <summary>By name, case-insensitively, or null.</summary>
    public static BotCharacter? Named(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Somebody between two of the cast, with a little jitter. No two tables alike.</summary>
    public static BotCharacter Improvise(Random rng)
    {
        var first = All[rng.Next(All.Count)];
        var second = All[rng.Next(All.Count)];
        var dials = first.Dials.Blend(second.Dials, rng.NextDouble());

        dials = new BotDials(
            dials.Risk + Jitter(rng),
            dials.Greed + Jitter(rng),
            dials.Patience + Jitter(rng),
            dials.Steadiness + Jitter(rng)).Clamped();

        return new BotCharacter(first.Name, $"Mostly {first.Name}, a streak of {second.Name}.", dials);
    }

    private static double Jitter(Random rng) => (rng.NextDouble() - 0.5) * 0.16;
}

/// <summary>What the bot chose on one roll, and why, and how long to look like it took.</summary>
/// <param name="Keep">Positions in the roll to set aside.</param>
/// <param name="Bank">Whether to bank after the keep rather than roll on.</param>
/// <param name="Seconds">A thinking time, longer when the two best options were close.</param>
/// <param name="Reason">One line, for the log and the console.</param>
public sealed record BotDecision(IReadOnlyList<int> Keep, bool Bank, double Seconds, string Reason);

/// <summary>
/// The house's regular.
///
/// ## One decision, not two
///
/// The only thing a Farkle player ever decides is what to set aside and whether to roll
/// again -- and those are one decision. `2 2 2 5 4 6` is 250 keeping the 5 and rolling
/// two dice, or 200 leaving it and rolling three, and which is right depends on what the
/// turn is already worth and how the two extra dice compare with fifty points. So for
/// every legal keep the bot values both banking after it and rolling after it, bends the
/// values by its dials, and takes the best of the lot.
///
/// The raw values come from <see cref="Odds"/>: a roll's worth is what it expects to add
/// times the chance it does not farkle. **A bot that used exactly those would be a bad
/// bot** -- always stopping precisely at the break-even line is as predictable as always
/// stopping at 300 -- which is what the dials are for.
///
/// ## The overrides, which are what a threshold bot lacks
///
/// The score decides some turns before the dice do. If banking now wins, bank. If this is
/// the one last turn after the opponent passed the target, banking anything short of
/// their score loses, so it is not on the menu. And an opponent one strong turn from
/// the target pushes the risk dial up, because banking 350 now hands them the game.
///
/// Every decision is written to the log with its numbers, because a seat that silently
/// does things is untestable and unwatchable.
/// </summary>
public sealed class FarkleBot(BotCharacter character, IGameLog? log = null)
{
    /// <summary>An opponent this close to the target changes how the bot plays.</summary>
    public const int Endgame = 1_500;

    private readonly IGameLog _log = log ?? GameLog.Null;

    public BotCharacter Character { get; } = character;

    /// <summary>-1 to +1. Moves with results, decays toward level. See <see cref="Observe"/>.</summary>
    public double Mood { get; private set; }

    /// <summary>The dials as they are right now: the character, bent by mood.</summary>
    public BotDials Current
    {
        get
        {
            var d = Character.Dials;
            var reach = 1 - d.Steadiness;

            // Losing does not do the same thing to everybody. A gambler steams -- more
            // risk, chasing it back in one turn -- and a careful player shuts down. Which
            // way a seat tilts falls out of its Risk, not a dial of its own. Winning makes
            // everybody a little bolder; confidence is not a personality type.
            var shift = Mood < 0
                ? -Mood * (d.Risk - 0.5) * 1.2 * reach
                : Mood * 0.2 * reach;

            return d with { Risk = Math.Clamp(d.Risk + shift, 0.05, 0.95) };
        }
    }

    /// <summary>
    /// Chooses for the current roll. The match must be choosing and the bot's seat current.
    /// </summary>
    public BotDecision Decide(FarkleMatch match)
    {
        if (match.Phase != Phase.Choosing)
        {
            throw new InvalidOperationException("Nothing is showing to decide about.");
        }

        var me = match.Current;
        var them = match.Other(me.Index);
        var dials = Current;
        var risk = dials.Risk;
        var finalTurn = match.FinalTurnFor == me.Index;

        // An opponent one good turn from the line: banking small hands them the game, so
        // the risk dial goes up in proportion to how close they are.
        var gap = match.Rules.Target - them.Score;

        if (!finalTurn && gap > 0 && gap < Endgame)
        {
            risk += (1 - risk) * (1 - (double)gap / Endgame) * 0.6;
        }

        var floor = dials.Patience * 2 * match.Rules.OpeningThreshold;
        var options = new List<Option>();

        foreach (var keep in match.LegalKeeps)
        {
            var after = match.TurnScore + keep.Value.Points;
            var remaining = match.DiceInHand - keep.Indices.Count;
            var hotDice = remaining == 0;

            if (hotDice)
            {
                remaining = Dice.InPlay;
            }

            var total = me.Score + after;

            // ---- bank ----
            double bank;

            if (!(me.OnBoard || after >= match.Rules.OpeningThreshold))
            {
                bank = double.NegativeInfinity;
            }
            else if (finalTurn)
            {
                // Beating them is the only bank worth anything. A tie loses: they set the
                // mark and this seat had its turn to pass it.
                bank = total > them.Score ? double.PositiveInfinity : double.NegativeInfinity;
            }
            else if (total >= match.Rules.Target)
            {
                bank = double.PositiveInfinity;
            }
            else if (hotDice)
            {
                // Six fresh dice farkle one time in forty. Nobody banks instead.
                bank = double.NegativeInfinity;
            }
            else
            {
                bank = after < floor ? after * (1 - dials.Patience) : after;
            }

            // ---- roll ----
            var farkle = Math.Clamp(Odds.FarkleChance(remaining) * (1.5 - risk), 0.005, 0.99);
            var gain = (0.5 + dials.Greed) * Odds.MeanBestKeep(remaining);
            var roll = (1 - farkle) * (after + gain);

            if (finalTurn && total <= them.Score)
            {
                // Behind with the last turn: nothing to lose by rolling, everything by not.
                roll = Math.Max(roll, after + gain);
            }

            options.Add(new Option(keep, true, bank, remaining));
            options.Add(new Option(keep, false, roll, remaining));
        }

        // Best value; among equals, the roll with the most dice left. Options ordered so
        // that ties resolve the same way every time.
        var ordered = options
            .OrderByDescending(o => o.Value)
            .ThenBy(o => o.Bank)
            .ThenByDescending(o => o.Remaining)
            .ToList();

        var best = ordered[0];
        var runnerUp = ordered.Skip(1).FirstOrDefault(o => !ReferenceEquals(o.Keep, best.Keep) || o.Bank != best.Bank);

        var kept = string.Join(" ", best.Keep.Indices.Select(i => match.Roll[i]));
        var reason =
            $"{me.Name} keeps {kept} ({best.Keep.Value.Points:N0}) and "
            + (best.Bank
                ? $"banks {match.TurnScore + best.Keep.Value.Points:N0}"
                : $"rolls {best.Remaining} at {match.TurnScore + best.Keep.Value.Points:N0}")
            + $" -- {Describe(best)} vs {(runnerUp is null ? "nothing" : Describe(runnerUp))}, risk {risk:0.00}"
            + (finalTurn ? ", last turn" : string.Empty);

        if (_log.Enabled)
        {
            _log.Write(reason);
        }

        return new BotDecision(best.Keep.Indices, best.Bank, Seconds(best, runnerUp), reason);
    }

    /// <summary>
    /// Reads how the bot's last turn went and moves its mood. Call once the turn has
    /// ended, with the match's <see cref="FarkleMatch.LastTurn"/> holding it.
    /// </summary>
    public void Observe(FarkleMatch match, int mySeat)
    {
        Mood *= 0.6;

        foreach (var e in match.LastTurn)
        {
            if (e.Seat != mySeat)
            {
                continue;
            }

            switch (e.Kind)
            {
                case TurnEventKind.Farkled:
                    Mood -= Math.Min(1, e.Points / 1500.0) * 0.6;
                    break;
                case TurnEventKind.Banked:
                    Mood += Math.Min(1, e.Points / 1500.0) * 0.3;
                    break;
            }
        }

        Mood = Math.Clamp(Mood, -1, 1);
    }

    private static double Seconds(Option best, Option? runnerUp)
    {
        if (runnerUp is null || double.IsInfinity(best.Value))
        {
            return 0.7;
        }

        var scale = Math.Max(200, Math.Abs(best.Value));
        var closeness = 1 - Math.Min(1, Math.Abs(best.Value - runnerUp.Value) / scale);

        return Math.Round(0.6 + 1.6 * closeness, 2);
    }

    private static string Describe(Option o) =>
        (o.Bank ? "bank " : "roll ") + (double.IsInfinity(o.Value) ? (o.Value > 0 ? "wins" : "loses") : o.Value.ToString("N0"));

    private sealed record Option(Keep Keep, bool Bank, double Value, int Remaining);
}
