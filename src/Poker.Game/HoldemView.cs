using System.Text.Json.Serialization;

namespace Poker.Game;

/// <summary>One seat as the person this view was built for can see it.</summary>
/// <param name="IsPlayer">
/// A person sits here rather than a bot. True of *every* person at the table, so it
/// is not "this is me" -- see <see cref="HoldemView.ViewerSeat"/> for that.
/// </param>
/// <param name="Cards">
/// Empty unless this seat's cards may be seen by this viewer. See
/// <see cref="HoldemView.From"/> for the rule -- they are absent rather than blanked,
/// because anything sent to the client is knowable by the client.
/// </param>
public sealed record SeatSnapshot(
    int Index,
    bool IsPlayer,
    string Name,
    int Stack,
    IReadOnlyList<string> Cards,
    int CommittedThisStreet,
    int CommittedThisHand,
    bool Folded,
    bool IsAllIn,
    bool IsTurn,
    string? Hand,
    int Won);

/// <summary>
/// The whole table, as one person is allowed to see it.
///
/// This is the only thing a transport ever sends. Building it is the one place the
/// hidden-card rule is applied, so there is a single line to get right rather than
/// one per screen.
///
/// **One view per viewer, not one per table.** With two people at the same table a
/// single snapshot cannot be both people's -- either it carries both hands, which
/// hands each of them the other's cards, or it carries neither, which leaves them
/// playing blind.
/// </summary>
/// <param name="AwaitingPlayer">
/// True when the table is waiting on **this viewer**. On the one-human table that is
/// the same thing <see cref="HoldemTable.AwaitingPlayer"/> has always meant, which is
/// why the name did not change.
/// </param>
/// <param name="Options">
/// What this viewer may legally do, and null when it is not their turn. Somebody
/// else's options are not merely useless to a client, they are a small leak: what a
/// seat can afford to raise to is its stack.
/// </param>
/// <param name="ViewerSeat">
/// Which seat this view was built for -- the client's "me". Needed because
/// <see cref="SeatSnapshot.IsPlayer"/> is true of every person at the table, so a
/// client with two of them cannot pick itself out any other way.
/// </param>
public sealed record HoldemView(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HoldemStreet Street,
    IReadOnlyList<SeatSnapshot> Seats,
    IReadOnlyList<string> Community,
    int Pot,
    int Button,
    int? ActorSeat,
    bool AwaitingPlayer,
    BettingOptions? Options,
    int SmallBlind,
    int BigBlind,
    int ViewerSeat)
{
    /// <summary>
    /// Takes a snapshot of the table for the one person sitting at it.
    ///
    /// Throws on a table with more than one person, by way of <see cref="HoldemTable.Player"/>:
    /// there is no honest single answer to "what does the table look like" once two
    /// people are looking at it, and quietly answering with seat 0's would send one
    /// person the other's hole cards.
    /// </summary>
    public static HoldemView Of(HoldemTable table, bool showEverything = false) =>
        From(table, table.Player.Index, showEverything);

    /// <summary>
    /// Takes a snapshot of the table as one seat may see it.
    ///
    /// **The reveal rule is the only subtle thing here.** A seat's cards are shown to
    /// that seat, and otherwise only once it has reached a showdown -- which the
    /// engine signals by filling in <see cref="HoldemSeat.Hand"/>. Keying off the
    /// street instead leaks the winner's hole cards on every pot that ended with
    /// everybody folding, which is most of them, and is exactly the bug the terminal
    /// harness printed on the first hand it ever drew.
    ///
    /// It is keyed on the **seat this view is for**, not on whether a person sits
    /// there. Those were the same thing while only one person could sit down; with two
    /// they are not, and keying on <see cref="HoldemSeat.IsPlayer"/> would put every
    /// person's hole cards in every person's view.
    ///
    /// Hidden cards are absent rather than flagged. This record is serialised straight
    /// to a client, and a card that reaches the wire is a card the client has -- a
    /// "hidden" flag next to the value only hides it from an honest reader.
    /// </summary>
    /// <param name="showEverything">
    /// Lifts the reveal rule entirely. A debugging switch for the terminal harness and
    /// the tests; nothing that answers a client may pass true.
    /// </param>
    public static HoldemView From(HoldemTable table, int viewerSeat, bool showEverything = false)
    {
        var actor = table.Actor;

        // Validates the seat as well as answering the question, so a view for a seat
        // that does not exist is refused rather than quietly showing nobody's cards.
        var yourTurn = table.IsTurnFor(viewerSeat);

        return new HoldemView(
            table.Street,
            table.Seats.Select(seat => new SeatSnapshot(
                seat.Index,
                seat.IsPlayer,
                seat.Name,
                seat.Stack,
                seat.Index == viewerSeat || showEverything || seat.Hand is not null
                    ? seat.Cards.Select(card => card.Code).ToList()
                    : [],
                seat.CommittedThisStreet,
                seat.CommittedThisHand,
                seat.Folded,
                seat.IsAllIn,
                actor?.Index == seat.Index,
                seat.Hand?.Describe(),
                seat.Won)).ToList(),
            table.Community.Select(card => card.Code).ToList(),
            table.Pot,
            table.Button,
            actor?.Index,
            yourTurn,
            yourTurn ? table.Options() : null,
            table.Rules.SmallBlind,
            table.Rules.BigBlind,
            viewerSeat);
    }
}
