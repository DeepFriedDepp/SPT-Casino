using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Blackjack.Client
{
    /// <summary>
    /// Talks to the shared-table half of the server mod.
    ///
    /// The same shape as <see cref="BlackjackApi"/> and for the same reasons --
    /// everything through SPT's own <see cref="RequestHandler"/>, PascalCase bodies, and
    /// JObject back rather than typed models so a shape the client half-understands is
    /// better than a deserialiser that throws on an unfamiliar field.
    ///
    /// **Every action is a request, including at a shared table.** The socket only ever
    /// pushes downward: it has no session gate and no item-event response behind it, so a
    /// bet sent that way is a bet that never reaches the stash. What arrives on the socket
    /// is somebody else's move, and the only thing to do with it is redraw.
    ///
    /// ## Betting and dealing are two calls here, unlike the solo table's one
    ///
    /// `BlackjackApi.Deal(wager)` bets and deals together, which is what "deal" means when
    /// there is nobody else at the felt. At a shared table the money goes in the box first
    /// and every box has to have had its chance before a card is turned -- so
    /// <see cref="Bet"/> takes the money and <see cref="Deal"/> starts the round, and
    /// anybody seated may call the second.
    /// </summary>
    internal static class SharedBlackjackApi
    {
        /// <summary>Open tables, and which one this player is already at.</summary>
        internal static JObject Tables() => Post("/blackjack/shared/tables", "{}");

        internal static JObject State() => Post("/blackjack/shared/state", "{}");

        internal static JObject Deal() => Post("/blackjack/shared/deal", "{}");

        internal static JObject Leave() => Post("/blackjack/shared/leave", "{}");

        /// <summary>Opens a table others can join, and seats the host at box 0.</summary>
        internal static JObject Open(int seats, string wallet) =>
            Post(
                "/blackjack/shared/open",
                "{\"Seats\":" + Num(seats) + ",\"Wallet\":\"" + Escape(wallet) + "\"}");

        /// <summary>Takes a free box at somebody else's table.</summary>
        internal static JObject Join(string tableId) =>
            Post("/blackjack/shared/join", "{\"TableId\":\"" + Escape(tableId) + "\"}");

        /// <summary>
        /// Puts a stake in this player's box. The money leaves the stash here, before any
        /// card is dealt -- so a refusal from this call means nothing has moved.
        /// </summary>
        internal static JObject Bet(int wager, string wallet) =>
            Post(
                "/blackjack/shared/bet",
                "{\"Wager\":" + Num(wager) + ",\"Wallet\":\"" + Escape(wallet) + "\"}");

        internal static JObject Act(string action) =>
            Post("/blackjack/shared/action", "{\"Action\":\"" + Escape(action) + "\"}");

        /// <summary>
        /// Invariant formatting, so a machine with a comma decimal separator does not send
        /// a number the server's parser rejects.
        /// </summary>
        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The table id is a hex string the server made and the action is one of four
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
                BlackjackClientPlugin.Log.LogError("[Blackjack] " + route + " failed: " + ex.Message);

                return Failed(ex.Message);
            }
        }

        private static JObject Failed(string error) =>
            new JObject { ["Ok"] = false, ["Error"] = error };
    }
}
