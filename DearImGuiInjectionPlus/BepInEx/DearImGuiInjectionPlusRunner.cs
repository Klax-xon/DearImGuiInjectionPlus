using System;
using DearImGuiInjectionPlus.Patches;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

public class DearImGuiInjectionPlusRunner : MonoBehaviour
{
    public DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle) { }

    private void Update()
    {
        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            NewInputSystemBlocker.EnforceBlocked();
        }
        else
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible)
            {
                UnityInputBlocker.LastMousePosition = Input.mousePosition;
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

    private void OnDestroy()
    {
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown();
    }
}
