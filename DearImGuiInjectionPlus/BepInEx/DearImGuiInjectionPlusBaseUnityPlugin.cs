using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjectionPlus.Patches;
using DearImGuiInjectionPlus.Windows;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RendererFinder;
using RendererFinder.Renderers;
using UnityEngine;
using PluginLog = DearImGuiInjectionPlus.Log;

namespace DearImGuiInjectionPlus.BepInEx;

[BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version)]
public class DearImGuiInjectionPlusBaseUnityPlugin : BasePlugin
{
    private static Harmony _harmony;
    private static GameObject _runnerGo;
    private static ConfigEntry<bool> _blockAllInputsConfig;
    private ConfigFile _customConfigFile;

    public override void Load()
    {
        PluginLog.Init(new BepInExLog(base.Log));
        PluginLog.Info($"[DearImGuiInjectionPlus] Starting plugin bootstrap v{Metadata.Version} by {Metadata.Author}");

        var deviceType = SystemInfo.graphicsDeviceType;
        PluginLog.Info($"[DearImGuiInjectionPlus] Unity Engine Graphics Backend: {deviceType}");
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
            PluginLog.Error($"[DearImGuiInjectionPlus] Unsupported graphics backend: {deviceType}. Aborting initialization.");
            return;
        }

        var pluginInfo = IL2CPPChainloader.Instance?.Plugins != null && IL2CPPChainloader.Instance.Plugins.TryGetValue(Metadata.GUID, out var info)
            ? info
            : null;
        var metadata = pluginInfo?.Metadata ?? new BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version);
        var pluginDir = pluginInfo?.Location != null
            ? Path.GetDirectoryName(pluginInfo.Location)
            : Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        PreloadNativeLibraries(pluginDir);

        // Ensure config is strictly saved to DearImGuiInjectionPlus.cfg
        var configFilePath = Path.Combine(Paths.ConfigPath, "DearImGuiInjectionPlus.cfg");
        _customConfigFile = new ConfigFile(configFilePath, true, metadata);

        var cursorVisibilityConfig = new BepInExConfigEntry<VirtualKey>(
            _customConfigFile.Bind(
                "Keybinds",
                "CursorVisibility",
                DearImGuiInjectionPlus.CursorVisibilityToggleDefault,
                "Key for switching the Dear ImGui cursor and menu visibility."));

        _blockAllInputsConfig = _customConfigFile.Bind(
            "InputBlocking",
            "BlockAllInputsWhenOpen",
            true,
            "Whether all game mouse clicks and keyboard inputs are swallowed while the menu is open.");

        DearImGuiInjectionPlus.BlockAllInputsWhenOpen = _blockAllInputsConfig.Value;
        _blockAllInputsConfig.SettingChanged += OnBlockAllInputsSettingChanged;

        var assetsFolder = Path.Combine(pluginDir, "Assets");
        if (!Directory.Exists(assetsFolder))
        {
            var fallback = Path.Combine(Paths.PluginPath, "DearImGuiInjectionPlus", "Assets");
            if (Directory.Exists(fallback))
            {
                assetsFolder = fallback;
            }
        }

        DearImGuiInjectionPlus.Init(Paths.ConfigPath, assetsFolder, cursorVisibilityConfig);

        try
        {
            _harmony = new Harmony(Metadata.GUID);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            PluginLog.Info("[DearImGuiInjectionPlus] Successfully applied Harmony patches for cursor management.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[DearImGuiInjectionPlus] Failed to apply Harmony patches: {ex}");
        }

        // Register internal MonoBehaviour types prior to adding components
        ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>();
        ClassInjector.RegisterTypeInIl2Cpp<DearImGuiInjectionPlusRunner>();

        // Spawn a dedicated GameObject marked with Object.DontDestroyOnLoad to host frame tick operations
        _runnerGo = new GameObject("DearImGuiInjectionPlus_Runner");
        _runnerGo.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(_runnerGo);

        _runnerGo.AddComponent<UnityMainThreadDispatcher>();
        _runnerGo.AddComponent<DearImGuiInjectionPlusRunner>();

        PluginLog.Info("[DearImGuiInjectionPlus] Bootstrap complete.");
    }

    private static void OnBlockAllInputsSettingChanged(object sender, EventArgs args)
    {
        if (_blockAllInputsConfig == null)
            return;

        DearImGuiInjectionPlus.BlockAllInputsWhenOpen = _blockAllInputsConfig.Value;
        if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            NewInputSystemBlocker.RestoreDevicesIfNeeded();
        }
    }

    private void PreloadNativeLibraries(string pluginDir)
    {
        try
        {
            var searchPaths = new[]
            {
                Path.Combine(pluginDir, "cimgui.dll"),
                Path.Combine(pluginDir, "libs", "cimgui", "win-x64", "cimgui.dll"),
                Path.Combine(Paths.PluginPath, "DearImGuiInjectionPlus", "cimgui.dll"),
                Path.Combine(Paths.PluginPath, "DearImGuiInjectionPlus", "libs", "cimgui", "win-x64", "cimgui.dll"),
                Path.Combine(Paths.BepInExRootPath, "plugins", "DearImGuiInjectionPlus", "cimgui.dll"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    var handle = Kernel32.LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        PluginLog.Info($"[DearImGuiInjectionPlus] Preloaded native cimgui.dll from: {path}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[DearImGuiInjectionPlus] PreloadNativeLibraries exception: {ex.Message}");
        }
    }

    public override bool Unload()
    {
        Shutdown();
        return true;
    }

    internal static void Shutdown()
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
            return;

        PluginLog.Info("[DearImGuiInjectionPlus] Engine shutdown initiated...");
        RendererFinder.RendererFinder.IsShuttingDown = true;

        try
        {
            NewInputSystemBlocker.SetBlocked(false);
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[DearImGuiInjectionPlus] Error unblocking NewInputSystem on shutdown: {ex.Message}");
        }

        try
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[DearImGuiInjectionPlus] Error unpatching Harmony: {ex.Message}");
        }

        // LC-02 / TH-01: Invert teardown order for unmanaged safety
        // 1. Signal the frame gate to drain in-flight rendering frames
        if (!FrameGate.CloseAndDrain(1000))
        {
            PluginLog.Warning("[DearImGuiInjectionPlus] Teardown drain timed out; skipping destruction to avoid use-after-free crash.");
            return;
        }

        // 2. Disable and deactivate all native hooks
        RendererFinder.RendererFinder.Dispose();

        // 3. Restore original window procedures
        InputManager.Unsubclass();

        // 4. Wait for GPU idle & 5. Destroy graphics resources and ImGui context
        DearImGuiInjectionPlus.Dispose();

        if (_runnerGo != null)
        {
            var go = _runnerGo;
            _runnerGo = null;
            try
            {
                UnityEngine.Object.Destroy(go);
            }
            catch
            {
                // Ignored during engine exit
            }
        }

        PluginLog.Info("[DearImGuiInjectionPlus] Plugin shutdown completed safely.");
    }
}
