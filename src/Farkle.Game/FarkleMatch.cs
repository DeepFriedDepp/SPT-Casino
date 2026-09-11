namespace Farkle.Game;

/// <summary>
/// What a match is played to. Chosen per table when it opens, from <see cref="Targets"/>.
/// </summary>
/// <param name="Target">First to bank this many wins, on the spot.</param>
public sealed record FarkleRules(int Target = 10_000)
{
    /// <summary>
    /// The targets a table may be opened at. A short race is a different game from a
    /// long one -- 1,000 is two or three good turns and a coin flip with money on it;
    /// 10,000 is the standard match -- and the owner wanted the choice on the table.
    /// </summary>
    public static IReadOnlyList<int> Targets { get; } = [1_000, 2_000, 3_000, 5_000, 10_000];

    public static bool Allows(int target) => Targets.Contains(target);
}

public enum Phase
{
    /// <summary>One seat taken, the other empty. Nothing can be rolled.</summary>
    WaitingForOpponent,

    /// <summary>The current seat must roll, or may bank what the turn has set aside.</summary>
    Rolling,

    /// <summary>Dice are showing. The current seat must set some aside.</summary>
    Choosing,

    Finished,
}

public enum Ending
{
    None,

    /// <summary>Somebody banked to the target. First there wins.</summary>
    ReachedTarget,

    /// <summary>Somebody stood up mid-match. The other seat wins outright.</summary>
    Forfeit,
}

/// <summary>One of the two chairs.</summary>
public sealed class Seat(int index)
{
    public int Index { get; } = index;

    public bool Occupied { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public bool IsBot { get; internal set; }

    public int Score { get; internal set; }
}

/// <summary>Something that happened in a turn, as the client replays it.</summary>
/// <param name="Dice">The faces involved: the roll for a roll or a farkle, the kept faces for a keep.</param>
/// <param name="Indices">
/// For a keep, WHICH dice of the showing roll were taken, by position. A client replaying
/// the other seat's turn lights those up before moving them aside, so the watcher sees the
/// choice rather than only its result. Empty for every other kind.
/// </param>
public sealed record TurnEvent(
    int Seat,
    TurnEventKind Kind,
    IReadOnlyList<int> Dice,
    IReadOnlyList<int> Indices,
    int Points,
    string Note);

public enum TurnEventKind
{
    Rolled,
    Kept,
    HotDice,
    Banked,
    Farkled,
    Yielded,
    Forfeited,
    Won,
}

/// <summary>
/// A two-seat race to the target.
///
/// ## What this is and is not
///
/// The whole game, with no notion of who is a human and who is a bot: a seat is a seat,
/// and the server decides whose turn it is to be asked. It holds no money and knows
/// nothing about currency. What a rouble is belongs with the wallet, which is the same
/// boundary the other four engines draw and the reason this is testable without a server.
///
/// **It decides every die and every score.** The client is handed faces and animates
/// towards them; a client that rolled its own dice would be a client that could roll its
/// own two triplets.
///
/// ## The turn
///
/// A turn is roll, set aside, roll again or bank, until either the seat banks or a roll
/// scores nothing -- a farkle -- and the turn's points are gone. Setting aside every die
/// is hot dice: six fresh dice, the turn's points intact. A keep must have every die in
/// it scoring (`Scored.EveryDieCounts`); a roll is a farkle only when nothing at all
/// scores. **Anything set aside may be banked**: there is no opening threshold. The
/// standard 500 was built first and taken out on 2026-09-11 at the owner's call -- a
/// player forced to keep rolling a turn they wanted to keep read it as a missing button,
/// and the rule bought nothing the stake did not already buy.
///
/// ## The end
///
/// **First to bank at or past the target wins, on the spot.** No last turn for the other
/// seat, no tie to break. The standard last-turn rule was also built first and also came
/// out with the threshold: with the target chosen per table, a race is what was asked for.
///
/// ## Two rules the server leans on
///
/// <see cref="Yield"/> is what a seat that has gone quiet does -- bank if it may, lose
/// the turn if it may not -- so the table never waits on somebody who closed the game.
/// <see cref="Forfeit"/> is what standing up mid-match is. Both are named here rather
/// than inferred in the server, because one of them decides where two stakes go.
/// </summary>
public sealed class FarkleMatch
{
    private readonly Seat[] _seats = [new Seat(0), new Seat(1)];
    private readonly List<int> _setAside = [];
    private readonly List<TurnEvent> _turn = [];
    private readonly IGameLog _log;

    private int[] _roll = [];
    private IReadOnlyList<TurnEvent> _lastTurn = [];

    public FarkleMatch(FarkleRules? rules = null, IGameLog? log = null)
    {
        Rules = rules ?? new FarkleRules();
        _log = log ?? GameLog.Null;

        if (Rules.Target <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "A target above zero.");
        }
    }

    public FarkleRules Rules { get; }

    public IReadOnlyList<Seat> Seats => _seats;

