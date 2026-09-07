using System.Collections.Concurrent;
using Poker.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Poker.Server;

/// <summary>Where a seat's occupant came from.</summary>
public enum SeatKind
{
    /// <summary>Nobody has taken it; an agent plays it.</summary>
    Bot,

    /// <summary>A person, with a profile and real money behind their stack.</summary>
    Human,
}

/// <summary>One seat at a shared table.</summary>
public sealed class SharedSeat
{
    public required int Index { get; init; }

    public required SeatKind Kind { get; init; }

    /// <summary>The player's session, when <see cref="Kind"/> is Human. Null for a bot.</summary>
    public string? SessionId { get; init; }

    /// <summary>What the table shows above the seat.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// When this player was last heard from, as unix seconds.
    ///
    /// A seat that goes quiet folds rather than stopping the hand -- four other seats
    /// are waiting and a table that hangs because somebody closed the game is worse
    /// than one that plays on. See <c>docs/memory/2026-09-07-shared-table-design.md</c>.
    /// Meaningless for a bot, which is never late.
    /// </summary>
    public long LastSeenUtc { get; set; }
}

/// <summary>
/// A table several people sit at.
///
/// The engine inside is an ordinary <see cref="HoldemTable"/> -- it does not know it is
/// shared, and does not need to. What is new is the mapping from a seat index to the
/// person sitting in it, which is the thing the single-player model never had: there,
/// the human was always seat 0 by construction.
///
/// **Every field here is guarded by <c>Casino.Server.TableGate</c>, keyed by
/// <see cref="Id"/>, and that gate is the OUTER lock.** A session gate is never held
/// while reaching for it. See the type's own remarks for why the other order deadlocks
/// on a table that charges blinds.
/// </summary>
public sealed class SharedTable
{
    public required string Id { get; init; }

    /// <summary>Who opened it, for the lobby list. Not a permission -- there is no owner.</summary>
    public required string HostName { get; init; }

    public required HoldemTable Table { get; init; }

    public required List<BotAgent> Agents { get; init; }

    public required IReadOnlyList<PokerPersonality> Characters { get; init; }

    /// <summary>Seat index to occupant, every seat present from the moment it is created.</summary>
    public required Dictionary<int, SharedSeat> Seats { get; init; }

    public required int BuyIn { get; init; }

    public required int BigBlind { get; init; }

    /// <summary>
    /// What the buy-in is paid in, fixed for the table rather than per player.
    ///
    /// One chip has to mean one thing at a table, or a pot is not a number. Somebody
    /// sitting down in dollars against somebody in roubles would need an exchange rate,
    /// and there is no rate a player would agree with -- the same reason the single
    /// player's stats keep currencies apart instead of summing them.
    /// </summary>
    public required Wallet Wallet { get; init; }

    public required long OpenedAtUtc { get; init; }

    public IEnumerable<SharedSeat> HumanSeats =>
        Seats.Values.Where(seat => seat.Kind == SeatKind.Human);

    public IEnumerable<string> HumanSessions =>
        HumanSeats.Select(seat => seat.SessionId!).Where(id => id is not null);

    public bool HasRoom => Seats.Values.Any(seat => seat.Kind == SeatKind.Bot);

    public SharedSeat? SeatOf(MongoId sessionId)
    {
        var key = sessionId.ToString();
        return Seats.Values.FirstOrDefault(
            seat => seat.Kind == SeatKind.Human && seat.SessionId == key);
    }
}

/// <summary>
/// Every shared table on the server, and who is at which.
///
/// ## Why this sits beside <see cref="TableStore"/> rather than replacing it
///
/// `TableStore` is keyed by session: one player, their own table, nobody else. That is
/// what every existing player has and it keeps working untouched -- a shared table that
/// broke the solo one would be a bad trade. So shared tables live here, private ones
/// stay there, and the service checks here first.
///
/// ## In memory, like its neighbour, and for the same reason
///
/// A half-played hand has no business surviving a restart. What must survive is the
/// money, and that is already handled per player by `EscrowStore` -- each human's stack
/// is recorded against their own session, so a server that dies mid-hand gives everyone
/// their chips back individually. Nothing about a shared table changes where the money
/// lives; the pot is chips, and chips only become currency when somebody stands up.
///
/// ## The one invariant
///
/// **A player is at one table at a time, shared or private.** `Casino.Server.TableClaims`
/// is what makes that cheap to check and cheap to enforce -- without it, finding
/// somebody's table means walking every table's seats on every request, and worse, two
/// concurrent joins could seat one person twice with two buy-ins taken.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SharedTableStore
{
    private readonly ConcurrentDictionary<string, SharedTable> _tables = new();

    /// <summary>
    /// Session to table, and the "one table at a time" rule that guards a buy-in.
    ///
    /// Shared with blackjack's store rather than written twice -- see
    /// <see cref="Casino.Server.TableClaims"/> for why that rule is money and not
    /// bookkeeping, and why each game owns its own instance instead of the casino
    /// having one.
    /// </summary>
    private readonly Casino.Server.TableClaims _claims = new();

    public IReadOnlyCollection<SharedTable> All => _tables.Values.ToList();

    public SharedTable? Get(string tableId) =>
        _tables.TryGetValue(tableId, out var table) ? table : null;

    /// <summary>The shared table this player is sitting at, or null if they are not at one.</summary>
    public SharedTable? For(MongoId sessionId) =>
        _claims.TableOf(sessionId) is { } tableId ? Get(tableId) : null;

    /// <summary>
    /// Claims a place for this player before anything expensive happens.
    ///
    /// The caller must <see cref="Release"/> it if the join then fails, or the player is
    /// stranded at a table they never sat down at.
    /// </summary>
    public bool TryClaim(MongoId sessionId, string tableId) => _claims.TryClaim(sessionId, tableId);

    /// <summary>Gives up a claim. Safe to call when there is nothing to give up.</summary>
    public void Release(MongoId sessionId) => _claims.Release(sessionId);

    public void Add(SharedTable table) => _tables[table.Id] = table;

    /// <summary>
    /// Forgets a table and everyone at it.
    ///
    /// Only correct once every human has been cashed out -- this drops the seats, so
    /// anything still owed has to have been settled first. The escrow rows are per
    /// player and survive this regardless, which is the safety net rather than the plan.
    /// </summary>
    public void Remove(string tableId)
    {
        if (!_tables.TryRemove(tableId, out var table))
        {
            return;
        }

        _claims.ReleaseAll(table.HumanSessions);
    }

    /// <summary>
    /// Test seam, and public for the same reason <see cref="TableStore.Seed"/> is: no
    /// `*.Server` project has an `InternalsVisibleTo`, so `internal` here just means the
    /// tests cannot reach it.
    /// </summary>
    public void Seed(SharedTable table, params MongoId[] sitting)
    {
        Add(table);

        foreach (var session in sitting)
        {
            _claims.Seed(session, table.Id);
        }
    }
}
