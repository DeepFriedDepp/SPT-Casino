using System.Collections.Concurrent;
using Casino.Server;
using Farkle.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Farkle.Server;

/// <summary>
/// One Farkle table: a match, the humans at it, and what each of them staked.
///
/// **Guarded by <c>Casino.Server.TableGate</c>, keyed by <see cref="Id"/>, and that gate
/// is the OUTER lock** -- a session gate is never held while reaching for it. Same rule
/// as poker and blackjack, same reason: settling touches the table and two profiles.
///
/// ## Human against human, or human against the house. Never both
///
/// Decided at <see cref="VsBot"/> when the table opens and fixed for its life. A bot
/// table has one human and a <see cref="Bot"/> in seat 1 from the first moment, so it is
/// never listed for joining; a two-human table has no bot and lists until somebody
/// takes the second chair. That is the whole of what makes Farkle smaller than shared
/// poker or shared blackjack: there is no mixed seating to reason about.
///
/// <see cref="Occupants"/> holds humans only -- a bot has no session to be addressed
/// by, no stake in escrow, and nothing to push to.
/// </summary>
public sealed class FarkleTable
{
    public required string Id { get; init; }

    public required string HostName { get; init; }

    public required FarkleMatch Match { get; init; }

    /// <summary>Seat index to the session sitting in it. Humans only.</summary>
    public required ConcurrentDictionary<int, string> Occupants { get; init; }

    public required Wallet Wallet { get; init; }

    /// <summary>What each player put up. The winner is paid twice this.</summary>
    public required int Stake { get; init; }

    public required bool VsBot { get; init; }

    /// <summary>The house's regular, in seat 1, on a bot table. Null otherwise.</summary>
    public FarkleBot? Bot { get; init; }

    /// <summary>
    /// The bot decided to bank after its last keep. Read when the match comes back round to
    /// Rolling for the bot's seat, because the engine asks keep and bank as two steps and
    /// the bot answers them as one.
    /// </summary>
    public bool BotWillBank { get; set; }

    /// <summary>Paid out. Set once, by settlement, so a finished match cannot pay twice.</summary>
    public bool Settled { get; set; }

    public required long OpenedAtUtc { get; init; }

    /// <summary>When each human was last heard from, for timing an absent player out.</summary>
    public ConcurrentDictionary<string, long> LastSeenUtc { get; } = new();

    public IEnumerable<string> Sessions => Occupants.Values;

    /// <summary>Listed for joining: waiting for a second human.</summary>
    public bool HasRoom => !VsBot && Match.Phase == Phase.WaitingForOpponent;

    public int? SeatOf(MongoId sessionId)
    {
        var key = sessionId.ToString();

        foreach (var pair in Occupants)
        {
            if (pair.Value == key)
            {
                return pair.Key;
            }
        }

        return null;
    }
}

/// <summary>
/// Every Farkle table, and who is at which.
///
/// In memory only. A half-played match has no business surviving a restart, and what
/// must survive -- the money -- is already recorded per player in `escrow-farkle.json`
/// against their own session, so a server that dies mid-match gives everybody their
/// stake back individually on next contact.
///
/// The "one table at a time" rule is <see cref="TableClaims"/>, shared with poker and
/// blackjack rather than copied; one instance per game, not per casino, so a player may
/// be at a Farkle table and a poker table at once. See that class for why.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SharedFarkleStore
{
    private readonly ConcurrentDictionary<string, FarkleTable> _tables = new();

    private readonly TableClaims _claims = new();

    public IReadOnlyCollection<FarkleTable> All => _tables.Values.ToList();

    public FarkleTable? Get(string tableId) =>
        _tables.TryGetValue(tableId, out var table) ? table : null;

    public FarkleTable? For(MongoId sessionId) =>
        _claims.TableOf(sessionId) is { } tableId ? Get(tableId) : null;

    public bool TryClaim(MongoId sessionId, string tableId) => _claims.TryClaim(sessionId, tableId);

    public void Release(MongoId sessionId) => _claims.Release(sessionId);

    public void Add(FarkleTable table) => _tables[table.Id] = table;

    /// <summary>
    /// Forgets a table and frees everybody at it. Only correct once every stake has been
    /// settled or refunded -- the escrow rows are the safety net, not the plan.
    /// </summary>
    public void Remove(string tableId)
    {
        if (_tables.TryRemove(tableId, out var table))
        {
            _claims.ReleaseAll(table.Sessions);
        }
    }

    /// <summary>Test seam.</summary>
    public void Seed(FarkleTable table, params MongoId[] sitting)
    {
        Add(table);

        foreach (var session in sitting)
        {
            _claims.Seed(session, table.Id);
        }
    }
}