    public Phase Phase { get; private set; } = Phase.WaitingForOpponent;

    public Ending Ending { get; private set; }

    /// <summary>Whose turn it is. Meaningless once finished; see <see cref="Winner"/>.</summary>
    public int CurrentSeat { get; private set; }

    public int? Winner { get; private set; }

    /// <summary>The faces showing. Empty between rolls.</summary>
    public IReadOnlyList<int> Roll => _roll;

    /// <summary>How many dice the next roll throws.</summary>
    public int DiceInHand { get; private set; } = Dice.InPlay;

    public int TurnScore { get; private set; }

    /// <summary>The faces set aside so far this turn, in the order they were kept.</summary>
    public IReadOnlyList<int> SetAside => _setAside;

    /// <summary>What has happened this turn so far.</summary>
    public IReadOnlyList<TurnEvent> Turn => _turn;

    /// <summary>The whole of the previous turn, for a client that wants to replay it.</summary>
    public IReadOnlyList<TurnEvent> LastTurn => _lastTurn;

    public int TurnNumber { get; private set; }

    /// <summary>
    /// Which turn <see cref="LastTurn"/> is. Usually one less than <see cref="TurnNumber"/>,
    /// but equal to it once the match has finished, because finishing does not start a
    /// new turn. A client that replays turns keys on this rather than guessing.
    /// </summary>
    public int LastTurnNumber { get; private set; }

    public Seat Current => _seats[CurrentSeat];

    public Seat Other(int seat) => _seats[1 - seat];

    /// <summary>Whether the current seat may bank right now: something set aside this turn.</summary>
    public bool CanBank => Phase == Phase.Rolling && TurnScore > 0;

    /// <summary>The keeps the current roll allows, by position. Empty unless choosing.</summary>
    public IReadOnlyList<Keep> LegalKeeps => Phase == Phase.Choosing ? Scoring.Keeps(_roll) : [];

    // ---- seating ---------------------------------------------------------------------

    /// <summary>
    /// Puts somebody in a chair. The match starts the moment both are taken, with seat 0
    /// -- the host -- rolling first.
    /// </summary>
    public void Sit(int seat, string name, bool isBot = false)
    {
        if (Phase != Phase.WaitingForOpponent)
        {
            throw new InvalidOperationException("The match has started; nobody else can sit down.");
        }

        if (seat is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "Two chairs: 0 and 1.");
        }

        if (_seats[seat].Occupied)
        {
            throw new InvalidOperationException($"Seat {seat} is taken.");
        }

        _seats[seat].Occupied = true;
        _seats[seat].Name = string.IsNullOrWhiteSpace(name) ? $"Seat {seat}" : name;
        _seats[seat].IsBot = isBot;

