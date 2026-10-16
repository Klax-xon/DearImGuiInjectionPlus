using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus.Backends;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;
using RendererFinder.Renderers;

namespace DearImGuiInjectionPlus;

public static class DearImGuiInjectionPlus
{
    private static volatile bool _initialized;

    /// <summary>
    /// True if the injection and renderer backend have been initialized, else false.
    /// </summary>
    public static bool Initialized
    {
        get => _initialized;
        internal set => _initialized = value;
    }

    public static IntPtr Context { get; internal set; }

    public static ImGuiIOPtr IO { get; internal set; }

    public static string ImGuiIniConfigPath { get; private set; }
    private const string IniFileName = "DearImGuiInjectionPlus_imgui.ini";

    public static string AssetsFolderPath { get; private set; }

    private static volatile bool _isCursorVisible;

    /// <summary>
    /// True if the Dear ImGui GUI cursor / overlay is visible.
    /// </summary>
    public static bool IsCursorVisible
    {
        get => _isCursorVisible;
        internal set => _isCursorVisible = value;
    }

    private static volatile bool _wantCaptureMouse;
    private static volatile bool _wantCaptureKeyboard;

    /// <summary>
    /// True if Dear ImGui wants to capture mouse input (published safely by the render thread after each frame).
    /// </summary>
    public static bool WantCaptureMouse => _wantCaptureMouse;

    /// <summary>
    /// True if Dear ImGui wants to capture keyboard input (published safely by the render thread after each frame).
    /// </summary>
    public static bool WantCaptureKeyboard => _wantCaptureKeyboard;

    /// <summary>
    /// Key for switching the cursor visibility. Defaults to Insert.
    /// </summary>
    public static IConfigEntry<VirtualKey> CursorVisibilityToggle { get; internal set; }
    public const VirtualKey CursorVisibilityToggleDefault = VirtualKey.Insert;

    /// <summary>
    /// If true, blocks all game inputs (mouse clicks, movement, keystrokes) while the menu is open.
    /// </summary>
    public static bool BlockAllInputsWhenOpen { get; set; } = true;

    public static ImGuiStyle Style { get; private set; }

    /// <summary>
    /// User/mod supplied function to render Dear ImGui UI.
    /// </summary>
    public static event Action Render
    {
        add
        {
            lock (_renderLock)
            {
                _renderAction += value;
                UpdateRenderSnapshot();
            }
        }
        remove
        {
            lock (_renderLock)
            {
                _renderAction -= value;
                UpdateRenderSnapshot();
            }
        }
    }
    private static Action _renderAction;
    private static Action[] _renderSnapshot = Array.Empty<Action>();
    private static readonly object _renderLock = new();

    // Extensible Modular Window Registry
    private static readonly List<IImGuiWindow> _registeredWindows = new();
    private static IImGuiWindow[] _windowsSnapshot = Array.Empty<IImGuiWindow>();
    private static readonly object _windowLock = new();

    // Tab Registry for Menu Bar navigation
    public sealed class RegisteredTab
    {
        public string Category { get; set; }
        public string Title { get; set; }
        public Action Callback { get; set; }
    }
    private static readonly List<RegisteredTab> _registeredTabs = new();
    private static RegisteredTab[] _tabsSnapshot = Array.Empty<RegisteredTab>();
    private static readonly object _tabLock = new();

    /// <summary>
    /// Registers a modular ImGui window.
    /// </summary>
    public static void RegisterWindow(IImGuiWindow window)
    {
        if (window == null)
            return;

        lock (_windowLock)
        {
            if (!_registeredWindows.Contains(window))
            {
                _registeredWindows.Add(window);
                _windowsSnapshot = _registeredWindows.ToArray();
            }
        }
    }

