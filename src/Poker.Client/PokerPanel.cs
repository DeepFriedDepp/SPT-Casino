using Casino.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Poker.Client
{
    /// <summary>
    /// The table window.
    ///
    /// The server owns the game completely. This renders the view it is handed and
    /// posts what the player pressed; it never decides whether a move is legal, never
    /// works out who won, and never knows a card the server did not send. When the
    /// engine refuses a move it answers with the real view attached, so the fix for a
    /// client that has drifted is simply to draw what came back.
    ///
    /// **Two kinds of table come through here, and one set of drawing code draws both.**
    /// The solo table is what everybody has and stays the default; a shared table is the
    /// same view over a different set of routes, with other people in some of the seats.
    /// <see cref="_shared"/> is the only thing that differs at the point a request is
    /// made -- everything below <see cref="Render"/> reads a view and does not care where
    /// it came from, which is what stopped the shared table becoming a second panel.
    ///
    /// At a shared table the view also arrives unasked, pushed down the casino's socket
    /// when somebody else moves. That is the only route by which this panel redraws
    /// without the player pressing anything; see <see cref="OnPushed"/>.
    /// </summary>
    internal static class PokerPanel
    {
        private const string RootName = "PokerTableCanvas";

        /// <summary>
        /// The table photograph is an oval on a rectangular image, and 1.655 is that
        /// image's aspect -- keeping it stops the cloth stretching into a shape no
        /// table has.
        /// </summary>
        private const float TableAspect = 1.655f;

        private const float FeltWidth = 1080f;
        private const float FeltHeight = FeltWidth / TableAspect;

        /// <summary>The board's cards, and the gap between them once they are that size.</summary>
        private const float BoardCardScale = 0.78f;

        private const float BoardCardGap = 10f;

        /// <summary>
        /// Where the cloth actually is inside the photograph, as fractions of the image.
        ///
        /// **The oval is not centred in table.png and does not fill it.** The cloth is
        /// 0.42 x 0.34 of the image and sits 2.1% above its middle, so anything placed at
        /// the centre of the felt *rect* -- the board, the pot, the ring the seats sit on
        /// -- is placed against the picture rather than against the table in it.
        ///
        /// Measured by scanning the image for pixels greener than they are red or blue.
        /// That test rather than a brightness one, because the cloth is in shadow down its
        /// left side: a brightness test drops the shadowed edge and reports the cloth 3%
        /// right of where it is, which is a plausible-looking answer and the wrong one.
        /// </summary>
        private const float ClothHalfWidth = 0.4207f;

        private const float ClothHalfHeight = 0.3364f;

        private const float ClothRise = 0.021f;

        private static float ClothX => FeltWidth * ClothHalfWidth;

        private static float ClothY => FeltHeight * ClothHalfHeight;

        private static float ClothCentreY => FeltHeight * ClothRise;

        private const float SeatWidth = 240f;

        private const float SeatHeight = 180f;

        /// <summary>Taller: the player's cards are bigger and carry a hand reading.</summary>
        private const float PlayerSeatHeight = 214f;

        /// <summary>What a plaque keeps between itself and the cloth.</summary>
        private const float SeatClearance = 12f;

        /// <summary>
        /// How far the felt sits above the middle of the screen.
        ///
        /// Not a taste: it is the one number that has to satisfy both ends at once. The
        /// seats above the table have to clear the cloth and stay under the title, and the
        /// player's seat below it has to clear the cloth and stay above the status line --
        /// which together leave this between about 22 and 41. Move the title, the status
        /// line or the action strip and this has to be worked out again.
        /// </summary>
        private const float StageRise = 32f;

        private static readonly Color Gold = new Color(0.72f, 0.62f, 0.34f, 1f);
        private static readonly Color Ink = new Color(0.88f, 0.86f, 0.80f, 1f);
        private static readonly Color Dim = new Color(0.50f, 0.49f, 0.46f, 1f);

        /// <summary>
        /// Another person, as opposed to a bot. Cool where the rest of the table is warm,
        /// because it has to be told apart at a glance from the gold that means "you" and
        /// the plain ink that means "a bot".
        ///
        /// Colour rather than a word, and that is a real trade rather than laziness: the
        /// plaque's second line is the stack and the bet, which is the thing actually
        /// being read during a hand, and a tag pushed in beside it would be competing
        /// with it every street. So the seat says "somebody is sitting here" three ways
        /// at once -- this colour on the name, a heavier border, and a dot in the corner
        /// -- and none of them takes a line away from the numbers.
        /// </summary>
        private static readonly Color Person = new Color(0.45f, 0.68f, 0.86f, 1f);

        private static GameObject _root;
        private static TMP_FontAsset _font;

        private static RectTransform _board;
        private static RectTransform _potHolder;
        private static RectTransform _seatLayer;
        private static RectTransform _actionRow;
        private static TextMeshProUGUI _status;

        // The shared-table lobby: a sheet over the felt listing what is open, because a
        // list of tables with stakes and seat counts on it does not fit in the one status
        // line the rest of the panel says everything in.
        private static GameObject _lobbySheet;
        private static RectTransform _lobbyRows;
        private static TextMeshProUGUI _lobbyNote;

        /// <summary>
        /// Whether the table on screen is a shared one, and so which set of routes an
        /// action goes to.
        ///
        /// False everywhere the solo table is involved, which is what keeps the solo game
        /// exactly what it was: with this false, every path below is the one that shipped.
        /// It means "the table being drawn is shared" rather than "this player is seated
        /// at a shared table somewhere" -- those differ while the lobby is up, and the
        /// narrower reading is the one that decides correctly what a button does.
        /// </summary>
        private static bool _shared;

        /// <summary>
        /// Which shared table, when it is known, so a push meant for another one is
        /// ignored.
        ///
        /// Null is normal rather than a fault. Opening a table gets the view back but not
        /// the id -- the server names it -- and nothing else needs one, because every
        /// shared route keys off the session. So this is learned where it is cheap (the
        /// lobby list, and joining, both of which name the table already) and adopted from
        /// the first push otherwise. Adopting is safe: the server pushes a table only to
        /// the seats sitting at it, and a player can only be at one.
        /// </summary>
        private static string _tableId;

        // What the player is asking to raise to. Held between redraws because the
        // whole action strip is rebuilt whenever the view changes.
        private static int _raiseTo;

        // The last view the server sent. Kept so that changing the raise amount can
        // redraw the strip without a round trip -- picking a number is not a move,
        // and asking the server for a view it has already sent invites the screen to
        // change under the player between pressing + and pressing raise.
        private static JObject _lastReply;

        // The running fade, and whether it is on its way out. IsOpen has to read as
        // closed the moment a close starts, or the menu button toggles it straight
        // back open again mid-fade.
        private static Coroutine _fade;
        private static bool _closing;

        private static string TableImagePath => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(PokerClientPlugin.Instance?.Info?.Location ?? ".") ?? ".",
            "table.png");

        internal static bool IsOpen => _root != null && _root.activeSelf && !_closing;

        internal static void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            Open();
        }

        internal static void Open()
        {
            try
            {
                if (_root == null)
                {
                    Build();
                }

                if (_root == null)
                {
                    return;
                }

                _closing = false;
                _root.SetActive(true);
                FadeTo(1f, null);

                // A canvas built this frame has not had a layout pass yet, so its
                // controls have no real size or position until one happens.
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_root.transform);

                // Before the first request, not after: a hand dealt by somebody else
                // between the state call and the subscription would be a move this panel
                // never hears about, and the table would sit looking frozen until the
                // next one.
                Listen();

                // Resume rather than assume: a hand can still be live from an earlier
                // visit, and /poker/state is what says so. Its failure is the ordinary
                // "not at a table" case, not an error worth showing as one.
                var state = PokerApi.State();

                // Asking for the table is also what gives back a stack left behind by a
                // session that never finished -- see PokerService.StateAsync -- so the
                // reply can carry money as well as a view. Two things follow, and both
                // are easy to miss because the usual reply carries neither.
                var note = (string)state?["Note"];

                if (Ok(state))
                {
                    _shared = false;
                    Render(state);
                }
                else if (!ResumeShared())
                {
                    ShowLobby();
                }

                if (!string.IsNullOrEmpty(note))
                {
                    // The refund went through a static route, so the running game does
                    // not know its stash changed. Without this the roubles are in the
                    // profile and invisible until a reload, which is the failure that
                    // reads as the mod having eaten them.
                    ProfileSync.Request("PokerSync");

                    SetStatus(note);
                }
            }
            catch (Exception ex)
            {
                PokerClientPlugin.Log.LogError("[Poker] could not open the table: " + ex);
            }
        }

        internal static void Close()
        {
            if (_root == null || !_root.activeSelf || _closing)
            {
                return;
            }

            // Straight away rather than when the fade finishes. A push landing during the
            // fade would redraw a table on its way out, and -- worse -- a subscription
            // left behind is a second one on the next open, which draws every move twice.
            Deafen();

            _closing = true;

            FadeTo(0f, () =>
            {
                _root.SetActive(false);
                _closing = false;
            });
        }

        /// <summary>
        /// Fades the whole canvas, backdrop included.
        ///
        /// Short on purpose. This is not an animation anybody should notice; it exists
        /// so the eye is handed back to the menu instead of having the menu appear
        /// where a table was.
        ///
        /// Falls back to snapping if there is no coroutine host -- outside the menu
        /// there is nothing to run it on, and a panel that will not close is worse than
        /// one that closes abruptly.
        /// </summary>
        private static void FadeTo(float target, Action done)
        {
            var group = _root == null ? null : _root.GetComponent<CanvasGroup>();
            var host = PokerClientPlugin.Instance;

            if (group == null || host == null)
            {
                if (group != null)
                {
                    group.alpha = target;
                }

                if (done != null)
                {
                    done();
                }

                return;
            }

            if (_fade != null)
            {
                host.StopCoroutine(_fade);
            }

            _fade = host.StartCoroutine(Fade(group, target, done));
        }

        private static IEnumerator Fade(CanvasGroup group, float target, Action done)
        {
            // A sixth of a second, linear, both directions -- the same numbers
            // Blackjack settled on, because that is the version that was tried and
            // found to read correctly. Nothing here is a guess to be improved on.
            const float duration = 0.16f;

            var start = group.alpha;
            var elapsed = 0f;

            // Clicks stop landing the moment a close begins, so a button pressed
            // during the fade cannot fire at a table that is on its way out.
            group.blocksRaycasts = target > 0f;
            group.interactable = target > 0f;

            while (elapsed < duration)
            {
                // Unscaled: the menu is not necessarily running at a normal timescale,
                // and a fade that stalls with it would hang the panel open.
                elapsed += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(start, target, Mathf.Clamp01(elapsed / duration));
                yield return null;
            }

            group.alpha = target;
            _fade = null;

            if (done != null)
            {
                done();
            }
        }

        /// <summary>
        /// Escape closes the table. Nothing is stacked over it yet; when a confirm
        /// prompt exists it is handled here first, in the order things are stacked,
        /// or escape closes the table out from under an unanswered question.
        /// </summary>
        internal static void OnEscape()
        {
            if (_root == null || !_root.activeSelf)
            {
                return;
            }

            Close();
        }

        // ---------------------------------------------------------------- actions

        /// <summary>
        /// Seats and blinds, in one place so the label cannot lie.
        ///
        /// Both fixed. Five seats is what the engine caps at, and the blinds stay put
        /// so that changing the buy-in changes how deep the table is rather than what
        /// game it is.
        /// </summary>
        private const int TableSeats = 5;

        private const int BigBlindChips = 20_000;

        /// <summary>
        /// What sitting down costs, from the F12 menu.
        ///
        /// Read every time rather than cached, so a player who changes it does not have
        /// to restart the game to see the button change price.
        ///
        /// Clamped and rounded here rather than trusted, even though the setting is now
        /// a list of round figures and cannot be dragged to an awkward one.
        ///
        /// The list is what the F12 menu offers; the config file behind it is a text
        /// file somebody can type any integer into, and two floors matter then: the
        /// server refuses anything under ten big blinds, and a stack that is not a whole
        /// number of 10,000 chips is one the table cannot draw. Out of range would be
        /// refused by the server anyway -- this only decides whether the player finds
        /// out before pressing the button or after.
        /// </summary>
        private static int BuyInChips
        {
            get
            {
                var chosen = PokerClientPlugin.BuyIn?.Value ?? DefaultBuyIn;
                var clamped = Mathf.Clamp(chosen, BigBlindChips * 10, MaxBuyIn);

                return (clamped / ChipStep) * ChipStep;
            }
        }

        private const int DefaultBuyIn = 1_000_000;

        /// <summary>The most the server's rouble wallet takes. See WalletInfo.</summary>
        private const int MaxBuyIn = 5_000_000;

        /// <summary>The smallest chip the table draws with.</summary>
        private const int ChipStep = 10_000;

        /// <summary>Thousands separators without depending on the machine's locale.</summary>
        private static string Roubles(int amount) =>
            amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Asks before spending anything.
        ///
        /// Sitting down used to be free and is not any more: it debits the buy-in from
        /// a real stash. A single unlabelled button that quietly takes two million
        /// roubles is the kind of thing a player only finds out about afterwards, so
        /// the price goes on the button and the button asks twice.
        /// </summary>
        private static void ConfirmSit()
        {
            SetStatus(
                "This will take " + Roubles(BuyInChips) + " roubles from your stash and put it"
                + " on the table as chips. Whatever is left when you stand up comes back.");

            BuildActions(new[]
            {
                Action("BUY IN FOR " + Roubles(BuyInChips), Sit),
                Action("NOT YET", ShowLobby),
            });
        }

        private static void Sit()
        {
            var reply = PokerApi.Sit(seats: TableSeats, buyIn: BuyInChips, bigBlind: BigBlindChips);

            if (!Ok(reply))
            {
                SetStatus(ErrorOf(reply) ?? "Could not sit down.");
                return;
            }

            // The buy-in has just left the stash. Without this the game keeps showing
            // roubles the server has already deleted, and the next stack the player
            // drags fails to merge against an item that is no longer there.
            ProfileSync.Request("PokerSync");

            // Said rather than assumed. A player who was at a shared table, stood up and
            // then sat down alone would otherwise send their next fold to the shared
            // routes, which refuse it -- correctly, and confusingly.
            _shared = false;
            _tableId = null;

            Render(reply);
        }

        private static void Leave()
        {
            if (_shared)
            {
                LeaveShared();
                return;
            }

            PokerApi.Leave();

            // The chips have just come back as currency, so the same applies in the
            // other direction.
            ProfileSync.Request("PokerSync");

            ShowLobby();
        }

        private static void Deal()
        {
            var reply = _shared ? SharedPokerApi.Deal() : PokerApi.Deal();

            if (!Ok(reply))
            {
                SetStatus(ErrorOf(reply) ?? "Could not deal.");

                // A refusal still carries the table, so the screen stays truthful.
                if (reply?["Table"] != null)
                {
                    Render(reply, keepStatus: true);
                }

                return;
            }

            _raiseTo = 0;
            Render(reply);
        }

        private static void Act(string move, int to = 0)
        {
            var reply = _shared ? SharedPokerApi.Act(move, to) : PokerApi.Act(move, to);

            if (reply == null)
            {
                SetStatus("No answer from the server.");
                return;
            }

            // The engine is the authority on legality, and when it refuses it hands
            // back the real view with the reason attached. Draw the view either way:
            // a client whose picture has drifted is exactly the case this covers.
            var error = ErrorOf(reply);

            _raiseTo = 0;
            Render(reply, keepStatus: error != null);

            if (error != null)
            {
                SetStatus(error);
            }
        }

        // ---------------------------------------------------- shared tables: the actions

        /// <summary>
        /// Puts the player back at the shared table they are already sitting at, if they
        /// are sitting at one. Answers whether it drew anything.
        ///
        /// Asked only after the solo table has said no, so the ordinary player -- who has
        /// never opened a shared table and never will -- pays nothing for it beyond one
        /// request on a panel that has just made another one. It is worth that much: the
        /// shared seat holds a real stack in escrow, and reopening the panel to be offered
        /// a fresh buy-in while a stack of yours is sitting on a table somewhere is the
        /// sort of thing a player only works out after paying twice.
        /// </summary>
        private static bool ResumeShared()
        {
            var state = SharedPokerApi.State();

            if (!Ok(state) || state["Table"] == null)
            {
                return false;
            }

            _shared = true;
            Render(state);

            return true;
        }

        /// <summary>
        /// Asks the server what is open and draws the list.
        ///
        /// Every entry into this lobby is a button press, so the request is made the same
        /// way every other one here is. There is no timer behind it: a lobby that refetches
        /// itself would be a request every few seconds for the whole time somebody leaves
        /// the panel sitting open, and the socket -- not polling -- is what keeps a table
        /// in view being live.
        /// </summary>
        private static void ShowSharedLobby(string note = null)
        {
            _lastReply = null;
            _shared = false;

            SetBoard(null);
            ClearSeats();
            SetPot(null);

            var reply = SharedPokerApi.Tables();
            var yours = (string)reply?["YourTable"];

            _tableId = yours;

            ShowTables(reply?["Tables"] as JArray, yours, Ok(reply) ? null : ErrorOf(reply));

            SetStatus(note ?? (yours != null
                ? "You are already sitting at a table. Take your seat back rather than opening"
                    + " another one -- your chips are at that one."
                : "Joining a table takes its buy-in from your stash, the same as sitting down"
                    + " alone does. Opening one seats you at " + TableSeats + " chairs for "
                    + Roubles(BuyInChips) + " roubles; the empty ones play as bots until"
                    + " somebody takes them."));

            var actions = new List<KeyValuePair<string, Action>>();

            if (yours != null)
            {
                actions.Add(Action("BACK TO YOUR TABLE", ReturnToShared));
            }
            else
            {
                actions.Add(Action("OPEN A TABLE", ConfirmOpen));
            }

            actions.Add(Action("REFRESH", () => ShowSharedLobby()));
            actions.Add(Action("BACK", ShowLobby));
            actions.Add(Action("CLOSE", Close));

            BuildActions(actions);
        }

        private static void ReturnToShared()
        {
            var state = SharedPokerApi.State();

            if (!Ok(state) || state["Table"] == null)
            {
                // The table is gone rather than merely unreachable -- somebody stood up
                // last and it closed behind them. Say so from the lobby, which is where
                // that leaves them.
                ShowSharedLobby(ErrorOf(state) ?? "That table is gone.");
                return;
            }

            _shared = true;
            _raiseTo = 0;

            Render(state);
        }

        /// <summary>
        /// Asks before spending anything, exactly as <see cref="ConfirmSit"/> does and for
        /// the same reason. The list stays on screen while it asks, so the table being
        /// paid for is still the one being looked at.
        /// </summary>
        private static void ConfirmJoin(string tableId, int buyIn)
        {
            SetStatus(
                "This will take " + Roubles(buyIn) + " roubles from your stash and put it on"
                + " that table as chips. Whatever is left when you stand up comes back.");

            BuildActions(new[]
            {
                Action("JOIN FOR " + Roubles(buyIn), () => JoinShared(tableId)),
                Action("NOT YET", () => ShowSharedLobby()),
            });
        }

        private static void JoinShared(string tableId)
        {
            var reply = SharedPokerApi.Join(tableId);

            if (!Ok(reply))
            {
                // Still in the lobby, and the list is still the one that was drawn. The
                // usual refusals -- the table filled up, or a hand started -- are answered
                // by pressing refresh, so leaving the list up is the useful thing.
                SetStatus(ErrorOf(reply) ?? "Could not join that table.");
                return;
            }

            ProfileSync.Request("PokerSync");

            _shared = true;
            _tableId = tableId;
            _raiseTo = 0;

            Render(reply);
        }

        private static void ConfirmOpen()
        {
            SetStatus(
                "This will take " + Roubles(BuyInChips) + " roubles from your stash and open a"
                + " table others can join. The seats nobody takes play as bots, and whatever"
                + " is left when you stand up comes back.");

            BuildActions(new[]
            {
                Action("OPEN FOR " + Roubles(BuyInChips), OpenShared),
                Action("NOT YET", () => ShowSharedLobby()),
            });
        }

        private static void OpenShared()
        {
            var reply = SharedPokerApi.Open(seats: TableSeats, buyIn: BuyInChips, bigBlind: BigBlindChips);

            if (!Ok(reply))
            {
                SetStatus(ErrorOf(reply) ?? "Could not open a table.");
                return;
            }

            ProfileSync.Request("PokerSync");

            _shared = true;

            // The server names the table and the reply does not carry the name. Nothing
            // needs it until a push arrives, and the first one says which table it is
            // about -- see _tableId.
            _tableId = null;
            _raiseTo = 0;

            Render(reply);
        }

        /// <summary>
        /// Asks the server for the table again.
        ///
        /// On the strip whenever it is not this player's turn, which is exactly when a
        /// push that never arrived would leave the screen frozen with nothing to press.
        /// The socket reconnects on its own and this is not a substitute for it; it is
        /// the one button that gets a player unstuck without closing the panel.
        /// </summary>
        private static void RefreshShared()
        {
            var state = SharedPokerApi.State();

            if (!Ok(state) || state["Table"] == null)
            {
                ShowSharedLobby(ErrorOf(state) ?? "That table is gone.");
                return;
            }

            Render(state);
        }

        private static void LeaveShared()
        {
            var reply = SharedPokerApi.Leave();

            if (!Ok(reply))
            {
                // "Finish the hand first" is the one that happens, and the table it
                // refers to is still on screen and still correct.
                SetStatus(ErrorOf(reply) ?? "Could not stand up.");
                return;
            }

            ProfileSync.Request("PokerSync");

            _shared = false;
            _tableId = null;

            ShowSharedLobby((string)reply["Note"]);
        }

        // ---------------------------------------------------- shared tables: the push
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
        /// is compiled into TWO assemblies: Poker.Client, which still builds a standalone
        /// plugin and has no websocket reference at all, and Casino.Client, which is what
        /// ships and owns the socket. Naming `CasinoSocketClient` here breaks the first
        /// build, and a project reference the other way round is a cycle.
        ///
        /// `Host` is already the seam for exactly this -- "the things the shared code
        /// needs from whoever is hosting it" -- and it costs nothing to be the third.
        /// Reflection was the other way across that gap and it worked, but a seam that
        /// resolves by string survives a rename silently and then stops delivering, which
        /// at a live table means everybody quietly stops seeing each other's moves.
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
        ///
        /// The payload is one table as this seat is allowed to see it: other seats' cards
        /// are absent from the object rather than flagged, so there is nothing here to be
        /// careful with. Drawing it is the entire job.
        /// </summary>
        private static void OnPushed(string payload)
        {
            // Not while a solo table or the lobby is on screen. A player can be seated at
            // a shared table and looking at something else, and the push is about the
            // table, not about what is being drawn.
            if (!_shared || _root == null || !_root.activeSelf || _closing)
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
                // One socket carries the whole casino, so something that is not a poker
                // table is an ordinary thing to receive, not a fault.
                PokerClientPlugin.Log.LogDebug("[Poker] ignored a push it could not read: " + ex.Message);
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

            // The table moved, so an amount picked against the version before it did is
            // no longer the amount that was meant.
            _raiseTo = 0;

            var kind = (string)message["Kind"];
            var seated =
                string.Equals(kind, "joined", StringComparison.Ordinal) ||
                string.Equals(kind, "left", StringComparison.Ordinal);

            Render(new JObject { ["Ok"] = true, ["Table"] = view }, keepStatus: seated);

            if (seated)
            {
                // Both only ever arrive between hands, so nothing about the hand is being
                // written over. Who it was is not worth saying: a seat somebody has just
                // taken is named "Seat 3" by the server until they are dealt in.
                SetStatus(string.Equals(kind, "joined", StringComparison.Ordinal)
                    ? "Somebody took a seat."
                    : "Somebody stood up.");
            }
        }

        // ---------------------------------------------------------------- rendering

        /// <summary>
        /// The way in, and it is still the solo one.
        ///
        /// A second button rather than a second screen in front of this one: playing alone
        /// is what everybody has and what nearly everybody wants, and putting a choice
        /// between a player and the table they have always sat at would be a cost paid by
        /// all of them for a feature two of them use.
        /// </summary>
        private static void ShowLobby()
        {
            _lastReply = null;
            _shared = false;
            _tableId = null;

            HideTables();
            SetBoard(null);
            ClearSeats();
            SetPot(null);

            SetStatus(
                TableSeats + " seats, blinds 10k / 20k."
                + "     Buying in costs " + Roubles(BuyInChips) + " roubles from your stash"
                + " (" + (BuyInChips / BigBlindChips) + " big blinds, set it in the F12 menu)."
                + " You take back whatever is in front of you when you stand up.");

            BuildActions(new[]
            {
                Action("PLAY ALONE", ConfirmSit),
                Action("SHARED TABLES", () => ShowSharedLobby()),
                Action("CLOSE", Close),
            });
        }

        private static void Render(JObject reply, bool keepStatus = false)
        {
            var table = reply?["Table"] as JObject;

            if (table == null)
            {
                ShowLobby();
                return;
            }

            _lastReply = reply;

            var street = (string)table["Street"] ?? "Idle";
            var pot = (int?)table["Pot"] ?? 0;
            var awaiting = (bool?)table["AwaitingPlayer"] ?? false;
            var button = (int?)table["Button"] ?? -1;

            // Which seat is being looked out of. Absent means seat 0, which is what a
            // one-human table has always meant and where the solo player has always sat.
            var viewer = (int?)table["ViewerSeat"] ?? 0;

            HideTables();
            SetBoard(table["Community"]?.Select(c => (string)c).ToArray());
            SetPot(pot);
            RenderSeats(table["Seats"] as JArray, button, viewer);

            if (!keepStatus)
            {
                SetStatus(Headline(street, table, viewer));
            }

            BuildActions(ActionsFor(street, awaiting, table["Options"] as JObject));
        }

        /// <summary>
        /// One line saying where the hand is. At a showdown it says who won instead,
        /// because that is the only moment the player cannot read it off the table.
        ///
        /// Mid-hand it also names whoever the table is waiting on. That is not decoration
        /// at a shared table: four of the five seats can be somebody else, none of them is
        /// obliged to act quickly, and a street name on its own leaves a player looking at
        /// a table that has not moved for ten seconds with no way to tell a slow friend
        /// from a mod that has stopped working. Named whether the seat is a bot or a
        /// person, because what is being said is "not you yet" and the difference between
        /// those two is the wait, not the fault.
        /// </summary>
        private static string Headline(string street, JObject table, int viewer)
        {
            if (string.Equals(street, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                return "Waiting to deal.";
            }

            if (!string.Equals(street, "Showdown", StringComparison.OrdinalIgnoreCase))
            {
                return street + Waiting(table, viewer);
            }

            var winners = (table["Seats"] as JArray)?
                .Where(s => ((int?)s["Won"] ?? 0) > 0)
                .Select(s =>
                {
                    // Whether this is the seat being looked out of, not whether a person
                    // sits in it: with several people at the table IsPlayer is true of all
                    // of them, and every one of them would be told they had won.
                    var isPlayer = ((int?)s["Index"] ?? -1) == viewer;
                    var name = isPlayer ? "You" : (string)s["Name"] ?? "A seat";

                    // "You wins". The seat's name is a third person and the player is a
                    // second, so the verb cannot be part of the sentence's fixed half --
                    // which is what it was, and it read as broken English on every pot
                    // the player took.
                    var verb = isPlayer ? "win" : "wins";

                    var won = (int?)s["Won"] ?? 0;
                    var hand = (string)s["Hand"];

                    return hand == null
                        ? $"{name} {verb} {won:N0}"
                        : $"{name} {verb} {won:N0} with {hand.ToLowerInvariant()}";
                })
                .ToArray();

            return winners != null && winners.Length > 0
                ? string.Join("          ", winners)
                : "Hand over.";
        }

        /// <summary>
        /// Whose move it is, as a clause to hang off the street.
        ///
        /// "Your move" is said at a shared table only, and that is the one place the solo
        /// game would otherwise have read differently than it always has: alone, a view
        /// comes back only once the bots have finished, so the actor is the player on
        /// essentially every view and the line would have grown a phrase on every street
        /// to say what the buttons underneath it already say. Somebody else being the
        /// actor is worth naming at either kind of table, and alone it is rare enough to
        /// be worth an explanation when it happens.
        /// </summary>
        private static string Waiting(JObject table, int viewer)
        {
            var actor = (int?)table["ActorSeat"];

            if (!actor.HasValue)
            {
                return string.Empty;
            }

            if (actor.Value == viewer)
            {
                return _shared ? "     your move." : string.Empty;
            }

            var name = (table["Seats"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(s => ((int?)s["Index"] ?? -1) == actor.Value);

            return "     waiting for " + ((string)name?["Name"] ?? ("seat " + actor.Value)) + ".";
        }

        private static void RenderSeats(JArray seats, int button, int viewer)
        {
            ClearSeats();

            if (seats == null || _seatLayer == null)
            {
                return;
            }

            var all = seats.OfType<JObject>().ToList();

            foreach (var seat in all)
            {
                BuildSeat(seat, button, all.Count, viewer);
            }
        }

        /// <summary>
        /// One seat, placed on the ellipse the felt is drawn as.
        ///
        /// The seat being looked out of is always drawn at the bottom, which is where the
        /// person at the keyboard expects to be sitting. That used to be seat 0 because
        /// seat 0 was the only seat a person could have; it is now whichever seat this
        /// view was built for, which is the same thing at a solo table. Where a seat is
        /// drawn is presentation and never reaches the engine: the deal order is fixed by
        /// seat index, not by position on screen.
        ///
        /// **Three kinds of occupant, not two.** IsPlayer means "a person sits here" and
        /// is true of everybody at a shared table, so it can no longer answer "is this
        /// me" -- the view carries ViewerSeat for that. Reading it the old way at a table
        /// with two people would draw two seats as the player, both with the big cards,
        /// both labelled YOU.
        /// </summary>
        private static void BuildSeat(JObject seat, int button, int total, int viewer)
        {
            var index = (int?)seat["Index"] ?? 0;
            var name = (string)seat["Name"] ?? ("Seat " + index);
            var stack = (int?)seat["Stack"] ?? 0;
            var committed = (int?)seat["CommittedThisStreet"] ?? 0;
            var folded = (bool?)seat["Folded"] ?? false;
            var allIn = (bool?)seat["IsAllIn"] ?? false;
            var isTurn = (bool?)seat["IsTurn"] ?? false;
            var hand = (string)seat["Hand"];
            var won = (int?)seat["Won"] ?? 0;

            var isPlayer = index == viewer;
            var isSomebody = ((bool?)seat["IsPlayer"] ?? false) && !isPlayer;

            // Cards are absent rather than blanked when they may not be seen, so an
            // empty list is the honest instruction to draw backs. Never key this off
            // the street: a hand that ends with everybody folding never reaches a
            // showdown, and reading the street would show the winner's cards on most
            // pots.
            var cards = seat["Cards"]?.Select(c => (string)c).ToArray() ?? new string[0];

            var holder = NewBox("Seat" + index, _seatLayer, Color.clear);
            holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
            holder.pivot = new Vector2(0.5f, 0.5f);
            holder.sizeDelta = new Vector2(SeatWidth, isPlayer ? PlayerSeatHeight : SeatHeight);
            holder.anchoredPosition = SeatPosition(index, total, viewer, isPlayer);

            var column = holder.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 5f;
            column.childAlignment = TextAnchor.UpperCenter;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            column.childControlWidth = false;
            column.childControlHeight = false;

            BuildSeatCards(holder, cards, isPlayer, folded);

            BuildSeatPlaque(
                holder, name, stack, committed, folded, allIn, isTurn, isPlayer, isSomebody, index == button);

            // Only at a showdown, and only for a seat that reached one -- the server
            // fills Hand in exactly then.
            if (hand != null)
            {
                var reading = NewText(
                    "Hand",
                    holder,
                    won > 0 ? hand + "   +" + won.ToString("N0") : hand,
                    15f,
                    TextAlignmentOptions.Center);

                reading.rectTransform.sizeDelta = new Vector2(SeatWidth, 22f);
                reading.color = won > 0 ? Gold : Dim;
            }
        }

        /// <summary>
        /// The seat's two cards. The player's are larger because they are the ones
        /// actually being read; everyone else's only need to be identifiable.
        /// </summary>
        /// <summary>
        /// One card, in a slot the size the card is actually drawn at.
        ///
        /// **A layout group measures a child's rect and ignores its localScale.** Every
        /// card here is scaled rather than resized -- CardView sizes its pips and corner
        /// blocks in absolute units, so a smaller rect would not make a smaller card --
        /// and the rows were therefore laid out at full 96x138 for cards drawn at 44% and
        /// 78%. A seat's two cards reserved 198 of width to draw 90, and the five on the
        /// board reserved 520 to draw 414. That is where most of the table's crowding came
        /// from, and the reason the gaps between cards looked nothing like the spacing
        /// asked for: the spacing was right and the slots either side of it were twice the
        /// size of what was in them.
        ///
        /// The slot carries the drawn size and the card keeps its scale, so the row is
        /// measured on what it shows.
        /// </summary>
        private static GameObject CardSlot(RectTransform row, string code, float scale)
        {
            var slot = NewBox("Slot", row, Color.clear);
            slot.sizeDelta = new Vector2(CardView.Width * scale, CardView.Height * scale);

            var card = CardView.Build(slot, code, _font);

            var rect = (RectTransform)card.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = new Vector3(scale, scale, 1f);

            return card;
        }

        private static void BuildSeatCards(RectTransform holder, string[] cards, bool isPlayer, bool folded)
        {
            var scale = isPlayer ? 0.66f : 0.44f;

            var cardRow = NewBox("Cards", holder, Color.clear);
            cardRow.sizeDelta = new Vector2(SeatWidth, (CardView.Height * scale) + 4f);

            var pair = cardRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            pair.spacing = 6f;
            pair.childAlignment = TextAnchor.MiddleCenter;
            pair.childForceExpandWidth = false;
            pair.childForceExpandHeight = false;
            pair.childControlWidth = false;
            pair.childControlHeight = false;

            for (var i = 0; i < 2; i++)
            {
                var code = i < cards.Length ? cards[i] : null;

                // Scaled rather than resized, so CardView keeps its own proportions --
                // including the drawn fallback it uses when the art is missing. The slot
                // is what the row is measured on; see CardSlot.
                var card = CardSlot(cardRow, code, scale);

                // A folded seat keeps its cards, dimmed, so the shape of the table
                // stays readable instead of seats vanishing mid-hand.
                if (folded)
                {
                    Fade(card, 0.3f);
                }
            }
        }

        private static void BuildSeatPlaque(
            RectTransform holder,
            string name,
            int stack,
            int committed,
            bool folded,
            bool allIn,
            bool isTurn,
            bool isPlayer,
            bool isSomebody,
            bool hasButton)
        {
            var plaque = NewBox("Plaque", holder, Color.white);
            plaque.sizeDelta = new Vector2(SeatWidth, isPlayer ? 76f : 68f);

            // Whose turn it is wins over who is sitting there, and has to: it changes
            // every action and it is what the eye is looking for. Another person's seat
            // takes the marking only while the table is not waiting on it.
            var face = plaque.GetComponent<Image>();
            face.sprite = Textures.RoundedBox(
                8,
                isTurn ? new Color(0.22f, 0.19f, 0.08f, 0.97f) : new Color(0.05f, 0.06f, 0.06f, 0.93f),
                isTurn ? Gold : isSomebody ? Person : new Color(0.30f, 0.30f, 0.28f, 1f),
                isTurn ? 3 : isSomebody ? 2 : 1);
            face.type = Image.Type.Sliced;

            // Your own seat shows YOUR NAME with "(you)" after it, rather than the bare
            // word YOU. At a shared table the name is the useful half -- your friend has
            // to be able to point at a seat and say whose it is, and the two of you
            // reading different words for the same chair is how the first version of
            // this went wrong. The marker stays because the plaque is small, the seats
            // are rotated so yours is at the bottom, and losing track of which is yours
            // mid-hand is worse than a slightly longer label.
            var title = NewText(
                "Name",
                plaque,
                isPlayer ? name + "  (you)" : name,
                isPlayer ? 19f : 18f,
                TextAlignmentOptions.Center);

            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);

            // Inset, so a long name stops before the dealer badge rather than under it.
            title.rectTransform.sizeDelta = new Vector2(-52f, 28f);
            title.rectTransform.anchoredPosition = new Vector2(0f, -4f);
            title.color = folded ? Dim : (isPlayer ? Gold : isSomebody ? Person : Ink);
            title.overflowMode = TextOverflowModes.Ellipsis;

            var detail = folded
                ? "folded"
                : allIn
                    ? stack.ToString("N0") + "     all in"
                    : committed > 0
                        ? stack.ToString("N0") + "     bet " + committed.ToString("N0")
                        : stack.ToString("N0");

            var under = NewText("Stack", plaque, detail, 16f, TextAlignmentOptions.Center);
            under.rectTransform.anchorMin = new Vector2(0f, 0f);
            under.rectTransform.anchorMax = new Vector2(1f, 0f);
            under.rectTransform.pivot = new Vector2(0.5f, 0f);
            under.rectTransform.sizeDelta = new Vector2(-10f, 26f);
            under.rectTransform.anchoredPosition = new Vector2(0f, 6f);
            under.color = folded ? Dim : Ink;

            // A dot in the corner the dealer badge does not use, for a seat somebody is
            // sitting in. A shape rather than a letter or a word: the font is borrowed
            // from whatever the game happens to have loaded, so anything beyond plain
            // letters is a glyph that might not be in it, and there is no room for a word.
            if (isSomebody)
            {
                var dot = NewBox("Player", plaque, Color.white);
                dot.anchorMin = dot.anchorMax = new Vector2(1f, 1f);
                dot.pivot = new Vector2(1f, 1f);
                dot.sizeDelta = new Vector2(14f, 14f);
                dot.anchoredPosition = new Vector2(-9f, -12f);

                var dotFace = dot.GetComponent<Image>();
                dotFace.sprite = Textures.RoundedBox(7, folded ? Dim : Person, Color.clear);
                dotFace.type = Image.Type.Sliced;
            }

            // The dealer button as a marker rather than a word: it moves every hand,
            // and a badge is read at a glance where a letter in a list is not.
            if (!hasButton)
            {
                return;
            }

            var badge = NewBox("Button", plaque, Color.white);
            badge.anchorMin = badge.anchorMax = new Vector2(0f, 1f);
            badge.pivot = new Vector2(0f, 1f);
            badge.sizeDelta = new Vector2(28f, 28f);
            badge.anchoredPosition = new Vector2(7f, -5f);

            var badgeFace = badge.GetComponent<Image>();
            badgeFace.sprite = Textures.RoundedBox(14, new Color(0.93f, 0.91f, 0.86f, 1f), Gold, 2);
            badgeFace.type = Image.Type.Sliced;

            var d = NewText("D", badge, "D", 15f, TextAlignmentOptions.Center);
            Stretch(d.rectTransform);
            d.color = new Color(0.10f, 0.10f, 0.10f, 1f);
        }

        /// <summary>
        /// Where a seat sits: out along its own direction until its plaque is clear of
        /// the cloth.
        ///
        /// The seat this view was built for goes at the bottom and the rest run round from
        /// there, so the table reads the way one looks at it from a chair. The ring is
        /// rotated by the viewer's index rather than the seats being reordered, which
        /// keeps clockwise on screen the same as clockwise in the engine -- the dealer
        /// button has to be seen to travel the right way, and two people watching the same
        /// hand from different chairs have to agree about which way it went.
        ///
        /// At a solo table the viewer is seat 0 and the rotation is nothing, so this is
        /// the arithmetic that already shipped.
        ///
        /// **Pushed out until it clears, rather than placed on a fixed ellipse.** The
        /// ellipse was 0.52 x 0.74 of the felt *rect*, which put the seats either side of
        /// the table 534 out with a 240-wide plaque on them -- an inner edge at 414
        /// against a cloth reaching 454, so they sat on the playing surface. An ellipse
        /// wide enough to clear it sideways throws the seats above the table off the top
        /// of the screen, because the ring is elliptical and those seats sit at 0.81 of
        /// its height while the player sits at all of it. There is no single ellipse that
        /// satisfies both.
        ///
        /// Solving it per seat does. The seat travels along its own direction until its
        /// box is outside the cloth in one axis or the other, which is the smaller of the
        /// two distances below -- so a seat to the side goes far enough sideways and one
        /// above goes far enough up, and neither pays for the other. It holds at every
        /// seat count rather than at the one the numbers were tuned against.
        ///
        /// Measured from the cloth's own centre, which is not the felt's -- see
        /// <see cref="ClothRise"/>.
        /// </summary>
        private static Vector2 SeatPosition(int index, int total, int viewer, bool isPlayer)
        {
            // Positive remainder: a viewer in a later seat than this one gives a negative
            // difference, and C# keeps the sign, which would throw the seat round the
            // wrong side of the table.
            var place = total <= 0 ? 0 : (((index - viewer) % total) + total) % total;

            var degrees = total <= 1 ? -90f : -90f - (place * (360f / total));
            var radians = degrees * Mathf.Deg2Rad;

            var dx = Mathf.Cos(radians);
            var dy = Mathf.Sin(radians);

            var halfHeight = (isPlayer ? PlayerSeatHeight : SeatHeight) * 0.5f;
            var clearX = ClothX + (SeatWidth * 0.5f) + SeatClearance;
            var clearY = ClothY + halfHeight + SeatClearance;

            var out_ = Mathf.Min(
                Mathf.Abs(dx) > 0.001f ? clearX / Mathf.Abs(dx) : float.MaxValue,
                Mathf.Abs(dy) > 0.001f ? clearY / Mathf.Abs(dy) : float.MaxValue);

            return new Vector2(dx * out_, ClothCentreY + (dy * out_));
        }

        /// <summary>
        /// What the player may press. Built from the server's own list of legal moves
        /// rather than from the client's idea of the rules -- there is one authority
        /// on legality and it is not this side.
        ///
        /// **Nothing to bet with unless the table is waiting on this viewer.** That was
        /// already true and did not need changing: <c>AwaitingPlayer</c> is now built per
        /// viewer and means "waiting on you", and <c>Options</c> is null when it is not.
        /// The server refuses an out-of-turn move anyway, so the reason for the gate here
        /// is not safety -- it is that a live button which does nothing reads as a broken
        /// mod, and at a shared table it would be live for four seats out of five.
        /// </summary>
        private static List<KeyValuePair<string, Action>> ActionsFor(
            string street, bool awaiting, JObject options)
        {
            var actions = new List<KeyValuePair<string, Action>>();

            var betweenHands =
                string.Equals(street, "Idle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(street, "Showdown", StringComparison.OrdinalIgnoreCase);

            if (betweenHands)
            {
                actions.Add(Action("DEAL", Deal));

                if (_shared)
                {
                    actions.Add(Action("REFRESH", RefreshShared));
                }

                actions.Add(Action("LEAVE", Leave));
                actions.Add(Action("CLOSE", Close));
                return actions;
            }

            if (!awaiting || options == null)
            {
                // Somebody else's move. At a solo table that is a bot and it will be over
                // in a moment; at a shared one it is a person who might have walked away,
                // and the push that says they moved is the one thing here that can fail
                // to arrive. So there is something to press that is not "close the table".
                if (_shared)
                {
                    actions.Add(Action("REFRESH", RefreshShared));
                }

                actions.Add(Action("CLOSE", Close));
                return actions;
            }

            var moves = options["Moves"]?.Select(m => (string)m).ToArray() ?? new string[0];
            var toCall = (int?)options["ToCall"] ?? 0;
            var minRaise = (int?)options["MinRaiseTo"] ?? 0;
            var maxRaise = (int?)options["MaxRaiseTo"] ?? 0;

            foreach (var move in moves)
            {
                if (string.Equals(move, "Raise", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var label = string.Equals(move, "Call", StringComparison.OrdinalIgnoreCase) && toCall > 0
                    ? "CALL " + toCall.ToString("N0")
                    : move.ToUpperInvariant();

                var captured = move;
                actions.Add(Action(label, () => Act(captured)));
            }

            if (moves.Any(m => string.Equals(m, "Raise", StringComparison.OrdinalIgnoreCase)) && maxRaise > 0)
            {
                _raiseTo = Mathf.Clamp(_raiseTo <= 0 ? minRaise : _raiseTo, minRaise, maxRaise);

                // Step by the smallest chip, so every amount the player can pick is
                // one the table can actually show.
                var step = ChipView.Smallest;

                actions.Add(Action("-", () => Nudge(-step, minRaise, maxRaise)));
                actions.Add(Action("RAISE TO " + _raiseTo.ToString("N0"), () => Act("Raise", _raiseTo)));
                actions.Add(Action("+", () => Nudge(step, minRaise, maxRaise)));

                if (maxRaise > minRaise)
                {
                    actions.Add(Action("ALL IN " + maxRaise.ToString("N0"), () => Act("Raise", maxRaise)));
                }
            }

            return actions;
        }

        /// <summary>
        /// Steps the raise. Redrawn from the view already in hand rather than by
        /// asking the server, because choosing an amount is not a move: a round trip
        /// here would let the table change between the player pressing + and pressing
        /// raise.
        /// </summary>
        private static void Nudge(int by, int min, int max)
        {
            _raiseTo = Mathf.Clamp(_raiseTo + by, min, max);

            if (_lastReply != null)
            {
                Render(_lastReply, keepStatus: true);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static KeyValuePair<string, Action> Action(string label, Action onClick) =>
            new KeyValuePair<string, Action>(label, onClick);

        private static bool Ok(JObject reply) => reply != null && ((bool?)reply["Ok"] ?? false);

        private static string ErrorOf(JObject reply)
        {
            var error = (string)reply?["Error"];
            return string.IsNullOrEmpty(error) ? null : error;
        }

        private static void SetStatus(string text)
        {
            if (_status != null)
            {
                _status.text = text;
            }
        }

        /// <summary>Dims a built card without knowing how it was assembled.</summary>
        private static void Fade(GameObject card, float alpha)
        {
            foreach (var image in card.GetComponentsInChildren<Image>(true))
            {
                var c = image.color;
                image.color = new Color(c.r, c.g, c.b, c.a * alpha);
            }

            foreach (var label in card.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                var c = label.color;
                label.color = new Color(c.r, c.g, c.b, c.a * alpha);
            }
        }

        /// <summary>
        /// The pot, drawn as chips. Rebuilt each view rather than mutated, the same as
        /// the board and the action strip: it changes on nearly every action.
        /// </summary>
        private static void SetPot(int? pot)
        {
            if (_potHolder == null)
            {
                return;
            }

            for (var i = _potHolder.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_potHolder.GetChild(i).gameObject);
            }

            if (!pot.HasValue || pot.Value <= 0)
            {
                return;
            }

            ChipView.Build(_potHolder, pot.Value, _font, size: 40f);
        }

        private static void ClearSeats()
        {
            if (_seatLayer == null)
            {
                return;
            }

            for (var i = _seatLayer.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_seatLayer.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// Redraws the community cards. Rebuilt rather than mutated: five cards is
        /// nothing to build, and reusing them means tracking which slot holds what.
        /// </summary>
        private static void SetBoard(string[] codes)
        {
            if (_board == null)
            {
                return;
            }

            for (var i = _board.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_board.GetChild(i).gameObject);
            }

            for (var i = 0; i < 5; i++)
            {
                var dealt = codes != null && i < codes.Length;
                var card = CardSlot(_board, dealt ? codes[i] : null, BoardCardScale);

                // An undealt slot is left as a ghost rather than a card back. A back
                // means a card exists and is hidden, which on the board never happens.
                if (!dealt)
                {
                    Fade(card, 0.16f);
                }
            }
        }

        // ------------------------------------------------------- the shared-table list

        /// <summary>The columns the list is laid out in. Header and rows share them.</summary>
        private const float ListWho = 300f;

        private const float ListSeats = 250f;

        private const float ListStakes = 300f;

        private const float ListState = 160f;

        private const float ListTail = 150f;

        private const float ListRow = 1240f;

        /// <summary>
        /// How many tables are drawn before the rest are counted instead.
        ///
        /// The sheet is a fixed height with no scrolling, which is the right trade at the
        /// numbers this sees: a shared table needs two people who have arranged to meet,
        /// so a server with six of them at once does not happen. Five rows fit; anything
        /// past that is a line saying how many were left out, which is at least honest
        /// about it rather than silently drawing four of nine.
        /// </summary>
        private const int ListRows = 5;

        private static void HideTables()
        {
            if (_lobbySheet != null && _lobbySheet.activeSelf)
            {
                _lobbySheet.SetActive(false);
            }
        }

        private static void ShowTables(JArray tables, string yours, string error)
        {
            if (_lobbySheet == null || _lobbyRows == null || _lobbyNote == null)
            {
                return;
            }

            _lobbySheet.SetActive(true);

            for (var i = _lobbyRows.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_lobbyRows.GetChild(i).gameObject);
            }

            var all = tables?.OfType<JObject>().ToList() ?? new List<JObject>();

            foreach (var table in all.Take(ListRows))
            {
                BuildTableRow(table, yours);
            }

            _lobbyNote.color = error != null ? new Color(0.80f, 0.42f, 0.36f, 1f) : Dim;

            _lobbyNote.text =
                error
                ?? (all.Count > ListRows
                    ? "and " + (all.Count - ListRows) + " more"
                    : all.Count == 0
                        ? "Nothing open. Open one and it appears here for anybody else on this server."
                        : string.Empty);
        }

        /// <summary>
        /// One listed table.
        ///
        /// A table with a hand in progress has no JOIN on it, because the server refuses
        /// to seat anybody mid-hand -- it takes no money for the attempt, but a button
        /// whose only outcome is a refusal is worse than no button. The state column says
        /// why, and refreshing once the hand ends brings it back.
        /// </summary>
        private static void BuildTableRow(JObject table, string yours)
        {
            var id = (string)table["Id"] ?? "?";
            var host = (string)table["HostName"] ?? "Somebody";
            var seats = (int?)table["Seats"] ?? 0;
            var people = (int?)table["People"] ?? 0;
            var free = (int?)table["FreeSeats"] ?? 0;
            var buyIn = (int?)table["BuyIn"] ?? 0;
            var blind = (int?)table["BigBlind"] ?? 0;
            var inHand = (bool?)table["InHand"] ?? false;

            var mine = string.Equals(id, yours, StringComparison.Ordinal);

            var row = NewBox("Table_" + id, _lobbyRows, Color.white);
            row.sizeDelta = new Vector2(ListRow, 52f);

            var face = row.GetComponent<Image>();
            face.sprite = Textures.RoundedBox(
                6,
                mine ? new Color(0.13f, 0.12f, 0.06f, 0.95f) : new Color(0.09f, 0.10f, 0.10f, 0.95f),
                mine ? Gold : new Color(1f, 1f, 1f, 0.10f),
                mine ? 2 : 1);
            face.type = Image.Type.Sliced;

            var strip = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            strip.spacing = 10f;
            strip.padding = new RectOffset(16, 16, 0, 0);
            strip.childAlignment = TextAnchor.MiddleLeft;
            strip.childForceExpandWidth = false;
            strip.childForceExpandHeight = false;
            strip.childControlWidth = false;
            strip.childControlHeight = false;

            Column(row, host + "     " + id, ListWho, mine ? Gold : Ink);
            Column(row, people + " of " + seats + " seated     " + free + " free", ListSeats, Ink);
            Column(row, Roubles(buyIn) + "     blind " + Roubles(blind), ListStakes, Ink);
            Column(row, inHand ? "in a hand" : "between hands", ListState, inHand ? Dim : Ink);

            // The button in a holder of its own, so a row with no button on it is still
            // the same shape as one that has -- a horizontal layout measures what is in
            // it, and columns that shift about between rows are unreadable as a table.
            var tail = NewBox("Tail", row, Color.clear);
            tail.sizeDelta = new Vector2(ListTail, 44f);

            var holder = tail.gameObject.AddComponent<HorizontalLayoutGroup>();
            holder.childAlignment = TextAnchor.MiddleCenter;
            holder.childForceExpandWidth = false;
            holder.childForceExpandHeight = false;
            holder.childControlWidth = false;
            holder.childControlHeight = false;

            if (mine)
            {
                Column(tail, "YOUR SEAT", ListTail, Gold, TextAlignmentOptions.Center);
                return;
            }

            if (inHand)
            {
                return;
            }

            var captured = id;
            var stake = buyIn;

            BuildButton(tail, "JOIN", () => ConfirmJoin(captured, stake));
        }

        private static TextMeshProUGUI Column(
            Transform parent,
            string text,
            float width,
            Color colour,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var label = NewText("Column", parent, text, 17f, alignment);
            label.rectTransform.sizeDelta = new Vector2(width, 40f);
            label.color = colour;
            label.overflowMode = TextOverflowModes.Ellipsis;

            return label;
        }

        // ---------------------------------------------------------------- building

        private static void Build()
        {
            _font = BorrowFont();

            var canvasObject = new GameObject(
                RootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Above the menu, which is the only place this opens from.
            canvas.sortingOrder = 30000;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Match height, not a blend. Blending grows the table with screen width,
            // so an ultrawide gets it stretched across the monitor instead of one
            // table with dark either side.
            scaler.matchWidthOrHeight = 1f;

            _root = canvasObject;

            // Faded rather than switched. The backdrop is nearly opaque, so toggling
            // the canvas takes the whole screen from menu to table and back in a
            // single frame -- which is what makes leaving feel like a jump cut.
            var group = canvasObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;

            // The backdrop is what swallows clicks meant for the menu underneath. It
            // needs a Graphic to be raycast at all, hence a nearly-opaque image rather
            // than an empty transform. Darker than it was: the menu showing through
            // behind the seats was most of what made the table hard to read.
            var backdrop = NewBox("Backdrop", canvasObject.transform, new Color(0f, 0f, 0f, 0.93f));
            Stretch(backdrop);

            var title = NewText("Title", canvasObject.transform, "POKER", 30f, TextAlignmentOptions.Center);
            title.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            title.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);
            title.rectTransform.sizeDelta = new Vector2(600f, 44f);
            title.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            title.color = Gold;

            // The felt sits above centre: the seat ring reaches further below it than
            // above, and the bottom seat is the player's, which needs to clear the
            // status line and the action strip.
            var stage = NewBox("Stage", canvasObject.transform, Color.clear);
            stage.anchorMin = stage.anchorMax = new Vector2(0.5f, 0.5f);
            stage.pivot = new Vector2(0.5f, 0.5f);
            stage.sizeDelta = new Vector2(FeltWidth, FeltHeight);
            stage.anchoredPosition = new Vector2(0f, StageRise);

            BuildTable(stage);

            // Seats on their own layer above the felt, so a plaque overlapping the rim
            // draws over the cloth rather than under it.
            _seatLayer = NewBox("Seats", stage, Color.clear);
            Stretch(_seatLayer);

            BuildStatus(canvasObject.transform);
            BuildActionRow(canvasObject.transform);
            BuildLobbySheet(canvasObject.transform);
        }

        /// <summary>
        /// The sheet the shared tables are listed on, built once and switched off.
        ///
        /// Over the felt rather than instead of it -- the same thing Blackjack does with
        /// its stats -- so choosing a table still looks like standing in the room the
        /// tables are in. It clears the status line and the action strip, which is what
        /// decided its height; see StageRise for the other end of that constraint.
        /// </summary>
        private static void BuildLobbySheet(Transform parent)
        {
            var sheet = NewBox("Lobby", parent, Color.white);
            sheet.anchorMin = sheet.anchorMax = new Vector2(0.5f, 0.5f);
            sheet.pivot = new Vector2(0.5f, 0.5f);
            sheet.sizeDelta = new Vector2(1300f, 470f);
            sheet.anchoredPosition = new Vector2(0f, 70f);

            var face = sheet.GetComponent<Image>();
            face.sprite = Textures.RoundedBox(
                14, new Color(0.05f, 0.06f, 0.06f, 0.95f), new Color(1f, 1f, 1f, 0.10f), 2);
            face.type = Image.Type.Sliced;

            var column = sheet.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 10f;
            column.padding = new RectOffset(24, 24, 18, 18);
            column.childAlignment = TextAnchor.UpperCenter;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            column.childControlWidth = false;
            column.childControlHeight = false;

            var heading = NewText("Heading", sheet, "SHARED TABLES", 22f, TextAlignmentOptions.Center);
            heading.rectTransform.sizeDelta = new Vector2(ListRow, 28f);
            heading.color = Gold;

            // A header row on the same columns as the rows, so the numbers underneath do
            // not each have to carry a word saying what they are.
            var header = NewBox("Header", sheet, Color.clear);
            header.sizeDelta = new Vector2(ListRow, 22f);

            var headerStrip = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            headerStrip.spacing = 10f;
            headerStrip.padding = new RectOffset(16, 16, 0, 0);
            headerStrip.childAlignment = TextAnchor.MiddleLeft;
            headerStrip.childForceExpandWidth = false;
            headerStrip.childForceExpandHeight = false;
            headerStrip.childControlWidth = false;
            headerStrip.childControlHeight = false;

            Column(header, "HOST AND TABLE", ListWho, Dim).fontSize = 14f;
            Column(header, "SEATS", ListSeats, Dim).fontSize = 14f;
            Column(header, "BUY-IN AND BLIND", ListStakes, Dim).fontSize = 14f;
            Column(header, "STATE", ListState, Dim).fontSize = 14f;
            Column(header, string.Empty, ListTail, Dim).fontSize = 14f;

            var rule = NewBox("Rule", sheet, new Color(1f, 1f, 1f, 0.10f));
            rule.sizeDelta = new Vector2(ListRow, 2f);

            _lobbyRows = NewBox("Rows", sheet, Color.clear);
            _lobbyRows.sizeDelta = new Vector2(ListRow + 8f, 290f);

            var rows = _lobbyRows.gameObject.AddComponent<VerticalLayoutGroup>();
            rows.spacing = 6f;
            rows.childAlignment = TextAnchor.UpperCenter;
            rows.childForceExpandWidth = false;
            rows.childForceExpandHeight = false;
            rows.childControlWidth = false;
            rows.childControlHeight = false;

            _lobbyNote = NewText("Note", sheet, string.Empty, 18f, TextAlignmentOptions.Center);
            _lobbyNote.rectTransform.sizeDelta = new Vector2(ListRow, 26f);
            _lobbyNote.color = Dim;

            _lobbySheet = sheet.gameObject;
            _lobbySheet.SetActive(false);
        }

        /// <summary>
        /// The felt, with the community cards and the pot on it.
        ///
        /// The photograph is loaded from beside the DLL. If it is missing, FromFile
        /// returns null and the cloth falls back to a flat green -- a table without a
        /// photograph is still a table, and a hard failure here would take the whole
        /// panel with it.
        /// </summary>
        private static void BuildTable(RectTransform parent)
        {
            var felt = NewBox("Felt", parent, Color.white);
            Stretch(felt);

            var image = felt.GetComponent<Image>();
            var photo = Textures.FromFile(TableImagePath);

            if (photo != null)
            {
                image.sprite = photo;
                image.preserveAspect = true;
            }
            else
            {
                image.color = new Color(0.09f, 0.28f, 0.18f, 1f);
                PokerClientPlugin.Log.LogWarning(
                    "[Poker] no table.png beside the plugin; falling back to flat cloth.");
            }

            // On the cloth's centre, not the picture's. The board sits a little above it
            // and the pot below, which is where a dealer puts them.
            _board = NewBox("Board", felt, Color.clear);
            _board.anchorMin = _board.anchorMax = new Vector2(0.5f, 0.5f);
            _board.pivot = new Vector2(0.5f, 0.5f);
            _board.sizeDelta = new Vector2(
                (5f * CardView.Width * BoardCardScale) + (4f * BoardCardGap),
                CardView.Height * BoardCardScale);
            _board.anchoredPosition = new Vector2(0f, ClothCentreY + 34f);

            var row = _board.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = BoardCardGap;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childControlWidth = false;
            row.childControlHeight = false;

            _potHolder = NewBox("Pot", felt, Color.clear);
            _potHolder.anchorMin = _potHolder.anchorMax = new Vector2(0.5f, 0.5f);
            _potHolder.pivot = new Vector2(0.5f, 0.5f);
            // Tall enough for the chips and the number beneath them, and dropped a
            // little so the extra height does not reach back up towards the board.
            _potHolder.sizeDelta = new Vector2(460f, 92f);
            _potHolder.anchoredPosition = new Vector2(0f, ClothCentreY - 80f);

            var potRow = _potHolder.gameObject.AddComponent<HorizontalLayoutGroup>();
            potRow.childAlignment = TextAnchor.MiddleCenter;
            potRow.childForceExpandWidth = false;
            potRow.childForceExpandHeight = false;
            potRow.childControlWidth = false;
            potRow.childControlHeight = false;

            SetBoard(null);
        }

        private static void BuildStatus(Transform parent)
        {
            _status = NewText("Status", parent, string.Empty, 19f, TextAlignmentOptions.Center);
            _status.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _status.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _status.rectTransform.pivot = new Vector2(0.5f, 0f);
            _status.rectTransform.sizeDelta = new Vector2(1600f, 30f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, 88f);
            _status.color = Ink;
        }

        private static void BuildActionRow(Transform parent)
        {
            _actionRow = NewBox("Actions", parent, Color.clear);
            _actionRow.anchorMin = new Vector2(0.5f, 0f);
            _actionRow.anchorMax = new Vector2(0.5f, 0f);
            _actionRow.pivot = new Vector2(0.5f, 0f);
            _actionRow.sizeDelta = new Vector2(1600f, 52f);
            _actionRow.anchoredPosition = new Vector2(0f, 26f);

            var strip = _actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            strip.spacing = 10f;
            strip.childAlignment = TextAnchor.MiddleCenter;
            strip.childForceExpandWidth = false;
            strip.childForceExpandHeight = false;
            strip.childControlWidth = false;
            strip.childControlHeight = false;
        }

        /// <summary>
        /// Rebuilt from scratch on every view, rather than shown and hidden. What is
        /// legal changes every action, and a stale button that is still clickable is
        /// a move the player did not mean to make.
        /// </summary>
        private static void BuildActions(IEnumerable<KeyValuePair<string, Action>> actions)
        {
            if (_actionRow == null)
            {
                return;
            }

            for (var i = _actionRow.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_actionRow.GetChild(i).gameObject);
            }

            foreach (var action in actions)
            {
                BuildButton(_actionRow, action.Key, action.Value);
            }
        }

        private static void BuildButton(Transform parent, string label, Action onClick)
        {
            var box = NewBox("Button_" + label, parent, Color.white);
            box.sizeDelta = new Vector2(Mathf.Max(72f, 26f + (label.Length * 12f)), 44f);

            var image = box.GetComponent<Image>();
            image.sprite = Textures.ButtonFace(
                6,
                new Color(0.19f, 0.20f, 0.19f, 1f),
                new Color(0.11f, 0.12f, 0.11f, 1f),
                Gold);
            image.type = Image.Type.Sliced;

            var text = NewText("Label", box, label, 18f, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);

            box.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());
        }

        private static RectTransform NewBox(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = colour;
            return (RectTransform)go.transform;
        }

        private static TextMeshProUGUI NewText(
            string name, Transform parent, string text, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = Ink;
            label.raycastTarget = false;

            if (_font != null)
            {
                label.font = _font;
            }

            return label;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Borrows a font the game has already loaded rather than shipping one.
        /// TextMeshPro renders nothing at all with a null font, so a label that never
        /// appears looks like a layout bug rather than a missing asset.
        /// </summary>
        private static TMP_FontAsset BorrowFont()
        {
            try
            {
                return Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            }
            catch (Exception ex)
            {
                PokerClientPlugin.Log.LogWarning("[Poker] could not borrow a font: " + ex.Message);
                return null;
            }
        }
    }
}
