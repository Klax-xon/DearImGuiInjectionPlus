using System;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus.Backends;
using DearImGuiInjectionPlus.BepInEx;
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
    private static readonly UIntPtr SubclassId = new(1);
    private static readonly Comctl32.SubclassProc _subclassProcDelegate = SubclassHandler;
    private static bool _isSubclassed;

    public static IntPtr WindowHandle => _windowHandle;

    internal static void Subclass(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || (_windowHandle == hWnd && _isSubclassed))
            return;

        if (DearImGuiInjectionPlus.Context == IntPtr.Zero)
        {
            Log.Warning("[InputManager] Subclass called before ImGui context was created! Initializing ImGui context first...");
            DearImGuiInjectionPlus.InitImGui();
        }

        if (_windowHandle != IntPtr.Zero && _windowHandle != hWnd)
        {
            Unsubclass();
        }

        bool wasSameWindow = _windowHandle == hWnd;
        _windowHandle = hWnd;

        // Initialize Win32 backend immediately so ImGui IO, viewport, and NewFrame() are ready
        if (!wasSameWindow)
        {
            ImGuiWin32Impl.Init(hWnd);
        }

        // comctl32 SetWindowSubclass requires execution on the thread that created the window (Unity Main Thread)
        InstallSubclass(hWnd);
    }

    private static void InstallSubclass(IntPtr hWnd)
    {
        void TrySubclass()
        {
            if (_isSubclassed || _windowHandle != hWnd)
                return;

            _isSubclassed = Comctl32.SetWindowSubclass(hWnd, _subclassProcDelegate, SubclassId, UIntPtr.Zero);
            if (_isSubclassed)
            {
                Log.Info($"[InputManager] Subclassed window {hWnd:X8} via SetWindowSubclass on thread {Kernel32.GetCurrentThreadId()}. Input hook listening for {ToggleKey}.");
            }
            else
            {
                Log.Error($"[InputManager] Failed to subclass window {hWnd:X8} via SetWindowSubclass. Error: {Marshal.GetLastWin32Error()}");
            }
        }

        uint windowThreadId = User32.GetWindowThreadProcessId(hWnd, out _);
        uint currentThreadId = Kernel32.GetCurrentThreadId();

        if (windowThreadId == currentThreadId || UnityMainThreadDispatcher.IsMainThread)
        {
            TrySubclass();
        }
        else
        {
            UnityMainThreadDispatcher.Enqueue(TrySubclass);
        }
    }

    internal static void Unsubclass()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            var hWnd = _windowHandle;
            _windowHandle = IntPtr.Zero;

            void TryUnsubclass()
            {
                if (_isSubclassed)
                {
                    bool removed = Comctl32.RemoveWindowSubclass(hWnd, _subclassProcDelegate, SubclassId);
                    if (removed)
                    {
                        Log.Info($"[InputManager] Removed window subclass on window {hWnd:X8}");
                    }
                    else
                    {
                        Log.Warning($"[InputManager] Failed to remove window subclass on window {hWnd:X8}. Error: {Marshal.GetLastWin32Error()}");
                    }
                    _isSubclassed = false;
                }
            }

            uint windowThreadId = User32.GetWindowThreadProcessId(hWnd, out _);
            uint currentThreadId = Kernel32.GetCurrentThreadId();

            if (windowThreadId == currentThreadId || UnityMainThreadDispatcher.IsMainThread)
            {
                TryUnsubclass();
            }
            else
            {
                UnityMainThreadDispatcher.Enqueue(TryUnsubclass);
            }
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
        return SubclassHandler(hWnd, msg, wParam, lParam, SubclassId, UIntPtr.Zero);
    }

    public static unsafe IntPtr SubclassHandler(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        try
        {
            // Automatically unhook when the window is destroyed
            if (msg == WindowMessage.WM_NCDESTROY)
            {
                Comctl32.RemoveWindowSubclass(hWnd, _subclassProcDelegate, SubclassId);
                _isSubclassed = false;
                if (_windowHandle == hWnd)
                {
                    _windowHandle = IntPtr.Zero;
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            if (RendererFinder.RendererFinder.IsShuttingDown)
            {
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // 1. Always allow Alt+Enter (fullscreen toggle) through to Unity & Windows
            if ((msg == WindowMessage.WM_SYSKEYDOWN || msg == WindowMessage.WM_SYSKEYUP) && (int)wParam == 0x0D)
            {
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
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
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
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
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Swallow keyboard messages (letters, numbers, modifiers) if blocking all inputs or ImGui wants keyboard capture
            if (IsKeyboardMessage(msg))
            {
                if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureKeyboard)
                {
                    return IntPtr.Zero;
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Forward non-input window events (WM_PAINT, WM_SIZE, WM_ACTIVATE, etc.)
            return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error($"[InputManager] Error in SubclassHandler: {ex}");
            return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
        }
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
