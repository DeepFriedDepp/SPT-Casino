using System;
using System.Collections.Generic;
using System.Globalization;
using Casino.Shared;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Blackjack.Client
{
    /// <summary>
    /// The shared-table half of the blackjack panel.
    ///
    /// A second file over the same class rather than a second panel, because a shared
    /// table IS the table -- same felt, same cards, same buttons. What changes is that
    /// other people's boxes are on it and the turn is not always yours. Building a
    /// separate screen would have meant a second copy of the card rendering, which is
    /// most of the panel and all of the part worth getting right.
    ///
    /// ## What is genuinely different from the solo table
    ///
    /// **Betting and dealing come apart.** Alone, "deal" means bet and deal at once,
    /// because there is nobody to wait for. Here the money goes in the box first, every
    /// box gets its chance, and then anybody seated may start the round.
    ///
    /// **The turn belongs to a seat.** `Phase == "PlayerTurn"` is true while somebody
    /// else is playing, so the action buttons hang off `ActiveSeat == YourSeat` instead.
    /// Reading the phase alone would put HIT in front of a player whose turn it is not,
    /// and the server would refuse every press.
    ///
    /// **Nothing here is filtered per viewer, and it should not be.** Every player's
    /// cards are face up in blackjack; the one concealed card is the dealer's hole card
    /// and the server hides it from everybody equally. Poker needed a view per person
    /// because hole cards are secret -- copying that here would make the game wrong.
    /// </summary>
    internal static partial class BlackjackPanel
    {
        /// <summary>How many boxes a table opened from here has.</summary>
        private const int TableSeats = 5;

        private static bool _shared;

        /// <summary>
        /// Which table, once known. Null until a reply or a push says.
        ///
        /// One socket carries the whole casino, so a push has to be checked against this
        /// before it is drawn -- otherwise a poker table's frame redraws the blackjack
        /// felt with nothing on it.
        /// </summary>
        private static string _tableId;

        /// <summary>
        /// Which box is this player's.
        ///
        /// Sent by the server rather than worked out here. Every box looks alike in the
        /// view -- `IsOccupied` says a person is there, not which person -- so a client
        /// without this has no way to tell its own cards from its neighbour's, and would
        /// have to guess from a name two players could share.
        /// </summary>
        private static int _yourSeat = -1;

        /// <summary>The other people's boxes, drawn under the player's own hand.</summary>
        private static RectTransform _othersRow;

        private static GameObject _sharedPanel;
        private static RectTransform _tableList;
        private static TextMeshProUGUI _lobbyNote;

        // ------------------------------------------------------------------- the lobby

        /// <summary>
        /// Asks the server what is open and draws the list.
        ///
        /// Every entry into this lobby is a button press, so the request is made the same
        /// way every other one here is. There is no timer behind it: a lobby that refetched
        /// itself would be a request every few seconds for the whole time somebody leaves
        /// the panel open, and the socket -- not polling -- is what keeps a table in view
        /// being live.
        /// </summary>
        private static void ShowSharedLobby(string note = null)
        {
            if (_sharedPanel == null)
            {
                return;
            }

            HideStats();

            _shared = false;
            _sharedPanel.SetActive(true);

            Clear(_tableList);

            var reply = SharedBlackjackApi.Tables();
            var yours = (string)reply?["YourTable"];

            _tableId = yours;

            if (!TruthOf(reply))
            {
                SetLobbyNote(ErrorOf(reply) ?? "Could not reach the server.", Bad);
            }
            else
            {
                SetLobbyNote(
                    note ?? (yours != null
                        ? "You are already at a table. Go back to it rather than opening another."
                        : "A shared table costs nothing to sit at -- only the bets you place"
                          + " leave your stash, exactly as they do alone."),
                    note != null ? Ink : Faint);
            }

            var tables = reply?["Tables"] as JArray;

            if (tables == null || tables.Count == 0)
            {
                SetSize(
                    Label(_tableList, "Nobody has a table open.", 19f, Faint, TextAlignmentOptions.Center)
                        .rectTransform,
                    760f,
                    30f);
            }
            else
            {
                foreach (var entry in tables)
                {
                    BuildTableRow(entry as JObject);
                }
            }

            Clear(_actionRow);

            if (yours != null)
            {
                Chip(_actionRow, "BACK TO YOUR TABLE", 260f, ReturnToShared, primary: true);
            }
            else
            {
                Chip(_actionRow, "OPEN A TABLE", 200f, OpenShared, primary: true);
            }

            Chip(_actionRow, "REFRESH", 150f, () => ShowSharedLobby());
            Chip(_actionRow, "PLAY ALONE", 180f, LeaveSharedLobby);
        }

        /// <summary>
        /// One open table, with what it would cost to play at it.
        ///
        /// The minimum is shown rather than a buy-in because there is no buy-in: sitting
        /// down at a blackjack table costs nothing, and the number that matters is the
        /// smallest bet the table will take.
        /// </summary>
        private static void BuildTableRow(JObject table)
        {
            if (table == null)
            {
                return;
            }

            var id = (string)table["Id"];
            var host = (string)table["HostName"] ?? "Somebody";
            var seats = table["Seats"]?.ToObject<int>() ?? 0;
            var free = table["FreeSeats"]?.ToObject<int>() ?? 0;
            var min = table["MinBet"]?.ToObject<long>() ?? 0;
            var max = table["MaxBet"]?.ToObject<long>() ?? 0;
            var wallet = (string)table["Wallet"] ?? "Roubles";
            var inRound = table["InRound"]?.ToObject<bool>() ?? false;

            var row = NewBox("Table", _tableList, new Color(0f, 0f, 0f, 0.30f), 10, Gold, 1);
            SetSize(row, 780f, 56f);

            var line = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            line.childAlignment = TextAnchor.MiddleLeft;
            line.spacing = 12f;
            line.padding = new RectOffset(16, 16, 0, 0);
            line.childForceExpandWidth = false;
            line.childForceExpandHeight = false;
            line.childControlWidth = false;
            line.childControlHeight = false;

            SetSize(
                Label(row, host + "'s table", 20f, Ink, TextAlignmentOptions.Left).rectTransform,
                240f,
                30f);

            SetSize(
                Label(
                    row,
                    (seats - free) + " of " + seats + " boxes",
                    17f,
                    Faint,
                    TextAlignmentOptions.Left).rectTransform,
                140f,
                26f);

            SetSize(
                Label(
                    row,
                    min.ToString("N0", CultureInfo.InvariantCulture) + " - "
                    + max.ToString("N0", CultureInfo.InvariantCulture) + " " + Short(wallet),
                    17f,
                    Faint,
                    TextAlignmentOptions.Left).rectTransform,
                200f,
                26f);

            // A full table and a table mid-round both refuse, and they refuse for
            // different reasons -- so they say different things rather than sharing one
            // greyed button that leaves the player guessing which it was.
            if (free <= 0)
            {
                SetSize(Label(row, "FULL", 17f, Faint, TextAlignmentOptions.Center).rectTransform, 120f, 26f);
                return;
            }

            if (inRound)
            {
                SetSize(
                    Label(row, "IN A ROUND", 17f, Faint, TextAlignmentOptions.Center).rectTransform,
                    120f,
                    26f);

                return;
            }

            var captured = id;
            Chip(row, "SIT DOWN", 130f, () => JoinShared(captured));
        }

        private static void JoinShared(string tableId)
        {
            var reply = SharedBlackjackApi.Join(tableId);

            if (!TruthOf(reply))
            {
                // Still in the lobby, and the list on screen is still the one that was
                // drawn. The usual refusals -- the table filled up, or a round started --
                // are answered by pressing refresh, so leaving the list up is the useful
                // thing to do.
                SetLobbyNote(ErrorOf(reply) ?? "Could not sit down there.", Bad);
                return;
            }

            _tableId = tableId;

            EnterShared(reply);
        }

        private static void OpenShared()
        {
            var reply = SharedBlackjackApi.Open(TableSeats, _wallet);

            if (!TruthOf(reply))
            {
                SetLobbyNote(ErrorOf(reply) ?? "Could not open a table.", Bad);
                return;
            }

            // The server names the table and this reply does not carry the name. Nothing
            // needs it until a push arrives, and the first one says which table it is
            // about -- see the check in OnPushed.
            _tableId = null;

            EnterShared(reply);
        }

        /// <summary>
        /// Puts a player back at the shared table they never stood up from.
        ///
        /// False when they are not at one, which is the ordinary answer and is not an
        /// error -- the panel then opens on the solo table as it always has.
        /// </summary>
        private static bool ResumeShared()
        {
            var state = SharedBlackjackApi.State();

            if (!TruthOf(state) || state["SharedTable"] == null)
            {
                return false;
            }

            EnterShared(state);

            return true;
        }

        private static void ReturnToShared()
        {
            var state = SharedBlackjackApi.State();

            if (!TruthOf(state))
            {
                // Gone rather than merely unreachable -- somebody left last and it closed
                // behind them. Say so from the lobby, which is where that leaves them.
                ShowSharedLobby(ErrorOf(state) ?? "That table is gone.");
                return;
            }

            EnterShared(state);
        }

        /// <summary>Puts the table on screen and starts listening for other people's moves.</summary>
        private static void EnterShared(JObject reply)
        {
            _shared = true;
            _sharedPanel.SetActive(false);

            Listen();
            RefreshBalances();
            Render(reply);
        }

        /// <summary>
        /// Back to the solo table, which is what the panel opens on.
        ///
        /// Only reachable from the lobby, so nobody is seated: leaving a table you are at
        /// is <see cref="LeaveShared"/>, and that one has money to hand back.
        /// </summary>
        private static void LeaveSharedLobby()
        {
            _shared = false;
            _tableId = null;
            _yourSeat = -1;

            Deafen();

            _sharedPanel.SetActive(false);

            Render(BlackjackApi.State(), true);
        }

        private static void LeaveShared()
        {
            var reply = SharedBlackjackApi.Leave();

            if (!TruthOf(reply))
            {
                // "Finish the round first" is the one that happens, and the table it
                // refers to is on screen and still correct.
                Say(ErrorOf(reply) ?? "Could not stand up.", Bad);
                return;
            }

            ProfileSync.Request("BlackjackSync");

            _shared = false;
            _tableId = null;
            _yourSeat = -1;

            Deafen();
            RefreshBalances();

            ShowSharedLobby((string)reply["Note"]);
        }

        /// <summary>
        /// Asks the server for the table again.
        ///
        /// On the row whenever it is not this player's turn, which is exactly when a push
        /// that never arrived would leave the screen frozen with nothing to press. The
        /// socket reconnects on its own and this is not a substitute for it; it is the one
        /// button that gets somebody unstuck without closing the panel.
        /// </summary>
        private static void RefreshShared()
        {
            var state = SharedBlackjackApi.State();

            if (!TruthOf(state))
            {
                ShowSharedLobby(ErrorOf(state) ?? "That table is gone.");
                return;
            }

            Render(state);
        }

        // ------------------------------------------------------------------ the actions

        /// <summary>Puts this player's stake in their box. Takes the money.</summary>
        private static void SharedBet()
        {
            var reply = SharedBlackjackApi.Bet((int)_wager, _wallet);

            ProfileSync.Request("BlackjackSync");
            RefreshBalances();
            Render(reply);
        }

        /// <summary>
        /// Starts the round, for everybody who has bet.
        ///
        /// Anybody seated may press it, which is deliberate: a table that could only be
        /// dealt by whoever opened it stops dead the moment that person walks away, and
        /// there is nothing to abuse -- a box that has not bet is simply not dealt to.
        /// </summary>
        private static void SharedDeal()
        {
            var reply = SharedBlackjackApi.Deal();

            ProfileSync.Request("BlackjackSync");
            RefreshBalances();
            Render(reply);
        }

        private static void SharedAct(string action)
        {
            var reply = SharedBlackjackApi.Act(action);

            ProfileSync.Request("BlackjackSync");
            RefreshBalances();
            Render(reply);
        }

        // -------------------------------------------------------------------- the push

        /// <summary>
        /// The delegate hung off the casino's push channel, kept so the same one can be
        /// taken back off. Two delegates over one method compare equal, but holding the
        /// one that was added is what makes that true rather than nearly true.
        /// </summary>
        private static Action<string> _listener;

        /// <summary>
        /// Subscribes to the casino's push channel.
        ///
        /// Through <see cref="Host"/> rather than by naming the socket, because this file
        /// is compiled into TWO assemblies: Blackjack.Client, which still builds a
        /// standalone plugin and has no websocket reference at all, and Casino.Client,
        /// which is what ships and owns the socket. Naming `CasinoSocketClient` here
        /// breaks the first build, and a project reference the other way is a cycle.
        ///
        /// In the standalone plugin nothing ever pushes, so the event never fires and the
        /// table falls back to asking. That is not a fault and is not reported as one.
        /// </summary>
        private static void Listen()
        {
            if (_listener != null)
            {
                return;
            }

            _listener = OnPushed;
            Host.Pushed += _listener;
        }

        private static void Deafen()
        {
            var listener = _listener;
            _listener = null;

            if (listener != null)
            {
                Host.Pushed -= listener;
            }
        }

        /// <summary>
        /// Somebody else moved.
        ///
        /// **Raised on Unity's main thread**, from the pump the casino runs once a frame,
        /// which is the whole reason that pump exists -- so this draws directly and needs
        /// no dispatcher.
        /// </summary>
        private static void OnPushed(string payload)
        {
            // Not while the solo table or the lobby is on screen. A player can be seated
            // at a shared table and looking at something else, and the push is about the
            // table rather than about what is being drawn.
            if (!_shared || _root == null || !_root.activeSelf)
            {
                return;
            }

            JObject message;

            try
            {
                message = JObject.Parse(payload);
            }
            catch (Exception ex)
            {
                // One socket carries the whole casino, so something that is not a
                // blackjack table is an ordinary thing to receive, not a fault.
                BlackjackClientPlugin.Log.LogDebug("[Blackjack] ignored a push it could not read: " + ex.Message);
                return;
            }

            var table = (string)message["Table"];

            if (_tableId != null && !string.Equals(table, _tableId, StringComparison.Ordinal))
            {
                return;
            }

            var view = message["View"] as JObject;

            if (view == null)
            {
                return;
            }

            _tableId = _tableId ?? table;

            var kind = (string)message["Kind"];
            var seated =
                string.Equals(kind, "joined", StringComparison.Ordinal) ||
                string.Equals(kind, "left", StringComparison.Ordinal);

            // The push carries no balance -- it is about the table, not about anybody's
            // stash -- so the one already on screen is kept rather than being drawn as
            // zero. A settled round refreshes it properly below.
            RenderShared(view, quiet: true);

            if (seated)
            {
                Say(
                    string.Equals(kind, "joined", StringComparison.Ordinal)
                        ? "Somebody sat down."
                        : "Somebody stood up.",
                    Faint);
            }

            if ((string)view["Phase"] == "Settled")
            {
                RefreshBalances();
            }
        }

        // ----------------------------------------------------------------- the drawing

        /// <summary>
        /// Draws a shared table.
        ///
        /// The player's own box goes where the solo hand always goes, so the cards are in
        /// the same place whichever kind of table this is; everybody else's is a strip of
        /// small boxes beneath it. That asymmetry is the point -- the player is looking at
        /// their own hand and glancing at the others, which is what sitting at a table is
        /// like.
        /// </summary>
        private static void RenderShared(JObject view, bool quiet = false)
        {
            Clear(_dealerCards);
            Clear(_handsRow);
            Clear(_othersRow);

            var phase = (string)view["Phase"] ?? "AwaitingBet";
            var betting = phase == "AwaitingBet" || phase == "Settled";
            var seats = view["Seats"] as JArray ?? new JArray();
            var active = view["ActiveSeat"]?.ToObject<int?>();
            var mine = SeatAt(seats, _yourSeat);
            var yourTurn = active.HasValue && active.Value == _yourSeat;

            // Betting is per box and only between rounds. The controls come up when this
            // player has not yet put anything in theirs -- a second bet into an occupied
            // box is refused by the server, and offering it would be offering a refusal.
            var staked = mine?["PendingBet"]?.ToObject<int>() ?? 0;
            _betControls.SetActive(betting && staked == 0);

            if (_leave != null)
            {
                _leave.SetActive(betting);
            }

            if (_statsButton != null)
            {
                // Not at a shared table at all. The stats sheet covers the whole cloth,
                // and covering a table other people are playing at means missing their
                // turns; the sheet is still there from the solo table.
                _statsButton.SetActive(false);
            }

            var dealer = view["Dealer"] as JObject;
            var dealerCards = dealer?["Cards"]?.ToObject<List<string>>() ?? new List<string>();

            foreach (var card in dealerCards)
            {
                CardView.Build(_dealerCards, card, _font);
            }

            if (phase == "PlayerTurn" && dealerCards.Count > 0)
            {
                // The hole card is not in the view during play. Drawing a back in its
                // place is honest: the client does not have it to show.
                CardView.Build(_dealerCards, null, _font);
            }

            var dealerValue = dealer?["Value"]?.ToObject<int>() ?? 0;
            _dealerValue.text = dealerCards.Count == 0
                ? ""
                : (phase == "PlayerTurn" ? dealerValue + " + ?" : dealerValue.ToString());

            var hands = mine?["Hands"] as JArray;
            var anyHands = hands != null && hands.Count > 0;

            _bettingSpot.SetActive(!anyHands);

            if (anyHands)
            {
                var activeHand = mine["ActiveHandIndex"]?.ToObject<int>() ?? -1;

                for (var i = 0; i < hands.Count; i++)
                {
                    BuildHand(_handsRow, (JObject)hands[i], i == activeHand && yourTurn);
                }
            }

            FitHands();

            foreach (var seat in seats)
            {
                var box = seat as JObject;
                var index = box?["Index"]?.ToObject<int>() ?? -1;

                if (box == null || index == _yourSeat || !(box["IsOccupied"]?.ToObject<bool>() ?? false))
                {
                    continue;
                }

                BuildOtherBox(box, active.HasValue && active.Value == index);
            }

            RenderSharedActions(mine, phase, yourTurn, staked);

            if (!quiet && phase == "PlayerTurn" && !yourTurn)
            {
                var waitingOn = NameOf(SeatAt(seats, active ?? -1));

                Say(waitingOn == null ? "Waiting for the table." : "Waiting for " + waitingOn + ".", Faint);
            }
        }

        /// <summary>
        /// Somebody else's box: their name, what they staked, and their cards small.
        ///
        /// Cards and all, because that is blackjack -- watching somebody bust while you
        /// stand on 19 is the reason to sit together rather than alone.
        /// </summary>
        private static void BuildOtherBox(JObject seat, bool theirTurn)
        {
            var name = (string)seat["Name"] ?? "Somebody";
            var inRound = seat["IsInRound"]?.ToObject<bool>() ?? false;
            var pending = seat["PendingBet"]?.ToObject<int>() ?? 0;
            var hands = seat["Hands"] as JArray;

            var box = NewBox(
                "Box",
                _othersRow,
                new Color(0f, 0f, 0f, theirTurn ? 0.34f : 0.18f),
                10,
                theirTurn ? Gold : new Color(0f, 0f, 0f, 0f),
                theirTurn ? 2 : 0);

            var column = box.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childAlignment = TextAnchor.UpperCenter;
            column.spacing = 3f;
            column.padding = new RectOffset(10, 10, 6, 6);
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            column.childControlWidth = false;
            column.childControlHeight = false;

            box.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            SetSize(
                Label(box, name, 15f, theirTurn ? Gold : Ink, TextAlignmentOptions.Center).rectTransform,
                150f,
                20f);

            if (hands != null && hands.Count > 0)
            {
                foreach (var entry in hands)
                {
                    BuildSmallHand(box, entry as JObject);
                }

                return;
            }

            SetSize(
                Label(
                    box,
                    pending > 0
                        ? pending.ToString("N0", CultureInfo.InvariantCulture) + " in"
                        : (inRound ? "playing" : "not betting"),
                    14f,
                    Faint,
                    TextAlignmentOptions.Center).rectTransform,
                150f,
                18f);
        }

        /// <summary>
        /// One of somebody else's hands: the cards as text, and the total.
        ///
        /// Text rather than <see cref="CardView"/> on purpose. Four other boxes of real
        /// cards is four times the drawing and does not fit under the player's own hand at
        /// any size that stays readable, and what a player actually wants off a neighbour
        /// is the number and whether they are still in it.
        /// </summary>
        private static void BuildSmallHand(RectTransform parent, JObject hand)
        {
            if (hand == null)
            {
                return;
            }

            var cards = hand["Cards"]?.ToObject<List<string>>() ?? new List<string>();
            var value = hand["Value"]?.ToObject<int>() ?? 0;
            var outcome = (string)hand["Outcome"];
            var wager = hand["Wager"]?.ToObject<int>() ?? 0;

            SetSize(
                Label(parent, string.Join(" ", cards.ToArray()), 15f, Ink, TextAlignmentOptions.Center)
                    .rectTransform,
                150f,
                20f);

            var settled = !string.IsNullOrEmpty(outcome);
            var colour = Faint;

            if (settled)
            {
                var won = outcome == "Win" || outcome == "Blackjack";
                var pushed = outcome == "Push";
                colour = pushed ? Faint : (won ? Good : Bad);
            }

            SetSize(
                Label(
                    parent,
                    settled
                        ? value + "  " + outcome.ToUpperInvariant()
                        : value + "  (" + wager.ToString("N0", CultureInfo.InvariantCulture) + ")",
                    14f,
                    colour,
                    TextAlignmentOptions.Center).rectTransform,
                150f,
                18f);
        }

        /// <summary>
        /// The buttons at a shared table.
        ///
        /// **The turn is a seat, not a phase.** `Phase == "PlayerTurn"` is true while
        /// somebody else is playing, so hanging HIT off the phase alone would put it in
        /// front of a player whose turn it is not and the server would refuse every press.
        /// </summary>
        private static void RenderSharedActions(JObject mine, string phase, bool yourTurn, int staked)
        {
            Clear(_actionRow);

            if (phase == "PlayerTurn")
            {
                if (yourTurn)
                {
                    var actions = mine?["AvailableActions"]?.ToObject<List<string>>() ?? new List<string>();

                    // A fixed order, not the server's. Hit and Stand are muscle memory and
                    // should not move about because the legal set changed.
                    foreach (var action in new[] { "Hit", "Stand", "Double", "Split" })
                    {
                        if (!actions.Contains(action))
                        {
                            continue;
                        }

                        var captured = action;
                        Chip(_actionRow, action.ToUpperInvariant(), 150f, () => SharedAct(captured));
                    }

                    return;
                }

                // Nothing to press while somebody else plays, except the one button that
                // gets a player unstuck if a push never arrived.
                Chip(_actionRow, "REFRESH", 150f, RefreshShared);
                return;
            }

            if (staked > 0)
            {
                // Their money is in the box. What is left is to start the round -- and
                // anybody may, so this is not greyed out for anyone.
                Chip(_actionRow, "DEAL", 190f, SharedDeal, primary: true);
                Chip(_actionRow, "REFRESH", 150f, RefreshShared);
                Chip(_actionRow, "TABLES", 150f, () => ShowSharedLobby());
                return;
            }

            var bet = Chip(_actionRow, "BET", 190f, SharedBet, primary: true);

            Chip(_actionRow, "REFRESH", 150f, RefreshShared);
            Chip(_actionRow, "TABLES", 150f, () => ShowSharedLobby());

            // Greyed when the stake cannot be placed, rather than looking ready and then
            // refusing. Still clickable, because the refusal explains itself and a button
            // that silently does nothing is worse than one that answers.
            var ceiling = CeilingFor(_wallet);
            var withinTable = ceiling <= 0 || _wager <= ceiling;
            var affordable = withinTable
                && (!Balances.TryGetValue(_wallet, out var held) || (_wager > 0 && _wager <= held));

            if (affordable)
            {
                return;
            }

            Grey(bet);
        }

        /// <summary>
        /// The list of open tables, laid over the cloth exactly as the stats sheet is.
        ///
        /// On the table rather than in a window of its own, and for the same reason: a
        /// table with a sheet of names lying on it reads as a table between rounds, where
        /// a panel floating over a dealt hand is just in the way of it.
        /// </summary>
        private static void BuildSharedLobby(RectTransform felt)
        {
            var sheet = NewBox("SharedLobby", felt, new Color(0f, 0f, 0f, 0f), 0, default, 0);
            Stretch(sheet);
            sheet.offsetMin = new Vector2(120f, 96f);
            sheet.offsetMax = new Vector2(-120f, -96f);
            _sharedPanel = sheet.gameObject;

            var card = NewBox(
                "Sheet",
                sheet,
                new Color(0.06f, 0.07f, 0.07f, 0.92f),
                14,
                new Color(1f, 1f, 1f, 0.10f),
                2);

            Stretch(card);

            var column = card.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childAlignment = TextAnchor.UpperCenter;
            column.spacing = 10f;
            column.padding = new RectOffset(24, 24, 18, 18);
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            column.childControlWidth = false;
            column.childControlHeight = false;

            SetSize(Label(card, "SHARED TABLES", 22f, Gold, TextAlignmentOptions.Center).rectTransform, 780f, 26f);

            _lobbyNote = Label(card, "", 17f, Faint, TextAlignmentOptions.Center);
            _lobbyNote.enableWordWrapping = true;
            SetSize(_lobbyNote.rectTransform, 780f, 44f);

            SetSize(NewBox("Rule", card, new Color(1f, 1f, 1f, 0.10f), 0, default, 0), 780f, 2f);

            // Four rows at 56 plus their spacing comes to 260, and a table seats five --
            // so a full lobby of open tables fits without anything having to scroll.
            _tableList = NewBox("Tables", card, new Color(0f, 0f, 0f, 0f), 0, default, 0);
            SetSize(_tableList, 820f, 300f);

            var rows = _tableList.gameObject.AddComponent<VerticalLayoutGroup>();
            rows.childAlignment = TextAnchor.UpperCenter;
            rows.spacing = 8f;
            rows.childForceExpandWidth = false;
            rows.childForceExpandHeight = false;
            rows.childControlWidth = false;
            rows.childControlHeight = false;

            _sharedPanel.SetActive(false);
        }

        // -------------------------------------------------------------------- odds and ends

        private static JObject SeatAt(JArray seats, int index)
        {
            if (index < 0)
            {
                return null;
            }

            foreach (var seat in seats)
            {
                var box = seat as JObject;

                if ((box?["Index"]?.ToObject<int>() ?? -1) == index)
                {
                    return box;
                }
            }

            return null;
        }

        private static string NameOf(JObject seat) => (string)seat?["Name"];

        private static bool TruthOf(JObject reply) => reply?["Ok"]?.ToObject<bool>() ?? false;

        private static string ErrorOf(JObject reply)
        {
            var error = (string)reply?["Error"];

            return string.IsNullOrEmpty(error) ? null : error;
        }

        private static void SetLobbyNote(string text, Color colour)
        {
            if (_lobbyNote == null)
            {
                return;
            }

            _lobbyNote.text = text;
            _lobbyNote.color = colour;
        }
    }
}
