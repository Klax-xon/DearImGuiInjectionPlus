using System;
using HarmonyLib;
using UnityEngine;
using DearImGuiInjectionPlus.Windows;

namespace DearImGuiInjectionPlus;

[HarmonyPatch(typeof(Cursor), nameof(Cursor.lockState), MethodType.Setter)]
internal static class CursorLockStatePatch
{
    public static CursorLockMode DesiredGameLockState = CursorLockMode.None;
    internal static bool SuppressStateCapture = false;

    private static bool Prefix(ref CursorLockMode value)
    {
        if (!SuppressStateCapture)
        {
            DesiredGameLockState = value;
        }
        if (DearImGuiInjectionPlus.IsCursorVisible)
        {
            value = CursorLockMode.None;
            // INP-09: Prevent game from clipping cursor via Unity lockState setter
            User32.ClipCursor(IntPtr.Zero);
        }
        return true;
    }
}

[HarmonyPatch(typeof(Cursor), nameof(Cursor.visible), MethodType.Setter)]
internal static class CursorVisiblePatch
{
    public static bool DesiredGameVisible = true;
    internal static bool SuppressStateCapture = false;

    private static bool Prefix(ref bool value)
    {
        if (!SuppressStateCapture)
        {
            DesiredGameVisible = value;
        }
        if (DearImGuiInjectionPlus.IsCursorVisible)
        {
            value = true;
        }
        return true;
    }
}
