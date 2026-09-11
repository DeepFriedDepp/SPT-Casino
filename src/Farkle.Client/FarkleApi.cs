using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Responses come back as JObject rather than typed models. The client renders what
    /// it is handed and never decides anything, so a shape it half-understands is better
    /// than a deserialiser that throws on an unfamiliar field.
    ///
    /// **Every action is a request.** The socket only ever pushes downward: what arrives
    /// on it is the other seat's move, and the only thing to do with it is redraw.
    /// </summary>
    internal static class FarkleApi
    {
        internal static JObject Ping() => Post("/farkle/ping", "{}");

        internal static JObject Tables() => Post("/farkle/tables", "{}");

        internal static JObject State() => Post("/farkle/state", "{}");

        internal static JObject Roll() => Post("/farkle/roll", "{}");

        internal static JObject Bank() => Post("/farkle/bank", "{}");

        internal static JObject Leave() => Post("/farkle/leave", "{}");

        /// <summary>
        /// Opens a table. The stake leaves the stash here, so a refusal from this call
        /// means nothing has moved. PascalCase keys, like every body in the casino: SPT
        /// matches case-sensitively and a lowercase key binds to nothing.
        /// </summary>
        internal static JObject Open(long stake, int target, bool vsBot, string bot) =>
            Post(
                "/farkle/open",
                "{\"Stake\":" + Num(stake) + ",\"Target\":" + Num(target) + ",\"VsBot\":" + (vsBot ? "true" : "false")
                + ",\"Bot\":\"" + Escape(bot ?? string.Empty) + "\"}");

        internal static JObject Join(string tableId) =>
            Post("/farkle/join", "{\"TableId\":\"" + Escape(tableId) + "\"}");

        /// <summary>Sets dice aside, by position in the showing roll.</summary>
        internal static JObject Keep(IEnumerable<int> indices) =>
            Post("/farkle/keep", "{\"Indices\":[" + string.Join(",", indices.Select(Num)) + "]}");

        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Escape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static JObject Post(string route, string body)
        {
            try
            {
                var raw = RequestHandler.PostJson(route, body);

                return string.IsNullOrWhiteSpace(raw) ? Failed("The server said nothing.") : JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                // A failed request must not take the menu down with it. The caller shows
                // the player that something went wrong and stays open.
                FarkleClientPlugin.Log?.LogError("[Farkle] " + route + " failed: " + ex.Message);

                return Failed(ex.Message);
            }
        }

        private static JObject Failed(string error) => new JObject { ["Ok"] = false, ["Error"] = error };
    }
}
