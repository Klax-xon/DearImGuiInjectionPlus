# Forensic Post-Migration Audit: `DearImGuiInjectionPlus` (BepInEx 6 IL2CPP / .NET 6)

**Target Branch:** `feature/bepinex-6` (`559492a` — *Initial migration*)  
**Target Runtime:** BepInEx `6.0.0-be.790` (`BepInEx.Unity.IL2CPP`) | .NET `6.0` (`CoreCLR`) | `x64`  
**Audited Scope:** `DearImGuiInjectionPlus/`, `RendererFinder/`, `TestPlugin/`

---

## 1. Executive Migration Verdict

### Verdict: **`ACTION REQUIRED`** *(Baseline Functional; High-Priority Hardening Needed for Production IL2CPP Games)*

The migration to BepInEx 6 IL2CPP (`net6.0`) is architecturally sound in its core graphics and concurrency design:
- Native DirectX 11 (`IDXGISwapChain::Present` / `Present1`) and DirectX 12 (`ID3D12CommandQueue::ExecuteCommandLists`) hooks in `RendererFinder` operate purely against native COM VTables via `Reloaded.Hooks` and `SharpDX`, remaining completely insulated from IL2CPP runtime overhead.
- Custom `MonoBehaviour` types (`UnityMainThreadDispatcher`, `DearImGuiInjectionPlusRunner`, `TestPluginRunner`) correctly implement the required `public MyType(IntPtr handle) : base(handle)` native pointer constructors and register via `ClassInjector.RegisterTypeInIl2Cpp<T>()` prior to instantiation.
- CoreCLR memory ordering in `StateSnapshotPipeline<T>`, `StructSnapshotPipeline<T>`, and `FrameGate` uses `Volatile.Read`/`Volatile.Write` and `Interlocked` primitives appropriately for `x64` RyuJIT.

**Why Action Is Required Before Production Release:**  
Forensic inspection uncovered **two critical IL2CPP runtime hazards** and **four high/medium lifecycle and performance defects** that do not prevent compilation or baseline test-game startup, but will cause broken input blocking, log spam, or crashes in real-world IL2CPP games with method stripping or Unity's New Input System:

1. **[CRITICAL] `NewInputSystemBlocker` Broken `IEnumerable` Cast & Cross-Thread IL2CPP Invocation:** `InputSystem.devices` in `Il2CppInterop` implements `Il2CppSystem.Collections.IEnumerable`, not managed `System.Collections.IEnumerable`, causing `devicesObj is System.Collections.IEnumerable` (`NewInputSystemBlocker.cs:134`) to silently fail on every frame. Additionally, `NewInputSystemBlocker.SetBlocked()` is invoked synchronously from `InputManager.OnCursorVisibilityToggled()` (`InputManager.cs:557`) without main-thread dispatching.
2. **[CRITICAL] Monolithic `_harmony.PatchAll()` Vulnerability to IL2CPP Method Stripping:** Bundling all `UnityEngine.Input` and `UnityEngine.EventSystems.EventSystem` patches into a single `_harmony.PatchAll()` call (`DearImGuiInjectionPlusBaseUnityPlugin.cs:92`), combined with an unguarded per-frame `Input.mousePosition` call in `DearImGuiInjectionPlusRunner.Update()` (`DearImGuiInjectionPlusRunner.cs:21`), will fail partway through patching or throw per-frame `EntryPointNotFoundException` in IL2CPP games where `UnityLinker` has stripped unused legacy `Input` or `uGUI` methods.
3. **[HIGH] `HideFlags.HideAndDontSave` Suppresses `OnDestroy` on Engine Exit & Re-Entrant `Object.Destroy`:** Setting `HideFlags.HideAndDontSave` on `_runnerGo` (`DearImGuiInjectionPlusBaseUnityPlugin.cs:106`, `TestPlugin.cs:136`) prevents Unity from invoking `OnDestroy()` during normal `Application.Quit()` scene cleanup, while `Shutdown()`/`Teardown()` re-entrantly calls `UnityEngine.Object.Destroy(_runnerGo)` when invoked from inside `OnDestroy()`.
4. **[MEDIUM] `UnityMainThreadDispatcher` Registration Bypass & Bootstrap Race Window:** `DearImGuiInjectionPlus.Init()` hooks `Present` before `UnityMainThreadDispatcher` sets `_mainThreadId`, and `DearImGuiInjectionPlusBaseUnityPlugin.Load()` bypasses `UnityMainThreadDispatcher.Register()`.
5. **[MEDIUM] Per-Frame IL2CPP String Allocation in `TestPluginRunner.Update()`:** Calling `gameObject.scene.name` every `Update()` tick (`TestPluginRunner.cs:25`) invokes `IL2CPP.Il2CppStringToManaged()`, allocating a new managed `System.String` and `PlayerDataSnapshot` object on every frame and polluting `MemoryBenchmarkTestPanel` (`TM-09`) steady-state GC metrics.

