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

    // Bitmask for tracking keys/buttons (0-255) depressed when overlay became visible (INP-02)
    private static ulong _downKeys0;
    private static ulong _downKeys1;
    private static ulong _downKeys2;
    private static ulong _downKeys3;

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
            try
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
            catch (Exception ex)
            {
                Log.Error($"[InputManager] Exception during SetWindowSubclass: {ex}");
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
                try
                {
                    if (_isSubclassed)
                    {
                        ClearAllDownKeys();
                        if (User32.GetCapture() != IntPtr.Zero)
                        {
                            User32.ReleaseCapture();
                        }
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
                catch (Exception ex)
                {
                    Log.Error($"[InputManager] Exception during RemoveWindowSubclass: {ex}");
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

        ImGuiWin32Impl.Shutdown();
    }

    public static VirtualKey ToggleKey => DearImGuiInjectionPlus.CursorVisibilityToggle != null
        ? DearImGuiInjectionPlus.CursorVisibilityToggle.Get()
        : VirtualKey.Insert;

    public static unsafe IntPtr WndProcHandler(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return SubclassHandler(hWnd, msg, wParam, lParam, SubclassId, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            try
            {
                Log.Fatal($"[InputManager] Fatal exception in WndProcHandler: {ex}");
                return User32.DefWindowProc(hWnd, msg, wParam, lParam);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
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
                ClearAllDownKeys();
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
            // INP-07: Check bit 30 of lParam (previous key state: 0 = first press, 1 = repeat) to ignore auto-repeat
            if (msg == WindowMessage.WM_KEYDOWN && (int)wParam == (int)ToggleKey)
            {
                bool isRepeat = ((long)lParam & (1L << 30)) != 0;
                if (!isRepeat)
                {
                    DearImGuiInjectionPlus.ToggleCursorVisibility();
                }
                return IntPtr.Zero; // Swallow toggle key and auto-repeats
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
            // INP-09: Continuously free native cursor clipping while menu is open
            User32.ClipCursor(IntPtr.Zero);
            ImGuiWin32Impl.WndProcHandler(hWnd, msg, wParam, lParam);

            // INP-08: Respond to WM_UNICHAR query probe
            if (msg == WindowMessage.WM_UNICHAR && (int)wParam == 0xFFFF)
            {
                return (IntPtr)1;
            }

            // INP-07: Prevent cursor fighting with Unity for client area; allow OS cursors (sizing, borders) in non-client area
            if (msg == WindowMessage.WM_SETCURSOR)
            {
                const ushort HTCLIENT = 1;
                if (ImGuiWin32Impl.LOWORD(lParam) == HTCLIENT)
                {
                    return (IntPtr)1;
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Swallow Raw Input (WM_INPUT = 0x00FF)
            // This stops Unity's camera from rotating while moving the mouse over ImGui
            if ((int)msg == 0x00FF || msg == WindowMessage.WM_INPUT)
            {
                if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureMouse)
                {
                    // Let Windows clean up raw input buffers, but DO NOT forward to Unity
                    return User32.DefWindowProc(hWnd, msg, wParam, lParam);
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Swallow mouse messages (clicks, moves, wheels) if blocking all inputs or ImGui wants mouse capture
            if (IsMouseMessage(msg))
            {
                // INP-02: Permit mouse button UP events through if the button was depressed when overlay opened
                var mouseUpKey = GetMouseUpVirtualKey(msg, wParam);
                if (mouseUpKey.HasValue && CheckAndClearKey((int)mouseUpKey.Value))
                {
                    return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
                }

                if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureMouse)
                {
                    return IntPtr.Zero;
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Swallow keyboard messages (letters, numbers, modifiers) if blocking all inputs or ImGui wants keyboard capture
            if (IsKeyboardMessage(msg))
            {
                // INP-02: Permit key UP events through if the key was depressed when overlay opened
                if (msg == WindowMessage.WM_KEYUP || msg == WindowMessage.WM_SYSKEYUP)
                {
                    if (CheckAndClearKey((int)wParam))
                    {
                        return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
                    }
                }

                if (DearImGuiInjectionPlus.BlockAllInputsWhenOpen || DearImGuiInjectionPlus.WantCaptureKeyboard)
                {
                    // INP-08: Route IME system messages to DefWindowProc so the OS IME composition UI
                    // renders properly without leaking input events to the Unity game engine.
                    if (IsImeSystemMessage(msg))
                    {
                        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
                    }

                    return IntPtr.Zero;
                }
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            // Forward non-input window events (WM_PAINT, WM_SIZE, WM_ACTIVATE, etc.)
            return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            try
            {
                Log.Error($"[InputManager] Error in SubclassHandler: {ex}");
                return Comctl32.DefSubclassProc(hWnd, msg, wParam, lParam);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    private static void SetKeyDown(int vk)
    {
        if (vk is < 0 or > 255) return;
        int idx = vk >> 6;
        ulong bit = 1UL << (vk & 63);
        switch (idx)
        {
            case 0: _downKeys0 |= bit; break;
            case 1: _downKeys1 |= bit; break;
            case 2: _downKeys2 |= bit; break;
            case 3: _downKeys3 |= bit; break;
        }
    }

    private static void ClearKeyDown(int vk)
    {
        if (vk is < 0 or > 255) return;
        int idx = vk >> 6;
        ulong bit = 1UL << (vk & 63);
        switch (idx)
        {
            case 0: _downKeys0 &= ~bit; break;
            case 1: _downKeys1 &= ~bit; break;
            case 2: _downKeys2 &= ~bit; break;
            case 3: _downKeys3 &= ~bit; break;
        }
    }

    private static bool IsKeyDown(int vk)
    {
        if (vk is < 0 or > 255) return false;
        int idx = vk >> 6;
        ulong bit = 1UL << (vk & 63);
        return idx switch
        {
            0 => (_downKeys0 & bit) != 0,
            1 => (_downKeys1 & bit) != 0,
            2 => (_downKeys2 & bit) != 0,
            3 => (_downKeys3 & bit) != 0,
            _ => false
        };
    }

    private static bool HasAnyDownKeys() =>
        (_downKeys0 | _downKeys1 | _downKeys2 | _downKeys3) != 0;

    private static void ClearAllDownKeys()
    {
        _downKeys0 = _downKeys1 = _downKeys2 = _downKeys3 = 0;
    }

    private static void CaptureDepressedKeys()
    {
        ClearAllDownKeys();
        int toggle = (int)ToggleKey;
        for (int vk = 1; vk < 256; vk++)
        {
            if (vk == toggle)
                continue;

            if ((User32.GetAsyncKeyState((User32.VirtualKey)vk) & 0x8000) != 0)
            {
                SetKeyDown(vk);
            }
        }
    }

    private static bool CheckAndClearKey(int vk)
    {
        bool down = IsKeyDown(vk);
        if (vk == (int)VirtualKey.Shift)
        {
            down |= IsKeyDown((int)VirtualKey.LeftShift) || IsKeyDown((int)VirtualKey.RightShift);
            ClearKeyDown((int)VirtualKey.Shift);
            ClearKeyDown((int)VirtualKey.LeftShift);
            ClearKeyDown((int)VirtualKey.RightShift);
        }
        else if (vk is (int)VirtualKey.LeftShift or (int)VirtualKey.RightShift)
        {
            down |= IsKeyDown((int)VirtualKey.Shift);
            ClearKeyDown((int)VirtualKey.Shift);
            ClearKeyDown((int)VirtualKey.LeftShift);
            ClearKeyDown((int)VirtualKey.RightShift);
        }
        else if (vk == (int)VirtualKey.Control)
        {
            down |= IsKeyDown((int)VirtualKey.LeftControl) || IsKeyDown((int)VirtualKey.RightControl);
            ClearKeyDown((int)VirtualKey.Control);
            ClearKeyDown((int)VirtualKey.LeftControl);
            ClearKeyDown((int)VirtualKey.RightControl);
        }
        else if (vk is (int)VirtualKey.LeftControl or (int)VirtualKey.RightControl)
        {
            down |= IsKeyDown((int)VirtualKey.Control);
            ClearKeyDown((int)VirtualKey.Control);
            ClearKeyDown((int)VirtualKey.LeftControl);
            ClearKeyDown((int)VirtualKey.RightControl);
        }
        else if (vk == (int)VirtualKey.Menu)
        {
            down |= IsKeyDown((int)VirtualKey.LeftMenu) || IsKeyDown((int)VirtualKey.RightMenu);
            ClearKeyDown((int)VirtualKey.Menu);
            ClearKeyDown((int)VirtualKey.LeftMenu);
            ClearKeyDown((int)VirtualKey.RightMenu);
        }
        else if (vk is (int)VirtualKey.LeftMenu or (int)VirtualKey.RightMenu)
        {
            down |= IsKeyDown((int)VirtualKey.Menu);
            ClearKeyDown((int)VirtualKey.Menu);
            ClearKeyDown((int)VirtualKey.LeftMenu);
            ClearKeyDown((int)VirtualKey.RightMenu);
        }
        else
        {
            ClearKeyDown(vk);
        }
        return down;
    }

    private static VirtualKey? GetMouseUpVirtualKey(WindowMessage msg, IntPtr wParam) => msg switch
    {
        WindowMessage.WM_LBUTTONUP => VirtualKey.LeftButton,
        WindowMessage.WM_RBUTTONUP => VirtualKey.RightButton,
        WindowMessage.WM_MBUTTONUP => VirtualKey.MiddleButton,
        WindowMessage.WM_XBUTTONUP => ImGuiWin32Impl.GET_XBUTTON_WPARAM(wParam) == 1
            ? VirtualKey.ExtraButton1
            : VirtualKey.ExtraButton2,
        _ => null
    };

    private static void ReleaseRemainingDepressedKeys(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !HasAnyDownKeys())
        {
            ClearAllDownKeys();
            return;
        }

        try
        {
            for (int vk = 1; vk < 256; vk++)
            {
                if (!IsKeyDown(vk))
                    continue;

                switch ((VirtualKey)vk)
                {
                    case VirtualKey.LeftButton:
                        User32.PostMessage(hWnd, WindowMessage.WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                        break;
                    case VirtualKey.RightButton:
                        User32.PostMessage(hWnd, WindowMessage.WM_RBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                        break;
                    case VirtualKey.MiddleButton:
                        User32.PostMessage(hWnd, WindowMessage.WM_MBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                        break;
                    case VirtualKey.ExtraButton1:
                        User32.PostMessage(hWnd, WindowMessage.WM_XBUTTONUP, (IntPtr)(1 << 16), IntPtr.Zero);
                        break;
                    case VirtualKey.ExtraButton2:
                        User32.PostMessage(hWnd, WindowMessage.WM_XBUTTONUP, (IntPtr)(2 << 16), IntPtr.Zero);
                        break;
                    case VirtualKey.Shift:
                    case VirtualKey.LeftShift:
                    case VirtualKey.RightShift:
                        User32.PostMessage(hWnd, WindowMessage.WM_KEYUP, (IntPtr)vk, (IntPtr)0xC0000001L);
                        break;
                    case VirtualKey.Control:
                    case VirtualKey.LeftControl:
                    case VirtualKey.RightControl:
                        User32.PostMessage(hWnd, WindowMessage.WM_KEYUP, (IntPtr)vk, (IntPtr)0xC0000001L);
                        break;
                    case VirtualKey.Menu:
                    case VirtualKey.LeftMenu:
                    case VirtualKey.RightMenu:
                        User32.PostMessage(hWnd, WindowMessage.WM_SYSKEYUP, (IntPtr)vk, (IntPtr)0xC0000001L);
                        break;
                    default:
                        User32.PostMessage(hWnd, WindowMessage.WM_KEYUP, (IntPtr)vk, (IntPtr)0xC0000001L);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputManager] Error synthesizing key releases: {ex.Message}");
        }
        finally
        {
            ClearAllDownKeys();
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
        WindowMessage.WM_SYSCHAR or WindowMessage.WM_SYSDEADCHAR or
        WindowMessage.WM_UNICHAR or
        WindowMessage.WM_IME_SETCONTEXT or WindowMessage.WM_IME_NOTIFY or
        WindowMessage.WM_IME_CONTROL or WindowMessage.WM_IME_COMPOSITION or
        WindowMessage.WM_IME_CHAR or WindowMessage.WM_IME_STARTCOMPOSITION or
        WindowMessage.WM_IME_ENDCOMPOSITION or WindowMessage.WM_IME_COMPOSITIONFULL or
        WindowMessage.WM_IME_SELECT or WindowMessage.WM_IME_REQUEST or
        WindowMessage.WM_IME_KEYDOWN or WindowMessage.WM_IME_KEYUP => true,
        _ => false
    };

    private static bool IsImeSystemMessage(WindowMessage msg) => msg switch
    {
        WindowMessage.WM_IME_SETCONTEXT or
        WindowMessage.WM_IME_NOTIFY or
        WindowMessage.WM_IME_CONTROL or
        WindowMessage.WM_IME_COMPOSITION or
        WindowMessage.WM_IME_STARTCOMPOSITION or
        WindowMessage.WM_IME_ENDCOMPOSITION or
        WindowMessage.WM_IME_COMPOSITIONFULL or
        WindowMessage.WM_IME_SELECT or
        WindowMessage.WM_IME_REQUEST => true,
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
                CaptureDepressedKeys();
                User32.ClipCursor(IntPtr.Zero);

                // INP-09: Dispatch Unity cursor API mutations to the main scripting thread
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
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
                    catch (Exception ex)
                    {
                        Log.Warning($"[InputManager] Error applying cursor state on main thread: {ex.Message}");
                    }
                });
            }
            else
            {
                // INP-07: Release mouse capture if held mid-drag
                if (User32.GetCapture() != IntPtr.Zero)
                {
                    User32.ReleaseCapture();
                }

                // INP-02: Synthesize release events for keys/buttons still marked down
                ReleaseRemainingDepressedKeys(_windowHandle);

                // INP-09: Dispatch Unity cursor API mutations to the main scripting thread
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
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
                    catch (Exception ex)
                    {
                        Log.Warning($"[InputManager] Error restoring cursor state on main thread: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputManager] Error in OnCursorVisibilityToggled: {ex.Message}");
        }
    }

    internal static void EnforceCursorStateOnMainThread()
    {
        if (!DearImGuiInjectionPlus.IsCursorVisible)
            return;

        try
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

            // INP-09: Continuously unclip cursor in case game enforced confinement via native ClipCursor
            User32.ClipCursor(IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputManager] Error in EnforceCursorStateOnMainThread: {ex.Message}");
        }
    }
}
