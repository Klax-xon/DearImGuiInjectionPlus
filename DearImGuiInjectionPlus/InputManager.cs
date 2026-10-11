using System;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus.Backends;
using DearImGuiInjectionPlus.Patches;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;
using UnityEngine;

namespace DearImGuiInjectionPlus;

/// <summary>
/// Centralized Win32 input manager and window subclass.
/// Intercepts raw window messages, feeds Dear ImGui, blocks game input when menus/controls are active,
/// and synchronizes mouse cursor lock state with the Unity engine.
/// </summary>
internal static class InputManager
{
    private static IntPtr _windowHandle = IntPtr.Zero;
    private static User32.WndProcDelegate _wndProcDelegate;
    private static IntPtr _originalWndProc = IntPtr.Zero;
    private const int GWL_WNDPROC = -4;


    public static IntPtr WindowHandle => _windowHandle;

    internal static void Subclass(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || _windowHandle == hWnd)
            return;

        if (DearImGuiInjectionPlus.Context == IntPtr.Zero)
        {
            Log.Warning("[InputManager] Subclass called before ImGui context was created! Initializing ImGui context first...");
            DearImGuiInjectionPlus.InitImGui();
        }

        if (_originalWndProc != IntPtr.Zero && _windowHandle != IntPtr.Zero)
        {
            Unsubclass();
        }

        _windowHandle = hWnd;
        _wndProcDelegate = WndProcHandler;
        _originalWndProc = User32.SetWindowLong(hWnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        Log.Info($"[InputManager] Subclassed window {hWnd:X8}. Input hook listening for Insert key.");
        ImGuiWin32Impl.Init(hWnd);
    }

