using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Template.State;
using DearImGuiInjectionPlus.Template.Utils;
using Il2CppInterop.Runtime.Injection;
using ImGuiNET;
using UnityEngine;
using PluginLog = DearImGuiInjectionPlus.Template.Log;

namespace DearImGuiInjectionPlus.Template;

/// <summary>
/// BepInEx 6 IL2CPP plugin entry point for the Base GUI template.
/// Manages configuration bindings, lifecycle teardown, window registration,
/// and IL2CPP runner injection.
/// </summary>
[BepInDependency(global::DearImGuiInjectionPlus.Metadata.GUID)]
[BepInPlugin("com.template.basegui", "DearImGuiInjectionPlus.Template", "1.0.0")]
public class BaseGuiPlugin : BasePlugin
{
    public static BaseGuiPlugin Instance { get; private set; }

    // Configuration Entries
    public static ConfigEntry<KeyCode> ToggleKeyConfig { get; private set; }
    public static ConfigEntry<bool> AutoManageCursorConfig { get; private set; }
    public static ConfigEntry<GuiTheme> SelectedThemeConfig { get; private set; }
    public static ConfigEntry<float> UiScaleConfig { get; private set; }

    // State Pipelines & Model
    public StructSnapshotPipeline<GameTelemetrySnapshot> TelemetryPipeline { get; private set; }
    public PlayerControlState ControlState { get; private set; }
    public GuiConsole Console { get; private set; }
    public BaseGuiWindow Window { get; private set; }

    private GameObject _runnerGo;
    private bool _isTornDown;
    private bool _initialStyleApplied;

    public override void Load()
    {
        Instance = this;
        PluginLog.Init(base.Log);
        PluginLog.Info("[BaseGuiPlugin] Starting Base GUI Template plugin bootstrap...");

        // 1. Bind configuration entries - default to Insert for unified framework toggle
        ToggleKeyConfig = Config.Bind(
            "Keybinds",
            "ToggleKey",
            KeyCode.Insert,
            "Keyboard key used to open and close the Base GUI menu (defaults to Insert).");

        // Force-sanitize: if config had F1, migrate immediately to Insert
        if (ToggleKeyConfig.Value == KeyCode.F1)
        {
            ToggleKeyConfig.Value = KeyCode.Insert;
        }

        AutoManageCursorConfig = Config.Bind(
            "Input",
            "AutoManageCursor",
            true,
            "Whether opening or closing the window automatically toggles Dear ImGui cursor visibility and input blocking.");

        SelectedThemeConfig = Config.Bind(
            "Theme",
            "SelectedTheme",
            GuiTheme.ModernDark,
            "Active visual theme (0: ModernDark, 1: HighContrastEmerald, 2: ClassicImGui).");

        UiScaleConfig = Config.Bind(
            "Display",
            "UiScale",
            1.0f,
            "Global UI and font scaling multiplier.");

        // 2. Initialize state pipelines and console
        TelemetryPipeline = new StructSnapshotPipeline<GameTelemetrySnapshot>(GameTelemetrySnapshot.CreateDefault());
        ControlState = new PlayerControlState();
        Console = new GuiConsole();

        // 3. Create primary window
        Window = new BaseGuiWindow(
            TelemetryPipeline,
            ControlState,
            Console,
            onThemeChanged: SaveThemePreference,
            onScaleChanged: SaveScalePreference);

        // 4. Register window (Standalone overlay - no persistent top menu bar)
        global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(Window);

        // 5. Apply theme on first ImGui render
        global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render += ApplyInitialStyle;

        // 6. Register and spawn IL2CPP MonoBehaviour Runner
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<BaseGuiRunner>())
        {
            ClassInjector.RegisterTypeInIl2Cpp<BaseGuiRunner>();
        }

        SpawnRunner();

        PluginLog.Info("[BaseGuiPlugin] Base GUI Template loaded successfully. Press [Insert] to open UI.");
    }

    private void ApplyInitialStyle()
    {
        if (_initialStyleApplied)
            return;

        try
        {
            ThemeManager.ApplyTheme(SelectedThemeConfig.Value);
            ImGui.GetIO().FontGlobalScale = UiScaleConfig.Value;
            _initialStyleApplied = true;
            PluginLog.Info($"[BaseGuiPlugin] Applied theme '{SelectedThemeConfig.Value}' and scale '{UiScaleConfig.Value:F2}x'.");
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[BaseGuiPlugin] Error applying initial style: {ex.Message}");
        }
        finally
        {
            global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render -= ApplyInitialStyle;
        }
    }

    internal void SaveThemePreference(GuiTheme theme)
    {
        if (SelectedThemeConfig != null)
        {
            SelectedThemeConfig.Value = theme;
        }
    }

    internal void SaveScalePreference(float scale)
    {
        if (UiScaleConfig != null)
        {
            UiScaleConfig.Value = scale;
        }
    }

    private void SpawnRunner()
    {
        try
        {
            _runnerGo = new GameObject("BaseGuiPlugin_Runner");
            _runnerGo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_runnerGo);
            _runnerGo.AddComponent<BaseGuiRunner>();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[BaseGuiPlugin] Failed to spawn BaseGuiRunner: {ex.Message}");
        }
    }

    internal void EnsureRunnerAlive()
    {
        if (_isTornDown || BaseGuiRunner._isQuitting)
            return;

        try
        {
            if (_runnerGo == null)
            {
                SpawnRunner();
                PluginLog.Info("[BaseGuiPlugin] Successfully respawned BaseGuiRunner GameObject.");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[BaseGuiPlugin] Could not respawn runner: {ex.Message}");
        }
    }

    public override bool Unload()
    {
        Teardown();
        return true;
    }

    public void Teardown()
    {
        if (_isTornDown)
            return;

        _isTornDown = true;
        PluginLog.Info("[BaseGuiPlugin] Performing clean teardown...");

        global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render -= ApplyInitialStyle;

        if (Window != null)
        {
            global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(Window);
            Window.IsOpen = false;
        }

        if (_runnerGo != null)
        {
            try
            {
                UnityEngine.Object.Destroy(_runnerGo);
            }
            catch
            {
                // Ignored during engine exit
            }
            _runnerGo = null;
        }

        PluginLog.Info("[BaseGuiPlugin] Teardown complete.");
    }
}
