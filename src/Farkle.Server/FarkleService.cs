using Farkle.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Farkle.Server;

/// <summary>
/// The server-side game. Today that is the health check and nothing else.
///
/// ## The gate, from day one
///
/// Every public method takes the player's <see cref="Casino.Server.SessionGate"/> and
/// calls an ungated `*Core`. Ping moves nothing today, so the gate looks like
/// ceremony; it is here so that the day a refund path lands on Ping -- which is what
/// happened on every other table -- the gate is already around it rather than being
/// the thing somebody forgets. See `src/Casino.Server/SessionGate.cs` for why the gate
/// is one instance for the casino and is not reentrant.
///
/// ## What is not here yet, on purpose
///
/// No bank, no escrow, no table store, no engine state. Whether Farkle plays for
/// money, for how many players, and to what target are the open decisions in the work
/// order, and each one changes what this class holds. Guessing at them would produce
/// the wrong money code, and wrong money code in this repo is the expensive kind.
/// </summary>
[Injectable]
public class FarkleService(
    Casino.Server.SessionGate gate,
    IProfileGateway profiles,
    IFarkleLog log)
{
    /// <summary>
    /// The scoring table and the farkle odds, built once. Both are constants of the
    /// game, and the odds cost an enumeration of 46,656 rolls that need not be repeated
    /// per ping.
    /// </summary>
    private static readonly IReadOnlyList<ScoreLine> ScoringLines =
        Scoring.Table.Select(row => new ScoreLine { Combination = row.Combination, Points = row.Points }).ToList();

    private static readonly IReadOnlyList<double> FarkleChances =
        Enumerable.Range(1, Dice.InPlay).Select(n => Math.Round(Odds.FarkleChance(n) * 100, 2)).ToList();

    public async Task<PingResponse> PingAsync(MongoId sessionId)
    {
        using var _ = await gate.EnterAsync(sessionId);

        return PingCore(sessionId);
    }

    private PingResponse PingCore(MongoId sessionId)
    {
        var known = profiles.HasProfile(sessionId);

        log.Detail($"ping [{sessionId}] -- profile {(known ? "found" : "NOT FOUND")}");

        return new PingResponse
        {
            ModVersion = TableInfo.Version,
            SessionId = sessionId.ToString(),
            HasProfile = known,
            Scoring = ScoringLines,
            FarkleChance = FarkleChances,
        };
    }
}