---

## 2. Subsystem Breakdown

### 2.1 Entry Point & `MonoBehaviour` Lifecycle
- **`BasePlugin` Transition (`DearImGuiInjectionPlusBaseUnityPlugin.cs`, `TestPlugin.cs`):**
  - Both plugins properly inherit from `BepInEx.Unity.IL2CPP.BasePlugin`, override `Load()` and `Unload()`, and extract Unity frame-tick callbacks (`Update`, `LateUpdate`, `OnDestroy`) into dedicated `MonoBehaviour` runners (`DearImGuiInjectionPlusRunner`, `TestPluginRunner`).
- **Native Pointer Constructors & `ClassInjector`:**
  - `UnityMainThreadDispatcher(IntPtr handle) : base(handle)` (`UnityMainThreadDispatcher.cs:32`), `DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle)` (`DearImGuiInjectionPlusRunner.cs:9`), and `TestPluginRunner(IntPtr handle) : base(handle)` (`TestPluginRunner.cs:10`) are all properly defined.
  - **Defect:** `UnityMainThreadDispatcher.Register()` (`UnityMainThreadDispatcher.cs:38-44`) guards registration with `private static bool _registered`, but `DearImGuiInjectionPlusBaseUnityPlugin.Load()` (`DearImGuiInjectionPlusBaseUnityPlugin.cs:101`) calls `ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>()` directly instead of calling `UnityMainThreadDispatcher.Register()`. If a consumer plugin calls `UnityMainThreadDispatcher.Register()`, `ClassInjector` throws an exception for duplicate type registration.
- **Native-Managed Object Lifetime & `HideFlags`:**
  - Storing `_runnerGo` in a `static` reference (`DearImGuiInjectionPlusBaseUnityPlugin.cs:22`) keeps the managed wrapper alive, and `TestPluginRunner` holds `_playerSnapshotPipeline` (`TestPluginRunner.cs:8`), which is rooted in `Il2CppInterop`'s injected `GCHandle` table while the native component lives.
  - **Defect:** Both plugins set `_runnerGo.hideFlags = HideFlags.HideAndDontSave` (`DearImGuiInjectionPlusBaseUnityPlugin.cs:106`, `TestPlugin.cs:136`). Because `HideAndDontSave` includes `HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor`, Unity's scene manager ignores the object during application exit teardown—meaning `OnDestroy()` is not reliably called on `Application.Quit()`. Moreover, neither runner implements `OnApplicationQuit()`.

### 2.2 HarmonyX Detours & Input Blocking
- **Value-Type Return Overrides (`UnityInputBlocker.cs`):**
  - `BlockScrollDelta(ref Vector2 __result)` (`UnityInputBlocker.cs:103`) and `BlockMousePosition(ref Vector3 __result)` (`UnityInputBlocker.cs:158`) override value-type returns with `return false`.
  - Under HarmonyX's `Il2CppDetourMethodPatcher` on `x64`, `UnityEngine.Vector2` (8 bytes, sequential `float x, y`) and `UnityEngine.Vector3` (12 bytes, sequential `float x, y, z`) are blittable structs with identical memory layouts across CoreCLR and IL2CPP C++. HarmonyX's managed detour trampoline safely writes the `ref __result` value into the native return buffer/register without stack corruption.
- **Prefix/Postfix Suppression Semantics (`UnityInputBlocker.cs`, `CursorManager.cs`, `EventSystemBlocker.cs`):**
  - `UnityInputBlocker` and `EventSystemBlocker` prefixes return `false` to skip the original native IL2CPP function when blocking input, and `true` otherwise.
  - `CursorLockStatePatch` (`CursorManager.cs:14`) and `CursorVisiblePatch` (`CursorManager.cs:36`) prefixes mutate `ref value` and return `true` so the native setter executes with `CursorLockMode.None` / `true`. This is safe under HarmonyX IL2CPP detours.
- **String Return Override Allocation (`UnityInputBlocker.cs:145`):**
  - `BlockInputString(ref string __result)` sets `__result = string.Empty; return false;`. In HarmonyX's `Il2CppDetourMethodPatcher`, returning a managed `System.String` to native code invokes `IL2CPP.ManagedStringToIl2Cpp(__result)`, allocating a native `Il2CppString*` on every call when a game polls `Input.inputString` while the menu is open. Setting `__result = null` (or caching an empty `Il2CppSystem.String` if needed, though Unity converts null to empty or HarmonyX handles `string.Empty`) is minor, but worth noting.
