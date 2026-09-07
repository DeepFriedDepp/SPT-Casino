using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;

namespace Casino.Server;

/// <summary>
/// Which shared table a player is sitting at, and the rule that they are only ever at one.
///
/// ## Why this is money and not bookkeeping
///
/// Sitting down takes a buy-in out of a real stash. A player who ends up seated twice has
/// paid twice -- and the second seat is at a table nothing will ever cash them out of,
/// because every lookup finds only one. A double-clicked "join" is the ordinary way to
/// get there, and SPT does not serialise requests.
///
/// So <see cref="TryClaim"/> is an atomic `TryAdd` and never a read followed by a write.
/// A check-then-act here is the same defect the money work spent a day removing from the
/// four tables, except it costs a whole buy-in rather than a blind.
///
/// ## One instance per game, not one for the casino
///
/// Each game's store owns its own. That is deliberate rather than lazy: escrow is already
/// per game -- `escrow-poker.json` and `escrow-blackjack.json` are separate files keyed by
/// session -- so the money model underneath already assumes somebody can owe and be owed
/// at both. A casino-wide claim would be a stricter rule than the thing it is protecting
/// needs, and would stop a player sitting at a poker table and a blackjack table at once
/// for no reason anybody could point at.
///
/// ## Why this is shared code and the stores are not
///
/// Poker's shared table came first and blackjack's is the second. This class is the part
/// the two genuinely have in common; their tables are not -- poker's seats hold bots and
/// personalities, blackjack's hold a pending bet and can simply be empty.
///
/// Extracted at the second case rather than the first. This repo's oldest habit is four
/// copies of `Bank.cs` drifting apart until a bug in one is a bug in all four, and the
/// cure for that is not an abstraction guessed from a single example.
/// </summary>
public sealed class TableClaims
{
    private readonly ConcurrentDictionary<string, string> _whereTheyAre = new();

    /// <summary>The table this player is at, or null if they are not at one.</summary>
    public string? TableOf(MongoId sessionId) =>
        _whereTheyAre.TryGetValue(sessionId.ToString(), out var tableId) ? tableId : null;

    /// <summary>
    /// Claims a place before anything expensive happens.
    ///
    /// False when they are already somewhere. The caller must <see cref="Release"/> it if
    /// the join then fails, or the player is stranded at a table they never sat down at
    /// and cannot join another.
    /// </summary>
    public bool TryClaim(MongoId sessionId, string tableId) =>
        _whereTheyAre.TryAdd(sessionId.ToString(), tableId);

    /// <summary>Gives up a claim. Safe to call when there is nothing to give up.</summary>
    public void Release(MongoId sessionId) =>
        _whereTheyAre.TryRemove(sessionId.ToString(), out _);

    /// <summary>Frees everybody at one table, for when the table itself goes.</summary>
    public void ReleaseAll(IEnumerable<string> sessionIds)
    {
        foreach (var session in sessionIds)
        {
            _whereTheyAre.TryRemove(session, out _);
        }
    }

    /// <summary>Test seam. Not used at runtime.</summary>
    public void Seed(MongoId sessionId, string tableId) =>
        _whereTheyAre[sessionId.ToString()] = tableId;
}
