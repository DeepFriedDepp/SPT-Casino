using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Poker.Client
{
    /// <summary>
    /// Talks to the shared-table half of the server mod.
    ///
    /// The same shape as <see cref="PokerApi"/> and for the same reasons -- everything
    /// through SPT's own <see cref="RequestHandler"/>, PascalCase bodies, and JObject
    /// back rather than typed models so a shape the client half-understands is better
    /// than a deserialiser that throws on an unfamiliar field.
    ///
    /// **Every action is a request, including at a shared table.** The socket only ever
    /// pushes downward: it has no session gate and no item-event response behind it, so
    /// a bet sent that way is a bet that never reaches the stash. What arrives on the
    /// socket is somebody else's move, and the only thing to do with it is redraw.
    /// </summary>
    internal static class SharedPokerApi
    {
        /// <summary>Open tables, and which one this player is already at.</summary>
        internal static JObject Tables() => Post("/poker/shared/tables", "{}");

        internal static JObject State() => Post("/poker/shared/state", "{}");

        internal static JObject Deal() => Post("/poker/shared/deal", "{}");

        internal static JObject Leave() => Post("/poker/shared/leave", "{}");

        /// <summary>Opens a table others can join, and sits the host at it.</summary>
        internal static JObject Open(int seats, int buyIn, int bigBlind, int? seed = null)
        {
            var body =
                "{\"Seats\":" + Num(seats)
                + ",\"BuyIn\":" + Num(buyIn)
                + ",\"BigBlind\":" + Num(bigBlind)
                + (seed.HasValue ? ",\"Seed\":" + Num(seed.Value) : string.Empty)
                + "}";

            return Post("/poker/shared/open", body);
        }

        /// <summary>Takes a free seat at somebody else's table.</summary>
        internal static JObject Join(string tableId) =>
            Post("/poker/shared/join", "{\"Table\":\"" + Escape(tableId) + "\"}");

        internal static JObject Act(string move, int to = 0) =>
            Post("/poker/shared/act", "{\"Move\":\"" + Escape(move) + "\",\"To\":" + Num(to) + "}");

        /// <summary>
        /// Invariant formatting, so a machine with a comma decimal separator does not
        /// send a number the server's parser rejects.
        /// </summary>
        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The table id is a hex string the server made, and the move is one of four
        /// known words -- but both are pasted into JSON by hand here, so neither gets to
        /// carry a quote out of an unexpected value and truncate the body.
        /// </summary>
        private static string Escape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static JObject Post(string route, string body)
        {
            try
            {
                var raw = RequestHandler.PostJson(route, body);

                return string.IsNullOrWhiteSpace(raw)
                    ? Failed("The server said nothing.")
                    : JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                PokerClientPlugin.Log.LogError("[Poker] " + route + " failed: " + ex.Message);

                return Failed(ex.Message);
            }
        }

        private static JObject Failed(string error) =>
            new JObject { ["Ok"] = false, ["Error"] = error };
    }
}
