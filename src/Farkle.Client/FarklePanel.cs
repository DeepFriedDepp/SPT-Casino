using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Textures = Casino.Shared.Textures;

namespace Farkle.Client
{
    /// <summary>
    /// The fifth table, before it is a table.
    ///
    /// What this draws today is the scoring sheet and the odds, both fetched from the
    /// server so the panel prints the engine's own numbers, and a line saying plainly
    /// that the game is not built yet. It is here so the lobby has a fifth tile, the
    /// escape key has something to close, and the route from the game to the server
    /// half is proven end to end before any of the play is written -- the same order
    /// every other table went in.
    ///
    /// Same shape as the other four: a static class with IsOpen, Open and Close, a
    /// canvas at sorting order 30000 so it covers the lobby, and nothing in it that
    /// knows about the task bar, the lobby or the escape key. That is what lets
    /// `Casino.Client` compile it in without owning it.
    /// </summary>
    internal static class FarklePanel
    {
        private const string RootName = "FarkleCanvas";

        private static readonly Color Gold = new Color(0.85f, 0.72f, 0.38f, 1f);
        private static readonly Color Ink = new Color(0.93f, 0.91f, 0.86f, 1f);
        private static readonly Color Dim = new Color(0.65f, 0.63f, 0.58f, 1f);
        private static readonly Color Panel = new Color(0.10f, 0.11f, 0.12f, 0.96f);
        private static readonly Color Edge = new Color(0.45f, 0.38f, 0.22f, 1f);

        private static GameObject _root;
        private static CanvasGroup _group;
        private static TMP_FontAsset _font;
        private static Coroutine _fade;
        private static bool _closing;

        private static TextMeshProUGUI _status;
        private static TextMeshProUGUI _sheet;
        private static TextMeshProUGUI _odds;

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

                Render(FarkleApi.Ping());
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

            _closing = true;

            FadeTo(0f, () =>
            {
                _root.SetActive(false);
                _closing = false;
            });
        }

        // ------------------------------------------------------------------ content

        /// <summary>
        /// Prints what the server said. A null ping is the server not answering, which
        /// the panel says rather than hides -- a table that looks fine while its routes
        /// 404 is the install failure this repo has been bitten by before.
        /// </summary>
        private static void Render(JObject ping)
        {
            if (ping == null)
            {
                _status.text = "The server is not answering. Is Farkle.Server.dll in the casino's mod folder?";
                _status.color = new Color(0.90f, 0.45f, 0.35f, 1f);
                _sheet.text = string.Empty;
                _odds.text = string.Empty;
                return;
            }

            var version = ping.Value<string>("ModVersion") ?? "?";
            var hasProfile = ping.Value<bool?>("HasProfile") ?? false;

            _status.text = hasProfile
                ? $"Server v{version} answered. The scoring is settled; the game is not built yet."
                : $"Server v{version} answered but found no profile for this session.";
            _status.color = Dim;

            var lines = ping["Scoring"] as JArray;
            _sheet.text = lines == null
                ? string.Empty
                : string.Join(
                    "\n",
                    lines.Select(row =>
                        $"{row.Value<string>("Combination"),-18}{row.Value<string>("Points"),12}"));

            var chances = ping["FarkleChance"] as JArray;
            _odds.text = chances == null
                ? string.Empty
                : "Chance a roll scores nothing:  "
                  + string.Join("   ", chances.Select((c, i) => $"{i + 1} {(i == 0 ? "die" : "dice")} {c.Value<double>():0.0}%"));
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

            // Above the lobby, like every table. See CLAUDE.md, "The layers".
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

            var frame = NewBox("Frame", canvasObject.transform, Color.white);
            frame.sizeDelta = new Vector2(820f, 640f);
            frame.anchoredPosition = Vector2.zero;

            var face = frame.GetComponent<Image>();
            face.sprite = Textures.RoundedBox(12, Panel, Edge, 2);
            face.type = Image.Type.Sliced;

            var title = NewText("Title", frame, "FARKLE", 44f);
            title.rectTransform.sizeDelta = new Vector2(700f, 60f);
            title.rectTransform.anchoredPosition = new Vector2(0f, 260f);
            title.color = Gold;

            var sub = NewText("Sub", frame, "Six dice. Set aside what scores, roll the rest, or bank. Roll nothing and the turn is gone.", 19f);
            sub.rectTransform.sizeDelta = new Vector2(760f, 30f);
            sub.rectTransform.anchoredPosition = new Vector2(0f, 212f);
            sub.color = Dim;

            _sheet = NewText("Sheet", frame, string.Empty, 22f);
            _sheet.rectTransform.sizeDelta = new Vector2(520f, 340f);
            _sheet.rectTransform.anchoredPosition = new Vector2(0f, 20f);
            _sheet.alignment = TextAlignmentOptions.Top;
            _sheet.enableWordWrapping = false;

            // Monospaced digits so the two columns line up without a table component.
            _sheet.text = string.Empty;
            _sheet.fontStyle = FontStyles.Normal;
            _sheet.characterSpacing = 0f;
            _sheet.enableAutoSizing = false;
            _sheet.richText = true;

            _odds = NewText("Odds", frame, string.Empty, 17f);
            _odds.rectTransform.sizeDelta = new Vector2(780f, 26f);
            _odds.rectTransform.anchoredPosition = new Vector2(0f, -190f);
            _odds.color = Dim;

            _status = NewText("Status", frame, string.Empty, 18f);
            _status.rectTransform.sizeDelta = new Vector2(780f, 48f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, -232f);
            _status.enableWordWrapping = true;
            _status.color = Dim;

            BuildButton(frame, "CLOSE", new Vector2(0f, -288f), Close);
        }

        private static void BuildButton(Transform parent, string label, Vector2 at, Action onClick)
        {
            var box = NewBox("Button_" + label, parent, Color.white);
            box.sizeDelta = new Vector2(180f, 44f);
            box.anchoredPosition = at;

            var image = box.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(6, new Color(0.16f, 0.16f, 0.17f, 1f), Edge, 2);
            image.type = Image.Type.Sliced;

            var text = NewText("Label", box, label, 20f);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            text.color = Ink;

            box.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());
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
