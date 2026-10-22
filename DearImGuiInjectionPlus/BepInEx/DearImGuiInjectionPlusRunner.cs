using System;
using DearImGuiInjectionPlus.Patches;
using UnityEngine;
using PluginLog = DearImGuiInjectionPlus.Log;

namespace DearImGuiInjectionPlus.BepInEx;

public class DearImGuiInjectionPlusRunner : MonoBehaviour
{
    private static bool _legacyMousePosSupported = true;
    private bool _wasBlockingMouse;
    private bool _wasBlockingKeyboard;
    internal static bool _isQuitting;

    public DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle) { }

    private void Awake()
    {
        _isQuitting = false;
    }

    private void Update()
    {
        try
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
        catch (Exception ex)
        {
            PluginLog.Error($"[DearImGuiInjectionPlusRunner] Exception in Update: {ex}");
        }
    }

    private void LateUpdate()
    {
        try
        {
            // INP-09: Re-enforce cursor unlocked state and free clipping after game Update/LateUpdate loops run
            InputManager.EnforceCursorStateOnMainThread();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[DearImGuiInjectionPlusRunner] Exception in LateUpdate: {ex}");
        }
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        if (!_isQuitting)
        {
            PluginLog.Warning("[DearImGuiInjectionPlus] DearImGuiInjectionPlusRunner destroyed outside of application quit (scene transition or domain re-bind). Suppressing premature shutdown; DXGI swapchain hooks remain active.");
            DearImGuiInjectionPlusBaseUnityPlugin.EnsureRunnerAlive();
            return;
        }

        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: true);
    }
}
