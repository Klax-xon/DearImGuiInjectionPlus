using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjectionPlus.Patches;
using DearImGuiInjectionPlus.Windows;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RendererFinder;
using RendererFinder.Renderers;
using UnityEngine;
using PluginLog = DearImGuiInjectionPlus.Log;

namespace DearImGuiInjectionPlus.BepInEx;

[BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version)]
public class DearImGuiInjectionPlusBaseUnityPlugin : BasePlugin
{
    public static int HarmonyPatchesApplied { get; private set; }
    public static int HarmonyPatchesSkipped { get; private set; }

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

        // 1. Register and spawn internal MonoBehaviour types BEFORE activating native Present hooks (Fixes AUD-06)
        UnityMainThreadDispatcher.Register();
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<DearImGuiInjectionPlusRunner>())
        {
            ClassInjector.RegisterTypeInIl2Cpp<DearImGuiInjectionPlusRunner>();
        }

        // Use HideAndDontSave + DontDestroyOnLoad so Unity does not destroy the runner during preloader or scene transitions
        _runnerGo = new GameObject("DearImGuiInjectionPlus_Runner");
        _runnerGo.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(_runnerGo);

        _runnerGo.AddComponent<UnityMainThreadDispatcher>();
        _runnerGo.AddComponent<DearImGuiInjectionPlusRunner>();

        // 2. Apply Harmony patches with per-method isolation against IL2CPP UnityLinker stripping (Fixes AUD-01)
        ApplyHarmonyPatchesSafely();

        // 3. Initialize native SwapChain hooks after dispatcher and patches are ready
        DearImGuiInjectionPlus.Init(Paths.ConfigPath, assetsFolder, cursorVisibilityConfig);

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

    internal static void EnsureRunnerAlive()
    {
        if (RendererFinder.RendererFinder.IsShuttingDown || DearImGuiInjectionPlusRunner._isQuitting)
            return;

        try
        {
            if (_runnerGo == null)
            {
                _runnerGo = new GameObject("DearImGuiInjectionPlus_Runner");
                _runnerGo.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_runnerGo);

                _runnerGo.AddComponent<UnityMainThreadDispatcher>();
                _runnerGo.AddComponent<DearImGuiInjectionPlusRunner>();
                PluginLog.Info("[DearImGuiInjectionPlus] Successfully respawned DearImGuiInjectionPlus_Runner.");
            }
            else
            {
                if (!_runnerGo.GetComponent<UnityMainThreadDispatcher>())
                {
                    _runnerGo.AddComponent<UnityMainThreadDispatcher>();
                }
                if (!_runnerGo.GetComponent<DearImGuiInjectionPlusRunner>())
                {
                    _runnerGo.AddComponent<DearImGuiInjectionPlusRunner>();
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to respawn runner: {ex.Message}");
        }
    }

    private static bool IsPatchSignatureCompatible(MethodBase targetMethod, MethodInfo patchMethod, out string reason)
    {
        reason = null;
        if (targetMethod == null || patchMethod == null)
        {
            reason = "null method reference";
            return false;
        }

        var targetParams = targetMethod.GetParameters();
        var patchParams = patchMethod.GetParameters();

        foreach (var p in patchParams)
        {
            if (p.Name == "__result" && targetMethod is MethodInfo mi)
            {
                var expectedType = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (mi.ReturnType != expectedType)
                {
                    reason = $"Return type mismatch: Target returns {mi.ReturnType.Name}, patch expects {expectedType?.Name}";
                    return false;
                }
            }
            else if (p.Name != null && p.Name.StartsWith("__") && int.TryParse(p.Name.Substring(2), out int argIdx))
            {
                if (argIdx >= targetParams.Length)
                {
                    reason = $"Parameter index {argIdx} exceeds target parameter count {targetParams.Length}";
                    return false;
                }
            }
        }

        return true;
    }

    private static string FormatMethodSignature(MethodBase mb)
    {
        if (mb == null) return "unknown";
        string typeName = mb.DeclaringType?.FullName ?? "Unknown";
        var pars = string.Join(", ", mb.GetParameters().Select(p =>
        {
            var pt = p.ParameterType;
            if (pt == typeof(string)) return "string";
            if (pt == typeof(int)) return "int";
            if (pt == typeof(bool)) return "bool";
            if (pt == typeof(float)) return "float";
            return pt.Name;
        }));
        return $"{typeName}.{mb.Name}({pars})";
    }

    private static IntPtr GetNativeMethodPointer(MethodBase targetMethod)
    {
        if (targetMethod == null) return IntPtr.Zero;
        try
        {
            var nativePtrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(targetMethod);
            if (nativePtrField == null) return IntPtr.Zero;
            var methodInfoPtr = (IntPtr)nativePtrField.GetValue(null);
            if (methodInfoPtr == IntPtr.Zero) return IntPtr.Zero;
            return Marshal.ReadIntPtr(methodInfoPtr);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void ApplyHarmonyPatchesSafely()
    {
        _harmony = new Harmony(Metadata.GUID);
        int applied = 0;
        int skipped = 0;
        const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // 1. Detour underlying UnityPlayer.dll ICalls FIRST before applying any Harmony patches.
        // This ensures all native function pointers are resolved and registered so Harmony
        // never attempts to create a double detour on the same native addresses.
        UnityInputBlocker.ApplyNativeICallHooks(ref applied, ref skipped);

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            var classPatchAttrs = type.GetCustomAttributes<HarmonyPatch>(false).ToArray();
            if (classPatchAttrs.Length == 0)
                continue;

            // Patch class-level [HarmonyPatch] types (CursorLockStatePatch, CursorVisiblePatch, EventSystemBlocker)
            if (type != typeof(UnityInputBlocker))
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    applied++;
                    PluginLog.Info($"[DearImGuiInjectionPlus] Applied class patch: {type.FullName}");
                }
                catch (Exception ex)
                {
                    skipped++;
                    PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped patch class {type.Name} (method likely stripped in IL2CPP): {ex.Message}");
                }
                continue;
            }

            var classDeclaringType = classPatchAttrs.Select(a => a.info?.declaringType).FirstOrDefault(t => t != null);
            var patchGroups = new Dictionary<MethodBase, (HarmonyMethod Prefix, HarmonyMethod Postfix)>();

            // Patch UnityInputBlocker methods individually so a single stripped Input method does not abort the rest
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var patchAttrs = method.GetCustomAttributes<HarmonyPatch>(false);
                foreach (var attr in patchAttrs)
                {
                    try
                    {
                        var targetType = attr.info.declaringType ?? classDeclaringType;
                        var methodName = attr.info.methodName;
                        var methodType = attr.info.methodType ?? MethodType.Normal;
                        var argTypes = attr.info.argumentTypes;

                        if (targetType == null || string.IsNullOrEmpty(methodName))
                        {
                            skipped++;
                            PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped patch {method.Name}: targetType or methodName is null");
                            continue;
                        }

                        MethodBase targetMethod = null;
                        if (methodType == MethodType.Getter)
                        {
                            targetMethod = argTypes != null
                                ? targetType.GetProperty(methodName, AllFlags, null, null, argTypes, null)?.GetGetMethod(true)
                                : targetType.GetProperty(methodName, AllFlags)?.GetGetMethod(true);
                        }
                        else if (methodType == MethodType.Setter)
                        {
                            targetMethod = argTypes != null
                                ? targetType.GetProperty(methodName, AllFlags, null, null, argTypes, null)?.GetSetMethod(true)
                                : targetType.GetProperty(methodName, AllFlags)?.GetSetMethod(true);
                        }
                        else
                        {
                            if (argTypes != null)
                            {
                                targetMethod = targetType.GetMethod(methodName, AllFlags, null, argTypes, null);
                            }
                            else
                            {
                                try
                                {
                                    targetMethod = targetType.GetMethod(methodName, AllFlags);
                                }
                                catch (AmbiguousMatchException)
                                {
                                    // Method-binding logic associating typeof(Input) with string / int parameters
                                    if (methodName is "GetAxis" or "GetAxisRaw" or "GetButton" or "GetButtonDown" or "GetButtonUp")
                                    {
                                        targetMethod = targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(string) }, null);
                                    }
                                    else if (methodName is "GetMouseButton" or "GetMouseButtonDown" or "GetMouseButtonUp")
                                    {
                                        targetMethod = targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(int) }, null);
                                    }
                                    else if (methodName is "GetKey" or "GetKeyDown" or "GetKeyUp")
                                    {
                                        targetMethod = targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(KeyCode) }, null)
                                                       ?? targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(string) }, null);
                                    }
                                }

                                if (targetMethod == null)
                                {
                                    if (methodName is "GetAxis" or "GetAxisRaw" or "GetButton" or "GetButtonDown" or "GetButtonUp")
                                    {
                                        targetMethod = targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(string) }, null);
                                    }
                                    else if (methodName is "GetMouseButton" or "GetMouseButtonDown" or "GetMouseButtonUp")
                                    {
                                        targetMethod = targetType.GetMethod(methodName, AllFlags, null, new[] { typeof(int) }, null);
                                    }
                                }
                            }
                        }

                        if (targetMethod == null)
                        {
                            skipped++;
                            PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped: {targetType.FullName}.{methodName} (Method not found or stripped by UnityLinker)");
                            continue;
                        }

                        // Verify that the IL2CPP method has a non-null native MethodInfo pointer (was not stripped by UnityLinker)
                        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(targetType.TypeHandle);
                        var nativePtrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(targetMethod);
                        if (nativePtrField == null || (IntPtr)nativePtrField.GetValue(null) == IntPtr.Zero)
                        {
                            skipped++;
                            PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped: {FormatMethodSignature(targetMethod)} (NativeMethodInfoPtr is null/zero - stripped in IL2CPP)");
                            continue;
                        }

                        // INP-10: Never double-detour a method whose underlying native address was already hooked by an ICall hook
                        var methodPointer = GetNativeMethodPointer(targetMethod);
                        if (methodPointer != IntPtr.Zero && UnityInputBlocker.IsAddressAlreadyHooked(methodPointer))
                        {
                            skipped++;
                            PluginLog.Info($"[DearImGuiInjectionPlus] Skipped Harmony patch for {FormatMethodSignature(targetMethod)}: native function (0x{methodPointer.ToInt64():X}) already intercepted by native ICall hook");
                            continue;
                        }

                        var prefixMethod = method.GetCustomAttribute<HarmonyPrefix>() != null ? method : null;
                        var postfixMethod = method.GetCustomAttribute<HarmonyPostfix>() != null ? method : null;

                        if (prefixMethod != null && !IsPatchSignatureCompatible(targetMethod, prefixMethod, out var prefixError))
                        {
                            skipped++;
                            PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped: {FormatMethodSignature(targetMethod)} prefix incompatible: {prefixError}");
                            continue;
                        }

                        if (postfixMethod != null && !IsPatchSignatureCompatible(targetMethod, postfixMethod, out var postfixError))
                        {
                            skipped++;
                            PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped: {FormatMethodSignature(targetMethod)} postfix incompatible: {postfixError}");
                            continue;
                        }

                        var prefix = prefixMethod != null ? new HarmonyMethod { method = prefixMethod } : null;
                        var postfix = postfixMethod != null ? new HarmonyMethod { method = postfixMethod } : null;

                        if (patchGroups.TryGetValue(targetMethod, out var existing))
                        {
                            patchGroups[targetMethod] = (existing.Prefix ?? prefix, existing.Postfix ?? postfix);
                        }
                        else
                        {
                            patchGroups[targetMethod] = (prefix, postfix);
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped stripped IL2CPP Input patch {method.Name} ({attr.info.methodName}): {ex.Message}");
                    }
                }
            }

            foreach (var kvp in patchGroups)
            {
                try
                {
                    _harmony.Patch(kvp.Key, kvp.Value.Prefix, kvp.Value.Postfix);
                    applied++;
                    PluginLog.Info($"[DearImGuiInjectionPlus] Applied: {FormatMethodSignature(kvp.Key)}");
                }
                catch (Exception ex)
                {
                    skipped++;
                    PluginLog.Debug($"[DearImGuiInjectionPlus] Skipped IL2CPP Input detour for {FormatMethodSignature(kvp.Key)}: {ex.Message}");
                }
            }
        }

        // 2. Patch UnityEngine.Internal.InputUnsafeUtility (CallerCount: 57 for GetAxis, 47 for GetAxisRaw) & SendMouseEvents
        // Note: Skips any methods whose native address was already intercepted by native ICall hooks
        UnityInputBlocker.ApplyInternalHarmonyPatches(_harmony, ref applied, ref skipped);

        // 3. Apply managed fallbacks for any inputs where native ICall resolution was unavailable
        ApplyInputFallbacksIfNeeded(_harmony, ref applied, ref skipped);

        // 4. Also hook UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate for New Input System raw event filtering
        NewInputSystemBlocker.ApplyNativeInputHook(_harmony, ref applied, ref skipped);

        HarmonyPatchesApplied = applied;
        HarmonyPatchesSkipped = skipped;

        PluginLog.Info($"[DearImGuiInjectionPlus] Harmony & native ICall patching complete ({applied} applied, {skipped} skipped).");
    }

    private static void ApplyInputFallbacksIfNeeded(Harmony harmony, ref int applied, ref int skipped)
    {
        const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // 1. Cursor.lockState fallback
        if (!UnityInputBlocker.IsCursorLockStateHooked)
        {
            try
            {
                var lockProp = typeof(Cursor).GetProperty(nameof(Cursor.lockState), AllStatic);
                var setLockMethod = lockProp?.GetSetMethod(true);
                if (setLockMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(CursorLockStatePatch).GetMethod("Prefix", AllStatic));
                    harmony.Patch(setLockMethod, prefix: prefix);
                    applied++;
                    PluginLog.Info("[DearImGuiInjectionPlus] Applied managed fallback Harmony patch: Cursor.lockState setter");
                }
            }
            catch (Exception ex)
            {
                skipped++;
                PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to apply Cursor.lockState fallback: {ex.Message}");
            }
        }
        else
        {
            PluginLog.Info("[DearImGuiInjectionPlus] Cursor.lockState intercepted via native ICall hook (Harmony patch not required).");
        }

        // 2. Cursor.visible fallback
        if (!UnityInputBlocker.IsCursorVisibleHooked)
        {
            try
            {
                var visProp = typeof(Cursor).GetProperty(nameof(Cursor.visible), AllStatic);
                var setVisMethod = visProp?.GetSetMethod(true);
                if (setVisMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(CursorVisiblePatch).GetMethod("Prefix", AllStatic));
                    harmony.Patch(setVisMethod, prefix: prefix);
                    applied++;
                    PluginLog.Info("[DearImGuiInjectionPlus] Applied managed fallback Harmony patch: Cursor.visible setter");
                }
            }
            catch (Exception ex)
            {
                skipped++;
                PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to apply Cursor.visible fallback: {ex.Message}");
            }
        }
        else
        {
            PluginLog.Info("[DearImGuiInjectionPlus] Cursor.visible intercepted via native ICall hook (Harmony patch not required).");
        }

        // 3. KeyCode fallback (if native ICall hook for GetKeyInt was unavailable)
        if (!UnityInputBlocker.IsKeyIntHooked)
        {
            try
            {
                var blockKeyMethod = typeof(UnityInputBlocker).GetMethod("BlockKey", AllStatic);
                var prefix = new HarmonyMethod(blockKeyMethod);
                var getKeyMethod = typeof(Input).GetMethod(nameof(Input.GetKey), AllStatic, null, new[] { typeof(KeyCode) }, null);
                var getKeyDownMethod = typeof(Input).GetMethod(nameof(Input.GetKeyDown), AllStatic, null, new[] { typeof(KeyCode) }, null);
                var getKeyUpMethod = typeof(Input).GetMethod(nameof(Input.GetKeyUp), AllStatic, null, new[] { typeof(KeyCode) }, null);

                if (getKeyMethod != null) { harmony.Patch(getKeyMethod, prefix: prefix); applied++; }
                if (getKeyDownMethod != null) { harmony.Patch(getKeyDownMethod, prefix: prefix); applied++; }
                if (getKeyUpMethod != null) { harmony.Patch(getKeyUpMethod, prefix: prefix); applied++; }
                PluginLog.Info("[DearImGuiInjectionPlus] Applied managed fallback Harmony patches for Input.GetKey/Down/Up(KeyCode).");
            }
            catch (Exception ex)
            {
                skipped++;
                PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to apply KeyCode fallbacks: {ex.Message}");
            }
        }
        else
        {
            PluginLog.Info("[DearImGuiInjectionPlus] KeyCode input polling intercepted via native ICall hooks (Harmony patches not required).");
        }

        // 4. MouseButton fallback (if native ICall hook for GetMouseButton was unavailable)
        if (!UnityInputBlocker.IsMouseButtonHooked)
        {
            try
            {
                var blockMouseBtnMethod = typeof(UnityInputBlocker).GetMethod("BlockMouseButton", AllStatic);
                var prefix = new HarmonyMethod(blockMouseBtnMethod);
                var getMouseBtn = typeof(Input).GetMethod(nameof(Input.GetMouseButton), AllStatic, null, new[] { typeof(int) }, null);
                var getMouseBtnDown = typeof(Input).GetMethod(nameof(Input.GetMouseButtonDown), AllStatic, null, new[] { typeof(int) }, null);
                var getMouseBtnUp = typeof(Input).GetMethod(nameof(Input.GetMouseButtonUp), AllStatic, null, new[] { typeof(int) }, null);

                if (getMouseBtn != null) { harmony.Patch(getMouseBtn, prefix: prefix); applied++; }
                if (getMouseBtnDown != null) { harmony.Patch(getMouseBtnDown, prefix: prefix); applied++; }
                if (getMouseBtnUp != null) { harmony.Patch(getMouseBtnUp, prefix: prefix); applied++; }
                PluginLog.Info("[DearImGuiInjectionPlus] Applied managed fallback Harmony patches for Input.GetMouseButton*.");
            }
            catch (Exception ex)
            {
                skipped++;
                PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to apply MouseButton fallbacks: {ex.Message}");
            }
        }
        else
        {
            PluginLog.Info("[DearImGuiInjectionPlus] Mouse button polling intercepted via native ICall hooks (Harmony patches not required).");
        }

        // 5. AnyKey fallback (if native ICall hook for anyKey was unavailable)
        if (!UnityInputBlocker.IsAnyKeyHooked)
        {
            try
            {
                var blockAnyKeyMethod = typeof(UnityInputBlocker).GetMethod("BlockAnyKey", AllStatic);
                var prefix = new HarmonyMethod(blockAnyKeyMethod);
                var anyKeyProp = typeof(Input).GetProperty(nameof(Input.anyKey), AllStatic)?.GetGetMethod(true);
                var anyKeyDownProp = typeof(Input).GetProperty(nameof(Input.anyKeyDown), AllStatic)?.GetGetMethod(true);

                if (anyKeyProp != null) { harmony.Patch(anyKeyProp, prefix: prefix); applied++; }
                if (anyKeyDownProp != null) { harmony.Patch(anyKeyDownProp, prefix: prefix); applied++; }
                PluginLog.Info("[DearImGuiInjectionPlus] Applied managed fallback Harmony patches for Input.anyKey/anyKeyDown.");
            }
            catch (Exception ex)
            {
                skipped++;
                PluginLog.Warning($"[DearImGuiInjectionPlus] Failed to apply anyKey fallbacks: {ex.Message}");
            }
        }
    }

    public override bool Unload()
    {
        Shutdown(fromOnDestroy: false);
        return true;
    }

    internal static void Shutdown(bool fromOnDestroy = false)
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
            return;

        if (fromOnDestroy && !DearImGuiInjectionPlusRunner._isQuitting)
        {
            PluginLog.Warning("[DearImGuiInjectionPlus] Shutdown invoked from OnDestroy but application is not quitting. Ignoring premature shutdown request.");
            return;
        }

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
            UnityInputBlocker.DisposeNativeICallHooks();
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[DearImGuiInjectionPlus] Error disposing native ICall hooks: {ex.Message}");
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

        var go = _runnerGo;
        _runnerGo = null;
        if (!fromOnDestroy && go != null)
        {
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
