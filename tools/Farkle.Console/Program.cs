using Farkle.Game;

// The measurement harness, built with the bot rather than after it.
//
// Poker's characters every one had to be widened after measuring, and the doc's own
// instruction for Farkle is to measure before trusting a single dial value. This is how:
//
//   dotnet run --project tools/Farkle.Console                    measure the cast
//   dotnet run --project tools/Farkle.Console -- --watch         one match, every reason
//   dotnet run --project tools/Farkle.Console -- --watch --a Kolya --b Vanya --seed 7
//   dotnet run --project tools/Farkle.Console -- --matches 500   more matches per pair
//
// "Measure decisions where money was actually asked for": the table this prints is how
// often each character BANKS when it has the choice, by how many dice it would roll on
// with and by what the turn is already worth. Those are the numbers a person at the table
// would notice, and the ones that separate the characters. If two rows look alike, one
// of the characters is not a character.

var args_ = new Args(args);

if (args_.Has("--help") || args_.Has("-h"))
{
    System.Console.WriteLine("--watch [--a NAME --b NAME] [--seed N]   play one match and print every decision");
    System.Console.WriteLine("--matches N                               matches per pair in the round robin (default 200)");
    return 0;
}

if (args_.Has("--watch"))
{
    Watch(args_);
    return 0;
}

Measure(args_.Int("--matches", 200), args_.Int("--seed", 1));
return 0;

// ---- one match, narrated ---------------------------------------------------------------

static void Watch(Args a)
{
    var first = BotCharacter.Named(a.Value("--a")) ?? BotCharacter.Grinder;
    var second = BotCharacter.Named(a.Value("--b")) ?? BotCharacter.Gambler;
    var rng = new Random(a.Int("--seed", Environment.TickCount));
    var log = GameLog.To(System.Console.WriteLine);

    var match = new FarkleMatch(log: log);
    match.Sit(0, first.Name, isBot: true);
    match.Sit(1, second.Name, isBot: true);

    var bots = new[] { new FarkleBot(first, log), new FarkleBot(second, log) };

    PlayOut(match, bots, rng);

    System.Console.WriteLine();
    System.Console.WriteLine($"{match.Seats[match.Winner!.Value].Name} wins in {match.TurnNumber} turns: "
        + $"{match.Seats[0].Score:N0} to {match.Seats[1].Score:N0}.");
}

// ---- the cast, measured ----------------------------------------------------------------

static void Measure(int matchesPerPair, int seed)
{
    var cast = BotCharacter.All;
    var stats = cast.ToDictionary(c => c.Name, _ => new Tally());
    var wins = cast.ToDictionary(c => c.Name, _ => 0);
    var played = cast.ToDictionary(c => c.Name, _ => 0);
    var rng = new Random(seed);

    foreach (var a in cast)
    {
        foreach (var b in cast)
        {
            if (string.CompareOrdinal(a.Name, b.Name) >= 0)
            {
                continue;
            }

            for (var i = 0; i < matchesPerPair; i++)
            {
                // Alternate who rolls first; first roll is an edge in a race.
                var (home, away) = i % 2 == 0 ? (a, b) : (b, a);
                var match = new FarkleMatch();
                match.Sit(0, home.Name, isBot: true);
                match.Sit(1, away.Name, isBot: true);

                var bots = new[] { new FarkleBot(home), new FarkleBot(away) };
                PlayOut(match, bots, rng, stats);

                var winner = match.Seats[match.Winner!.Value].Name;
                wins[winner]++;
                played[home.Name]++;
                played[away.Name]++;
            }
        }
    }

    System.Console.WriteLine($"{cast.Count} characters, {matchesPerPair} matches per pair, seed {seed}.");
    System.Console.WriteLine();
    System.Console.WriteLine("How often each banks when it has the choice, by dice it would roll on with:");
    System.Console.WriteLine();
    System.Console.WriteLine($"{"",-8}{"1 die",8}{"2",8}{"3",8}{"4",8}{"5",8}{"6",8}{"all",9}{"avg bank",10}{"farkle%",9}{"wins",7}");

    foreach (var c in cast)
    {
        var t = stats[c.Name];
        System.Console.Write($"{c.Name,-8}");

        for (var dice = 1; dice <= 6; dice++)
        {
            System.Console.Write($"{t.BankRate(dice),8:P0}");
        }

        System.Console.WriteLine(
            $"{t.BankRate(0),9:P0}{t.AverageBank,10:N0}{t.FarkleRate,9:P0}{(double)wins[c.Name] / played[c.Name],7:P0}");
    }

    System.Console.WriteLine();
    System.Console.WriteLine("How often each banks by what the turn is already worth (any dice count):");
    System.Console.WriteLine();
    System.Console.Write($"{"",-8}");

    foreach (var bucket in Tally.Buckets)
    {
        System.Console.Write($"{bucket,8}");
    }

    System.Console.WriteLine();

    foreach (var c in cast)
    {
        System.Console.Write($"{c.Name,-8}");

        foreach (var bucket in Tally.Buckets)
        {
            System.Console.Write($"{stats[c.Name].BankRateAt(bucket),8:P0}");
        }

        System.Console.WriteLine();
    }

    System.Console.WriteLine();
    System.Console.WriteLine("A character whose row matches another's is the same person with a different name.");
    System.Console.WriteLine("Break-even by dice, for reference: "
        + string.Join("  ", Enumerable.Range(1, 6).Select(n => $"{n}:{Odds.BreakEven(n):N0}")));
}

