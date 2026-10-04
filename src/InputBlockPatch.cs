using Amplitude.Framework.Input;
using Amplitude.Mercury.UI;
using Amplitude.UI.Interactables;
using HarmonyLib;

namespace AutoFoundations
{
    // While the cursor is over the window, mouse presses and the wheel stop at the game's UI layer, so they don't also
    // click or zoom the map underneath. Releases always go through, so nothing stays half-pressed.
    [HarmonyPatch(typeof(UIInteractivityManager))]
    internal static class InputBlockPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("TryCatchLeftClickAction")]
        private static bool LeftClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchRightClickAction")]
        private static bool RightClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchMiddleClickAction")]
        private static bool MiddleClick(ButtonState buttonState, ref bool __result) => Pass(buttonState, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch("TryCatchMouseScrollAction")]
        private static bool Scroll(ref bool __result)
        {
            if (!FoundationWindow.MouseOver)
            {
                return true;
            }
            __result = true;
            return false;
        }

        private static bool Pass(ButtonState buttonState, ref bool __result)
        {
            if (buttonState != ButtonState.Down || !FoundationWindow.MouseOver)
            {
                return true;
            }
            __result = true;
            return false;
        }
    }
}