        if (_seats.All(s => s.Occupied))
        {
            CurrentSeat = 0;
            TurnNumber = 1;
            Phase = Phase.Rolling;
            Say($"match on: {_seats[0].Name} against {_seats[1].Name}, to {Rules.Target:N0}. {_seats[0].Name} rolls first.");
        }
    }

    // ---- the turn --------------------------------------------------------------------

    /// <summary>Rolls whatever is in hand. A roll that scores nothing ends the turn with nothing.</summary>
    public void RollDice(Random rng)
    {
        if (Phase != Phase.Rolling)
        {
            throw new InvalidOperationException(Phase switch
            {
                Phase.Choosing => "Set some dice aside before rolling again.",
                Phase.Finished => "The match is over.",
                _ => "Nobody to roll against yet.",
            });
        }

        _roll = Dice.Roll(rng, DiceInHand);

        if (Scoring.IsFarkle(_roll))
        {
            var lost = TurnScore;
            Record(TurnEventKind.Farkled, _roll, lost, $"{Current.Name} rolled {Faces(_roll)} -- nothing. {lost:N0} gone.");
            TurnScore = 0;
            EndTurn();
            return;
        }

        Record(TurnEventKind.Rolled, _roll, 0, $"{Current.Name} rolled {Faces(_roll)} with {DiceInHand} dice.");
        Phase = Phase.Choosing;
    }

    /// <summary>
    /// Sets dice aside from the showing roll, by position. Every die chosen must score.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not choosing, or the keep is not legal.</exception>
    public Scored KeepDice(IReadOnlyList<int> indices)
    {
        if (Phase != Phase.Choosing)
        {
            throw new InvalidOperationException("There is nothing showing to set aside.");
        }

        if (indices.Count == 0)
        {
            throw new InvalidOperationException("Set at least one die aside.");
        }

        if (indices.Distinct().Count() != indices.Count || indices.Any(i => i < 0 || i >= _roll.Length))
        {
            throw new InvalidOperationException("Those are not dice on the table.");
        }

        var faces = indices.Select(i => _roll[i]).ToList();
        var scored = Scoring.Of(faces);

        if (!scored.EveryDieCounts)
        {
            throw new InvalidOperationException(
                scored.Scores
                    ? $"{Faces(faces)} includes a die that scores nothing. Leave it on the table."
                    : $"{Faces(faces)} scores nothing.");
        }

        TurnScore += scored.Points;
        _setAside.AddRange(faces);
        DiceInHand -= indices.Count;
        Record(
            TurnEventKind.Kept,
            faces,
            scored.Points,
            $"{Current.Name} set aside {Faces(faces)} for {scored.Points:N0}. Turn: {TurnScore:N0}.",
            indices: indices);

        if (DiceInHand == 0)
        {
            DiceInHand = Dice.InPlay;
            Record(TurnEventKind.HotDice, [], 0, $"Hot dice -- {Current.Name} gets all six back.");
        }

        _roll = [];
        Phase = Phase.Rolling;

        return scored;
    }

    /// <summary>Banks the turn. Anything set aside may be banked.</summary>
    public void Bank()
    {
        if (Phase != Phase.Rolling)
        {
            throw new InvalidOperationException(Phase == Phase.Choosing
                ? "Set some dice aside first, then bank."
                : "Nothing to bank.");
        }

        if (TurnScore <= 0)
        {
            throw new InvalidOperationException("Nothing to bank. Roll first.");
        }

        BankCore();
    }

    /// <summary>
    /// What a seat that has gone quiet does: bank if it may, and otherwise lose the turn.
    /// Costs it nothing it had not already put at risk, and the table moves on.
    /// </summary>
    public void Yield()
    {
        if (Phase is not (Phase.Rolling or Phase.Choosing))
        {
            return;
        }

        if (CanBank)
        {
            Record(TurnEventKind.Yielded, [], TurnScore, $"{Current.Name} went quiet and banks {TurnScore:N0}.");
            BankCore();
            return;
        }

        Record(TurnEventKind.Yielded, [], TurnScore, $"{Current.Name} went quiet. {TurnScore:N0} gone.");
        TurnScore = 0;
        EndTurn();
    }

    /// <summary>Standing up mid-match. The other seat wins outright.</summary>
    public void Forfeit(int seat)
    {
        if (Phase == Phase.Finished)
        {
            return;
        }

        if (Phase == Phase.WaitingForOpponent)
        {
            throw new InvalidOperationException("There is no match to forfeit yet.");
        }

        var winner = 1 - seat;
        Record(TurnEventKind.Forfeited, [], 0, $"{_seats[seat].Name} stood up. {_seats[winner].Name} wins by forfeit.", seat);
        Finish(winner, Ending.Forfeit);
    }

    /// <summary>
    /// Test seam: puts the current seat mid-turn at a chosen score with a chosen number of
    /// dice in hand, as though it had got there by keeping. Internal because only play may
    /// move a real turn; the bot measurements need positions faster than play reaches them.
    /// </summary>
    internal void Force(int turnScore, int diceInHand)
    {
        if (Phase != Phase.Rolling)
        {
            throw new InvalidOperationException("Force only between rolls.");
        }

        TurnScore = turnScore;
        DiceInHand = diceInHand;
    }

    // ---- inside ----------------------------------------------------------------------

    private void BankCore()
    {
        Current.Score += TurnScore;
        Record(TurnEventKind.Banked, [], TurnScore, $"{Current.Name} banks {TurnScore:N0} -- {Current.Score:N0}.");
        EndTurn();
    }

    private void EndTurn()
    {
        var finished = CurrentSeat;
        var other = 1 - finished;

        if (_seats[finished].Score >= Rules.Target)
        {
            Finish(finished, Ending.ReachedTarget);
            return;
        }

        _lastTurn = _turn.ToList();
        LastTurnNumber = TurnNumber;
        _turn.Clear();
        _setAside.Clear();
        _roll = [];
        TurnScore = 0;
        DiceInHand = Dice.InPlay;
        CurrentSeat = other;
        TurnNumber++;
        Phase = Phase.Rolling;
    }

    private void Finish(int winner, Ending ending)
    {
        Winner = winner;
        Ending = ending;
        Phase = Phase.Finished;
        Record(TurnEventKind.Won, [], _seats[winner].Score, $"{_seats[winner].Name} wins, {_seats[winner].Score:N0} to {Other(winner).Score:N0}.", winner);
        _lastTurn = _turn.ToList();
        LastTurnNumber = TurnNumber;
        _roll = [];
    }

    private void Record(
        TurnEventKind kind,
        IReadOnlyList<int> dice,
        int points,
        string note,
        int? seat = null,
        IReadOnlyList<int>? indices = null)
    {
        _turn.Add(new TurnEvent(seat ?? CurrentSeat, kind, dice.ToList(), indices?.ToList() ?? [], points, note));
        Say(note);
    }

    private void Say(string message)
    {
        if (_log.Enabled)
        {
            _log.Write(message);
        }
    }

    private static string Faces(IEnumerable<int> dice) => string.Join(" ", dice);
}
