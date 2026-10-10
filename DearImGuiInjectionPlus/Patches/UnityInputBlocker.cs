using HarmonyLib;
using UnityEngine;

namespace DearImGuiInjectionPlus.Patches
{
    [HarmonyPatch]
    internal static class UnityInputBlocker
    {
        // 1. Zero out mouse axes (stops camera scripts polling Input.GetAxis)
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        [HarmonyPrefix]
        private static bool BlockAxis(ref float __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        // 2. Block key states (stops character movement scripts polling Input.GetKey)
        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(KeyCode))]
        [HarmonyPrefix]
        private static bool BlockKey(ref bool __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 3. Block GetButton (Fire1, Fire2, Jump, etc.)
        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp))]
        [HarmonyPrefix]
        private static bool BlockButton(ref bool __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 4. Block GetKey string overloads ("mouse 0", "mouse 1", "mouse 2", "mouse 3", "mouse 4")
        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(string))]
        [HarmonyPrefix]
        private static bool BlockKeyString(ref bool __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 5. Zero out scroll wheel deltas in legacy input
        [HarmonyPatch(typeof(Input), nameof(Input.mouseScrollDelta), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockScrollDelta(ref Vector2 __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = Vector2.zero;
                return false;
            }
            return true;
        }

        // 6. Block mouse button states (stops shooting / interacting polling Input.GetMouseButton)
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp))]
        [HarmonyPrefix]
        private static bool BlockMouseButton(ref bool __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 7. Block general key flags
        [HarmonyPatch(typeof(Input), nameof(Input.anyKey), MethodType.Getter)]
        [HarmonyPatch(typeof(Input), nameof(Input.anyKeyDown), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockAnyKey(ref bool __result)
        {
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
