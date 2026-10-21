using System;
using DearImGuiInjectionPlus.Patches;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

public class DearImGuiInjectionPlusRunner : MonoBehaviour
{
    private static bool _legacyMousePosSupported = true;

    public DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle) { }

    private void Update()
    {
        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            NewInputSystemBlocker.EnforceBlocked();
        }
        else
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible && _legacyMousePosSupported)
            {
                try
                {
                    UnityInputBlocker.LastMousePosition = Input.mousePosition;
                }
                catch
                {
                    _legacyMousePosSupported = false;
                }
            }
            NewInputSystemBlocker.RestoreDevicesIfNeeded();
        }

        if (DearImGuiInjectionPlus.IsCursorVisible)
        {
            InputManager.EnforceCursorStateOnMainThread();
        }
    }

    private void LateUpdate()
    {
        // INP-09: Re-enforce cursor unlocked state and free clipping after game Update/LateUpdate loops run
        InputManager.EnforceCursorStateOnMainThread();
    }

    private void OnApplicationQuit()
    {
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: true);
    }
}
