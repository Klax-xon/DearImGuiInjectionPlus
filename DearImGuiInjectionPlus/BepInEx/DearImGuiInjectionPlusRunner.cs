using System;
using DearImGuiInjectionPlus.Patches;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

public class DearImGuiInjectionPlusRunner : MonoBehaviour
{
    private static bool _legacyMousePosSupported = true;
    private bool _wasBlockingMouse;
    private bool _wasBlockingKeyboard;

    public DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle) { }

    private void Update()
    {
        bool blockMouse = UnityInputBlocker.ShouldBlockMouse();
        bool blockKeyboard = UnityInputBlocker.ShouldBlockKeyboard();

        // Detect transition from passthrough (false) to blocked (true) and immediately flush held keys/axes
        if ((!_wasBlockingKeyboard && blockKeyboard) || (!_wasBlockingMouse && blockMouse))
        {
            InputManager.FlushDepressedKeysOnCaptureTransition();
            UnityInputBlocker.TryResetInputAxes();
        }

        _wasBlockingMouse = blockMouse;
        _wasBlockingKeyboard = blockKeyboard;

        if (blockMouse || blockKeyboard)
        {
            NewInputSystemBlocker.EnforceBlocked(blockMouse, blockKeyboard);
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
