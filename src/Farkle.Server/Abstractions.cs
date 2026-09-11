using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Farkle.Server;

/// <summary>
/// What the service needs from SPT, as interfaces so it can be tested without a
/// running server. SPT's DI registers a class against every interface it implements,
/// so <see cref="Bank"/>, <see cref="ProfileGateway"/> and the rest resolve for these
/// with no extra wiring.
///
/// The same set the other four tables carry, minus stats and minus a random source of
/// its own: Farkle takes <see cref="Casino.Server.IRandomSource"/> from the casino,
/// which is where the third copy of that pair was supposed to go.
/// </summary>
public interface IBank
{
    int GetBalance(MongoId sessionId, Wallet wallet);

    /// <summary>
    /// Takes money. False means nothing was touched, so the caller must not seat a
    /// player it has not been paid for.
    /// </summary>
    bool TryDebit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output);

    /// <summary>
    /// Pays money back, splitting it across stacks and posting anything the stash
    /// refuses as mail rather than losing it.
    /// </summary>
    void Credit(MongoId sessionId, Wallet wallet, int amount, ItemEventRouterResponse output);

    /// <summary>The running server's stack limit for a wallet, clamped to at least 1.</summary>
    int MaxStackSize(Wallet wallet);
}

public interface IProfileGateway
{
    bool HasProfile(MongoId sessionId);

    /// <summary>The player's own nickname, or null when the session resolves to nobody.</summary>
    string? NameOf(MongoId sessionId);

    /// <summary>Flushes changes to disk. Money that is not saved did not move.</summary>
    Task SaveAsync(MongoId sessionId);
}

/// <summary>
/// The mod's own logging, as an interface so the service can be given a quiet one in
/// a test rather than a real server's console.
/// </summary>
public interface IFarkleLog
{
    void Info(string message);

    void Detail(string message);

    void Error(string message);
}

/// <summary>What the table is holding of one player's money.</summary>
public class OutstandingStake
{
    public string Wallet { get; set; } = nameof(Server.Wallet.Roubles);

    /// <summary>The stake taken at sit-down and not yet settled.</summary>
    public int Amount { get; set; }

    public long TakenAtUtc { get; set; }
}

/// <summary>
/// The record of money the house is holding for a player, for the length of a match.
/// See <see cref="EscrowStore"/>.
/// </summary>
public interface IEscrowStore
{
    OutstandingStake? Get(MongoId sessionId);

    void Record(MongoId sessionId, Wallet wallet, int amount);

    void Release(MongoId sessionId);
}

/// <summary>
/// A change record for a session other than the one whose request this is.
///
/// Settling a match pays the winner, who may not be the player whose request ended it.
/// An `ItemEventRouterResponse` is per session -- it is the change list handed back to
/// ONE client -- so crediting the winner through the loser's response would put the
/// winner's roubles into the wrong client's reply. Blackjack found that the hard way.
/// `EventOutputHolder` is a concrete SPT type, so this is the seam a test can fake.
/// </summary>
public interface IOutputs
{
    ItemEventRouterResponse For(MongoId sessionId);
}
