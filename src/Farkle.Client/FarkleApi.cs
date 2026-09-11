using System;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Farkle.Client
{
    /// <summary>
    /// Talks to the server mod.
    ///
    /// Everything goes through SPT's own <see cref="RequestHandler"/>, which already
    /// knows the backend address, attaches the session cookie, speaks HTTPS to the
    /// self-signed certificate and handles the zlib framing the listener expects.
    ///
    /// Responses come back as JObject rather than typed models. The client renders
    /// what it is handed and never decides anything, so a shape it half-understands is
    /// better than a deserialiser that throws on an unfamiliar field.
    ///
    /// One route so far. The play routes arrive with Phase 2.
    /// </summary>
    internal static class FarkleApi
    {
        internal static JObject Ping() => Post("/farkle/ping", "{}");

        private static JObject Post(string route, string json)
        {
            try
            {
                var body = RequestHandler.PostJson(route, json);

                if (string.IsNullOrEmpty(body))
                {
                    FarkleClientPlugin.Log?.LogWarning($"[Farkle] {route} returned nothing.");
                    return null;
                }

                return JObject.Parse(body);
            }
            catch (Exception ex)
            {
                // A failed request must not take the menu down with it. The caller
                // shows the player that something went wrong and stays open.
                FarkleClientPlugin.Log?.LogError($"[Farkle] {route} failed: {ex.Message}");
                return null;
            }
        }
    }
}
