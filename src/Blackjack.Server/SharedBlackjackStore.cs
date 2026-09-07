using System.Collections.Concurrent;
using Blackjack.Game;
using Casino.Server;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Blackjack.Server;

/// <summary>
/// A blackjack table several people sit at.
///
/// The engine inside is an ordinary <see cref="BlackjackTable"/> with more than one box
/// occupied. It does not know it is shared and does not need to; what is new here is the
/// mapping from a seat to the person in it, and the money that belongs to them.
///
/// **Guarded by <c>Casino.Server.TableGate</c>, keyed by <see cref="Id"/>, and that gate
/// is the OUTER lock** -- a session gate is never held while reaching for it. Same rule
/// as poker, same reason: settling a round touches the table and several profiles
/// together, so the order has to be fixed or two requests deadlock.
///
/// ## No bots, and no chip stack
///
/// Both differences from poker's table, and both make this simpler.
///
/// An empty chair can just be empty: blackjack against the dealer plays identically
/// whether one box or five are in the round, so nobody has to fill a seat to keep the
/// game the same. Poker needs bots because two-handed hold'em is a different game.
///
/// And there is no stack. A blackjack wager is taken and settled inside one round, so
/// between rounds a seat owes nothing and is owed nothing -- which is why escrow here
/// holds a bet rather than a running stack, exactly as the single-player table already
/// does.
/// </summary>
public sealed class SharedBlackjackTable
{
    public required string Id { get; init; }

    public required string HostName { get; init; }

    public required BlackjackTable Table { get; init; }

    /// <summary>Seat index to the session sitting in it. Absent means an empty chair.</summary>
    public required ConcurrentDictionary<int, string> Occupants { get; init; }

    /// <summary>
    /// What one box may stake, fixed for the table.
    ///
    /// One currency per table for the same reason poker has one: a table where somebody
    /// is playing dollars beside somebody playing roubles needs an exchange rate to say
    /// what the minimum even is, and there is no rate a player would agree with.
    /// </summary>
    public required Wallet Wallet { get; init; }

    public required int MinBet { get; init; }

    public required int MaxBet { get; init; }

    public required long OpenedAtUtc { get; init; }

    /// <summary>When each seat was last heard from, for timing an absent player out.</summary>
    public ConcurrentDictionary<string, long> LastSeenUtc { get; } = new();

    public IEnumerable<string> Sessions => Occupants.Values;

    public bool HasRoom => Occupants.Count < Table.Seats.Count;

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

    /// <summary>The lowest chair nobody is in, or null when the table is full.</summary>
    public int? FirstFreeSeat()
    {
        for (var index = 0; index < Table.Seats.Count; index++)
        {
            if (!Occupants.ContainsKey(index))
            {
                return index;
            }
        }

        return null;
    }
}

/// <summary>
/// Every shared blackjack table, and who is at which.
///
/// Beside <see cref="TableStore"/> rather than replacing it, exactly as poker's is: the
/// session-keyed store is what every existing player has and it keeps working untouched.
///
/// In memory only. A half-played round has no business surviving a restart, and what
/// must survive -- the money -- is already recorded per player in `escrow-blackjack.json`
/// against their own session, so a server that dies mid-round gives everybody their bet
/// back individually.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SharedBlackjackStore
{
    private readonly ConcurrentDictionary<string, SharedBlackjackTable> _tables = new();

    /// <summary>
    /// The "one table at a time" rule, shared with poker's store rather than copied.
    /// See <see cref="TableClaims"/> -- it guards a bet against a double-clicked join.
    /// </summary>
    private readonly TableClaims _claims = new();

    public IReadOnlyCollection<SharedBlackjackTable> All => _tables.Values.ToList();

    public SharedBlackjackTable? Get(string tableId) =>
        _tables.TryGetValue(tableId, out var table) ? table : null;

    public SharedBlackjackTable? For(MongoId sessionId) =>
        _claims.TableOf(sessionId) is { } tableId ? Get(tableId) : null;

    public bool TryClaim(MongoId sessionId, string tableId) => _claims.TryClaim(sessionId, tableId);

    public void Release(MongoId sessionId) => _claims.Release(sessionId);

    public void Add(SharedBlackjackTable table) => _tables[table.Id] = table;

    /// <summary>
    /// Forgets a table and frees everybody at it.
    ///
    /// Only correct once every seat has settled -- this drops the occupants, so anything
    /// still owed has to have been paid first. The escrow rows are per player and survive
    /// regardless, which is the safety net rather than the plan.
    /// </summary>
    public void Remove(string tableId)
    {
        if (_tables.TryRemove(tableId, out var table))
        {
            _claims.ReleaseAll(table.Sessions);
        }
    }

    /// <summary>Test seam, public for the same reason <see cref="TableStore.Seed"/> is.</summary>
    public void Seed(SharedBlackjackTable table, params MongoId[] sitting)
    {
        Add(table);

        foreach (var session in sitting)
        {
            _claims.Seed(session, table.Id);
        }
    }
}
