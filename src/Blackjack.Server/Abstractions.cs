using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server;

/// <summary>
/// Everything the game logic needs to do with the player's money.
///
/// This exists as an interface because SPT's InventoryHelper and ProfileHelper are
/// concrete classes with non-virtual methods -- depending on them directly makes
/// the calling code impossible to test without a running server. SPT's DI registers
/// a class against every interface it implements, so <see cref="Bank"/> resolves
/// for this with no extra wiring.
///
/// Note it takes a session id rather than a PmcData: that keeps every SPT profile
/// model out of the game logic entirely.
/// </summary>
public interface IBank
{
    int GetBalance(MongoId sessionId, Wallet wallet);

    /// <summary>
    /// Takes money. False means nothing was touched.
    ///
    /// <paramref name="output"/> collects what changed. Handed back to the client, it
    /// is what keeps the stash view in step; discarded, the money moves on the server
    /// and the client's own copy never hears about it.
    /// </summary>
    bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output);

    void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output);

    /// <summary>
    /// The running server's stack limit for a wallet, which item mods change. Exposed
    /// so startup can report what is actually in force rather than what is assumed.
    /// </summary>
    int MaxStackSize(Wallet wallet);
}

public interface IProfileGateway
{
    bool HasProfile(MongoId sessionId);

    /// <summary>
    /// The player's PMC nickname -- what everybody else at a shared table calls them.
    ///
    /// Poker shipped a shared table without this and it went wrong in exactly the way
    /// the engine's fallback invites: an unnamed person is called "You", which is a
    /// relationship to whoever is looking rather than a name, so the same word reached
    /// everybody and one player saw their friend labelled "You". The table stores real
    /// names and only the panel says "you".
    ///
    /// Null when the profile cannot be read; callers fall back rather than refusing a
    /// seat over a label.
    /// </summary>
    string? NameOf(MongoId sessionId);

    /// <summary>Flushes money changes to disk. Money that is not saved did not move.</summary>
    Task SaveAsync(MongoId sessionId);
}

/// <summary>
/// Lifetime stats per profile. An interface for the same reason the others are --
/// so the accounting can be tested without a filesystem.
/// </summary>
public interface IStatsStore
{
    PlayerStats Get(MongoId sessionId);

    void Save(MongoId sessionId, PlayerStats stats);
}

/// <summary>
/// Money taken from a player whose round has not settled. See <see cref="EscrowStore"/>
/// for why an in-memory table makes this necessary.
/// </summary>
public interface IEscrowStore
{
    OutstandingStake? Get(MongoId sessionId);

    void Hold(MongoId sessionId, Wallet wallet, int amount);

    void Release(MongoId sessionId);
}

/// <summary>
/// Where a player's item changes are collected for the reply to THEIR client.
///
/// ## Why the shared table needs this and the solo table never did
///
/// An <see cref="ItemEventRouterResponse"/> is per session: SPT holds the profile changes
/// it has made for one player and hands them back on that player's next item event. The
/// solo table only ever moves one person's money in one request, so passing the caller's
/// response down was both correct and obviously correct.
///
/// **Settling a shared round pays several people at once**, and every one of them was
/// being credited through the *acting* player's response. The server-side profiles were
/// right -- the money really moved -- but the change record went to the wrong client: the
/// player who pressed Stand was told about items that are not theirs, and everybody else
/// was told nothing and sat on a stale stash until something made them resync.
///
/// So settlement asks for each seat's own response instead. An interface because
/// `EventOutputHolder` is a concrete SPT type, and a service that names it cannot be
/// tested without a server -- the same reason <see cref="IBank"/> exists.
/// </summary>
public interface IOutputs
{
    /// <summary>
    /// This session's collector. The same instance for the same session within a request,
    /// so asking for the acting player's is the response the caller already holds.
    /// </summary>
    ItemEventRouterResponse For(MongoId sessionId);
}