- **Module & Method Stripping Fragility (`DearImGuiInjectionPlusBaseUnityPlugin.cs:92`, `DearImGuiInjectionPlusRunner.cs:21`):**
  - `_harmony.PatchAll(Assembly.GetExecutingAssembly())` patches all 19 target methods across `Cursor`, `EventSystem`, and `Input` in one unisolated batch. In IL2CPP, `UnityLinker` strips any unreferenced `Input` method (e.g., `Input.GetButton`, `Input.GetKey(string)`, `Input.anyKeyDown`) or `EventSystem.Update` from `GameAssembly.dll`. When `Il2CppDetourMethodPatcher` hits the first stripped method (`Il2CppMethodInfo* == null`), `PatchAll` throws and aborts all remaining patches!
  - Worse, `DearImGuiInjectionPlusRunner.Update()` (`DearImGuiInjectionPlusRunner.cs:21`) directly calls `Input.mousePosition` every frame when the menu is closed. If a game uses only the New Input System and stripped `Input.get_mousePosition`, `Update()` throws an exception every frame.

### 2.3 IL2CPP Interop & Memory Safety
- **`NewInputSystemBlocker` Type Mismatch (`NewInputSystemBlocker.cs:134`):**
  - In `Il2CppInterop`, `InputSystem.devices` returns `ReadOnlyArray<InputDevice>`, which implements `Il2CppSystem.Collections.IEnumerable`, **not** `System.Collections.IEnumerable`. Line 134 (`if (devicesObj is System.Collections.IEnumerable deviceList)`) is dead code in IL2CPP.
- **`Il2CppObjectBase` Wrapper Identity & Per-Frame Reflection (`NewInputSystemBlocker.cs:19, 117-171`):**
  - `DearImGuiInjectionPlusRunner.Update()` calls `NewInputSystemBlocker.EnforceBlocked()` **every single frame** while the menu is open (`DearImGuiInjectionPlusRunner.cs:15`).
  - Every frame, `EnforceBlocked()` invokes `_devicesProp.GetValue(null, null)` via reflection (boxing the `ReadOnlyArray<InputDevice>` struct), fails the `System.Collections.IEnumerable` check, and then queries `Mouse.current` and `Keyboard.current` via reflection.
  - Furthermore, `_disabledDevices` (`HashSet<object>`) stores `Il2CppObjectBase` proxy instances. In `Il2CppInterop`, property getters return pooled/new managed wrapper instances whose `Object.GetHashCode()` / `Object.Equals()` are not guaranteed to compare the underlying native `Il2CppObject*` (`Pointer`) across GC cycles.
- **Per-Frame Native-to-Managed String Marshaling (`TestPluginRunner.cs:25`):**
  - `TestPluginRunner.Update()` evaluates `gameObject.scene.name` on every `Update()` tick, which calls `IL2CPP.Il2CppStringToManaged` and allocates a new `System.String` and a new `PlayerDataSnapshot` instance every frame.

### 2.4 Concurrency & CoreCLR Compatibility
- **`StateSnapshotPipeline<T>` & `StructSnapshotPipeline<T>` (`StateSnapshotPipeline.cs`):**
  - Fully compatible with .NET 6 CoreCLR memory semantics (`Interlocked.Exchange`, `Interlocked.CompareExchange`, `Volatile.Read`). Zero torn reads or memory reordering hazards on `x64`.
- **`UnityMainThreadDispatcher` (`UnityMainThreadDispatcher.cs`):**
  - `DispatchCompletionHandle` reference counting (`Interlocked.CompareExchange`, `Interlocked.Decrement`) safely prevents `ObjectDisposedException` on `ManualResetEventSlim` when a blocking call times out.
  - **Bootstrap Race:** `DearImGuiInjectionPlus.Init()` (`DearImGuiInjectionPlusBaseUnityPlugin.cs:87`) hooks DXGI `Present` **before** `UnityMainThreadDispatcher` is added to `_runnerGo` (line 109), leaving `_mainThreadId == -1` (`IsMainThread == false`) during `_harmony.PatchAll()`.
- **Cross-Thread Unity API Call in `InputManager.OnCursorVisibilityToggled` (`InputManager.cs:557`):**
  - `NewInputSystemBlocker.SetBlocked(visible)` is called synchronously outside `UnityMainThreadDispatcher.Enqueue(...)`. If `DearImGuiInjectionPlus.ToggleCursorVisibility()` is called from the render thread (e.g., an ImGui close button), native IL2CPP `InputSystem.DisableDevice` / `EnableDevice` methods are invoked on the DirectX Present thread instead of the Unity main thread.

