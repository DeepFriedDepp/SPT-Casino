using Blackjack.Game;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Utils;

namespace Blackjack.Server;

public record DealRequest : IRequestData
{
    public string Wallet { get; set; } = nameof(Server.Wallet.Roubles);

    public int Wager { get; set; }

    /// <summary>
    /// Set when the player has turned the table maximum off.
    ///
    /// Taken at the client's word deliberately. This is single player: the person
    /// sending it owns the server it is sent to, and the setting lives in the
    /// BepInEx menu because that is where they will look for it rather than in a
    /// JSON file that needs a restart. Nothing is being defended against here.
    ///
    /// The minimum is not waivable. A bet of nothing is not a bet.
    /// </summary>
    public bool IgnoreMaximum { get; set; }
}

public record ActionRequest : IRequestData
{
    /// <summary>Hit, Stand, Double or Split. Parsed case-insensitively.</summary>
    public string Action { get; set; } = string.Empty;
}

public record StateRequest : IRequestData;

public record StatsRequest : IRequestData;

public record PingRequest : IRequestData;

/// <summary>
/// Answers the questions that must be true before a bet is worth attempting: did the
/// mod load, is the route reachable, did the session resolve to a real profile, and
/// can its money be read at all.
/// </summary>
public record PingResponse
{
    public bool Ok { get; init; } = true;

    public string ModVersion { get; init; } = string.Empty;

    /// <summary>Empty here means the session cookie did not resolve.</summary>
    public string SessionId { get; init; } = string.Empty;

    public bool HasProfile { get; init; }

    public Dictionary<string, int> Balances { get; init; } = [];

    /// <summary>
    /// What each wallet will take in a hand. Sent with the balances because the
    /// client has to be able to offer a legal bet: without these it can only offer
    /// the whole balance and let the table refuse it, which reads as a broken button
    /// rather than as a rule.
    /// </summary>
    public Dictionary<string, BetLimits> Limits { get; init; } = [];
}

/// <summary>
/// The table's ceiling and floor for one wallet. The player's own holdings are not
/// part of this -- these are the house's rules and are the same for everyone.
/// </summary>
public record BetLimits
{
    public int Min { get; init; }

    public int Max { get; init; }
}

/// <summary>
/// What every route returns. <see cref="Ok"/> false means the request was refused
/// before anything changed -- the client should show <see cref="Error"/> and keep
/// displaying the round it already had.
/// </summary>
public record BlackjackResponse
{
    public bool Ok { get; init; } = true;

    public string? Error { get; init; }

    public RoundView? Round { get; init; }

    /// <summary>
    /// Set when the round proceeded but something went wrong behind it -- notably a
    /// stake that could not be collected. The request still succeeded; the server
    /// operator needs to know, the player does not.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// Something worth recording that is not a fault, currently only a refunded
    /// stake. Without it a recovered stake reaches the log as an unexplained credit,
    /// which looks identical to a payout bug.
    ///
    /// The service carries no logger of its own -- that is what keeps it testable
    /// without a server -- so it reports here and the transport writes the line.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Balance in the wallet the round is denominated in, after settlement.
    ///
    /// Sent explicitly because a custom static route does not flow through the
    /// ItemEventRouter, so the client's own inventory model is stale until it
    /// refreshes. The UI must trust this number over anything it computes locally.
    /// </summary>
    public int Balance { get; init; }

    public string Wallet { get; init; } = nameof(Server.Wallet.Roubles);

    /// <summary>
    /// The whole shared table, when the reply is about one. Null for a private table,
    /// where <see cref="Round"/> is the view and nothing else is at the felt.
    ///
    /// Unfiltered, and that is the game rather than an oversight: every player's cards
    /// are face up in blackjack and the one concealed card -- the dealer's hole card --
    /// is hidden from everybody equally by the engine's own view. See
    /// <see cref="Game.TableView"/>.
    /// </summary>
    public Game.TableView? SharedTable { get; init; }

    /// <summary>
    /// Which box in <see cref="SharedTable"/> belongs to whoever asked.
    ///
    /// Sent rather than inferred. Every seat looks alike in the view -- `IsOccupied`
    /// says a person is there, not which person -- so a client with no seat index has
    /// no way to tell its own cards from its neighbour's, and would have to guess from
    /// a name that two players could share.
    /// </summary>
    public int? YourSeat { get; init; }

    /// <summary>The tables anybody could sit down at. Only ever set by the list route.</summary>
    public IReadOnlyList<SharedBlackjackSummary>? Tables { get; init; }

    public static BlackjackResponse Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// The deal, as an item-event action. Sent to the same endpoint the client already
/// uses for moving items, so the response carries ProfileChanges and the stash
/// updates without a reload.
/// </summary>
public record BlackjackDealAction : BaseInteractionRequestData
{
    public string Wallet { get; set; } = nameof(Server.Wallet.Roubles);

    public int Wager { get; set; }
}

/// <summary>
/// Hit, Stand, Double or Split. Named Move because the base class already owns
/// Action, which carries the event name itself.
/// </summary>
public record BlackjackPlayAction : BaseInteractionRequestData
{
    public string Move { get; set; } = string.Empty;
}

/// <summary>
/// Carries nothing, because it asks for nothing. The client sends this when it
/// needs the profile changes the server has been holding for it, and the reply
/// carries them by virtue of being an item-event reply at all.
/// </summary>
public record BlackjackSyncAction : BaseInteractionRequestData;

/// <summary>
/// Opening a blackjack table other people can sit down at.
///
/// The currency is fixed for the table rather than per seat, exactly as poker's is:
/// the minimum and maximum a box may stake are amounts, and an amount means nothing
/// until you say what of. A table where one person is in roubles and another in
/// bitcoin would need an exchange rate to answer "what is the minimum", and there is
/// no rate a player would agree with.
/// </summary>
public record OpenTableRequest : IRequestData
{
    /// <summary>Boxes at the table, including the one opening it. Two to seven.</summary>
    public int Seats { get; set; } = 5;

    public string Wallet { get; set; } = nameof(Server.Wallet.Roubles);
}

/// <summary>Sitting down at somebody else's table.</summary>
public record JoinTableRequest : IRequestData
{
    public string TableId { get; set; } = string.Empty;
}
