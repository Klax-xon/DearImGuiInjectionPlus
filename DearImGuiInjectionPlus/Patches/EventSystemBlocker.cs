using HarmonyLib;
using UnityEngine.EventSystems;

namespace DearImGuiInjectionPlus.Patches;

[HarmonyPatch(typeof(EventSystem), nameof(EventSystem.Update))]
internal static class EventSystemBlocker
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        // Drop uGUI raycasts and event updates whenever Dear ImGui cursor is active
        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            return false;
        }

        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.Initialized && DearImGuiInjectionPlus.WantCaptureMouse)
        {
            return false;
        }

        return true;
    }
}