    /// <summary>
    /// Unregisters a modular ImGui window.
    /// </summary>
    public static void UnregisterWindow(IImGuiWindow window)
    {
        if (window == null)
            return;

        lock (_windowLock)
        {
            if (_registeredWindows.Remove(window))
            {
                _windowsSnapshot = _registeredWindows.ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a menu bar tab item under a specified top-level menu bar category.
    /// </summary>
    public static void RegisterTab(string category, string title, Action callback)
    {
        if (callback == null)
            return;

        lock (_tabLock)
        {
            _registeredTabs.Add(new RegisteredTab
            {
                Category = category ?? "Mods",
                Title = title ?? "Untitled",
                Callback = callback
            });
            _tabsSnapshot = _registeredTabs.ToArray();
        }
    }

    private static void UpdateRenderSnapshot()
    {
        if (_renderAction == null)
        {
            _renderSnapshot = Array.Empty<Action>();
            return;
        }

        var list = _renderAction.GetInvocationList();
        var arr = new Action[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            arr[i] = (Action)list[i];
        }
        _renderSnapshot = arr;
    }

    internal static void Init(string imguiIniConfigDirectoryPath, string assetsFolder, IConfigEntry<VirtualKey> cursorVisibilityConfig)
    {
        ImGuiIniConfigPath = Path.Combine(imguiIniConfigDirectoryPath, IniFileName);
        AssetsFolderPath = assetsFolder;
        CursorVisibilityToggle = cursorVisibilityConfig;

        // Ensure TargetRendererKind is set if not already set by BepInEx plugin
        if (RendererFinder.RendererFinder.TargetRendererKind == RendererKind.None)
        {
            try
            {
                var deviceType = UnityEngine.SystemInfo.graphicsDeviceType;
                if (deviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                {
                    RendererFinder.RendererFinder.TargetRendererKind = RendererKind.D3D11;
                }
                else if (deviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                {
                    RendererFinder.RendererFinder.TargetRendererKind = RendererKind.D3D12;
                }
                else
                {
                    Log.Error($"[DearImGuiInjectionPlus] Unsupported graphics backend: {deviceType}. Aborting initialization.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DearImGuiInjectionPlus] Failed to determine graphics backend: {ex.Message}. Aborting initialization.");
                return;
            }
        }

        if (RendererFinder.RendererFinder.TargetRendererKind == RendererKind.None)
        {
            Log.Error("[DearImGuiInjectionPlus] TargetRendererKind is None. Aborting initialization.");
            return;
        }

        RendererFinder.RendererFinder.OnRendererDetected += OnRendererDetected;

        RendererFinder.RendererFinder.Init();
    }

    private static void OnRendererDetected(RendererKind kind, IntPtr swapChainPtr)
    {
        Log.Info($"[DearImGuiInjectionPlus] OnRendererDetected: {kind} on SwapChain {swapChainPtr:X8}");
        InitImplementationFromRendererKind(kind, swapChainPtr);
    }

    internal static unsafe void InitImGui()
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            Context = ImGui.CreateContext(null);
            Log.Info($"[DearImGuiInjectionPlus] Created ImGui context: {Context:X8}");
        }
        else
        {
            Context = ImGui.GetCurrentContext();
        }

        IO = ImGui.GetIO();
        IO.MouseDrawCursor = false;
        IO.ConfigFlags &= ~ImGuiConfigFlags.NavEnableSetMousePos;
        IO.WantSetMousePos = false;
        IO.ConfigFlags |= ImGuiConfigFlags.NoMouse;

        IO.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        if (IO.NativePtr != null && !string.IsNullOrEmpty(ImGuiIniConfigPath) && IO.NativePtr->IniFilename == null)
        {
            IO.NativePtr->IniFilename = (byte*)Marshal.StringToHGlobalAnsi(ImGuiIniConfigPath);
        }

        DearImGuiTheme.Init();
        Log.Info("[DearImGuiInjectionPlus] ImGui context and IO initialized.");
    }

    internal static unsafe void Dispose()
    {
        RendererFinder.RendererFinder.OnRendererDetected -= OnRendererDetected;

        if (Initialized)
        {
            // Restore window procedure before disposing graphics backends/context
            InputManager.Unsubclass();
            _wantCaptureMouse = false;
            _wantCaptureKeyboard = false;

            var kind = _activeRendererKind != RendererKind.None ? _activeRendererKind : RendererFinder.RendererFinder.RendererKind;
            DisposeImplementationFromRendererKind(kind);
            _activeRendererKind = RendererKind.None;

            lock (_renderLock)
            {
                _renderAction = null;
                _renderSnapshot = Array.Empty<Action>();
            }

            lock (_windowLock)
            {
                _registeredWindows.Clear();
                _windowsSnapshot = Array.Empty<IImGuiWindow>();
            }

            lock (_tabLock)
            {
                _registeredTabs.Clear();
                _tabsSnapshot = Array.Empty<RegisteredTab>();
            }

            if (ImGui.GetIO().NativePtr != null && ImGui.GetIO().NativePtr->IniFilename != null)
            {
                var iniPtr = (IntPtr)ImGui.GetIO().NativePtr->IniFilename;
                try
                {
                    if (!string.IsNullOrEmpty(ImGuiIniConfigPath))
                    {
                        ImGui.SaveIniSettingsToDisk(ImGuiIniConfigPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[DearImGuiInjectionPlus] Error saving ini settings: {ex.Message}");
                }

                ImGui.GetIO().NativePtr->IniFilename = null;

                if (Context != IntPtr.Zero)
                {
                    ImGui.DestroyContext(Context);
                    Context = IntPtr.Zero;
                }

                Marshal.FreeHGlobal(iniPtr);
            }
            else
            {
                if (Context != IntPtr.Zero)
                {
                    ImGui.DestroyContext(Context);
                    Context = IntPtr.Zero;
                }
            }
            IO = null;

            Initialized = false;
        }
    }

    private static bool _implementationInitialized = false;
    private static RendererKind _activeRendererKind = RendererKind.None;

    internal static void InitImplementationFromRendererKind(RendererKind rendererKind, IntPtr swapChainPtr = default)
    {
        if (_implementationInitialized)
            return;

        _activeRendererKind = rendererKind;

        switch (rendererKind)
        {
            case RendererKind.None:
                return;
            case RendererKind.D3D11:
                _implementationInitialized = true;
                InitImGuiDX11();
                break;
            case RendererKind.D3D12:
                _implementationInitialized = true;
                InitImGuiDX12(swapChainPtr);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitImGuiDX11() => ImGuiDX11.Init();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitImGuiDX12(IntPtr swapChainPtr) => ImGuiDX12.Init(swapChainPtr);

    private static void DisposeImplementationFromRendererKind(RendererKind rendererKind)
    {
        _implementationInitialized = false;
        switch (rendererKind)
        {
            case RendererKind.None:
                break;
            case RendererKind.D3D11:
                ImGuiDX11.Dispose();
                break;
            case RendererKind.D3D12:
                ImGuiDX12.Dispose();
                break;
        }
    }

    internal static void ToggleCursor()
    {
        IsCursorVisible = !IsCursorVisible;
        UpdateCursorVisibility();
        InputManager.OnCursorVisibilityToggled(IsCursorVisible);
    }

    public static void ToggleCursorVisibility() => ToggleCursor();

    internal static void UpdateCursorVisibility()
    {
        float mouseX = 0;
        float mouseY = 0;
        if (InputManager.WindowHandle != IntPtr.Zero &&
            User32.GetCursorPos(out var screenPt) &&
            User32.ScreenToClient(InputManager.WindowHandle, ref screenPt))
        {
            mouseX = screenPt.X;
            mouseY = screenPt.Y;
        }

        ImGuiWin32Impl.EnqueueCursorVisibility(IsCursorVisible, mouseX, mouseY);
    }

    /// <summary>
    /// Executes a single Dear ImGui frame with zero allocations and isolated exception boundaries.
    /// </summary>
    internal static unsafe void NewFrame()
    {
        ImGuiWin32Impl.NewFrame();
        ImGui.NewFrame();

        // 1. Render Registered Menu Bar Tabs
        var tabs = _tabsSnapshot;
        if (tabs.Length > 0 && IsCursorVisible)
        {
            if (ImGui.BeginMainMenuBar())
            {
                var categories = new Dictionary<string, List<RegisteredTab>>();
                for (int i = 0; i < tabs.Length; i++)
                {
                    var tab = tabs[i];
                    if (!categories.TryGetValue(tab.Category, out var list))
                    {
                        list = new List<RegisteredTab>();
                        categories[tab.Category] = list;
                    }
                    list.Add(tab);
                }

                foreach (var kvp in categories)
                {
                    if (ImGui.BeginMenu(kvp.Key))
                    {
                        for (int i = 0; i < kvp.Value.Count; i++)
                        {
                            var tab = kvp.Value[i];
                            try
                            {
                                tab.Callback?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[DearImGuiInjectionPlus] Error rendering tab '{tab.Title}': {ex}");
                            }
                        }
                        ImGui.EndMenu();
                    }
                }

                ImGui.EndMainMenuBar();
            }
        }

        // 2. Render Registered Modular Windows
        var windows = _windowsSnapshot;
        for (int i = 0; i < windows.Length; i++)
        {
            var win = windows[i];
            if (win != null && win.IsOpen)
            {
                try
                {
                    win.OnRender();
                }
                catch (Exception ex)
                {
                    Log.Error($"[DearImGuiInjectionPlus] Error rendering window '{win.Title}': {ex}");
                }
            }
        }

        // 3. Render Legacy / Event-based Listeners
        var renderActions = _renderSnapshot;
        for (int i = 0; i < renderActions.Length; i++)
        {
            try
            {
                renderActions[i]?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error($"[DearImGuiInjectionPlus] Error in Render callback: {ex}");
            }
        }

        ImGui.EndFrame();
        ImGui.Render();

        _wantCaptureMouse = IO.NativePtr != null && IO.WantCaptureMouse;
        _wantCaptureKeyboard = IO.NativePtr != null && IO.WantCaptureKeyboard;
    }
}
