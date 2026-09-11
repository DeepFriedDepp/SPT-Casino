namespace Farkle.Game;

/// <summary>One chair, as the client draws it.</summary>
public sealed record SeatView(int Index, string Name, bool IsBot, bool Occupied, int Score);

/// <summary>One thing that happened, as the client replays it. Kind travels as a string.</summary>
public sealed record TurnEventView(int Seat, string Kind, IReadOnlyList<int> Dice, int Points, string Note);

/// <summary>A keep the current roll allows, so the client can light up what may be set aside.</summary>
public sealed record KeepView(IReadOnlyList<int> Indices, int Points, IReadOnlyList<string> Parts);

/// <summary>
/// The whole match, as both players see it.
///
/// **One view for everybody.** Every die is on the table for both seats, so there is
/// nothing to filter per viewer -- the blackjack shape, not the poker one. Enums travel
/// as strings because SPT's own converter would otherwise send them as integers.
/// </summary>
public sealed record MatchView(
    string Phase,
    IReadOnlyList<SeatView> Seats,
    int CurrentSeat,
    IReadOnlyList<int> Roll,
    int DiceInHand,
    int TurnScore,
    IReadOnlyList<int> SetAside,
    IReadOnlyList<KeepView> Keeps,
    bool CanBank,
    int Target,
    int TurnNumber,
    int? Winner,
    string Ending,
    IReadOnlyList<TurnEventView> Turn,
    IReadOnlyList<TurnEventView> LastTurn)
{
    public static MatchView From(FarkleMatch match) => new(
        match.Phase.ToString(),
        match.Seats.Select(s => new SeatView(s.Index, s.Name, s.IsBot, s.Occupied, s.Score)).ToList(),
        match.CurrentSeat,
        match.Roll,
        match.DiceInHand,
        match.TurnScore,
        match.SetAside,
        match.LegalKeeps
            .Select(k => new KeepView(k.Indices, k.Value.Points, k.Value.Parts.Select(p => p.ToString()).ToList()))
            .ToList(),
        match.CanBank,
        match.Rules.Target,
        match.TurnNumber,
        match.Winner,
        match.Ending.ToString(),
        match.Turn.Select(Event).ToList(),
        match.LastTurn.Select(Event).ToList());

    private static TurnEventView Event(TurnEvent e) => new(e.Seat, e.Kind.ToString(), e.Dice, e.Points, e.Note);
}
