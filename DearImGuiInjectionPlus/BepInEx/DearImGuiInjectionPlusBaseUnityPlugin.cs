using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using DearImGuiInjectionPlus.Patches;
using DearImGuiInjectionPlus.Windows;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using RendererFinder;
using RendererFinder.Renderers;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

[BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version)]
public class DearImGuiInjectionPlusBaseUnityPlugin : BaseUnityPlugin
{
    private Hook _eventSystemUpdateHook;
    private Harmony _harmony;
    private ConfigFile _customConfigFile;

    private void Awake()
    {
        Log.Init(new BepInExLog(Logger));
        Log.Info($"[DearImGuiInjectionPlus] Starting plugin bootstrap v{Metadata.Version} by {Metadata.Author}");

        var deviceType = SystemInfo.graphicsDeviceType;
        Log.Info($"[DearImGuiInjectionPlus] Unity Engine Graphics Backend: {deviceType}");
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

        PreloadNativeLibraries();

        // Ensure config is strictly saved to DearImGuiInjectionPlus.cfg
        var configFilePath = Path.Combine(Paths.ConfigPath, "DearImGuiInjectionPlus.cfg");
        _customConfigFile = new ConfigFile(configFilePath, true, Info.Metadata);

        var cursorVisibilityConfig = new BepInExConfigEntry<VirtualKey>(
            _customConfigFile.Bind(
                "Keybinds",
                "CursorVisibility",
                DearImGuiInjectionPlus.CursorVisibilityToggleDefault,
                "Key for switching the Dear ImGui cursor and menu visibility."));

        var blockAllInputsConfig = _customConfigFile.Bind(
            "InputBlocking",
            "BlockAllInputsWhenOpen",
            true,
            "Whether all game mouse clicks and keyboard inputs are swallowed while the menu is open.");

        DearImGuiInjectionPlus.BlockAllInputsWhenOpen = blockAllInputsConfig.Value;

        var assetsFolder = Path.Combine(Path.GetDirectoryName(Info.Location), "Assets");
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
            Log.Info("[DearImGuiInjectionPlus] Successfully applied Harmony patches for cursor management.");
        }
        catch (Exception ex)
        {
            Log.Error($"[DearImGuiInjectionPlus] Failed to apply Harmony patches: {ex}");
        }

        SetupIgnoreUIObjectsWhenImGuiCursorIsVisible();

        gameObject.AddComponent<UnityMainThreadDispatcher>();

        Log.Info("[DearImGuiInjectionPlus] Bootstrap complete.");
    }

    private void PreloadNativeLibraries()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(Info.Location);
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
                        Log.Info($"[DearImGuiInjectionPlus] Preloaded native cimgui.dll from: {path}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[DearImGuiInjectionPlus] PreloadNativeLibraries exception: {ex.Message}");
        }
    }

    private void SetupIgnoreUIObjectsWhenImGuiCursorIsVisible()
    {
        try
        {
            var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var unityEngineUIDll = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(ass => ass.GetName().Name == "UnityEngine.UI");
            if (unityEngineUIDll == null)
            {
                return;
            }

            var eventSystemType = unityEngineUIDll.GetType("UnityEngine.EventSystems.EventSystem");
            if (eventSystemType == null)
            {
                return;
            }

            var eventSystemUpdate = eventSystemType.GetMethod("Update", allFlags);
            if (eventSystemUpdate != null)
            {
                _eventSystemUpdateHook = new Hook(eventSystemUpdate, new Action<Action<object>, object>(IgnoreUIObjectsWhenImGuiCursorIsVisible));
                Log.Info("[DearImGuiInjectionPlus] Successfully hooked UnityEngine.EventSystems.EventSystem.Update");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[DearImGuiInjectionPlus] Could not hook EventSystem.Update (game might not use uGUI): {e.Message}");
        }
    }

    private static void IgnoreUIObjectsWhenImGuiCursorIsVisible(Action<object> orig, object self)
    {
        // Drop uGUI raycasts and event updates whenever Dear ImGui cursor is active
        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            return;
        }

        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.Initialized && DearImGuiInjectionPlus.WantCaptureMouse)
        {
            return;
        }

        orig(self);
    }


    private void OnDestroy()
    {
        Log.Info("[DearImGuiInjectionPlus] Engine shutdown initiated...");
        RendererFinder.RendererFinder.IsShuttingDown = true;

        try
        {
            NewInputSystemBlocker.SetBlocked(false);
        }
        catch (Exception ex)
        {
            Log.Warning($"[DearImGuiInjectionPlus] Error unblocking NewInputSystem on shutdown: {ex.Message}");
        }

        try
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
        catch (Exception ex)
        {
            Log.Warning($"[DearImGuiInjectionPlus] Error unpatching Harmony: {ex.Message}");
        }

        _eventSystemUpdateHook?.Dispose();
        _eventSystemUpdateHook = null;

        // LC-02 / TH-01: Invert teardown order for unmanaged safety
        // 1. Signal the frame gate to drain in-flight rendering frames
        if (!FrameGate.CloseAndDrain(1000))
        {
            Log.Warning("[DearImGuiInjectionPlus] Teardown drain timed out; skipping destruction to avoid use-after-free crash.");
            return;
        }

        // 2. Disable and deactivate all native hooks
        RendererFinder.RendererFinder.Dispose();

        // 3. Restore original window procedures
        InputManager.Unsubclass();

        // 4. Wait for GPU idle & 5. Destroy graphics resources and ImGui context
        DearImGuiInjectionPlus.Dispose();

        Log.Info("[DearImGuiInjectionPlus] Plugin shutdown completed safely.");
    }
}
