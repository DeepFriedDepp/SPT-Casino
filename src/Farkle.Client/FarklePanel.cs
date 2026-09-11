using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Casino.Shared;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Textures = Casino.Shared.Textures;

namespace Farkle.Client
{
    /// <summary>
    /// The fifth table.
    ///
    /// Three screens in one frame: the lobby (open a table for a friend, play the house,
    /// or join somebody's), the wait for an opponent, and the match. The server decides
    /// every die and every score; this draws what it is handed and asks for what the
    /// player wants. **Every action is a request.** The socket only pushes downward, and
    /// what arrives on it is the other seat's move, so the only thing to do with it is
    /// redraw -- or replay it, when it was a whole turn.
    ///
    /// Same shape as the other four: a static class with IsOpen, Open and Close, a canvas
    /// at sorting order 30000 so it covers the lobby, and nothing in it that knows about
    /// the task bar, the casino lobby or the escape key. That is what lets
    /// `Casino.Client` compile it in without owning it.
    ///
    /// ## The opponent's turn is replayed, not dumped
    ///
    /// The server plays a bot's whole turn inside one request and the other human's turn
    /// arrives as pushes per move, but either way the view carries the turn's events.
    /// When a new view shows a turn that was not ours and has not been shown, its events
    /// are stepped through with a pause each -- the roll, the keep, the bank or the
    /// farkle -- so the player watches it happen rather than seeing a score change.
    /// </summary>
    internal static class FarklePanel
    {
        private const string RootName = "FarkleCanvas";
        private const string SyncAction = "FarkleSync";

        private const float FrameWidth = 1240f;
        private const float FrameHeight = 760f;
        private const float DieSize = 84f;
        private const float DieGap = 18f;
        private const float SmallDie = 40f;

        private static readonly Color Gold = new Color(0.85f, 0.72f, 0.38f, 1f);
        private static readonly Color Ink = new Color(0.93f, 0.91f, 0.86f, 1f);
        private static readonly Color Dim = new Color(0.65f, 0.63f, 0.58f, 1f);
        private static readonly Color Panel = new Color(0.10f, 0.11f, 0.12f, 0.96f);
        private static readonly Color Edge = new Color(0.45f, 0.38f, 0.22f, 1f);
        private static readonly Color Bone = new Color(0.87f, 0.86f, 0.83f, 1f);
        private static readonly Color Pip = new Color(0.05f, 0.05f, 0.05f, 1f);
        private static readonly Color Bad = new Color(0.90f, 0.45f, 0.35f, 1f);
        private static readonly Color Good = new Color(0.55f, 0.80f, 0.45f, 1f);
        private static readonly Color Felt = new Color(0.07f, 0.16f, 0.10f, 1f);

        private static GameObject _root;
        private static CanvasGroup _group;
        private static TMP_FontAsset _font;
        private static Coroutine _fade;
        private static Coroutine _poll;
        private static Coroutine _animation;
        private static bool _closing;
        private static Action<string> _listener;

        // What the server last said.
        private static JObject _ping;
        private static JObject _view;
        private static string _tableId;
        private static int? _mySeat;
        private static long _stake = 50_000;
        private static int _botIndex;
        private static int _targetIndex = -1;

        // The other seat's turn, as it is shown. Events already drawn are counted per turn
        // so a push, a poll and a route reply can all arrive with overlapping views and
        // nothing is shown twice or skipped.
        private static int _shownTurn = -1;
        private static int _shownCount;
        private static readonly Queue<JObject> _pending = new Queue<JObject>();
        private static JObject _latest;
        private static JObject _stage;
        private static readonly List<int> _replaySelected = new List<int>();

        // The frame's pieces.
        private static RectTransform _frame;
        private static TextMeshProUGUI _status;
        private static TextMeshProUGUI _balance;
        private static GameObject _lobby;
        private static GameObject _match;
        private static RectTransform _tableList;
        private static TextMeshProUGUI _stakeLabel;
        private static TextMeshProUGUI _botLabel;
        private static TextMeshProUGUI _targetLabel;
        private static TextMeshProUGUI _leaveLabel;
        private static TextMeshProUGUI _sheet;
        private static TextMeshProUGUI _odds;
        private static readonly RectTransform[] _seatCards = new RectTransform[2];
        private static readonly TextMeshProUGUI[] _seatNames = new TextMeshProUGUI[2];
        private static readonly TextMeshProUGUI[] _seatScores = new TextMeshProUGUI[2];
        private static readonly TextMeshProUGUI[] _seatNotes = new TextMeshProUGUI[2];
        private static RectTransform _diceRow;
        private static RectTransform _asideRow;
        private static TextMeshProUGUI _turnLabel;
        private static TextMeshProUGUI _turnNote;
        private static RectTransform _rollButton;
        private static RectTransform _keepButton;
        private static RectTransform _bankButton;
        private static TextMeshProUGUI _keepLabel;
        private static TextMeshProUGUI _bankLabel;
        private static TextMeshProUGUI _banner;
        private static readonly List<int> _selected = new List<int>();

        internal static bool IsOpen => _root != null && _root.activeSelf && !_closing;

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
                Listen();

                _ping = FarkleApi.Ping();
                Note(_ping);

                if (_ping == null || _ping.Value<bool?>("Ok") == false)
                {
                    ShowLobby(null);
                    Say("The server is not answering. Is Farkle.Server.dll in the casino's mod folder?", Bad);
                    return;
                }

                // Already seated -- the game was closed mid-match, or this is a reconnect.
                var mine = _ping.Value<string>("YourTable");

                if (!string.IsNullOrEmpty(mine))
                {
                    var state = FarkleApi.State();

                    if (state != null && state.Value<bool?>("Ok") != false)
                    {
                        Render(state, replay: false);
                        StartPolling();
                        return;
                    }
                }

