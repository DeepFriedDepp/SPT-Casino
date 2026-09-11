using Farkle.Game;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Utils;

namespace Farkle.Server;

/// <summary>
/// The wire.
///
/// **Every property is PascalCase and nothing on it is an enum.** SPT matches request
/// bodies case-sensitively, so a lowercase key binds nothing and the field silently
/// takes its default -- which is how a 100,000 stake arrives as 0 while looking like it
/// bound. And SPT's own enum converter outranks a `[JsonConverter]` on the type, so
/// enums go over as integers unless every property carrying one is attributed. Strings
/// sidestep both.
/// </summary>
public record PingRequest : IRequestData;

/// <summary>Asking what is open. Carries nothing.</summary>
public record TablesRequest : IRequestData;

/// <summary>
/// Sitting down at a new table. The stake leaves the stash here.
/// </summary>
public record OpenTableRequest : IRequestData
{
    /// <summary>What each player puts up, in roubles. The winner is paid both.</summary>
    public long Stake { get; set; }

    /// <summary>
    /// Against the house's regular rather than waiting for a friend. Decided here, once:
    /// a table is human against human or human against a bot, never both, and it does
    /// not change after opening.
    /// </summary>
    public bool VsBot { get; set; }

    /// <summary>Which regular, by name, or empty for whoever is free. Ignored unless <see cref="VsBot"/>.</summary>
    public string Bot { get; set; } = string.Empty;
}

/// <summary>Taking the other chair at somebody's open table. The stake leaves the stash here.</summary>
public record JoinTableRequest : IRequestData
{
    public string TableId { get; set; } = string.Empty;
}

/// <summary>Standing up. Carries nothing -- there is only one table to leave.</summary>
public record LeaveTableRequest : IRequestData;

/// <summary>Rolling whatever is in hand. Carries nothing.</summary>
public record RollRequest : IRequestData;

/// <summary>Setting dice aside, by their position in the showing roll.</summary>
public record KeepRequest : IRequestData
{
    public List<int> Indices { get; set; } = [];
}

/// <summary>Banking the turn. Carries nothing.</summary>
public record BankRequest : IRequestData;

/// <summary>Asking for the table. Also the clock the table runs on -- see the service.</summary>
public record StateRequest : IRequestData;

/// <summary>Does nothing to the game. See <see cref="FarkleItemEventRouter"/>.</summary>
public record FarkleSyncAction : BaseInteractionRequestData;

// ---- responses -------------------------------------------------------------------------

/// <summary>One open table, as the lobby lists it.</summary>
public sealed record FarkleSummary(string Id, string HostName, int Stake, string Wallet, bool VsBot, string Phase);

/// <summary>What the lobby is handed.</summary>
public record TablesResponse
{
    public bool Ok { get; init; } = true;

    public IReadOnlyList<FarkleSummary> Tables { get; init; } = [];

    /// <summary>The table this player is already at, if any, so the lobby can go straight there.</summary>
    public string? YourTable { get; init; }
}

/// <summary>Answered by every table route.</summary>
public record FarkleResponse
{
    public bool Ok { get; init; } = true;

    public string? Error { get; init; }

    /// <summary>
    /// Something the player has to be told regardless of what they asked for -- money
    /// handed back, say. The client shows it and asks the game to resync its stash.
    /// </summary>
    public string? Note { get; init; }

    public string? TableId { get; init; }

    public int? YourSeat { get; init; }

    public MatchView? Match { get; init; }

    public int Stake { get; init; }

    public string Wallet { get; init; } = nameof(Server.Wallet.Roubles);

    public bool VsBot { get; init; }

    /// <summary>Whether the match has been paid out. True only once the match is finished.</summary>
    public bool Settled { get; init; }

    public int Balance { get; init; }

    public static FarkleResponse Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>One row of the scoring table, as the panel prints it.</summary>
public record ScoreLine
{
    public string Combination { get; init; } = string.Empty;

    public string Points { get; init; } = string.Empty;
}

/// <summary>
/// The health check. Answers "did the mod load, did the session resolve, can the money
/// be read", and carries the scoring table and odds so the panel prints the engine's own
/// numbers rather than a copy that can drift from them.
/// </summary>
public record PingResponse
{
    public bool Ok { get; init; } = true;

    public string ModVersion { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public bool HasProfile { get; init; }

    public int Balance { get; init; }

    public int MinStake { get; init; }

    public int MaxStake { get; init; }

    public int Target { get; init; }

    public int OpeningThreshold { get; init; }

    public IReadOnlyList<ScoreLine> Scoring { get; init; } = [];

    /// <summary>The chance a roll of N dice scores nothing, N = 1..6, as percentages.</summary>
    public IReadOnlyList<double> FarkleChance { get; init; } = [];

    /// <summary>The house's regulars, by name, for the panel to offer.</summary>
    public IReadOnlyList<string> Bots { get; init; } = [];

    public string? YourTable { get; init; }

    public string? Note { get; init; }
}
