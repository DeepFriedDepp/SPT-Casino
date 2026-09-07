namespace Blackjack.Game;

/// <summary>
/// One box at the table: who is standing there, what they bet, and the hands that
/// bet turned into.
///
/// Unlike a hold'em seat this one owns no stack. That is the whole difference on
/// the money side: blackjack settles every round back to the profile, so there is
/// nothing in front of a seat between rounds and nothing to bust out of. What a
/// seat carries instead is a **pending bet**, and whether it has one is the only
/// thing that decides if it is dealt to.
///
/// A seat holds more than one hand only because splitting makes more. That was
/// true when this class did not exist and the table kept a single list; seats are
/// the outer loop over people, hands are the inner loop over one person's cards.
/// </summary>
public sealed class BlackjackSeat
{
    private readonly List<Hand> _hands = [];

    internal BlackjackSeat(int index, bool occupied, string name)
    {
        Index = index;
        IsOccupied = occupied;
        Name = name;
    }

    public int Index { get; }

    /// <summary>
    /// True when somebody is standing at this box.
    ///
    /// Says "a person is here", not "this is you" -- with several people at the
    /// table it is true of all of them, so a client works out which box is its own
    /// from the seat index it was given when it sat down.
    /// </summary>
    public bool IsOccupied { get; internal set; }

    /// <summary>What to call whoever is here. "Seat n" while the box is empty.</summary>
    public string Name { get; internal set; }

    /// <summary>
    /// What this seat has staked on the round that has not started yet, or 0 for a
    /// seat that has not bet.
    ///
    /// Cleared the moment the round is dealt -- from then on the money lives on the
    /// hands, where doubling and splitting can move it. It is deliberately **not**
    /// carried over to the next round: a seat must bet every round, so a player who
    /// has walked away from the keyboard sits out from the next deal without anybody
    /// having to notice they are gone.
    /// </summary>
    public int PendingBet { get; internal set; }

    /// <summary>
    /// The payout multiplier this seat's naturals settle at, fixed when the bet was
    /// placed.
    ///
    /// Per seat rather than per round because two people at one table can stake two
    /// different things, and what a natural pays depends on what was staked -- see
    /// <see cref="Rules.BlackjackPayout"/>. One shoe serving every currency is why
    /// this cannot live on the table.
    /// </summary>
    public double BlackjackPayout { get; internal set; }

    /// <summary>
    /// True for a seat that was dealt into the round in progress. False for an empty
    /// box and for one whose player did not bet in time, which is the same state:
    /// dealt nothing, settles nothing, skipped by the turn order.
    /// </summary>
    public bool IsInRound { get; internal set; }

    /// <summary>This seat's hands, in the order they are played.</summary>
    public IReadOnlyList<Hand> Hands => _hands;

    /// <summary>
    /// Which of this seat's hands is being played. Only meaningful while the table
    /// is waiting on this seat -- see <see cref="BlackjackTable.ActiveSeat"/>. A
    /// seat that has finished leaves it one past the end.
    /// </summary>
    public int ActiveHandIndex { get; internal set; }

    /// <summary>
    /// This seat's money at risk. Per seat and never summed across the table: two
    /// people sharing a shoe must not share a wallet, and a total that crossed them
    /// would pay one person's win out of the other's stake.
    /// </summary>
    public int TotalWagered => _hands.Sum(hand => hand.Wager);

    public int TotalReturned => _hands.Sum(hand => hand.Returned);

    /// <summary>Profit or loss for this seat. Negative means the house won.</summary>
    public int Net => TotalReturned - TotalWagered;

    /// <summary>Splits spent this round. Per seat, so one player cannot use up another's.</summary>
    internal int SplitsUsed { get; set; }

    internal List<Hand> HandList => _hands;

    internal void ClearForNewRound()
    {
        _hands.Clear();
        SplitsUsed = 0;
        ActiveHandIndex = 0;
        IsInRound = false;
    }

    public override string ToString() =>
        IsOccupied ? $"{Name} (seat {Index})" : $"seat {Index}, empty";
}
