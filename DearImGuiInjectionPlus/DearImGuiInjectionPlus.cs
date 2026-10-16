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
    private static IntPtr _iniFilenamePtr;

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
    public sealed class RegisteredTab : IDisposable
    {
        public string Category { get; set; }
        public string Title { get; set; }
        public Action Callback { get; set; }

        public void Dispose()
        {
            UnregisterTab(this);
        }
    }
    private struct TabCategory
    {
        public string Name;
        public RegisteredTab[] Tabs;
    }
    private static readonly List<RegisteredTab> _registeredTabs = new();
    private static RegisteredTab[] _tabsSnapshot = Array.Empty<RegisteredTab>();
    private static TabCategory[] _tabCategoriesSnapshot = Array.Empty<TabCategory>();
    private static readonly object _tabLock = new();

    private static void RebuildTabCategories_NoLock()
    {
        _tabsSnapshot = _registeredTabs.ToArray();
        if (_registeredTabs.Count == 0)
        {
            _tabCategoriesSnapshot = Array.Empty<TabCategory>();
            return;
        }

        var categories = new Dictionary<string, List<RegisteredTab>>(StringComparer.Ordinal);
        for (int i = 0; i < _registeredTabs.Count; i++)
        {
            var tab = _registeredTabs[i];
            if (!categories.TryGetValue(tab.Category, out var list))
            {
                list = new List<RegisteredTab>();
                categories[tab.Category] = list;
            }
            list.Add(tab);
        }

        var result = new TabCategory[categories.Count];
        int idx = 0;
        foreach (var kvp in categories)
        {
            result[idx++] = new TabCategory
            {
                Name = kvp.Key,
                Tabs = kvp.Value.ToArray()
            };
        }
        _tabCategoriesSnapshot = result;
    }

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
    /// Returns an <see cref="IDisposable"/> token that unregisters the tab when disposed.
    /// </summary>
    public static IDisposable RegisterTab(string category, string title, Action callback)
    {
        if (callback == null)
            return null;

        string cat = category ?? "Mods";
        string tit = title ?? "Untitled";

        var tab = new RegisteredTab
        {
            Category = cat,
            Title = tit,
            Callback = callback
        };

        lock (_tabLock)
        {
            // Remove any existing tab with the same category and title to prevent duplicate UI entries
            _registeredTabs.RemoveAll(t => string.Equals(t.Category, cat, StringComparison.Ordinal) &&
                                           string.Equals(t.Title, tit, StringComparison.Ordinal));
            _registeredTabs.Add(tab);
            RebuildTabCategories_NoLock();
        }

        return tab;
    }

    /// <summary>
    /// Unregisters all menu bar tab items matching the specified category and title.
    /// </summary>
    public static bool UnregisterTab(string category, string title)
    {
        string cat = category ?? "Mods";
        string tit = title ?? "Untitled";

        lock (_tabLock)
        {
            int removed = _registeredTabs.RemoveAll(t => string.Equals(t.Category, cat, StringComparison.Ordinal) &&
                                                         string.Equals(t.Title, tit, StringComparison.Ordinal));
            if (removed > 0)
            {
                RebuildTabCategories_NoLock();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Unregisters a previously registered menu bar tab item.
    /// </summary>
    public static bool UnregisterTab(RegisteredTab tab)
    {
        if (tab == null)
            return false;

        lock (_tabLock)
        {
            if (_registeredTabs.Remove(tab))
            {
                RebuildTabCategories_NoLock();
                return true;
            }

            return false;
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
        bool isNewContext = false;
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            Context = ImGui.CreateContext(null);
            Log.Info($"[DearImGuiInjectionPlus] Created ImGui context: {Context:X8}");
            isNewContext = true;
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

        if (_iniFilenamePtr == IntPtr.Zero && !string.IsNullOrEmpty(ImGuiIniConfigPath))
        {
            _iniFilenamePtr = Marshal.StringToHGlobalAnsi(ImGuiIniConfigPath);
        }

        if (IO.NativePtr != null && _iniFilenamePtr != IntPtr.Zero)
        {
            IO.NativePtr->IniFilename = (byte*)_iniFilenamePtr;
        }

        if (isNewContext)
        {
            DearImGuiTheme.Init();
            Log.Info("[DearImGuiInjectionPlus] ImGui context and IO initialized.");
        }
    }

    internal static unsafe void Dispose()
    {
        RendererFinder.RendererFinder.OnRendererDetected -= OnRendererDetected;

        // Restore window procedure before disposing graphics backends/context
        InputManager.Unsubclass();
        _wantCaptureMouse = false;
        _wantCaptureKeyboard = false;

        var kind = _activeRendererKind != RendererKind.None ? _activeRendererKind : RendererFinder.RendererFinder.RendererKind;
        DisposeImplementationFromRendererKind(kind);
        _activeRendererKind = RendererKind.None;
        _implementationInitialized = false;

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
            RebuildTabCategories_NoLock();
        }

        if (Context != IntPtr.Zero)
        {
            if (ImGui.GetCurrentContext() != IntPtr.Zero)
            {
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

                var io = ImGui.GetIO();
                if (io.NativePtr != null)
                {
                    io.NativePtr->IniFilename = null;
                }
            }

            ImGui.DestroyContext(Context);
            Context = IntPtr.Zero;
        }

        if (_iniFilenamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_iniFilenamePtr);
            _iniFilenamePtr = IntPtr.Zero;
        }

        IO = null;
        Initialized = false;
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
        var categories = _tabCategoriesSnapshot;
        if (categories.Length > 0 && IsCursorVisible)
        {
            if (ImGui.BeginMainMenuBar())
            {
                for (int c = 0; c < categories.Length; c++)
                {
                    ref readonly var category = ref categories[c];
                    if (ImGui.BeginMenu(category.Name))
                    {
                        var catTabs = category.Tabs;
                        for (int i = 0; i < catTabs.Length; i++)
                        {
                            var tab = catTabs[i];
                            try
                            {
                                tab.Callback?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[DearImGuiInjectionPlus] Error rendering tab '{tab.Title}': {ex}");
                                RestoreImGuiWindowStack();
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
                    RestoreImGuiStack();
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
                RestoreImGuiStack();
            }
        }

        // Safety net: ensure any unclosed windows or unbalanced stacks are recovered before EndFrame/Render
        RestoreImGuiStack();

        ImGui.EndFrame();
        ImGui.Render();

        _wantCaptureMouse = IO.NativePtr != null && IO.WantCaptureMouse;
        _wantCaptureKeyboard = IO.NativePtr != null && IO.WantCaptureKeyboard;
    }

    private static class Native
    {
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igErrorCheckEndFrameRecover")]
        public static extern void ErrorCheckEndFrameRecover(IntPtr logCallback, IntPtr userData);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igErrorCheckEndWindowRecover")]
        public static extern void ErrorCheckEndWindowRecover(IntPtr logCallback, IntPtr userData);
    }

    /// <summary>
    /// Safely unwinds any dangling ImGui window and internal state stacks back to baseline.
    /// </summary>
    public static void RestoreImGuiStack()
    {
        try
        {
            Native.ErrorCheckEndFrameRecover(IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error($"[DearImGuiInjectionPlus] Error during ImGui stack recovery: {ex}");
        }
    }

    /// <summary>
    /// Safely unwinds any dangling ImGui state stacks on the current window back to its begin state.
    /// </summary>
    public static void RestoreImGuiWindowStack()
    {
        try
        {
            Native.ErrorCheckEndWindowRecover(IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error($"[DearImGuiInjectionPlus] Error during ImGui window stack recovery: {ex}");
        }
    }
}