static void PlayOut(FarkleMatch match, FarkleBot[] bots, Random rng, Dictionary<string, Tally>? stats = null)
{
    var willBank = false;
    var guard = 0;

    while (match.Phase != Phase.Finished && guard++ < 10_000)
    {
        var seat = match.CurrentSeat;
        var bot = bots[seat];

        if (match.Phase == Phase.Choosing)
        {
            var before = match.TurnScore;
            var decision = bot.Decide(match);
            var remaining = match.DiceInHand - decision.Keep.Count;
            var kept = match.KeepDice(decision.Keep);

            // Only count it where there was a choice: a keep that could not have been
            // banked (under the threshold) or that had to be rolled (hot dice) is not a
            // decision, and averaging over it drowns the differences.
            var couldBank = match.CanBank;

            if (stats is not null && couldBank && remaining > 0)
            {
                stats[bot.Character.Name].Record(remaining, before + kept.Points, decision.Bank);
            }

            willBank = decision.Bank;
            continue;
        }

        if (willBank && match.CanBank)
        {
            willBank = false;
            match.Bank();
            bots[seat].Observe(match, seat);
            continue;
        }

        willBank = false;
        var wasTurn = match.TurnNumber;
        match.RollDice(rng);

        if (match.TurnNumber != wasTurn || match.Phase == Phase.Finished)
        {
            stats?[bot.Character.Name].Farkled();
            bots[seat].Observe(match, seat);
        }
    }
}

/// <summary>Bank-or-roll decisions, bucketed the two ways a person would notice.</summary>
sealed class Tally
{
    public static readonly int[] Buckets = [300, 500, 700, 1000, 1500, 2000, 3000];

    private readonly int[] _asked = new int[7];
    private readonly int[] _banked = new int[7];
    private readonly Dictionary<int, (int Asked, int Banked)> _byScore = Buckets.ToDictionary(b => b, _ => (0, 0));
    private long _bankedTotal;
    private int _banks;
    private int _farkles;
    private int _turns;

    public void Record(int remaining, int turnWorth, bool banked)
    {
        _asked[remaining]++;
        _asked[0]++;

        var bucket = Buckets.FirstOrDefault(b => turnWorth <= b, Buckets[^1]);
        var entry = _byScore[bucket];
        _byScore[bucket] = (entry.Asked + 1, entry.Banked + (banked ? 1 : 0));

        if (banked)
        {
            _banked[remaining]++;
            _banked[0]++;
            _bankedTotal += turnWorth;
            _banks++;
            _turns++;
        }
    }

    public void Farkled()
    {
        _farkles++;
        _turns++;
    }

    public double BankRate(int remaining) => _asked[remaining] == 0 ? 0 : (double)_banked[remaining] / _asked[remaining];

    public double BankRateAt(int bucket) => _byScore[bucket].Asked == 0 ? 0 : (double)_byScore[bucket].Banked / _byScore[bucket].Asked;

    public double AverageBank => _banks == 0 ? 0 : (double)_bankedTotal / _banks;

    public double FarkleRate => _turns == 0 ? 0 : (double)_farkles / _turns;
}

sealed class Args(string[] raw)
{
    public bool Has(string flag) => raw.Contains(flag, StringComparer.OrdinalIgnoreCase);

    public string? Value(string flag)
    {
        var i = Array.FindIndex(raw, r => string.Equals(r, flag, StringComparison.OrdinalIgnoreCase));

        return i >= 0 && i + 1 < raw.Length ? raw[i + 1] : null;
    }

    public int Int(string flag, int fallback) => int.TryParse(Value(flag), out var v) ? v : fallback;
}