    internal static void Unsubclass()
    {
        if (_windowHandle != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
        {
            User32.SetWindowLong(_windowHandle, GWL_WNDPROC, _originalWndProc);
            Log.Info($"[InputManager] Restored original WndProc on window {_windowHandle:X8}");
            _originalWndProc = IntPtr.Zero;
            _windowHandle = IntPtr.Zero;
            _wndProcDelegate = null;
        }

        if (!RendererFinder.RendererFinder.IsShuttingDown)
        {
            ImGuiWin32Impl.Shutdown();
        }
    }

    public static VirtualKey ToggleKey => DearImGuiInjectionPlus.CursorVisibilityToggle != null
        ? DearImGuiInjectionPlus.CursorVisibilityToggle.Get()
        : VirtualKey.Insert;

    public static unsafe IntPtr WndProcHandler(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam)
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
        {
            return _originalWndProc != IntPtr.Zero
                ? User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;
        }

        // 1. Always allow Alt+Enter (fullscreen toggle) through to Unity & Windows
        if ((msg == WindowMessage.WM_SYSKEYDOWN || msg == WindowMessage.WM_SYSKEYUP) && (int)wParam == 0x0D)
        {
            return User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        // 2. Intercept Menu Toggle Key (Insert)
        if (msg == WindowMessage.WM_KEYDOWN && (int)wParam == (int)ToggleKey)
        {
            DearImGuiInjectionPlus.ToggleCursorVisibility();
            return IntPtr.Zero; // Swallow toggle key
        }
        if (msg == WindowMessage.WM_KEYUP && (int)wParam == (int)ToggleKey)
        {
            return IntPtr.Zero; // Swallow toggle key release
        }

        // 3. When menu is CLOSED: pass all input directly to the game
        if (!DearImGuiInjectionPlus.IsCursorVisible)
        {
            return User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        // 4. When menu is OPEN: Feed ImGui first
        ImGuiWin32Impl.WndProcHandler(hWnd, msg, wParam, lParam);

        // Prevent cursor fighting with Unity
        if (msg == WindowMessage.WM_SETCURSOR)
        {
            return (IntPtr)1;
        }

        // Swallow Raw Input (WM_INPUT = 0x00FF)
        // This stops Unity's camera from rotating while moving the mouse over ImGui
        if ((int)msg == 0x00FF || msg == WindowMessage.WM_INPUT)
        {
            // Let Windows clean up raw input buffers, but DO NOT forward to Unity
            return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // Swallow mouse messages (clicks, moves, wheels) if blocking all inputs or ImGui wants mouse capture
        if (IsMouseMessage(msg))
        {
            if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureMouse)
            {
                return IntPtr.Zero;
            }
            return User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        // Swallow keyboard messages (letters, numbers, modifiers) if blocking all inputs or ImGui wants keyboard capture
        if (IsKeyboardMessage(msg))
        {
            if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureKeyboard)
            {
                return IntPtr.Zero;
            }
            return User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        // Forward non-input window events (WM_PAINT, WM_SIZE, WM_ACTIVATE, etc.)
        return User32.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private static bool IsMouseMessage(WindowMessage msg) => msg switch
    {
        WindowMessage.WM_MOUSEMOVE or
        WindowMessage.WM_LBUTTONDOWN or WindowMessage.WM_LBUTTONUP or WindowMessage.WM_LBUTTONDBLCLK or
        WindowMessage.WM_RBUTTONDOWN or WindowMessage.WM_RBUTTONUP or WindowMessage.WM_RBUTTONDBLCLK or
        WindowMessage.WM_MBUTTONDOWN or WindowMessage.WM_MBUTTONUP or WindowMessage.WM_MBUTTONDBLCLK or
        WindowMessage.WM_XBUTTONDOWN or WindowMessage.WM_XBUTTONUP or WindowMessage.WM_XBUTTONDBLCLK or
        WindowMessage.WM_MOUSEWHEEL or WindowMessage.WM_MOUSEHWHEEL => true,
        _ => false
    };

    private static bool IsKeyboardMessage(WindowMessage msg) => msg switch
    {
        WindowMessage.WM_KEYDOWN or WindowMessage.WM_KEYUP or
        WindowMessage.WM_CHAR or WindowMessage.WM_DEADCHAR or
        WindowMessage.WM_SYSKEYDOWN or WindowMessage.WM_SYSKEYUP or
        WindowMessage.WM_SYSCHAR or WindowMessage.WM_SYSDEADCHAR => true,
        _ => false
    };

    internal static void OnCursorVisibilityToggled(bool visible)
    {
        try
        {
            NewInputSystemBlocker.SetBlocked(visible);
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputManager] Error in NewInputSystemBlocker.SetBlocked: {ex.Message}");
        }

        try
        {
            if (visible)
            {
                if (Cursor.lockState != CursorLockMode.None)
                {
                    CursorLockStatePatch.DesiredGameLockState = Cursor.lockState;
                }
                CursorVisiblePatch.DesiredGameVisible = Cursor.visible;

                try
                {
                    CursorLockStatePatch.SuppressStateCapture = true;
                    CursorVisiblePatch.SuppressStateCapture = true;

                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                finally
                {
                    CursorLockStatePatch.SuppressStateCapture = false;
                    CursorVisiblePatch.SuppressStateCapture = false;
                }

                User32.ClipCursor(IntPtr.Zero);
            }
            else
            {
                try
                {
                    CursorLockStatePatch.SuppressStateCapture = true;
                    CursorVisiblePatch.SuppressStateCapture = true;

                    Cursor.lockState = CursorLockStatePatch.DesiredGameLockState;
                    Cursor.visible = CursorVisiblePatch.DesiredGameVisible;
                }
                finally
                {
                    CursorLockStatePatch.SuppressStateCapture = false;
                    CursorVisiblePatch.SuppressStateCapture = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputManager] Error in OnCursorVisibilityToggled: {ex.Message}");
        }
    }

    internal static void EnforceCursorStateOnMainThread()
    {
        if (DearImGuiInjectionPlus.IsCursorVisible)
        {
            try
            {
                CursorLockStatePatch.SuppressStateCapture = true;
                CursorVisiblePatch.SuppressStateCapture = true;

                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                }

                if (!Cursor.visible)
                {
                    Cursor.visible = true;
                }
            }
            finally
            {
                CursorLockStatePatch.SuppressStateCapture = false;
                CursorVisiblePatch.SuppressStateCapture = false;
            }
        }
    }
}