                ShowLobby(FarkleApi.Tables());
            }
            catch (Exception ex)
            {
                FarkleClientPlugin.Log?.LogError("[Farkle] could not open the table: " + ex);
            }
        }

        internal static void Close()
        {
            if (_root == null || !_root.activeSelf || _closing)
            {
                return;
            }

            // Money may have moved while this was open. The running game is told on the
            // way out rather than left to find out at a reload.
            ProfileSync.Request(SyncAction);

            StopPolling();
            StopAnimation();
            Deafen();

            _closing = true;

            FadeTo(0f, () =>
            {
                _root.SetActive(false);
                _closing = false;
            });
        }

        // ------------------------------------------------------------------ the lobby

        private static void ShowLobby(JObject tables)
        {
            StopPolling();
            StopAnimation();
            _tableId = null;
            _shownTurn = -1;
            _shownCount = 0;
            _mySeat = null;
            _view = null;
            _selected.Clear();

            _lobby.SetActive(true);
            _match.SetActive(false);
            _banner.text = string.Empty;

            RenderBalance(_ping?.Value<int?>("Balance"));
            RenderSheet();
            _stakeLabel.text = _stake.ToString("N0") + " R";
            _botLabel.text = "vs " + BotName();
            _targetLabel.text = "First to " + TargetValue().ToString("N0");

            // Nothing to leave from here. The button closes the table instead, and says so.
            _leaveLabel.text = "CLOSE";

            for (var i = _tableList.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_tableList.GetChild(i).gameObject);
            }

            var rows = tables?["Tables"] as JArray;

            if (rows == null || rows.Count == 0)
            {
                var empty = NewText("Empty", _tableList, "Nobody is waiting for an opponent. Open a table, or play the house.", 17f);
                empty.rectTransform.sizeDelta = new Vector2(560f, 40f);
                empty.rectTransform.anchoredPosition = new Vector2(0f, -30f);
                empty.color = Dim;
                empty.enableWordWrapping = true;
            }
            else
            {
                var y = -26f;

                foreach (var row in rows.Take(8))
                {
                    var id = row.Value<string>("Id");
                    var host = row.Value<string>("HostName");
                    var stake = row.Value<long?>("Stake") ?? 0;

                    var line = NewBox("Row", _tableList, new Color(0f, 0f, 0f, 0.30f));
                    line.sizeDelta = new Vector2(560f, 44f);
                    line.anchoredPosition = new Vector2(0f, y);
                    line.GetComponent<Image>().sprite = Textures.RoundedBox(6, new Color(0f, 0f, 0f, 0.30f), Edge, 1);
                    line.GetComponent<Image>().type = Image.Type.Sliced;

                    var target = row.Value<int?>("Target") ?? 10000;
                    var label = NewText("Who", line, host + "  -  " + stake.ToString("N0") + " R a seat, to " + target.ToString("N0"), 18f);
                    label.rectTransform.sizeDelta = new Vector2(380f, 40f);
                    label.rectTransform.anchoredPosition = new Vector2(-70f, 0f);
                    label.alignment = TextAlignmentOptions.Left;

                    var chosen = id;
                    BuildButton(line, "JOIN", new Vector2(215f, 0f), 110f, () => Join(chosen));

                    y -= 52f;
                }
            }

            if (_status.text.Length == 0)
            {
                Say("Two seats, one race. Pick the stake and the target; the first to the target takes both stakes.", Dim);
            }
        }

        private static void OpenTable(bool vsBot)
        {
            var reply = FarkleApi.Open(_stake, TargetValue(), vsBot, vsBot ? BotName() : string.Empty);
            Note(reply);

            if (reply == null || reply.Value<bool?>("Ok") == false)
            {
                Say(reply?.Value<string>("Error") ?? "The server did not answer.", Bad);
                return;
            }

            // The stake has left the stash. Tell the running game now, so the counter
            // behind the table agrees with the server before a die is rolled.
            ProfileSync.Request(SyncAction);
            Render(reply, replay: false);
            StartPolling();
        }

        private static void Join(string tableId)
        {
            var reply = FarkleApi.Join(tableId);
            Note(reply);

            if (reply == null || reply.Value<bool?>("Ok") == false)
            {
                Say(reply?.Value<string>("Error") ?? "The server did not answer.", Bad);
                ShowLobby(FarkleApi.Tables());
                return;
            }

            ProfileSync.Request(SyncAction);
            Render(reply, replay: false);
            StartPolling();
        }

        private static void Leave()
        {
            // Not seated: there is no table to leave, so the button is CLOSE and closes
            // the panel. The first live run sent this request anyway, got "You are not at a
            // table" back with a default balance of zero, and painted "0 R in the stash".
            if (_tableId == null)
            {
                Close();
                return;
            }

            var reply = FarkleApi.Leave();
            Note(reply);

            if (reply != null && reply.Value<bool?>("Ok") == false && reply.Value<string>("Error") != "You are not at a table.")
            {
                Say(reply.Value<string>("Error"), Bad);
                return;
            }

            ProfileSync.Request(SyncAction);
            _status.text = string.Empty;
            ShowLobby(FarkleApi.Tables());

            // Only from a reply that succeeded. A refusal carries the record's default
            // balance, which is zero, and zero is not what is in the stash.
            if (reply != null && reply.Value<bool?>("Ok") != false && reply.Value<int?>("Balance") is int balance)
            {
                RenderBalance(balance);
            }
        }

        private static int TargetValue()
        {
            var targets = _ping?["Targets"] as JArray;

            if (targets == null || targets.Count == 0)
            {
                return 10_000;
            }

            if (_targetIndex < 0)
            {
                _targetIndex = targets.Count - 1;
            }

            _targetIndex = ((_targetIndex % targets.Count) + targets.Count) % targets.Count;

            return targets[_targetIndex].Value<int>();
        }

        private static string BotName()
        {
            var bots = _ping?["Bots"] as JArray;

            if (bots == null || bots.Count == 0)
            {
                return "the house";
            }

            _botIndex = ((_botIndex % bots.Count) + bots.Count) % bots.Count;

            return bots[_botIndex].Value<string>();
        }

        private static void StepStake(long by)
        {
            var min = _ping?.Value<long?>("MinStake") ?? 10_000;
            var max = _ping?.Value<long?>("MaxStake") ?? 1_000_000;

            _stake = Math.Max(min, Math.Min(max, _stake + by));
            _stakeLabel.text = _stake.ToString("N0") + " R";
        }

        // ------------------------------------------------------------------ the match

        private static void Act(Func<JObject> action)
        {
            var reply = action();
            Note(reply);

            if (reply == null)
            {
                Say("The server did not answer.", Bad);
                return;
            }

            if (reply.Value<bool?>("Ok") == false)
            {
                Say(reply.Value<string>("Error") ?? "Refused.", Bad);
            }

            // A refusal still carries the real view, and the real view is the fix for
            // whatever drift produced the refusal.
            if (reply["Match"] is JObject)
            {
                Render(reply, replay: true);
            }
        }

        private static void Roll() => Act(FarkleApi.Roll);

        private static void Bank() => Act(FarkleApi.Bank);

        private static void KeepSelected()
        {
            if (_selected.Count == 0)
            {
                Say("Pick the dice to set aside first.", Dim);
                return;
            }

            var indices = _selected.OrderBy(i => i).ToList();
            _selected.Clear();
            Act(() => FarkleApi.Keep(indices));
        }

        private static void ToggleDie(int index)
        {
            if (_view == null || (string)_view["Phase"] != "Choosing" || !MyTurn())
            {
                return;
            }

            if (_selected.Contains(index))
            {
                _selected.Remove(index);
            }
            else
            {
                _selected.Add(index);
            }

            RenderDice();
            RenderButtons();
        }

        private static bool MyTurn() =>
            _view != null && _mySeat.HasValue && _view.Value<int?>("CurrentSeat") == _mySeat.Value;

        /// <summary>Takes a response apart and draws it. Replays the other seat's turn when there is one to replay.</summary>
        private static void Render(JObject reply, bool replay)
        {
            var view = reply["Match"] as JObject;

            if (view == null)
            {
                return;
            }

            _tableId = reply.Value<string>("TableId") ?? _tableId;
            _mySeat = reply.Value<int?>("YourSeat") ?? _mySeat;

            if (reply.Value<int?>("Balance") is int balance)
            {
                RenderBalance(balance);
            }

            var stake = reply.Value<long?>("Stake");

            if (stake.HasValue)
            {
                _stake = stake.Value;
            }

            if (replay)
            {
                Absorb(view);
                return;
            }

            // A fresh start: whatever has happened so far is history, not a replay.
            StopAnimation();
            _shownTurn = view.Value<int?>("TurnNumber") ?? 0;
            _shownCount = (view["Turn"] as JArray)?.Count ?? 0;
            _view = view;
            Draw();
        }

        private static void Draw()
        {
            _lobby.SetActive(false);
            _match.SetActive(true);

            var phase = (string)_view["Phase"];
            var seats = _view["Seats"] as JArray;
            var current = _view.Value<int?>("CurrentSeat") ?? 0;
            var finished = phase == "Finished";
            var winner = _view.Value<int?>("Winner");

            for (var i = 0; i < 2; i++)
            {
                var seat = seats != null && seats.Count > i ? seats[i] as JObject : null;
                var occupied = seat?.Value<bool?>("Occupied") ?? false;
                var isMe = _mySeat == i;

                _seatNames[i].text = occupied
                    ? (seat.Value<string>("Name") ?? ("Seat " + i)) + (isMe ? "  (you)" : string.Empty)
                    : "Waiting for an opponent...";
                _seatNames[i].color = occupied ? Ink : Dim;
                _seatScores[i].text = occupied ? (seat.Value<int?>("Score") ?? 0).ToString("N0") : "-";

                _seatNotes[i].text = !occupied ? string.Empty
                    : finished ? (winner == i ? "WINNER" : string.Empty)
                    : (seat.Value<bool?>("IsBot") ?? false) ? "the house's regular" : string.Empty;

                var lit = !finished && phase != "WaitingForOpponent" && current == i;
                _seatCards[i].GetComponent<Image>().sprite = Textures.RoundedBox(8, lit ? new Color(0.16f, 0.15f, 0.10f, 1f) : new Color(0.12f, 0.12f, 0.13f, 1f), lit ? Gold : Edge, lit ? 3 : 1);
                _seatCards[i].GetComponent<Image>().type = Image.Type.Sliced;
            }

            RenderDice();
            RenderAside();
            RenderButtons();

            var turnScore = _view.Value<int?>("TurnScore") ?? 0;
            var inHand = _view.Value<int?>("DiceInHand") ?? 6;

            var target = _view.Value<int?>("Target") ?? 10000;

            _turnLabel.text = phase == "WaitingForOpponent" || finished
                ? string.Empty
                : "First to " + target.ToString("N0") + "      This turn: " + turnScore.ToString("N0") + "      " + inHand + " to roll";

            _leaveLabel.text = "LEAVE";

            // The latest thing that happened, in the engine's own words.
            var events = _view["Turn"] as JArray;
            var last = events != null && events.Count > 0 ? events[events.Count - 1] as JObject : null;

            if (last == null && (_view["LastTurn"] as JArray)?.Count > 0)
            {
                var lastTurn = (JArray)_view["LastTurn"];
                last = lastTurn[lastTurn.Count - 1] as JObject;
            }

            _turnNote.text = last?.Value<string>("Note") ?? string.Empty;

            if (phase == "WaitingForOpponent")
            {
                _banner.text = string.Empty;
                Say("Your " + _stake.ToString("N0") + " is on the table, first to " + target.ToString("N0") + ". LEAVE takes it back until somebody sits down.", Dim);
            }
            else if (finished)
            {
                var iWon = _mySeat.HasValue && winner == _mySeat.Value;
                var ending = (string)_view["Ending"];
                _banner.text = iWon ? "YOU WIN  +" + (_stake * 2).ToString("N0") + " R" : "YOU LOSE";
                _banner.color = iWon ? Good : Bad;
                Say(ending == "Forfeit" ? "The match ended by forfeit. LEAVE to go back." : "Match over. LEAVE to go back.", Dim);
                StopPolling();
                ProfileSync.Request(SyncAction);
            }
            else
            {
                _banner.text = string.Empty;

                if (MyTurn())
                {
                    Say(phase == "Choosing" ? "Pick the dice to set aside."
                        : (_view.Value<bool?>("CanBank") ?? false) ? "Roll on, or bank."
                        : "Roll.", Ink);
                }
                else
                {
                    Say("Their turn.", Dim);
                }
            }
        }

        private static void RenderDice()
        {
            for (var i = _diceRow.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_diceRow.GetChild(i).gameObject);
            }

            var roll = _view?["Roll"] as JArray;

            if (roll == null || roll.Count == 0)
            {
                var inHand = _view?.Value<int?>("DiceInHand") ?? 0;
                var phase = (string)_view?["Phase"];

                if (phase == "Rolling" && inHand > 0)
                {
                    var hint = NewText("Hint", _diceRow, inHand + (inHand == 1 ? " die" : " dice") + " in the cup", 20f);
                    hint.rectTransform.sizeDelta = new Vector2(400f, 40f);
                    hint.color = Dim;
                }

                return;
            }

            var keeps = _view["Keeps"] as JArray;
            var keepable = new HashSet<int>();

            if (keeps != null)
            {
                foreach (var keep in keeps)
                {
                    foreach (var index in (JArray)keep["Indices"])
                    {
                        keepable.Add(index.Value<int>());
                    }
                }
            }

            var span = roll.Count * DieSize + (roll.Count - 1) * DieGap;
            var left = -span * 0.5f + DieSize * 0.5f;

            for (var i = 0; i < roll.Count; i++)
            {
                var face = roll[i].Value<int>();
                var selected = MyTurn() ? _selected.Contains(i) : _replaySelected.Contains(i);
                var die = BuildDie(_diceRow, face, DieSize, selected ? Gold : keepable.Contains(i) && MyTurn() ? Edge : new Color(0.3f, 0.3f, 0.3f, 1f), selected ? 4 : 2);
                die.anchoredPosition = new Vector2(left + i * (DieSize + DieGap), 0f);

                var index = i;
                die.gameObject.AddComponent<Button>().onClick.AddListener(() => ToggleDie(index));
            }
        }

        private static void RenderAside()
        {
            for (var i = _asideRow.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_asideRow.GetChild(i).gameObject);
            }

            var aside = _view?["SetAside"] as JArray;

            if (aside == null || aside.Count == 0)
            {
                return;
            }

            var label = NewText("Label", _asideRow, "set aside", 15f);
            label.rectTransform.sizeDelta = new Vector2(100f, 30f);
            label.rectTransform.anchoredPosition = new Vector2(-(aside.Count * (SmallDie + 8f)) * 0.5f - 60f, 0f);
            label.color = Dim;

            var left = -(aside.Count * (SmallDie + 8f)) * 0.5f + SmallDie * 0.5f;

            for (var i = 0; i < aside.Count; i++)
            {
                var die = BuildDie(_asideRow, aside[i].Value<int>(), SmallDie, Edge, 1);
                die.anchoredPosition = new Vector2(left + i * (SmallDie + 8f), 0f);
            }
        }

        private static void RenderButtons()
        {
            var phase = (string)_view?["Phase"];
            var mine = MyTurn() && phase != "Finished" && phase != "WaitingForOpponent";

            _rollButton.gameObject.SetActive(mine && phase == "Rolling");
            _bankButton.gameObject.SetActive(mine && phase == "Rolling");
            _keepButton.gameObject.SetActive(mine && phase == "Choosing");

            if (mine && phase == "Rolling")
            {
                // Anything set aside may be banked. Before the first keep of a turn there is
                // nothing to bank, so the button waits, dimmed, rather than disappearing.
                var canBank = _view.Value<bool?>("CanBank") ?? false;
                var turnScore = _view.Value<int?>("TurnScore") ?? 0;

                _bankLabel.text = canBank ? "BANK  " + turnScore.ToString("N0") : "BANK";
                _bankLabel.color = canBank ? Ink : Dim;
                _bankButton.GetComponent<Image>().color = canBank ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            }

            if (phase == "Choosing")
            {
                var points = SelectedPoints();
                _keepLabel.text = points.HasValue ? "SET ASIDE  " + points.Value.ToString("N0") : _selected.Count == 0 ? "SET ASIDE" : "NOT A KEEP";
                _keepLabel.color = points.HasValue || _selected.Count == 0 ? Ink : Bad;
            }
        }

        /// <summary>What the selection is worth if it is one of the server's legal keeps, else null.</summary>
        private static int? SelectedPoints()
        {
            var keeps = _view?["Keeps"] as JArray;

            if (keeps == null || _selected.Count == 0)
            {
                return null;
            }

            var wanted = _selected.OrderBy(i => i).ToList();

            foreach (var keep in keeps)
            {
                var indices = ((JArray)keep["Indices"]).Select(t => t.Value<int>()).OrderBy(i => i).ToList();

                if (indices.SequenceEqual(wanted))
                {
                    return keep.Value<int?>("Points");
                }
            }

            return null;
        }

        private static void RenderBalance(int? balance)
        {
            if (balance.HasValue)
            {
                _balance.text = balance.Value.ToString("N0") + " R in the stash";
            }
        }

        private static void RenderSheet()
        {
            var lines = _ping?["Scoring"] as JArray;
            _sheet.text = lines == null
                ? string.Empty
                : string.Join("\n", lines.Select(row => string.Format("{0,-18}{1,10}", row.Value<string>("Combination"), row.Value<string>("Points"))));

            var chances = _ping?["FarkleChance"] as JArray;
            _odds.text = chances == null
                ? string.Empty
                : "Chance a roll scores nothing:  " + string.Join("   ", chances.Select((c, i) => (i + 1) + (i == 0 ? " die " : " dice ") + c.Value<double>().ToString("0.0") + "%"));
        }

        // ------------------------------------------------------------------ replaying

        /// <summary>
        /// Takes in a view and shows whatever the other seat has done since the last one,
        /// one event at a time, at a pace a person can follow.
        ///
        /// Both kinds of opponent come through here. A bot's whole turn arrives at once in
        /// `LastTurn`, because the server plays it inside the request that ended ours. A
        /// human's turn arrives a push at a time in `Turn`. Either way the events not yet
        /// shown are queued, and the queue is drawn out with pauses: the roll lands in the
        /// dice row exactly as ours does, the dice being taken light up gold for a second,
        /// then they move to the set-aside row. Our own moves are never replayed; we made
        /// them.
        /// </summary>
        private static void Absorb(JObject view)
        {
            _latest = view;

            var turnNumber = view.Value<int?>("TurnNumber") ?? 0;
            var lastTurnNumber = view.Value<int?>("LastTurnNumber") ?? 0;
            var turn = view["Turn"] as JArray ?? new JArray();
            var lastTurn = view["LastTurn"] as JArray ?? new JArray();

            if (_shownTurn < 0)
            {
                // First view of this table. Nothing to catch up on.
                _shownTurn = turnNumber;
                _shownCount = turn.Count;
            }
            else if (lastTurnNumber >= _shownTurn)
            {
                // The last completed turn is the one we were watching, or a later one. The
                // usual "later" case is a bot: our bank ends our turn and the bot's whole turn
                // is played inside the same reply, so the reply arrives two turns on from
                // what we last showed. Its LastTurn is the bot's turn, whole, and every event
                // of it is new to us. Our own turn's events are skipped by seat either way.
                EnqueueFrom(lastTurn, lastTurnNumber == _shownTurn ? _shownCount : 0);

                if (lastTurnNumber == turnNumber)
                {
                    // Finished: the last turn IS the current turn, and it ended the match.
                    _shownTurn = turnNumber;
                    _shownCount = lastTurn.Count;
                }
                else
                {
                    _shownTurn = turnNumber;
                    EnqueueFrom(turn, 0);
                    _shownCount = turn.Count;
                }
            }
            else if (turnNumber == _shownTurn)
            {
                EnqueueFrom(turn, _shownCount);
                _shownCount = turn.Count;
            }
            else
            {
                // A view older than what is on screen. Nothing to show from it.
                return;
            }

            if (_pending.Count > 0)
            {
                if (_animation == null && FarkleClientPlugin.Instance != null)
                {
                    _animation = FarkleClientPlugin.Instance.StartCoroutine(Animate());
                }

                return;
            }

            if (_animation == null)
            {
                _view = view;
                Draw();
            }
        }

        /// <summary>Queues the other seat's events from an index on. Ours are skipped.</summary>
        private static void EnqueueFrom(JArray events, int from)
        {
            for (var i = from; i < events.Count; i++)
            {
                var e = events[i] as JObject;

                if (e == null)
                {
                    continue;
                }

                if (_mySeat.HasValue && e.Value<int?>("Seat") == _mySeat.Value)
                {
                    continue;
                }

                _pending.Enqueue(e);
            }
        }

        private static IEnumerator Animate()
        {
            while (_pending.Count > 0)
            {
                var e = _pending.Dequeue();
                var kind = e.Value<string>("Kind");
                var dice = e["Dice"] as JArray ?? new JArray();
                var seat = e.Value<int?>("Seat") ?? 0;

                if (_stage == null)
                {
                    // Their turn as it stood before this event: our latest view, with the
                    // table cleared. Scores are left as the latest view has them; a bank
                    // shows its new total when the bank is shown.
                    _stage = (JObject)(_latest ?? _view).DeepClone();
                    _stage["Phase"] = "Rolling";
                    _stage["CurrentSeat"] = seat;
                    _stage["Keeps"] = new JArray();
                    _stage["Roll"] = new JArray();
                    _stage["SetAside"] = new JArray();
                    _stage["TurnScore"] = 0;
                    _stage["Turn"] = new JArray();
                    _stage["Winner"] = null;
                    _stage["Ending"] = "None";
                    _stage["DiceInHand"] = 6;

                    // The latest view may already hold the bank this turn ends with -- it
                    // does whenever the turn arrived whole. Take that bank back off the
                    // stage's score, so showing the bank later adds it once, not twice.
                    var seats = _stage["Seats"] as JArray;

                    if (seats != null && seat < seats.Count)
                    {
                        var alreadyBanked = 0;

                        foreach (var queued in new[] { e }.Concat(_pending))
                        {
                            if (queued.Value<string>("Kind") == "Banked" && queued.Value<int?>("Seat") == seat)
                            {
                                alreadyBanked += queued.Value<int?>("Points") ?? 0;
                            }
                        }

                        seats[seat]["Score"] = Math.Max(0, (seats[seat].Value<int?>("Score") ?? 0) - alreadyBanked);
                    }
                }

                _view = _stage;

                switch (kind)
                {
                    case "Rolled":
                    case "Farkled":
                        _stage["Roll"] = new JArray(dice);
                        _stage["Phase"] = "Choosing";
                        _replaySelected.Clear();
                        ((JArray)_stage["Turn"]).Add(e);
                        Draw();
                        _turnNote.text = e.Value<string>("Note") ?? string.Empty;
                        _turnNote.color = kind == "Farkled" ? Bad : Ink;

                        yield return new WaitForSecondsRealtime(kind == "Farkled" ? 1.8f : 1.2f);
                        break;

                    case "Kept":
                    {
                        // First the choice: the dice being taken light up on the roll, the
                        // way ours do when we pick them. Then the result.
                        var indices = e["Indices"] as JArray;
                        _replaySelected.Clear();

                        if (indices != null)
                        {
                            foreach (var index in indices)
                            {
                                _replaySelected.Add(index.Value<int>());
                            }
                        }

                        RenderDice();
                        _turnNote.text = e.Value<string>("Note") ?? string.Empty;
                        _turnNote.color = Ink;

                        yield return new WaitForSecondsRealtime(1.1f);

                        var aside = _stage["SetAside"] as JArray ?? new JArray();

                        foreach (var d in dice)
                        {
                            aside.Add(d.Value<int>());
                        }

                        _stage["SetAside"] = aside;
                        _stage["TurnScore"] = (_stage.Value<int?>("TurnScore") ?? 0) + (e.Value<int?>("Points") ?? 0);
                        _stage["DiceInHand"] = Math.Max(0, (_stage.Value<int?>("DiceInHand") ?? 6) - dice.Count);
                        _stage["Roll"] = new JArray();
                        _stage["Phase"] = "Rolling";
                        _replaySelected.Clear();
                        ((JArray)_stage["Turn"]).Add(e);
                        Draw();

                        yield return new WaitForSecondsRealtime(0.6f);
                        break;
                    }

                    case "HotDice":
                        _stage["DiceInHand"] = 6;
                        ((JArray)_stage["Turn"]).Add(e);
                        Draw();
                        _turnNote.text = e.Value<string>("Note") ?? string.Empty;
                        _turnNote.color = Gold;

                        yield return new WaitForSecondsRealtime(1.0f);
                        break;

                    case "Banked":
                    case "Yielded":
                    {
                        var seats = _stage["Seats"] as JArray;

                        if (seats != null && seat < seats.Count && kind == "Banked")
                        {
                            seats[seat]["Score"] = (seats[seat].Value<int?>("Score") ?? 0) + (e.Value<int?>("Points") ?? 0);
                        }

                        _stage["Roll"] = new JArray();
                        ((JArray)_stage["Turn"]).Add(e);
                        Draw();
                        _turnNote.text = e.Value<string>("Note") ?? string.Empty;
                        _turnNote.color = Good;

                        yield return new WaitForSecondsRealtime(1.4f);
                        break;
                    }

                    default:
                        ((JArray)_stage["Turn"]).Add(e);
                        Draw();
                        _turnNote.text = e.Value<string>("Note") ?? string.Empty;

                        yield return new WaitForSecondsRealtime(1.0f);
                        break;
                }

                // A bank, a farkle, a yield, a forfeit or a win ends their turn: the next
                // event, if any, starts on a clean stage.
                if (kind == "Banked" || kind == "Farkled" || kind == "Yielded" || kind == "Forfeited" || kind == "Won")
                {
                    _stage = null;
                }
            }

            _animation = null;
            _replaySelected.Clear();
            _turnNote.color = Ink;

            if (_latest != null)
            {
                _view = _latest;
                Draw();
            }
        }

        private static void StopAnimation()
        {
            if (_animation != null && FarkleClientPlugin.Instance != null)
            {
                FarkleClientPlugin.Instance.StopCoroutine(_animation);
            }

            _animation = null;
            _pending.Clear();
            _stage = null;
            _replaySelected.Clear();
        }

        // ------------------------------------------------------------------ hearing

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

        /// <summary>Somebody else moved. Raised on Unity's main thread by the casino's pump.</summary>
        private static void OnPushed(string payload)
        {
            if (_root == null || !_root.activeSelf || _tableId == null)
            {
                return;
            }

            JObject message;

            try
            {
                message = JObject.Parse(payload);
            }
            catch (Exception)
            {
                // One socket carries the whole casino; something that is not a Farkle
                // table is an ordinary thing to receive.
                return;
            }

            if (!string.Equals((string)message["Table"], _tableId, StringComparison.Ordinal))
            {
                return;
            }

            var view = message["View"] as JObject;

            if (view == null)
            {
                return;
            }

            var kind = (string)message["Kind"];

            if (kind == "left" && (string)view["Phase"] != "Finished")
            {
                Say("The other seat stood up.", Dim);
            }

            // Wrapped so Render sees the shape a route returns. The push carries no
            // balance; the one on screen is kept.
            Render(new JObject { ["Match"] = view, ["TableId"] = _tableId }, replay: true);
        }

        /// <summary>
        /// A fallback for the socket, and the clock the table runs on: the server notices
        /// an absent seat only when somebody asks. Every few seconds while seated.
        /// </summary>
        private static void StartPolling()
        {
            StopPolling();

            if (FarkleClientPlugin.Instance != null)
            {
                _poll = FarkleClientPlugin.Instance.StartCoroutine(Poll());
            }
        }

        private static void StopPolling()
        {
            if (_poll != null && FarkleClientPlugin.Instance != null)
            {
                FarkleClientPlugin.Instance.StopCoroutine(_poll);
            }

            _poll = null;
        }

        private static IEnumerator Poll()
        {
            while (IsOpen && _tableId != null)
            {
                yield return new WaitForSecondsRealtime(4f);

                if (!IsOpen || _tableId == null || _animation != null)
                {
                    continue;
                }

                var state = FarkleApi.State();

                if (state == null)
                {
                    continue;
                }

                if (state.Value<bool?>("Ok") == false && state["Match"] == null)
                {
                    // Not at a table any more: it closed under us.
                    Note(state);
                    ShowLobby(FarkleApi.Tables());
                    yield break;
                }

                Render(state, replay: true);
            }
        }

        // ------------------------------------------------------------------ drawing

        private static void Build()
        {
            _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();

            var canvasObject = new GameObject(RootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(canvasObject);
            _root = canvasObject;

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;

            _group = canvasObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            var backdrop = NewBox("Backdrop", canvasObject.transform, new Color(0f, 0f, 0f, 0.93f));
            backdrop.anchorMin = Vector2.zero;
            backdrop.anchorMax = Vector2.one;
            backdrop.offsetMin = Vector2.zero;
            backdrop.offsetMax = Vector2.zero;

            _frame = NewBox("Frame", canvasObject.transform, Color.white);
            _frame.sizeDelta = new Vector2(FrameWidth, FrameHeight);
            _frame.GetComponent<Image>().sprite = Textures.RoundedBox(12, Panel, Edge, 2);
            _frame.GetComponent<Image>().type = Image.Type.Sliced;

            var title = NewText("Title", _frame, "FARKLE", 40f);
            title.rectTransform.sizeDelta = new Vector2(400f, 50f);
            title.rectTransform.anchoredPosition = new Vector2(0f, FrameHeight * 0.5f - 44f);
            title.color = Gold;

            _balance = NewText("Balance", _frame, string.Empty, 17f);
            _balance.rectTransform.sizeDelta = new Vector2(360f, 30f);
            _balance.rectTransform.anchoredPosition = new Vector2(-FrameWidth * 0.5f + 200f, FrameHeight * 0.5f - 44f);
            _balance.alignment = TextAlignmentOptions.Left;
            _balance.color = Dim;

            var leave = BuildButton(_frame, "LEAVE", new Vector2(FrameWidth * 0.5f - 110f, FrameHeight * 0.5f - 44f), 160f, Leave);
            _leaveLabel = leave.GetComponentInChildren<TextMeshProUGUI>();

            _status = NewText("Status", _frame, string.Empty, 18f);
            _status.rectTransform.sizeDelta = new Vector2(FrameWidth - 80f, 44f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, -FrameHeight * 0.5f + 40f);
            _status.enableWordWrapping = true;
            _status.color = Dim;

            BuildLobby();
            BuildMatch();
        }

        private static void BuildLobby()
        {
            _lobby = new GameObject("Lobby", typeof(RectTransform));
            _lobby.transform.SetParent(_frame, false);
            var rect = (RectTransform)_lobby.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // Left: who is waiting.
            var listTitle = NewText("ListTitle", rect, "OPEN TABLES", 20f);
            listTitle.rectTransform.sizeDelta = new Vector2(560f, 30f);
            listTitle.rectTransform.anchoredPosition = new Vector2(-300f, 250f);
            listTitle.color = Gold;

            _tableList = NewBox("List", rect, new Color(0f, 0f, 0f, 0.001f));
            _tableList.sizeDelta = new Vector2(580f, 440f);
            _tableList.anchoredPosition = new Vector2(-300f, 10f);
            _tableList.pivot = new Vector2(0.5f, 1f);
            _tableList.anchoredPosition = new Vector2(-300f, 230f);
            _tableList.GetComponent<Image>().raycastTarget = false;

            // Right: sit down.
            var sitTitle = NewText("SitTitle", rect, "SIT DOWN", 20f);
            sitTitle.rectTransform.sizeDelta = new Vector2(480f, 30f);
            sitTitle.rectTransform.anchoredPosition = new Vector2(300f, 250f);
            sitTitle.color = Gold;

            var stakeTitle = NewText("StakeTitle", rect, "Stake a seat", 17f);
            stakeTitle.rectTransform.sizeDelta = new Vector2(200f, 30f);
            stakeTitle.rectTransform.anchoredPosition = new Vector2(300f, 205f);
            stakeTitle.color = Dim;

            BuildButton(rect, "-", new Vector2(150f, 170f), 56f, () => StepStake(-10_000));
            BuildButton(rect, "-100k", new Vector2(215f, 170f), 70f, () => StepStake(-100_000));
            _stakeLabel = NewText("Stake", rect, string.Empty, 24f);
            _stakeLabel.rectTransform.sizeDelta = new Vector2(160f, 40f);
            _stakeLabel.rectTransform.anchoredPosition = new Vector2(300f, 170f);
            _stakeLabel.color = Gold;
            BuildButton(rect, "+100k", new Vector2(385f, 170f), 70f, () => StepStake(100_000));
            BuildButton(rect, "+", new Vector2(450f, 170f), 56f, () => StepStake(10_000));

            // How long a race: 1,000 to 10,000, chosen here and fixed for the table.
            _targetLabel = NewText("Target", rect, string.Empty, 19f);
            _targetLabel.rectTransform.sizeDelta = new Vector2(220f, 30f);
            _targetLabel.rectTransform.anchoredPosition = new Vector2(265f, 122f);
            _targetLabel.color = Gold;
            BuildButton(rect, ">", new Vector2(405f, 122f), 44f, () =>
            {
                _targetIndex++;
                _targetLabel.text = "First to " + TargetValue().ToString("N0");
            });

            BuildButton(rect, "OPEN A TABLE FOR A FRIEND", new Vector2(300f, 70f), 360f, () => OpenTable(false));

            _botLabel = NewText("Bot", rect, string.Empty, 17f);
            _botLabel.rectTransform.sizeDelta = new Vector2(200f, 30f);
            _botLabel.rectTransform.anchoredPosition = new Vector2(260f, 22f);
            _botLabel.color = Dim;
            BuildButton(rect, ">", new Vector2(390f, 22f), 44f, () =>
            {
                _botIndex++;
                _botLabel.text = "vs " + BotName();
            });

            BuildButton(rect, "PLAY THE HOUSE", new Vector2(300f, -24f), 360f, () => OpenTable(true));

            _sheet = NewText("Sheet", rect, string.Empty, 16f);
            _sheet.rectTransform.sizeDelta = new Vector2(360f, 200f);
            _sheet.rectTransform.anchoredPosition = new Vector2(300f, -165f);
            _sheet.alignment = TextAlignmentOptions.Top;
            _sheet.color = Dim;

            _odds = NewText("Odds", rect, string.Empty, 15f);
            _odds.rectTransform.sizeDelta = new Vector2(FrameWidth - 120f, 26f);
            _odds.rectTransform.anchoredPosition = new Vector2(0f, -FrameHeight * 0.5f + 80f);
            _odds.color = Dim;
        }

        private static void BuildMatch()
        {
            _match = new GameObject("Match", typeof(RectTransform));
            _match.transform.SetParent(_frame, false);
            var rect = (RectTransform)_match.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _match.SetActive(false);

            for (var i = 0; i < 2; i++)
            {
                var card = NewBox("Seat" + i, rect, Color.white);
                card.sizeDelta = new Vector2(330f, 120f);
                card.anchoredPosition = new Vector2(i == 0 ? -400f : 400f, 200f);
                _seatCards[i] = card;

                _seatNames[i] = NewText("Name", card, string.Empty, 22f);
                _seatNames[i].rectTransform.sizeDelta = new Vector2(310f, 34f);
                _seatNames[i].rectTransform.anchoredPosition = new Vector2(0f, 34f);

                _seatScores[i] = NewText("Score", card, "0", 40f);
                _seatScores[i].rectTransform.sizeDelta = new Vector2(310f, 50f);
                _seatScores[i].rectTransform.anchoredPosition = new Vector2(0f, -6f);
                _seatScores[i].color = Gold;

                _seatNotes[i] = NewText("Note", card, string.Empty, 14f);
                _seatNotes[i].rectTransform.sizeDelta = new Vector2(310f, 24f);
                _seatNotes[i].rectTransform.anchoredPosition = new Vector2(0f, -44f);
                _seatNotes[i].color = Dim;
            }

            var felt = NewBox("Felt", rect, Color.white);
            // Below the seat cards, which end at 140: the top edge here is 120. The first
            // build put this at 60 with a height of 300 and covered both name tags.
            felt.sizeDelta = new Vector2(760f, 270f);
            felt.anchoredPosition = new Vector2(0f, -15f);
            felt.GetComponent<Image>().sprite = Textures.RoundedBox(16, Felt, Edge, 2);
            felt.GetComponent<Image>().type = Image.Type.Sliced;
            felt.GetComponent<Image>().raycastTarget = false;

            _banner = NewText("Banner", felt, string.Empty, 44f);
            _banner.rectTransform.sizeDelta = new Vector2(700f, 60f);
            _banner.rectTransform.anchoredPosition = new Vector2(0f, 85f);

            _diceRow = NewBox("Dice", felt, new Color(0f, 0f, 0f, 0.001f));
            _diceRow.sizeDelta = new Vector2(740f, DieSize + 10f);
            _diceRow.anchoredPosition = new Vector2(0f, 15f);
            _diceRow.GetComponent<Image>().raycastTarget = false;

            _asideRow = NewBox("Aside", felt, new Color(0f, 0f, 0f, 0.001f));
            _asideRow.sizeDelta = new Vector2(740f, SmallDie + 10f);
            _asideRow.anchoredPosition = new Vector2(0f, -62f);
            _asideRow.GetComponent<Image>().raycastTarget = false;

            _turnLabel = NewText("Turn", felt, string.Empty, 20f);
            _turnLabel.rectTransform.sizeDelta = new Vector2(700f, 30f);
            _turnLabel.rectTransform.anchoredPosition = new Vector2(0f, -108f);
            _turnLabel.color = Gold;

            _turnNote = NewText("TurnNote", rect, string.Empty, 18f);
            _turnNote.rectTransform.sizeDelta = new Vector2(900f, 30f);
            _turnNote.rectTransform.anchoredPosition = new Vector2(0f, -180f);

            _rollButton = BuildButton(rect, "ROLL", new Vector2(-220f, -245f), 200f, Roll);
            _keepButton = BuildButton(rect, "SET ASIDE", new Vector2(0f, -245f), 240f, KeepSelected);
            _keepLabel = _keepButton.GetComponentInChildren<TextMeshProUGUI>();
            _bankButton = BuildButton(rect, "BANK", new Vector2(220f, -245f), 220f, Bank);
            _bankLabel = _bankButton.GetComponentInChildren<TextMeshProUGUI>();
        }

        /// <summary>
        /// A die: a rounded bone face with the pips of one face. Drawn, not loaded, from
        /// the same rounded box every table uses; a pip is that box with its corners
        /// rounded all the way to a circle.
        /// </summary>
        private static RectTransform BuildDie(Transform parent, int face, float size, Color border, int borderWidth)
        {
            var die = NewBox("Die" + face, parent, Color.white);
            die.sizeDelta = new Vector2(size, size);

            var image = die.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(Mathf.Max(4, (int)(size * 0.18f)), Bone, border, borderWidth);
            image.type = Image.Type.Sliced;

            var pip = size * 0.16f;
            var a = -size * 0.26f;
            var c = size * 0.26f;

            var spots = face switch
            {
                1 => new[] { new Vector2(0f, 0f) },
                2 => new[] { new Vector2(a, c), new Vector2(c, a) },
                3 => new[] { new Vector2(a, c), new Vector2(0f, 0f), new Vector2(c, a) },
                4 => new[] { new Vector2(a, c), new Vector2(c, c), new Vector2(a, a), new Vector2(c, a) },
                5 => new[] { new Vector2(a, c), new Vector2(c, c), new Vector2(0f, 0f), new Vector2(a, a), new Vector2(c, a) },
                _ => new[] { new Vector2(a, c), new Vector2(c, c), new Vector2(a, 0f), new Vector2(c, 0f), new Vector2(a, a), new Vector2(c, a) },
            };

            foreach (var spot in spots)
            {
                var dot = NewBox("Pip", die, Color.white);
                dot.sizeDelta = new Vector2(pip, pip);
                dot.anchoredPosition = spot;
                dot.GetComponent<Image>().sprite = Textures.RoundedBox(8, Pip, Pip, 0);
                dot.GetComponent<Image>().type = Image.Type.Sliced;
                dot.GetComponent<Image>().raycastTarget = false;
            }

            return die;
        }

        private static RectTransform BuildButton(Transform parent, string label, Vector2 at, float width, Action onClick)
        {
            var box = NewBox("Button_" + label, parent, Color.white);
            box.sizeDelta = new Vector2(width, 44f);
            box.anchoredPosition = at;

            var image = box.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(6, new Color(0.16f, 0.16f, 0.17f, 1f), Edge, 2);
            image.type = Image.Type.Sliced;

            var text = NewText("Label", box, label, 18f);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            text.color = Ink;

            box.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());

            return box;
        }

        private static RectTransform NewBox(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            go.GetComponent<Image>().color = colour;

            return rect;
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Ink;
            label.raycastTarget = false;
            label.enableWordWrapping = false;

            if (_font != null)
            {
                label.font = _font;
            }

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            return label;
        }

        private static void Say(string message, Color colour)
        {
            _status.text = message ?? string.Empty;
            _status.color = colour;
        }

        /// <summary>Something the server had to say regardless of what was asked -- money handed back, mostly.</summary>
        private static void Note(JObject reply)
        {
            var note = reply?.Value<string>("Note");

            if (!string.IsNullOrEmpty(note))
            {
                Say(note, Gold);
                ProfileSync.Request(SyncAction);
            }
        }

        // ------------------------------------------------------------------ fading

        private static void FadeTo(float target, Action done)
        {
            var host = FarkleClientPlugin.Instance;

            if (host == null || _group == null)
            {
                if (_group != null)
                {
                    _group.alpha = target;
                }

                done?.Invoke();
                return;
            }

            if (_fade != null)
            {
                host.StopCoroutine(_fade);
            }

            _fade = host.StartCoroutine(Fade(target, done));
        }

        private static IEnumerator Fade(float target, Action done)
        {
            const float seconds = 0.13f;
            var from = _group.alpha;

            for (var t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                _group.alpha = Mathf.Lerp(from, target, t / seconds);
                yield return null;
            }

            _group.alpha = target;
            _fade = null;
            done?.Invoke();
        }
    }
}
