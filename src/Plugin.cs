using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AutoFoundations
{
    [BepInPlugin(Guid, DisplayName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "el2.autofoundations";
        public const string DisplayName = "Auto Foundations";
        public const string Version = BuildInfo.Version;

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AutoBuy;
        internal static ConfigEntry<int> KeepInfluence;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<bool> ShowNotice;
        internal static ConfigEntry<float> UiScale;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> LogPurchases;

        private Harmony harmony;
        private bool patched;
        private FoundationBuyer buyer;
        private FoundationWindow window;

        // Mod Menu (el2.modmenu) finds these by name and lists this mod with its own page.
        public string ModMenuTitle => "Foundations";

        public string ModMenuStatus => window?.MenuStatus;

        public void ModMenuDraw() => window?.DrawEmbedded();

        private void Awake()
        {
            Log = Logger;
            AutoBuy = Config.Bind("General", "AutoBuy", true,
                "Buy foundations wherever the game lets you build one, as soon as the influence above KeepInfluence pays for them (most tile yield per influence first). Also switchable in the window.");
            KeepInfluence = Config.Bind("General", "KeepInfluence", 1000,
                new ConfigDescription("Foundations are only bought with influence above this much. Free foundations are always taken.", new AcceptableValueRange<int>(0, 10000000)));
            ToggleKey = Config.Bind("Window", "ToggleKey", "Backquote",
                "Key that opens and closes the window (a Unity Input System key name: Backquote is the key left of 1). The game uses F1-F6 and F8-F11. Not used while Mod Menu is installed: its key (F7) opens the menu, with a Foundations page.");
            ShowNotice = Config.Bind("Window", "ShowNotice", false,
                "Show a short notice at the top of the screen when foundations are bought.");
            UiScale = Config.Bind("Window", "Size", 0f,
                new ConfigDescription("Size of the window and its text. 0 = automatic (follows the screen resolution: 1x at 1080p, 2x at 4K); otherwise a multiplier.", new AcceptableValueRange<float>(0f, 4f)));
            Opacity = Config.Bind("Window", "Opacity", 1f,
                new ConfigDescription("Opacity of the window background (1 = solid).", new AcceptableValueRange<float>(0.3f, 1f)));
            LogPurchases = Config.Bind("Debug", "LogPurchases", true,
                "Write every foundation bought to BepInEx/LogOutput.log.");

            harmony = new Harmony(Guid);
            buyer = new FoundationBuyer();
            window = new FoundationWindow(buyer);
            Log.LogInfo($"{DisplayName} {Version} waiting for the game data");
        }

        private void Update()
        {
            if (!patched)
            {
                if (!GameDataReady())
                {
                    return;
                }
                PatchAll();
            }
            Guard.UnpatchFailed(harmony);
            try
            {
                window.Update();
                buyer.Update();
            }
            catch (Exception e)
            {
                // Keep the game running; the next frame tries again.
                Log.LogError($"Update failed: {e}");
            }
        }

        private void OnGUI()
        {
            if (!patched)
            {
                return;
            }
            try
            {
                window.OnGUI();
            }
            catch (Exception e)
            {
                Log.LogError($"Drawing the window failed: {e}");
            }
        }

        private void OnDestroy()
        {
            FoundationCapture.Wanted = false;
            harmony?.UnpatchSelf();
        }

        // Patching runs a class's static constructor; wait until the game data those may read is loaded.
        private static bool GameDataReady()
        {
            try
            {
                return Amplitude.Mercury.Utils.DataUtils != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // One patch class at a time, so a game update that breaks one only disables that part, and the log names it.
        private void PatchAll()
        {
            patched = true;
            int failed = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                {
                    continue;
                }
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failed++;
                    Guard.MarkFailed(type);
                    Log.LogError($"Could not patch {type.Name}, that part of the mod is off (game update?): {e.GetBaseException().Message}");
                }
            }
            string open = FoundationWindow.MenuInstalled ? "Open Mod Menu (its key, F7 by default)" : $"Press {ToggleKey.Value}";
            Log.LogInfo($"{DisplayName} {Version} active (game {Application.version}, {failed} patches failed). {open} in game.");
        }
    }
}
