using System.Text.Json.Serialization;

namespace Blackjack.Game;

/// <summary>
/// One box as everybody sees it.
///
/// **Nothing here is filtered per viewer, and that is not an oversight.** In
/// blackjack every player's cards are face up -- that is the game, and watching
/// somebody bust while you stand on 19 is the reason to sit together at all. The
/// only concealed card is the dealer's hole card, hidden from everybody equally by
/// <see cref="BlackjackTable.ViewTable"/>. Hold'em needed a view per person
/// because hole cards are secret; copying that here would cost work and make the
/// game wrong.
/// </summary>
// The [property: JsonConverter] attributes are load-bearing for the same reason
// they are on HandView -- SPT registers its own enum converter into
// options.Converters, which outranks anything declared on the enum type itself.
public sealed record SeatView(
    int Index,
    string Name,
    bool IsOccupied,
    bool IsInRound,
    int PendingBet,
    IReadOnlyList<HandView> Hands,
    int ActiveHandIndex,
    [property: JsonConverter(typeof(StringEnumListConverter<PlayerAction>))]
    IReadOnlyList<PlayerAction> AvailableActions,
    int TotalWagered,
    int TotalReturned)
{
    /// <summary>This seat's profit or loss. Negative means the house won.</summary>
    public int Net => TotalReturned - TotalWagered;
}

/// <summary>
/// The whole shared table in one snapshot: every box, the one dealer hand, and
/// what is left in the one shoe.
///
/// **There is no table-wide TotalWagered or TotalReturned, deliberately.** A
/// number like that is either the house's exposure or one person's stake depending
/// on who reads it, and a transport that credited it to whoever asked would pay a
/// player their neighbour's winnings. Money is read off
/// <see cref="Seats"/>[index] or not at all.
///
/// <see cref="RoundView"/> remains the one-seat window onto the same state and is
/// unchanged -- see <see cref="BlackjackTable.View"/> for when each applies.
/// </summary>
public sealed record TableView(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] RoundPhase Phase,
    IReadOnlyList<SeatView> Seats,
    HandView Dealer,
    int? ActiveSeat,
    int ShoeRemaining);