---

## 3. Discovered Edge Cases / Residual Risks

| ID | Severity | Location | Description & Failure Mode |
| :--- | :---: | :--- | :--- |
| **AUD-01** | **CRITICAL** | [`DearImGuiInjectionPlusBaseUnityPlugin.cs:90-98`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L90-L98)<br>[`UnityInputBlocker.cs:42-176`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/Patches/UnityInputBlocker.cs#L42-L176)<br>[`EventSystemBlocker.cs:6-25`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/Patches/EventSystemBlocker.cs#L6-L25) | **Unisolated `_harmony.PatchAll()` aborts on stripped IL2CPP methods.** If a host game strips any single legacy `Input` method (e.g., `GetButton`, `inputString`) or `EventSystem.Update`, `Harmony.PatchAll()` throws partway through, leaving critical `Cursor` or remaining `Input` patches unapplied. |
| **AUD-02** | **CRITICAL** | [`DearImGuiInjectionPlusRunner.cs:19-22`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusRunner.cs#L19-L22) | **Unguarded per-frame `Input.mousePosition` call in `Update()`.** In games using only `Unity.InputSystem` where `UnityEngine.Input.get_mousePosition` is stripped in `GameAssembly.dll`, `DearImGuiInjectionPlusRunner.Update()` throws an IL2CPP method resolution exception on every frame while the cursor is hidden. |
| **AUD-03** | **CRITICAL** | [`NewInputSystemBlocker.cs:134`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs#L134) | **Broken `is System.Collections.IEnumerable` check in IL2CPP.** `InputSystem.devices` (`ReadOnlyArray<InputDevice>`) in `Il2CppInterop` does not implement `System.Collections.IEnumerable`. The loop at line 136 never executes in IL2CPP, failing to block gamepads/controllers and causing per-frame reflection boxing overhead. |
| **AUD-04** | **HIGH** | [`InputManager.cs:553-562`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/InputManager.cs#L553-L562)<br>[`NewInputSystemBlocker.cs:19, 209-215`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs#L209-L215) | **Off-main-thread IL2CPP invocation & unsynchronized `_disabledDevices.Count` read.** `NewInputSystemBlocker.SetBlocked(visible)` is called outside `UnityMainThreadDispatcher.Enqueue()`. Additionally, `RestoreDevicesIfNeeded()` reads `_disabledDevices.Count` without acquiring `_lock`, and `_disabledDevices` hashes `Il2CppObjectBase` wrappers rather than native `IntPtr` pointers. |
| **AUD-05** | **HIGH** | [`DearImGuiInjectionPlusBaseUnityPlugin.cs:104-110, 209-221`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L104-L110)<br>[`DearImGuiInjectionPlusRunner.cs:38-41`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusRunner.cs#L38-L41)<br>[`TestPlugin.cs:135-140, 229-241`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TestPlugin/TestPlugin.cs#L135-L140)<br>[`TestPluginRunner.cs:30-33`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TestPlugin/TestPluginRunner.cs#L30-L33) | **`HideFlags.HideAndDontSave` suppresses `OnDestroy()` on exit & re-entrant `Object.Destroy(_runnerGo)`.** Objects with `HideFlags.DontSave` are skipped by Unity's scene teardown during `Application.Quit()`, and neither runner implements `OnApplicationQuit()`. Furthermore, when `OnDestroy()` does fire, `Shutdown()`/`Teardown()` calls `UnityEngine.Object.Destroy(go)` re-entrantly on the `GameObject` already being destroyed. |
| **AUD-06** | **MEDIUM** | [`DearImGuiInjectionPlusBaseUnityPlugin.cs:87-110`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L87-L110)<br>[`UnityMainThreadDispatcher.cs:27-56`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/UnityMainThreadDispatcher.cs#L27-L56) | **`UnityMainThreadDispatcher` registration bypass & bootstrap ordering race.** `DearImGuiInjectionPlus.Init()` activates DXGI `Present` hooks before `UnityMainThreadDispatcher` is registered/attached, leaving `IsMainThread == false` (`_mainThreadId == -1`) during `Harmony` patching. `Load()` also bypasses `UnityMainThreadDispatcher.Register()`. |
| **AUD-07** | **MEDIUM** | [`TestPluginRunner.cs:15-28`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TestPlugin/TestPluginRunner.cs#L15-L28) | **Per-frame `Il2CppStringToManaged` & snapshot allocation in `TestPluginRunner.Update()`.** Accessing `gameObject.scene.name` and `new PlayerDataSnapshot()` every `Update()` tick allocates a new `System.String`, `GameObject` wrapper, and snapshot object on every frame, skewing `MemoryBenchmarkTestPanel` (`TM-09`) steady-state GC allocation metrics. |

---

## 4. Actionable Remediation Code

### 4.1 Harden `DearImGuiInjectionPlusBaseUnityPlugin.cs` & `DearImGuiInjectionPlusRunner.cs` (Fixes AUD-01, AUD-02, AUD-05, AUD-06)

1. Isolate each Harmony patch method individually so stripped IL2CPP methods log a debug warning without aborting other patches.
2. Initialize `UnityMainThreadDispatcher` **before** calling `DearImGuiInjectionPlus.Init()`.
3. Use `HideFlags.HideInHierarchy` (instead of `HideFlags.HideAndDontSave`) so Unity reliably invokes `OnApplicationQuit()` and `OnDestroy()` during engine shutdown, and avoid calling `UnityEngine.Object.Destroy(_runnerGo)` re-entrantly when `Shutdown()` is triggered from `OnDestroy()`.

#### `DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs`
```csharp
        // 1. Register and spawn internal MonoBehaviour types BEFORE activating native Present hooks (Fixes AUD-06)
        UnityMainThreadDispatcher.Register();
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<DearImGuiInjectionPlusRunner>())
        {
            ClassInjector.RegisterTypeInIl2Cpp<DearImGuiInjectionPlusRunner>();
        }

        // Use HideInHierarchy + DontDestroyOnLoad so Unity still dispatches OnApplicationQuit / OnDestroy on engine exit (Fixes AUD-05)
        _runnerGo = new GameObject("DearImGuiInjectionPlus_Runner");
        _runnerGo.hideFlags = HideFlags.HideInHierarchy;
        UnityEngine.Object.DontDestroyOnLoad(_runnerGo);

        _runnerGo.AddComponent<UnityMainThreadDispatcher>();
        _runnerGo.AddComponent<DearImGuiInjectionPlusRunner>();

        // 2. Apply Harmony patches with per-method isolation against IL2CPP UnityLinker stripping (Fixes AUD-01)
        ApplyHarmonyPatchesSafely();

        // 3. Initialize native SwapChain hooks after dispatcher and patches are ready
        DearImGuiInjectionPlus.Init(Paths.ConfigPath, assetsFolder, cursorVisibilityConfig);

        PluginLog.Info("[DearImGuiInjectionPlus] Bootstrap complete.");
```

Add `ApplyHarmonyPatchesSafely()` and update `Shutdown(bool fromOnDestroy = false)` in `DearImGuiInjectionPlusBaseUnityPlugin.cs`:
```csharp
    private static void ApplyHarmonyPatchesSafely()
    {
        _harmony = new Harmony(Metadata.GUID);
        int applied = 0;
        int skipped = 0;

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                continue;

            // Patch class-level [HarmonyPatch] types (CursorLockStatePatch, CursorVisiblePatch, EventSystemBlocker)
            if (type != typeof(UnityInputBlocker))
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    PluginLog.Warning($"[DearImGuiInjectionPlus] Skipped patch class {type.Name} (method likely stripped in IL2CPP): {ex.Message}");
                }
                continue;
            }

            // Patch UnityInputBlocker methods individually so a single stripped Input method does not abort the rest
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var patchAttrs = method.GetCustomAttributes<HarmonyPatch>( false);
                foreach (var attr in patchAttrs)
                {
                    try
                    {
                        var targetType = attr.info.declaringType;
                        var methodName = attr.info.methodName;
                        var methodType = attr.info.methodType ?? MethodType.Normal;
                        var argTypes = attr.info.argumentTypes;

                        MethodBase targetMethod = methodType switch
                        {
                            MethodType.Getter => argTypes != null
                                ? targetType.GetProperty(methodName, argTypes)?.GetGetMethod()
                                : targetType.GetProperty(methodName)?.GetGetMethod(),
                            MethodType.Setter => argTypes != null
                                ? targetType.GetProperty(methodName, argTypes)?.GetSetMethod()
                                : targetType.GetProperty(methodName)?.GetSetMethod(),
                            _ => argTypes != null
                                ? targetType.GetMethod(methodName, argTypes)
                                : targetType.GetMethod(methodName)
                        };

                        if (targetMethod == null)
                        {
                            skipped++;
                            continue;
                        }

                        var prefix = method.GetCustomAttribute<HarmonyPrefix>() != null ? new HarmonyMethod(method) : null;
                        var postfix = method.GetCustomAttribute<HarmonyPostfix>() != null ? new HarmonyMethod(method) : null;
                        _harmony.Patch(targetMethod, prefix, postfix);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        PluginLog.Warning($"[DearImGuiInjectionPlus] Skipped stripped IL2CPP Input patch {method.Name} ({attr.info.methodName}): {ex.Message}");
                    }
                }
            }
        }

        PluginLog.Info($"[DearImGuiInjectionPlus] Harmony patching complete ({applied} applied, {skipped} skipped).");
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
```

#### `DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusRunner.cs`
Guard `Input.mousePosition` against stripped `InputLegacyModule` and add `OnApplicationQuit()`:
```csharp
using System;
using DearImGuiInjectionPlus.Patches;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

public class DearImGuiInjectionPlusRunner : MonoBehaviour
{
    private static bool _legacyMousePosSupported = true;

    public DearImGuiInjectionPlusRunner(IntPtr handle) : base(handle) { }

    private void Update()
    {
        if (DearImGuiInjectionPlus.IsCursorVisible && DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
        {
            NewInputSystemBlocker.EnforceBlocked();
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

    private void LateUpdate()
    {
        // INP-09: Re-enforce cursor unlocked state and free clipping after game Update/LateUpdate loops run
        InputManager.EnforceCursorStateOnMainThread();
    }

    private void OnApplicationQuit()
    {
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        DearImGuiInjectionPlusBaseUnityPlugin.Shutdown(fromOnDestroy: true);
    }
}
```

---

### 4.2 Fix `UnityMainThreadDispatcher.cs` Registration & Main-Thread ID Initialization (Fixes AUD-06)

#### `DearImGuiInjectionPlus/BepInEx/UnityMainThreadDispatcher.cs`
```csharp
    /// <summary>
    /// Registers the UnityMainThreadDispatcher type with the IL2CPP domain and captures the calling main thread ID.
    /// </summary>
    public static void Register()
    {
        if (_mainThreadId <= 0)
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        if (!_registered)
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<UnityMainThreadDispatcher>())
            {
                ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>();
            }
            _registered = true;
        }
    }

    private void Awake()
    {
        _instance = this;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }
```

---

### 4.3 Fix `NewInputSystemBlocker.cs` & `InputManager.cs` for IL2CPP Interop & Thread Safety (Fixes AUD-03, AUD-04)

1. In `InputManager.OnCursorVisibilityToggled()`, move `NewInputSystemBlocker.SetBlocked(visible)` inside `UnityMainThreadDispatcher.Enqueue(...)` so native IL2CPP `InputSystem` methods are strictly invoked on the Unity main thread.
2. In `NewInputSystemBlocker.cs`, support `Il2CppObjectBase` native pointer tracking, index `ReadOnlyArray<InputDevice>` via `Count` + `get_Item(int)` property reflection (which works identically in both Mono and IL2CPP without relying on `System.Collections.IEnumerable`), and synchronize `_disabledDevices`.

#### `DearImGuiInjectionPlus/InputManager.cs` (`OnCursorVisibilityToggled`)
```csharp
    internal static void OnCursorVisibilityToggled(bool visible)
    {
        try
        {
            if (visible)
            {
                CaptureDepressedKeys();
                User32.ClipCursor(IntPtr.Zero);

                // INP-09: Dispatch Unity cursor and InputSystem mutations to the main scripting thread
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        NewInputSystemBlocker.SetBlocked(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[InputManager] Error in NewInputSystemBlocker.SetBlocked(true): {ex.Message}");
                    }

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

                // INP-09: Dispatch Unity cursor and InputSystem mutations to the main scripting thread
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        NewInputSystemBlocker.SetBlocked(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[InputManager] Error in NewInputSystemBlocker.SetBlocked(false): {ex.Message}");
                    }

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
```

#### `DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace DearImGuiInjectionPlus.Patches
{
    internal static class NewInputSystemBlocker
    {
        private static Type _inputSystemType;
        private static MethodInfo _disableDeviceMethod;
        private static MethodInfo _enableDeviceMethod;
        private static PropertyInfo _devicesProp;
        private static PropertyInfo _devicesCountProp;
        private static MethodInfo _devicesIndexerGetter;
        private static PropertyInfo _deviceEnabledProp;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _keyboardCurrentProp;
        private static int _disableMethodParamCount;
        private static bool _initialized;
        private static bool _hasSearched;

        // Keyed by native IL2CPP pointer (or RuntimeHelpers.GetHashCode fallback) to avoid Il2CppObjectBase wrapper identity churn
        private static readonly Dictionary<IntPtr, object> _disabledDevices = new();
        private static volatile bool _hasDisabledDevices;
        private static readonly object _lock = new();

        public static void EnsureInitialized(bool force = false)
        {
            if (_initialized || (_hasSearched && !force)) return;
            _hasSearched = true;

            try
            {
                _inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem")
                                   ?? Type.GetType("UnityEngine.InputSystem.InputSystem, Il2CppUnity.InputSystem");
                if (_inputSystemType == null)
                {
                    var inputAss = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name is "Unity.InputSystem" or "Il2CppUnity.InputSystem");
                    if (inputAss != null)
                    {
                        _inputSystemType = inputAss.GetType("UnityEngine.InputSystem.InputSystem");
                    }
                }

                if (_inputSystemType != null)
                {
                    var inputAss = _inputSystemType.Assembly;
                    var deviceType = inputAss.GetType("UnityEngine.InputSystem.InputDevice");

                    _devicesProp = _inputSystemType.GetProperty("devices", BindingFlags.Public | BindingFlags.Static);
                    if (_devicesProp != null)
                    {
                        var readOnlyArrayType = _devicesProp.PropertyType;
                        _devicesCountProp = readOnlyArrayType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                        _devicesIndexerGetter = readOnlyArrayType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    }

                    _deviceEnabledProp = deviceType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);

                    _disableDeviceMethod = _inputSystemType.GetMethod("DisableDevice", new[] { deviceType, typeof(bool) })
                                           ?? _inputSystemType.GetMethod("DisableDevice", new[] { deviceType });
                    _disableMethodParamCount = _disableDeviceMethod?.GetParameters().Length ?? 0;

                    _enableDeviceMethod = _inputSystemType.GetMethod("EnableDevice", new[] { deviceType });

                    var mouseType = inputAss.GetType("UnityEngine.InputSystem.Mouse");
                    _mouseCurrentProp = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    var keyboardType = inputAss.GetType("UnityEngine.InputSystem.Keyboard");
                    _keyboardCurrentProp = keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    _initialized = true;
                    Log.Info("[NewInputSystemBlocker] Successfully discovered and initialized UnityEngine.InputSystem hooks.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] Init exception: {ex}");
            }
        }

        private static IntPtr GetDeviceKey(object device)
        {
            if (device is Il2CppObjectBase il2cppObj)
                return il2cppObj.Pointer;
            return (IntPtr)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(device);
        }

        private static void TrackAndDisableDevice(object device)
        {
            if (device == null) return;
            IntPtr key = GetDeviceKey(device);
            if (key == IntPtr.Zero) return;

            if (IsDeviceEnabled(device))
            {
                DisableDeviceInternal(device);
                _disabledDevices[key] = device;
                _hasDisabledDevices = true;
            }
        }

        private static void DisableDeviceInternal(object device)
        {
            if (device == null || _disableDeviceMethod == null) return;
            try
            {
                if (_disableMethodParamCount == 2)
                    _disableDeviceMethod.Invoke(null, new object[] { device, false });
                else
                    _disableDeviceMethod.Invoke(null, new object[] { device });
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] DisableDeviceInternal error: {ex.Message}");
            }
        }

        private static void EnableDeviceInternal(object device)
        {
            if (device == null || _enableDeviceMethod == null) return;
            try
            {
                _enableDeviceMethod.Invoke(null, new object[] { device });
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] EnableDeviceInternal error: {ex.Message}");
            }
        }

        private static bool IsDeviceEnabled(object device)
        {
            if (device == null) return false;
            if (_deviceEnabledProp != null)
            {
                try
                {
                    return (bool)_deviceEnabledProp.GetValue(device, null);
                }
                catch
                {
                    return true;
                }
            }
            return true;
        }

        public static void EnforceBlocked()
        {
            if (!_initialized)
            {
                EnsureInitialized();
            }
            if (_inputSystemType == null) return;
            if (!DearImGuiInjectionPlus.IsCursorVisible || !DearImGuiInjectionPlus.BlockAllInputsWhenOpen) return;

            lock (_lock)
            {
                try
                {
                    bool handledAny = false;
                    if (_devicesProp != null && _devicesCountProp != null && _devicesIndexerGetter != null)
                    {
                        var devicesObj = _devicesProp.GetValue(null, null);
                        if (devicesObj != null)
                        {
                            int count = (int)_devicesCountProp.GetValue(devicesObj, null);
                            for (int i = 0; i < count; i++)
                            {
                                var device = _devicesIndexerGetter.Invoke(devicesObj, new object[] { i });
                                if (device == null) continue;
                                handledAny = true;
                                TrackAndDisableDevice(device);
                            }
                        }
                    }

                    if (!handledAny)
                    {
                        TrackAndDisableDevice(_mouseCurrentProp?.GetValue(null, null));
                        TrackAndDisableDevice(_keyboardCurrentProp?.GetValue(null, null));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[NewInputSystemBlocker] EnforceBlocked error: {ex.Message}");
                }
            }
        }

        public static void RestoreDevices()
        {
            if (!_initialized || _inputSystemType == null) return;

            lock (_lock)
            {
                try
                {
                    foreach (var kvp in _disabledDevices)
                    {
                        if (kvp.Value != null)
                        {
                            EnableDeviceInternal(kvp.Value);
                        }
                    }
                    _disabledDevices.Clear();
                    _hasDisabledDevices = false;

                    var mouse = _mouseCurrentProp?.GetValue(null, null);
                    if (mouse != null && !IsDeviceEnabled(mouse))
                    {
                        EnableDeviceInternal(mouse);
                    }

                    var keyboard = _keyboardCurrentProp?.GetValue(null, null);
                    if (keyboard != null && !IsDeviceEnabled(keyboard))
                    {
                        EnableDeviceInternal(keyboard);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[NewInputSystemBlocker] RestoreDevices error: {ex.Message}");
                }
            }
        }

        public static void RestoreDevicesIfNeeded()
        {
            if (_hasDisabledDevices)
            {
                RestoreDevices();
            }
        }

        public static void SetBlocked(bool blocked)
        {
            EnsureInitialized(force: true);
            if (_inputSystemType == null) return;

            if (blocked)
            {
                if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
                    return;

                EnforceBlocked();
            }
            else
            {
                RestoreDevices();
            }
        }
    }
}
```

---

### 4.4 Eliminate Per-Frame IL2CPP String Allocation & Harden Teardown in `TestPlugin` (Fixes AUD-05, AUD-07)

1. Cache `_lastSceneHandle` in `TestPluginRunner.cs` so `scene.name` (`IL2CPP.Il2CppStringToManaged`) is only marshaled when the active scene actually changes, and reuse/update snapshots only when state changes or via cached scene name.
2. Add `OnApplicationQuit()` to `TestPluginRunner.cs`, use `HideFlags.HideInHierarchy` on `TestPlugin_Runner`, and guard `Teardown(bool fromOnDestroy = false)` against re-entrant `Object.Destroy(_runnerGo)`.

#### `TestPlugin/TestPluginRunner.cs`
```csharp
using System;
using DearImGuiInjectionPlus;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TestPlugin;

public class TestPluginRunner : MonoBehaviour
{
    private StateSnapshotPipeline<PlayerDataSnapshot> _playerSnapshotPipeline;
    private int _lastSceneHandle = -1;
    private string _cachedSceneName = "SampleScene";

    public TestPluginRunner(IntPtr handle) : base(handle) { }

    internal void Initialize(StateSnapshotPipeline<PlayerDataSnapshot> snapshotPipeline)
    {
        _playerSnapshotPipeline = snapshotPipeline;
    }

    private void Update()
    {
        if (_playerSnapshotPipeline == null)
            return;

        // Avoid per-frame IL2CPP string marshaling (IL2CPP.Il2CppStringToManaged) unless the active scene handle changed
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.handle != _lastSceneHandle)
        {
            _lastSceneHandle = activeScene.handle;
            var name = activeScene.name;
            _cachedSceneName = string.IsNullOrEmpty(name) ? "SampleScene" : name;
        }

        var snapshot = new PlayerDataSnapshot
        {
            Position = transform.position,
            Health = 100f,
            MaxHealth = 100f,
            CurrentScene = _cachedSceneName
        };
        _playerSnapshotPipeline.Publish(snapshot);
    }

    private void OnApplicationQuit()
    {
        TestPlugin.Instance?.Teardown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        TestPlugin.Instance?.Teardown(fromOnDestroy: true);
    }
}
```

#### `TestPlugin/TestPlugin.cs` (Lines 132–140 & 174–242)
```csharp
        // 4. Register and spawn TestPluginRunner MonoBehaviour
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<TestPluginRunner>())
        {
            ClassInjector.RegisterTypeInIl2Cpp<TestPluginRunner>();
        }

        _runnerGo = new GameObject("TestPlugin_Runner");
        _runnerGo.hideFlags = HideFlags.HideInHierarchy;
        UnityEngine.Object.DontDestroyOnLoad(_runnerGo);

        _runner = _runnerGo.AddComponent<TestPluginRunner>();
        _runner.Initialize(_playerSnapshotPipeline);
```
```csharp
    public override bool Unload()
    {
        Teardown(fromOnDestroy: false);
        return true;
    }

    public void Teardown(bool fromOnDestroy = false)
    {
        if (_isTornDown)
            return;
        _isTornDown = true;

        // ... [existing unregistration & timestamp persistence steps 1-6] ...

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
    }
```
