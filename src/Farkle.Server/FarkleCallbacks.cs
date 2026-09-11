using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Farkle.Server;

/// <summary>
/// HTTP adapter. Serialises what <see cref="FarkleService"/> decided and surfaces
/// anything worth seeing to the server console.
///
/// Deliberately holds no game logic -- everything worth testing lives one layer down,
/// where it is reachable without a running server.
/// </summary>
[Injectable]
public class FarkleCallbacks(HttpResponseUtil httpResponseUtil, FarkleService service, FarkleLog log)
{
    public async ValueTask<string> Ping(PingRequest info, MongoId sessionId)
    {
        var response = await service.PingAsync(sessionId);

        log.Info($"ping from session '{response.SessionId}' -- profile {(response.HasProfile ? "found" : "NOT FOUND")}");

        if (!response.HasProfile)
        {
            log.Error("no profile for that session. If the id above is blank, the session cookie did not resolve.");
        }

        return httpResponseUtil.NoBody(response);
    }
}
