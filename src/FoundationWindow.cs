using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AutoFoundations
{
    // The in-game window (Unity IMGUI), opened with the backquote key (`) by default or shown as a page in Mod Menu, and a
    // short notice when foundations are bought.
    internal sealed class FoundationWindow
    {
        private const int WindowId = 0x41465731;
        private const float Width = 620f;
        private const float Height = 560f;
        private const float NoticeSeconds = 8f;
        private const string ModMenuGuid = "el2.modmenu";

        // Read by InputBlockPatch, on the main thread.
        internal static bool MouseOver;

        private readonly FoundationBuyer buyer;
        // Clicks only queue their effect; it runs in the next Update. Changing what the window draws in the middle of
        // an IMGUI pass makes the layout pass and the event pass disagree, which Unity reports as errors.
        private readonly List<Action> deferred = new List<Action>();
        private Rect rect = new Rect(0f, 0f, Width, Height);
        private bool visible;
        private bool positioned;
        private Vector2 scroll;
        private string keyName;
        private Key key = Key.Backquote;
        // Time.unscaledTime when Mod Menu last drew this mod's page.
        private float embeddedAt = -10f;

        private bool stylesReady;
        private Texture2D windowBackground;
        private Texture2D rowBackground;
        private float backgroundOpacity = -1f;
        private GUIStyle windowStyle;
        private GUIStyle textStyle;
        private GUIStyle mutedStyle;
        private GUIStyle goodStyle;
        private GUIStyle warnStyle;
        private GUIStyle badStyle;
        private GUIStyle headingStyle;
        private GUIStyle boxStyle;
        private GUIStyle noticeStyle;

        public FoundationWindow(FoundationBuyer buyer)
        {
            this.buyer = buyer;
        }

        private static bool AutoScale => Plugin.UiScale.Value < 0.25f;

        // Laid out for 1080p: automatic size grows with the screen height, in quarter steps.
        private static float Scale => AutoScale ? Mathf.Max(1f, Mathf.Round(Screen.height / 1080f * 4f) / 4f) : Mathf.Clamp(Plugin.UiScale.Value, 0.5f, 4f);

        public void Update()
        {
            if (deferred.Count > 0)
            {
                Action[] actions = deferred.ToArray();
                deferred.Clear();
                foreach (Action action in actions)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Window action failed: {e}");
                    }
                }
            }
            ReadKey();
            // With Mod Menu installed, its key opens the menu, where this mod has a page.
            bool menu = MenuInstalled;
            Keyboard keyboard = Keyboard.current;
            if (!menu && keyboard != null && key != Key.None && keyboard[key].wasPressedThisFrame && buyer.InGame)
            {
                visible = !visible;
            }
            visible = visible && !menu;
            MouseOver = visible && buyer.InGame && IsMouseInside();
            buyer.Watched = visible || Time.unscaledTime - embeddedAt < 0.5f;
        }

        // Mod Menu's page: the same content, without this mod's own window around it.
        public void DrawEmbedded()
        {
            embeddedAt = Time.unscaledTime;
            if (!buyer.InGame)
            {
                GUILayout.Label("Load a game to see where foundations can go.");
                return;
            }
            EnsureStyles();
            RefreshBackgrounds();
            DrawHeader(buyer.State);
            GUILayout.Space(4f);
            DrawContent(buyer.State, embedded: true);
        }

        // One line for Mod Menu's All mods list.
        public string MenuStatus
        {
            get
            {
                string mode = Plugin.AutoBuy.Value ? "On" : "Off";
                FoundationState state = buyer.State;
                if (state == null)
                {
                    return mode;
                }
                string bought = buyer.BoughtThisTurn > 0 ? $", {buyer.BoughtThisTurn} bought this turn for {buyer.SpentThisTurn:#,0} influence" : string.Empty;
                return $"{mode}: {state.Spots.Count} open {(state.Spots.Count == 1 ? "spot" : "spots")}{bought}";
            }
        }

        internal static bool MenuInstalled => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ModMenuGuid);

        public void OnGUI()
        {
            if (!buyer.InGame)
            {
                return;
            }
            EnsureStyles();
            RefreshBackgrounds();
            Matrix4x4 previous = GUI.matrix;
            float scale = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            if (!visible && Plugin.ShowNotice.Value && buyer.BoughtThisTurn > 0 && buyer.LastBoughtAt >= 0f && Time.unscaledTime - buyer.LastBoughtAt < NoticeSeconds)
            {
                var notice = new Rect((screenWidth - 460f) / 2f, 120f, 460f, 46f);
                string count = buyer.BoughtThisTurn == 1 ? "1 foundation" : $"{buyer.BoughtThisTurn} foundations";
                string details = MenuInstalled ? "Details in Mod Menu (Foundations)." : $"Press {KeyLabel} for details.";
                GUI.Box(notice, $"Auto Foundations: {count} bought this turn for {buyer.SpentThisTurn:#,0} influence.\n{details}", noticeStyle);
            }
            if (visible)
            {
                if (!positioned)
                {
                    rect.x = Mathf.Max(0f, (screenWidth - Width) / 2f);
                    rect.y = 90f;
                    positioned = true;
                }
                rect = GUI.Window(WindowId, rect, DrawWindow, $"Auto Foundations  ({KeyLabel} to close)", windowStyle);
                rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, screenWidth - rect.width));
                rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, screenHeight - 40f));
            }
            GUI.matrix = previous;
        }

        private string KeyLabel => key == Key.Backquote ? "`" : key.ToString();

        private void DrawWindow(int id)
        {
            FoundationState state = buyer.State;
            GUILayout.Space(2f);
            DrawHeader(state);
            GUILayout.Space(4f);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            DrawContent(state, embedded: false);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawHeader(FoundationState state)
        {
            GUILayout.BeginHorizontal();
            bool auto = Plugin.AutoBuy.Value;
            if (GUILayout.Button(auto ? "Buy automatically: ON" : "Buy automatically: OFF", GUILayout.Width(190f)))
            {
                Defer(() => Plugin.AutoBuy.Value = !auto);
            }
            GUILayout.Label(Status(state, out GUIStyle statusStyle), statusStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (Guard.AnyFailed)
            {
                GUILayout.Label("Part of the mod switched itself off after an error (game update?). See BepInEx/LogOutput.log.", badStyle);
            }
        }

        private void DrawContent(FoundationState state, bool embedded)
        {
            GUILayout.Label("Spends your influence on foundations wherever the game lets you build one, as soon as you can afford it. "
                + "Best first: most tile yield per influence, yields weighted by the city's job strategy. Districts and extractors "
                + "are built on top of foundations, so a foundation never blocks one.", mutedStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Influence {state.Influence:#,0} ({state.InfluenceNet:+#,0;-#,0;0}/turn)", textStyle, GUILayout.Width(200f));
            GUILayout.Label("Keep at least", textStyle, GUILayout.Width(85f));
            InfluenceStep("-1000", -1000);
            InfluenceStep("-100", -100);
            GUILayout.Label($"{Plugin.KeepInfluence.Value:#,0}", textStyle, GUILayout.Width(56f));
            InfluenceStep("+100", 100);
            InfluenceStep("+1000", 1000);
            GUILayout.EndHorizontal();

            List<FoundationSpot> spots = state.Spots;
            GUILayout.BeginHorizontal();
            if (spots.Count == 0)
            {
                GUILayout.Label("No spot for a foundation right now: new ones open up as your cities grow.", mutedStyle, GUILayout.ExpandWidth(true));
            }
            else
            {
                int free = spots.Count(s => s.Cost <= 0f);
                int cities = spots.Select(s => s.City).Distinct().Count();
                string freeText = free > 0 ? $", {free} of them free" : string.Empty;
                GUILayout.Label($"{spots.Count} spots in {cities} cities, {spots.Sum(s => s.Cost):#,0} influence for all{freeText}.", textStyle, GUILayout.ExpandWidth(true));
            }
            GUI.enabled = state.CanAct && state.IsHuman && spots.Count > 0 && !buyer.IsBuying;
            if (GUILayout.Button(new GUIContent("Buy now", "Buy every spot you can afford above the influence you keep, best first"), GUILayout.Width(90f)))
            {
                Defer(buyer.BuyNow);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (state.OtherCostSpots > 0)
            {
                GUILayout.Label($"{state.OtherCostSpots} more spots also cost dust or resources for your faction; the mod leaves those to you.", warnStyle);
            }
            if (buyer.BoughtThisTurn > 0)
            {
                GUILayout.Label($"This turn: {buyer.BoughtThisTurn} bought for {buyer.SpentThisTurn:#,0} influence.", goodStyle);
            }
            if (buyer.RefusedThisTurn > 0)
            {
                GUILayout.Label($"The game refused {buyer.RefusedThisTurn} spots this turn; trying them again next turn.", warnStyle);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Cities", headingStyle);
            foreach (FoundationCity city in state.Cities.OrderBy(c => buyer.CityName(c.Guid), StringComparer.CurrentCultureIgnoreCase))
            {
                List<FoundationSpot> citySpots = spots.Where(s => s.City == city.Guid).ToList();
                bool off = buyer.IsCityOff(city.Guid);
                GUILayout.BeginHorizontal(boxStyle);
                GUILayout.Label(buyer.CityName(city.Guid), textStyle, GUILayout.Width(190f));
                string spotText = citySpots.Count == 0 ? "no spot" : $"{citySpots.Count} {(citySpots.Count == 1 ? "spot" : "spots")}, {citySpots.Sum(s => s.Cost):#,0} influence";
                GUILayout.Label(spotText, citySpots.Count == 0 || off ? mutedStyle : textStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent(off ? "Off" : "Auto", off ? "The mod never buys foundations in this city" : "The mod buys foundations in this city"), GUILayout.Width(60f)))
                {
                    ulong guid = city.Guid;
                    Defer(() => buyer.SetCityOff(guid, !off));
                }
                GUILayout.EndHorizontal();
            }

            if (buyer.History.Count > 0)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Recently bought", headingStyle);
                foreach (string line in buyer.History)
                {
                    GUILayout.Label(line, mutedStyle);
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("Window", headingStyle);
            bool notice = GUILayout.Toggle(Plugin.ShowNotice.Value, " Show a notice when foundations are bought");
            if (notice != Plugin.ShowNotice.Value)
            {
                Defer(() => Plugin.ShowNotice.Value = notice);
            }
            if (embedded)
            {
                // Size and opacity are Mod Menu's then.
                GUILayout.Label($"Auto Foundations {Plugin.Version}", mutedStyle);
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Window size", textStyle, GUILayout.Width(300f));
            float size = Scale;
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.UiScale.Value = Mathf.Max(0.5f, size - 0.25f));
            }
            GUILayout.Label(AutoScale ? $"auto ({size:0.##}x)" : $"{size:0.##}x", textStyle, GUILayout.Width(100f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.UiScale.Value = Mathf.Min(4f, size + 0.25f));
            }
            GUI.enabled = !AutoScale;
            if (GUILayout.Button("Auto", GUILayout.Width(52f)))
            {
                Defer(() => Plugin.UiScale.Value = 0f);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Window opacity", textStyle, GUILayout.Width(300f));
            if (GUILayout.Button("-", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.Opacity.Value = Mathf.Max(0.3f, Mathf.Round((Plugin.Opacity.Value - 0.05f) * 20f) / 20f));
            }
            GUILayout.Label($"{Plugin.Opacity.Value * 100f:0}%", textStyle, GUILayout.Width(100f));
            if (GUILayout.Button("+", GUILayout.Width(26f)))
            {
                Defer(() => Plugin.Opacity.Value = Mathf.Min(1f, Mathf.Round((Plugin.Opacity.Value + 0.05f) * 20f) / 20f));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"Window key: {KeyLabel} (change ToggleKey in BepInEx/config/{Plugin.Guid}.cfg). Auto Foundations {Plugin.Version}", mutedStyle);
        }

        private string Status(FoundationState state, out GUIStyle style)
        {
            style = warnStyle;
            if (!state.IsHuman)
            {
                return "Your empire is not controlled by you right now.";
            }
            if (!Plugin.AutoBuy.Value)
            {
                return buyer.IsBuying ? "Buying..." : "Off: use Buy now, or switch it on.";
            }
            if (!state.CanAct)
            {
                return "Waiting for your turn.";
            }
            style = goodStyle;
            return buyer.IsBuying ? "Buying..." : "On: new spots are bought as they open up.";
        }

        private void InfluenceStep(string label, int step)
        {
            if (GUILayout.Button(label, GUILayout.Width(46f)))
            {
                Defer(() => Plugin.KeepInfluence.Value = Mathf.Clamp(Plugin.KeepInfluence.Value + step, 0, 10000000));
            }
        }

        private void Defer(Action action) => deferred.Add(action);

        private bool IsMouseInside()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }
            Vector2 position = mouse.position.ReadValue();
            float scale = Scale;
            var gui = new Vector2(position.x / scale, (Screen.height - position.y) / scale);
            return rect.Contains(gui);
        }

        private void ReadKey()
        {
            string name = Plugin.ToggleKey.Value;
            if (name == keyName)
            {
                return;
            }
            keyName = name;
            if (!Enum.TryParse(name?.Trim(), true, out Key parsed) || parsed == Key.None)
            {
                Plugin.Log.LogWarning($"Unknown ToggleKey '{name}', using Backquote.");
                parsed = Key.Backquote;
            }
            key = parsed;
        }

        private void EnsureStyles()
        {
            if (stylesReady)
            {
                return;
            }
            stylesReady = true;
            textStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            mutedStyle = new GUIStyle(textStyle);
            mutedStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            goodStyle = new GUIStyle(textStyle);
            goodStyle.normal.textColor = new Color(0.55f, 0.9f, 0.55f);
            warnStyle = new GUIStyle(textStyle);
            warnStyle.normal.textColor = new Color(1f, 0.8f, 0.35f);
            badStyle = new GUIStyle(textStyle);
            badStyle.normal.textColor = new Color(1f, 0.45f, 0.4f);
            headingStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(6, 6, 4, 4), margin = new RectOffset(0, 0, 2, 2), border = new RectOffset(0, 0, 0, 0) };
            noticeStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 14, wordWrap = true, border = new RectOffset(0, 0, 0, 0) };
            // Every state: a style copied from the skin keeps its dark text for hover etc., which made the notice go black.
            SetTextColor(noticeStyle, Color.white);
            windowStyle = new GUIStyle(GUI.skin.window) { border = new RectOffset(0, 0, 0, 0) };
            SetTextColor(windowStyle, new Color(0.95f, 0.85f, 0.6f));
        }

        private void RefreshBackgrounds()
        {
            float opacity = Mathf.Clamp(Plugin.Opacity.Value, 0.3f, 1f);
            if (Mathf.Abs(opacity - backgroundOpacity) < 0.001f && windowBackground != null)
            {
                return;
            }
            backgroundOpacity = opacity;
            Replace(ref windowBackground, new Color(0.08f, 0.09f, 0.11f, opacity));
            Replace(ref rowBackground, new Color(0.15f, 0.16f, 0.19f, opacity));
            SetBackground(windowStyle, windowBackground);
            SetBackground(boxStyle, rowBackground);
            SetBackground(noticeStyle, windowBackground);
        }

        private static void Replace(ref Texture2D texture, Color color)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
            texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
        }

        private static void SetTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.hover.textColor = color;
            style.onHover.textColor = color;
            style.active.textColor = color;
            style.onActive.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;
        }

        private static void SetBackground(GUIStyle style, Texture2D texture)
        {
            style.normal.background = texture;
            style.onNormal.background = texture;
            style.focused.background = texture;
            style.onFocused.background = texture;
            style.hover.background = texture;
            style.onHover.background = texture;
            style.active.background = texture;
            style.onActive.background = texture;
        }
    }
}
