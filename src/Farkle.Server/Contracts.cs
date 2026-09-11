using SPTarkov.Server.Core.Models.Utils;

namespace Farkle.Server;

/// <summary>
/// The wire.
///
/// **Every property is PascalCase and nothing on it is an enum.** SPT matches request
/// bodies case-sensitively, so a lowercase key binds nothing and the field silently
/// takes its default. And SPT's own enum converter outranks a `[JsonConverter]` on the
/// type, so enums go over as integers unless every property carrying one is
/// attributed. Strings sidestep both.
///
/// Only the health check so far. The play routes arrive with Phase 2, once the four
/// open decisions in the work order have answers -- see `docs/farkle.md`.
/// </summary>
public record PingRequest : IRequestData;

/// <summary>One row of the scoring table, as the panel prints it.</summary>
public record ScoreLine
{
    public string Combination { get; init; } = string.Empty;

    /// <summary>A number, or "face x 100" for the one row that is not a number.</summary>
    public string Points { get; init; } = string.Empty;
}

/// <summary>
/// The health check. Answers "did the mod load, did the session resolve" -- the first
/// thing worth having and the last thing to stop working.
///
/// It also carries the scoring table, so the panel draws the engine's own numbers
/// rather than a copy that can drift from them. Same arrangement as the slot machine's
/// paytable.
/// </summary>
public record PingResponse
{
    public bool Ok { get; init; } = true;

    public string ModVersion { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public bool HasProfile { get; init; }

    public IReadOnlyList<ScoreLine> Scoring { get; init; } = [];

    /// <summary>
    /// The chance a roll of N dice scores nothing, for N = 1..6, as percentages.
    /// Computed by the engine, printed by the panel, so a player can see what they are
    /// risking before they roll.
    /// </summary>
    public IReadOnlyList<double> FarkleChance { get; init; } = [];

    public string? Note { get; init; }
}
