using SPTarkov.Server.Core.Models.Common;

namespace Farkle.Server;

/// <summary>
/// What the service needs from SPT, as interfaces so it can be tested without a
/// running server. SPT's DI registers a class against every interface it implements,
/// so <see cref="ProfileGateway"/> resolves for this with no extra wiring.
///
/// **Deliberately short.** The other four tables carry `IBank`, `IEscrowStore`,
/// `IRandomSource` and `IStatsStore` here. Farkle does not yet, because whether it
/// moves money at all is open decision #2 in the work order, and a bank with nothing
/// to bank is a fifth copy of `Bank.cs` drifting for no reason. They arrive with
/// Phase 2, once that is decided -- see `docs/farkle.md`.
/// </summary>
public interface IProfileGateway
{
    bool HasProfile(MongoId sessionId);

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
