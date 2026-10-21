# IN-GAME VALIDATION & STRESS TEST MATRIX — DearImGuiInjectionPlus (Mono & IL2CPP)

**Target Assembly:** `DearImGuiInjectionPlus.dll`, `RendererFinder.dll`, `TestPlugin.dll`  
**Target Runtimes:** BepInEx 6 Unity IL2CPP (`net6.0` / CoreCLR, `6.0.0-be.790+`) & BepInEx 5 Unity Mono (`netstandard2.0`) — Unity 2019 LTS, 2021 LTS, 2022 LTS, Unity 6 — Windows x64  
**Graphics APIs:** Direct3D 11 (`-force-d3d11`) & Direct3D 12 (`-force-d3d12`)  

This test matrix defines systematic runtime validation scenarios, IL2CPP interop boundary verifications, execution procedures, instrumentation hooks for `TestPlugin`, and strict pass/fail criteria for certifying `DearImGuiInjectionPlus` across diverse Unity Mono and IL2CPP games.

---

## 1. Summary Test Matrix

| Test ID | Category | Target API | Related Audit IDs | Scenario Summary | Priority |
| :--- | :--- | :---: | :--- | :--- | :---: |
| **TM-01** | Bootstrap & Hook Discovery | D3D11, D3D12, Vulkan/GL | `DXH-02`, `DXH-07`, `DXH-10`, `LC-08`, `AUD-06` | Cold start, Unicode install path (`LoadLibraryW`), `ClassInjector` pre-registration, `DXGI_PRESENT_TEST` filtering, unsupported API abort | **P0** |
| **TM-02** | SwapChain Resize & Fullscreen | D3D11, D3D12 | `MEM-01`, `INP-02`, `DXH-06` | Rapid `Alt+Enter`, windowed drag-resize, 1080p↔4K resolution switching, minimize/restore (`DisplaySize == 0`) | **P0** |
| **TM-03** | SDR, sRGB & HDR Backbuffers | D3D11, D3D12 | `DXH-01`, `DXH-11` | `R8G8B8A8_UNorm`, `B8G8R8A8_UNorm_SRgb`, `R10G10B10A2_UNorm` (HDR10), `R16G16B16A16_Float` (scRGB) PSO & gamma | **P0** |
| **TM-04** | Concurrency & High-Rate Input | D3D11, D3D12 | `TH-02`, `TH-05`, `TH-06`, `AUD-04`, `AUD-06` | 1000–8000 Hz mouse movement + rapid typing + 200+ FPS `Present` + `StateSnapshotPipeline` / `StructSnapshotPipeline` & CoreCLR memory ordering stress | **P0** |
| **TM-05** | Input Blocking & Stuck Keys | D3D11, D3D12 | `INP-02`, `INP-05`, `INP-06`, `INP-07`, `INP-09`, `AUD-08`, `AUD-09` | Held `W`+`Shift`+`RMB` across overlay & passthrough toggles, `Insert` auto-repeat, 3D mouse look/click swallowing, Raw Input & uGUI blocking | **P0** |
| **TM-06** | Unicode, Dead Keys & CJK IME | D3D11, D3D12 | `INP-01`, `INP-08`, `SEC-01` | Non-ASCII input (Cyrillic, Polish, German umlauts, Emoji `WM_UNICHAR`, Japanese/Chinese IME) in both ImGui and native Unity UI | **P1** |
| **TM-07** | Consumer Fault & Stack Recovery | D3D11, D3D12 | `LC-03`, `LC-04`, `LC-05`, `LC-07` | Deliberate exception mid-`ImGui.Begin()`, unclosed `BeginMenu()`, `RegisterTab`/`UnregisterTab` churn, circuit breaker trip | **P1** |
| **TM-08** | Device Loss & TDR Recovery | D3D11, D3D12 | `DXH-03`, `DXH-04`, `DXH-06`, `DXH-08` | Simulated GPU TDR (`dxcap -forcetdr`), vertex/index buffer dynamic growth (>50k vertices), resource invalidation & re-init | **P1** |
| **TM-09** | Steady-State Memory & Soak | D3D11, D3D12 | `MEM-01`–`MEM-08`, `AUD-07` | 60-minute continuous soak at 120+ FPS monitoring VRAM, DXGI live objects, native heap, `Il2CppStringToManaged` zero-alloc, and CoreCLR Gen0/Gen2 GC | **P0** |
| **TM-10** | Quiescent Teardown & Exit | D3D11, D3D12 | `TH-01`, `LC-01`, `LC-02`, `DXH-05`, `SEC-02`, `INP-03`, `AUD-05` | Clean `Application.Quit()` and `Alt+F4` under heavy UI load; `HideFlags.HideInHierarchy` `OnApplicationQuit`/`OnDestroy` execution & `imgui.ini` save | **P0** |
| **TM-11** | IL2CPP Patch & Stripping Resilience | D3D11, D3D12 | `AUD-01`, `AUD-02`, `AUD-08` | Per-method HarmonyX isolation, `NativeMethodInfoPtr` validation, C++-inlined `GetKeyInt`/`_Injected` detours, and stripped `InputLegacyModule`/`uGUI` survival | **P0** |
| **TM-12** | 3D Gameplay Mouse Look & `NotifyUpdate` | D3D11, D3D12 | `AUD-03`, `AUD-04`, `AUD-08` | 3D camera look (`GetRawInputBuffer()`, `Mouse.current.delta`, `Input.GetAxis`, `mousePositionDelta`) & click suppression via `NativeInputSystem.NotifyUpdate` & `UnityInputBlocker` | **P0** |
| **TM-13** | Live Passthrough Toggle & Key Flush | D3D11, D3D12 | `AUD-09`, `INP-02`, `INP-06` | Holding `W`+`Shift`+`LMB` in passthrough (`BlockAllInputsWhenOpen = false`) then toggling to blocked or focusing ImGui widget; verifying immediate cutoff | **P0** |
| **TM-14** | IL2CPP Interop & `ClassInjector` Stress | D3D11, D3D12 | `AUD-03`–`AUD-07` | `ClassInjector` idempotency, `Il2CppObjectBase.Pointer` identity under forced GC, main-thread proxy enforcement, and scene transition string caching | **P0** |

---

## 2. Detailed Test Scenarios & Pass/Fail Criteria

### TM-01: Bootstrap, Unicode Path Preload & Hook Discovery
- **Objective:** Verify that `DearImGuiInjectionPlus` initializes cleanly on D3D11 and D3D12 even when installed in a non-ASCII directory path, registers IL2CPP `MonoBehaviour` types before hooking DXGI, filters `DXGI_PRESENT_TEST`, and cleanly aborts on Vulkan/OpenGL without hooking DXGI.
- **Setup & Procedure:**
  1. Place the game or BepInEx plugin folder under a path containing non-ASCII characters (e.g., `D:\Games\Тест_日本語_🎮\BepInEx\plugins\DearImGuiInjectionPlus\`).
  2. Launch with `-force-d3d11`, then `-force-d3d12`, and finally `-force-vulkan` (or `-force-glcore`).
- **Pass Criteria:**
  - **IL2CPP Bootstrap Ordering (`AUD-06`):** `UnityMainThreadDispatcher` and `DearImGuiInjectionPlusRunner` are registered in IL2CPP via `ClassInjector` and attached to `DearImGuiInjectionPlus_Runner` with `UnityMainThreadDispatcher.IsMainThread == true` **before** `ApplyHarmonyPatchesSafely()` and `DearImGuiInjectionPlus.Init()` execute.
  - **D3D11 & D3D12:** `BepInEx/LogOutput.log` records `[DearImGuiInjectionPlus] Preloaded native cimgui.dll from: ...` and `Step 5 complete: ImGui DX11/DX12 bootstrap finished successfully` on Frame 1.
  - **D3D12 Queue Discovery:** Log records `[RendererFinder] Acquired Direct ID3D12CommandQueue directly from swapchain` (or via QI/fallback matching `ExpectedDevicePointer` and `Present` thread ID).
  - **Vulkan / OpenGL:** Log records `[DearImGuiInjectionPlus] Unsupported graphics backend: Vulkan. Aborting initialization.` No `DX11Renderer` or `DX12Renderer` hooks are installed.
- **Fail Criteria:** `DllNotFoundException: cimgui`, `UnityMainThreadDispatcher.IsMainThread == false` during bootstrap, hooking DXGI when running under Vulkan/OpenGL, or capturing a Compute/Copy command queue on D3D12.

---

### TM-02: SwapChain Resize, `Alt+Enter` Fullscreen & Window Minimize Stress
- **Objective:** Validate that zero backbuffer references are leaked across frames (`MEM-01`) and that `IDXGISwapChain::ResizeBuffers` succeeds across all window state transitions.
- **Setup & Procedure:**
  1. Open the ImGui overlay (`Insert`) with `SampleModWindow` and the Dear ImGui Demo Window visible.
  2. Press `Alt+Enter` 20 times consecutively at ~2 Hz to toggle between Exclusive Fullscreen / Fullscreen Window and Windowed mode.
  3. In Windowed mode, drag the window border continuously for 15 seconds to trigger hundreds of `WM_SIZE` and `ResizeBuffers` calls.
  4. Change in-game resolution across `1280x720` → `1920x1080` → `2560x1440` → `3840x2160`.
  5. Minimize the game window to the taskbar (`Win+D` or minimize button) for 10 seconds, then restore it.
- **Pass Criteria:**
  - Zero `DXGI_ERROR_INVALID_CALL` (`0x887A0001`) errors in Unity `Player.log` or `LogOutput.log`.
  - `ImGui.GetIO().DisplaySize` updates immediately to match the new client rect; UI remains crisp and properly scaled without black screens or stretched aspect ratios.
  - While minimized (`DisplaySize.X <= 0 || DisplaySize.Y <= 0`), `RenderDrawData` early-outs cleanly with zero DirectX exceptions.
- **Fail Criteria:** Unity logs `d3d11: failed to resize buffers` / `DXGI_ERROR_INVALID_CALL`, `Alt+Enter` is swallowed while the overlay is open, or the game crashes/hangs during minimize or resize.

---

### TM-03: SDR, sRGB, and HDR Backbuffer Format Compatibility
- **Objective:** Confirm that D3D11 RTV creation and D3D12 PSO `RTVFormat` dynamically adapt to linear, `_SRgb`, 10-bit HDR (`R10G10B10A2_UNorm`), and 16-bit float HDR (`R16G16B16A16_Float`) swapchains.
- **Setup & Procedure:**
  1. Launch a Unity title configured with Linear color space (using `R8G8B8A8_UNorm_SRgb` or `B8G8R8A8_UNorm_SRgb` swapchain views) and with Windows HD Color (HDR) enabled (`R10G10B10A2_UNorm` / `R16G16B16A16_Float`).
  2. Toggle HDR on and off in-game while the ImGui overlay is open under D3D12.
- **Pass Criteria:**
  - Under D3D12, `LogOutput.log` reports `InitImGuiDX12Internal completed with format ...` matching the active backbuffer format, and if changed mid-session, logs `Backbuffer format changed from ... to ... Recreating PSO.`
  - Under D3D11 `_SRgb` formats, the non-sRGB RTV alias (`R8G8B8A8_UNorm` / `B8G8R8A8_UNorm`) prevents double-gamma washout.
  - Under HDR formats, `DearImGuiTheme.ApplyGammaCorrection(true)` applies gamma correction and no D3D12 Debug Layer PSO/RTV format mismatch errors occur.
- **Fail Criteria:** Blank/invisible overlay on D3D12 when backbuffer format is `B8G8R8A8_UNorm` or `R16G16B16A16_Float`, or washed-out/clipped colors on sRGB/HDR swapchains.

---

### TM-04: Concurrency Torture — High-Frequency Input Queue vs. Uncapped Render Thread
- **Objective:** Prove that `ImGuiContext` and `ImGuiIO` are never mutated concurrently (`TH-02`) and that `StateSnapshotPipeline<T>`, `StructSnapshotPipeline<T>`, and `UnityMainThreadDispatcher` operate without deadlocks, torn reads, or off-main-thread IL2CPP violations (`TH-05`, `TH-06`, `AUD-04`).
- **Setup & Procedure:**
  1. Disable VSync (`QualitySettings.vSyncCount = 0`, `Application.targetFrameRate = -1`) so `Present` runs at 200–500+ FPS on the render thread.
  2. Use a 1000 Hz–8000 Hz gaming mouse to sweep continuously across ImGui widgets while typing rapidly into `ImGui.InputText` and scrolling the mouse wheel.
  3. In `TestPlugin`, publish a multi-field `StructSnapshotPipeline<TransformMatrixSnapshot>` (where all fields equal an incrementing sequence number `seq`) every `Update()` on the main thread, and assert in `OnRender()` on the render thread that all fields of `GetLatest()` match (`fieldA == fieldB == fieldC == fieldD`).
  4. Click `"Spawn Cube (via Main Thread Dispatcher)"` 100 times rapidly from the ImGui render thread, and invoke `UnityMainThreadDispatcher.InvokeOnMainThread` from a background `Task.Run` worker thread.
- **Pass Criteria:**
  - Zero native crashes (`0xC0000005` / `0xC0000374` heap corruption in `cimgui.dll` or `GameAssembly.dll`) over 10 minutes of continuous high-frequency input.
  - Zero torn-struct assertions triggered in `StructSnapshotPipeline<T>.GetLatest()`.
  - Zero main↔render thread deadlocks; all 100 cubes spawn on the Unity main thread with zero IL2CPP thread-attachment or `UnityException` errors.
- **Fail Criteria:** Any native access violation in `cimgui.dll` or `GameAssembly.dll`, any torn read detected across struct fields, or any 2000 ms timeout stall on the render thread.

---

### TM-05: Multi-Layer Input Swallowing, Stuck-Key Prevention & `BlockAllInputsWhenOpen`
- **Objective:** Validate input blocking across Win32 (`WM_INPUT`, mouse/keyboard messages), Legacy `UnityEngine.Input` (including C++-inlined `GetKeyInt` / `get_mousePosition_Injected`), `Unity.InputSystem` (`NewInputSystemBlocker` + `NativeInputSystem.NotifyUpdate`), and `UnityEngine.EventSystems.EventSystem`, as well as held-key release synthesis across both overlay toggles and live passthrough toggles (`INP-02`, `AUD-08`, `AUD-09`).
- **Setup & Procedure:**
  1. **Overlay-Open Stuck-Key Test (`INP-02`):** In 3D gameplay (overlay closed), hold down `W` (move forward), `Left Shift` (sprint), and `Right Mouse Button` (aim). While holding all three, press `Insert` to open the overlay (`BlockAllInputsWhenOpen == true`). Verify the character stops moving/aiming immediately upon opening the overlay. Release `W`, `Left Shift`, and `RMB` while the overlay is open, then press `Insert` to close the overlay.
  2. **Live Passthrough Toggle Stuck-Key Test (`AUD-09`):** Open the overlay (`Insert`), uncheck `BlockAllInputsWhenOpen` in `InputTestPanel` (enabling Selective Swallowing / Passthrough), hold `W` so the character walks forward in-game, and **while still holding `W`**, check `BlockAllInputsWhenOpen` (or click into `ImGui.InputText`).
  3. **Auto-Repeat Test (`INP-07`):** Hold down the `Insert` key continuously for 3 seconds.
  4. **Mid-Drag Close Test (`INP-07`):** Click and drag an ImGui window title bar or slider (`LMB` held), and while dragging, press `Insert` to close the overlay.
  5. **3D Gameplay Mouse Look & Click Swallowing (`AUD-08`, `INP-06`):**
     - With `BlockAllInputsWhenOpen = true`: in active 3D gameplay, move the mouse rapidly and click `LMB`/`RMB`/scroll wheel both *inside* an ImGui window and *on empty 3D viewport space outside* any ImGui window.
     - With `BlockAllInputsWhenOpen = false`: move and click on empty 3D viewport space outside ImGui windows (must control the 3D camera/weapons normally), then hover and click/drag/scroll inside an ImGui window (must be 100% captured by ImGui without rotating the 3D camera or firing weapons).
  6. **Cursor Fighting & uGUI Raycast Penetration (`INP-09`):** Position an ImGui window directly over a clickable Unity uGUI `Button` in a 3D game that calls `Cursor.lockState = CursorLockMode.Locked` every `Update()`. Click the ImGui window 20 times.
- **Pass Criteria:**
  - Character stops moving/aiming **immediately** when the overlay opens (`BlockAllInputsWhenOpen == true`) AND when `BlockAllInputsWhenOpen` is toggled from `false` to `true` while holding `W`; zero stuck movement after releasing `W` or closing the overlay.
  - Zero 3D camera rotation (look) and zero weapon firing / 3D world clicks when `BlockAllInputsWhenOpen == true` or when hovering/clicking an ImGui window (`WantCaptureMouse == true`).
  - Holding `Insert` toggles the overlay **once** per physical press (no rapid strobe).
  - Closing mid-drag releases Win32 mouse capture (`GetCapture() == IntPtr.Zero`) and clears `_mouseButtonsDown`.
  - Underlying uGUI buttons never fire when clicking on an overlapping ImGui window, and the OS cursor stays unlocked, visible, and unclipped (`ClipCursor(NULL)`) across the entire monitor.
- **Fail Criteria:** Player keeps walking/firing after disabling passthrough or closing the overlay, 3D camera rotates or weapons fire while clicking/moving over ImGui, or uGUI buttons beneath ImGui windows are triggered.

---

### TM-06: Unicode Window Integrity, Dead Keys, Emoji (`WM_UNICHAR`) & CJK IME Input
- **Objective:** Verify that `InputManager` preserves Unity's Unicode window status (`IsWindowUnicode(hwnd) != 0`) and that international text input works in both ImGui and the game itself (`INP-01`, `INP-08`, `SEC-01`).
- **Setup & Procedure:**
  1. Inspect `User32.IsWindowUnicode(InputManager.WindowHandle)` before and after `InputManager.Subclass` — must remain `true`.
  2. Open the overlay and focus `ImGui.InputText` in `InputTestPanel`.
  3. Enter dead-key combinations (e.g., Polish `ąęćłńóśźż`, German `¨` + `o` → `ö`, French `^` + `e` → `ê`), Cyrillic (`Привет`), CJK characters via Microsoft Pinyin / Japanese IME (`日本語テスト`), and supplementary-plane characters / emojis via `Win+.` or `WM_UNICHAR`.
  4. Close the overlay (`Insert`) and type the same non-ASCII / IME strings into a native Unity `InputField` / `TMP_InputField` in-game.
- **Pass Criteria:**
  - `IsWindowUnicode(hwnd)` returns `true` throughout the entire session.
  - While the overlay is open, IME candidate windows render properly (`DefWindowProcW` routing) and committed characters appear accurately in `ImGui.InputText` without leaking to the game.
  - While the overlay is closed, native Unity text fields receive full Unicode and IME input without `?` corruption.
- **Fail Criteria:** `IsWindowUnicode(hwnd)` becomes `false`, `MultiByteToWideChar` corrupts the heap, or in-game chat/text fields break for non-ASCII characters when the overlay is closed.

---

### TM-07: Consumer Fault Isolation, Stack Recovery & Circuit Breaker Validation
- **Objective:** Verify that buggy third-party plugins throwing exceptions inside `ImGui.Begin()`/`BeginMenu()` or during `Present` cannot crash the game, corrupt ImGui's internal stacks, or flood `LogOutput.log` (`LC-03`, `LC-04`, `LC-05`, `LC-07`).
- **Setup & Procedure:**
  1. Trigger the fault-injection controls in `TestPlugin` (`FaultInjectionTestPanel`):
     - **Fault A (Window Stack Imbalance):** Inside `IImGuiWindow.OnRender()`, call `ImGui.Begin("FaultyWindow")`, `ImGui.PushID(123)`, `ImGui.BeginChild("Child")`, and throw `new InvalidOperationException("Simulated plugin crash")` before calling `EndChild()`, `PopID()`, or `End()`.
     - **Fault B (Tab Menu Stack Imbalance):** Inside a `RegisterTab` callback, call `ImGui.BeginMenu("SubMenu")`, `ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF)`, and throw an exception before `PopStyleColor()` / `EndMenu()`.
     - **Fault C (Tab Lifecycle Churn):** Register and unregister a tab 50 times in a loop.
  2. Observe `TestPlugin` panels and `ImGui.ShowDemoWindow()` rendering alongside the throwing window/tab.
- **Pass Criteria:**
  - Fault A and Fault B are caught per-callback; `RestoreImGuiStack()` (`igErrorCheckEndFrameRecover`) and `RestoreImGuiWindowStack()` (`igErrorCheckEndWindowRecover`) unwind the broken ImGui stacks cleanly.
  - All other registered windows continue rendering normally on the same and subsequent frames with zero native assertions in `cimgui.dll`.
  - Fault C results in **exactly one** active tab entry when registered and **zero** entries when disposed.
  - If a backend-level render error is forced for 5 consecutive frames, the circuit breaker trips and logs a single warning, pausing render attempts for 5 seconds instead of logging 60–240 stack traces per second.
- **Fail Criteria:** Unhandled exception terminates the Unity process, subsequent windows fail to render due to corrupted ImGui window/ID stack, or duplicate menu tabs accumulate.

---

### TM-08: Dynamic Geometry Growth & GPU Device-Loss / TDR Recovery
- **Objective:** Validate dynamic vertex/index upload buffer reallocation under heavy geometry load (`DXH-04`) and automatic backend recovery after `DXGI_ERROR_DEVICE_REMOVED` / `DXGI_ERROR_DEVICE_RESET` (`DXH-06`, `DXH-08`).
- **Setup & Procedure:**
  1. **Geometry Burst:** In `TestPlugin` (`FaultInjectionTestPanel`), trigger the geometry burst test rendering >100,000 vertices and >150,000 indices in a single frame across all swapchain backbuffers.
  2. **Simulated TDR / Device Reset:** Trigger a synthetic device invalidation (`InvalidateDevice()`) or TDR via Windows SDK `dxcap.exe -forcetdr`.
- **Pass Criteria:**
  - Vertex and index buffers in D3D11 (`_vertexBuffer`, `_indexBuffer`) and D3D12 (`currentFrameContext.VertexBuffer`, `currentFrameContext.IndexBuffer`) grow seamlessly without visual corruption, `IndexOutOfRangeException`, or use-after-free on in-flight D3D12 upload heaps.
  - Upon device loss (`DeviceRemovedReason.Failure`), `LogOutput.log` reports `Device loss detected. Invalidating device objects.` All stale COM objects are disposed without crashing the process, and the backend reinitializes automatically on the next valid `Present`.
- **Fail Criteria:** GPU hang, corrupted vertex triangles on D3D12 triple-buffered swapchains, or unhandled `SharpDXException` crashing the process.

---

### TM-09: 60-Minute Steady-State Memory, Zero-Alloc Interop & Resource Leak Soak
- **Objective:** Confirm zero COM reference leaks (`MEM-01`–`MEM-06`), zero per-frame `IL2CPP.Il2CppStringToManaged` string allocations (`AUD-07`), and negligible managed GC pressure (`MEM-07`) during extended gameplay.
- **Setup & Procedure:**
  1. Launch the game with the overlay open (`MemoryBenchmarkTestPanel` + `ShowDemoWindow` active, updating `StateSnapshotPipeline` every frame in `TestPluginRunner.Update()`).
  2. Record baseline metrics after 2 minutes of warmup:
     - Process Private Working Set & Unmanaged Heap (`Process.GetCurrentProcess().PrivateMemorySize64`)
     - Dedicated GPU Memory usage
     - Managed heap size (`GC.GetTotalMemory(false)`), per-frame allocation rate (`GC.GetAllocatedBytesForCurrentThread()`), and `GC.CollectionCount(0/1/2)`
  3. Let the game run at 120+ FPS for 60 minutes (~432,000 frames).
- **Pass Criteria:**
  - Unmanaged memory and Dedicated VRAM plateau after warmup (net growth $< 5\text{ MB}$ over 60 minutes, confirming zero leaked `ID3D11RenderTargetView` or pipeline state references per frame).
  - `TestPluginRunner.Update()` only marshals `activeScene.name` (`IL2CPP.Il2CppStringToManaged`) when `activeScene.handle` changes, maintaining near-zero steady-state managed allocations per frame.
  - If running with the DirectX Debug Layer (`IDXGIDebug1::ReportLiveObjects`), live RTV/SRV/Buffer counts remain constant between minute 2 and minute 60.
- **Fail Criteria:** Monotonic linear growth in VRAM or process private bytes proportional to frame count, or high Gen0 GC collection frequency caused by per-frame IL2CPP string/wrapper marshaling.

---

### TM-10: Quiescent Teardown, `FrameGate` Drain & Clean Process Exit
- **Objective:** Verify that exiting the game under both D3D11 and D3D12 completes cleanly through the inverted quiescence sequence without `Process.Kill()`, access violations, re-entrant `Object.Destroy` crashes, or skipped plugin `OnApplicationQuit`/`OnDestroy` handlers (`TH-01`, `LC-01`, `LC-02`, `DXH-05`, `SEC-02`, `INP-03`, `AUD-05`).
- **Setup & Procedure:**
  1. Verify that `DearImGuiInjectionPlus_Runner` and `TestPlugin_Runner` are created with `HideFlags.HideInHierarchy` (not `HideFlags.HideAndDontSave`).
  2. Open the ImGui overlay, move/dock windows to modify layout state, and keep the render thread busy at uncapped FPS.
  3. Trigger shutdown via:
     - Method A: In-game Quit button (`Application.Quit()`)
     - Method B: Window close button / `Alt+F4`
  4. Repeat 10 times on D3D11 and 10 times on D3D12.
- **Pass Criteria:**
  - `LogOutput.log` contains the complete teardown sequence in exact order:
    1. `[DearImGuiInjectionPlus] Engine shutdown initiated...`
    2. `[DX11Renderer] Successfully disabled SwapChainPresentHook.` / `SwapChainPresent1Hook.`
    3. `[InputManager] Removed window subclass on window ...`
    4. `[ImGuiDX11] Shutting down DX11 ImGui backend...` (or D3D12 `WaitForGpuIdle()` + `InvalidateDevice()`)
    5. `[DearImGuiInjectionPlus] Plugin shutdown completed safely.`
  - `TestPlugin`'s `TestPlugin_OnDestroy_Timestamp.txt` sentinel file is written on 100% of runs (`AUD-05`).
  - `BepInEx/config/DearImGuiInjectionPlus_imgui.ini` is saved cleanly to disk with updated window positions.
  - Process exits with exit code `0` and **zero** Windows Event Viewer Application Error (`0xC0000005` in `dxgi.dll`, `d3d12core.dll`, `cimgui.dll`, `GameAssembly.dll`, or `UnityPlayer.dll`) entries.
- **Fail Criteria:** Truncated `LogOutput.log` tail, missing `TestPlugin_OnDestroy_Timestamp.txt` execution, corrupted/missing `DearImGuiInjectionPlus_imgui.ini`, or any post-exit crash dump.

---

### TM-11: IL2CPP HarmonyX Detour Completeness, C++ Inlining & `UnityLinker` Stripping Resilience
- **Objective:** Certify that `ApplyHarmonyPatchesSafely()` accurately validates `NativeMethodInfoPtr_*` fields, patches private/internal C++-inlined `UnityEngine.Input` methods (`GetKeyInt`, `GetKeyDownInt`, `GetKeyUpInt`, `get_mousePosition_Injected`, `get_mouseScrollDelta_Injected`, `get_mousePositionDelta_Injected`), merges `[HarmonyPrefix]` + `[HarmonyPostfix]` detours per `MethodBase`, and survives aggressive `UnityLinker` stripping without aborting remaining patches or throwing per-frame exceptions (`AUD-01`, `AUD-02`, `AUD-08`).
- **Setup & Procedure:**
  1. Launch an IL2CPP game where specific legacy `UnityEngine.Input` methods (such as `GetKey(string)`, `inputString`, or `mousePositionDelta`) have been stripped by `UnityLinker` (having no `NativeMethodInfoPtr_*` in `UnityEngine.InputLegacyModule.dll`).
  2. Inspect `BepInEx/LogOutput.log` for the `[DearImGuiInjectionPlus] Harmony patching complete (X applied, Y skipped)` entry and verify that zero `EntryPointNotFoundException`, `NullReferenceException`, or `Il2CppDetourMethodPatcher` trampoline errors appear.
  3. Test an IL2CPP build where `Assembly-CSharp.dll` calls `Input.GetKey(KeyCode.W)`, `Input.GetKeyDown(KeyCode.Mouse0)`, `Input.mousePosition`, and `Input.mouseScrollDelta` (which the C++ compiler inlines into `Input_GetKeyInt_m...`, `Input_GetKeyDownInt_m...`, `Input_get_mousePosition_Injected_m...`, and `Input_get_mouseScrollDelta_Injected_m...`).
  4. Simulate or run in a game where `Input.get_mousePosition` is completely stripped while the overlay cursor is hidden (`!IsCursorVisible`), verifying that `DearImGuiInjectionPlusRunner.Update()` catches the missing method once and disables `_legacyMousePosSupported` without per-frame exceptions (`AUD-02`).
- **Pass Criteria:**
  - `ApplyHarmonyPatchesSafely()` skips stripped methods cleanly before invoking `_harmony.Patch()` by checking `Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(targetMethod)`.
  - All non-stripped public and private (`NonPublic`) `Input` methods—including `GetKeyInt`, `GetKeyDownInt`, `GetKeyUpInt`, `get_mousePosition_Injected`, and `get_mouseScrollDelta_Injected`—are detoured and return blocked values (`false`, `0f`, `Vector2.zero`, `LastMousePosition`) when `ShouldBlockMouse()` / `ShouldBlockKeyboard()` is active.
  - `Input.get_mousePosition` and `Input.get_mousePosition_Injected` each receive a single combined prefix+postfix detour rather than duplicate sequential detours.
- **Fail Criteria:** Any single stripped method causes `ApplyHarmonyPatchesSafely()` to abort remaining `Input`/`Cursor`/`EventSystem` patches, `NonPublic` `_Injected`/`*Int` methods are skipped due to missing `BindingFlags.NonPublic`, or C++-inlined `Input.GetKey(KeyCode)` / `Input.mousePosition` calls bypass blocking.

---

### TM-12: IL2CPP 3D Gameplay Camera Look, Raw Input & `NativeInputSystem.NotifyUpdate` Interception
- **Objective:** Certify that 3D gameplay mouse look (camera rotation) and mouse clicks (`LMB`/`RMB`/`MMB`) are 100% blocked when captured by ImGui across games using `Unity.InputSystem`, Legacy `UnityEngine.Input`, or Win32 `GetRawInputBuffer()`, including IL2CPP builds where `InputSystem.DisableDevice` is stripped (`AUD-03`, `AUD-04`, `AUD-08`).
- **Setup & Procedure:**
  1. Launch an IL2CPP 3D first-person or third-person game in active 3D gameplay (where moving the mouse rotates the 3D camera and clicking `LMB`/`RMB` attacks/aims).
  2. Inspect `LogOutput.log` to verify `[NewInputSystemBlocker] Hooked UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate.`
  3. **Full Swallowing Mode (`BlockAllInputsWhenOpen = true`):**
     - Press `Insert` to open the overlay.
     - Sweep the mouse across the screen, drag ImGui windows, click buttons inside ImGui windows, and click on empty 3D world space outside ImGui windows.
  4. **Selective Swallowing Mode (`BlockAllInputsWhenOpen = false`):**
     - Uncheck `BlockAllInputsWhenOpen` in `InputTestPanel`.
     - Move the mouse over empty 3D world space (`WantCaptureMouse == false`) and click/move to verify 3D camera rotation and gameplay actions pass through normally.
     - Move the cursor onto an ImGui window (`WantCaptureMouse == true`): sweep the mouse across the ImGui window, drag sliders, and click `LMB`/`RMB` inside the ImGui window while simultaneously holding `W` on the keyboard (`WantCaptureKeyboard == false`).
- **Pass Criteria:**
  - **Full Swallowing:** Zero 3D camera rotation (`Mouse.current.delta == Vector2.zero`, `Input.GetAxis("Mouse X/Y") == 0f`) and zero in-game mouse clicks occur anywhere on screen while the overlay is open.
  - **Selective Swallowing:** When hovering an ImGui window (`WantCaptureMouse == true`, `WantCaptureKeyboard == false`), `NativeInputSystem.NotifyUpdatePrefix` and `NewInputSystemBlocker` suppress mouse/pointer events while allowing keyboard events (`W`) to continue cleanly; when the cursor leaves ImGui windows (`WantCaptureMouse == false`), 3D mouse look and clicks immediately resume.
  - Works identically even if `InputSystem.DisableDevice` and `ReadOnlyArray<InputDevice>.get_Item` are stripped from `Unity.InputSystem.dll`.
- **Fail Criteria:** 3D camera rotates while dragging an ImGui slider/window, clicking an ImGui button fires the player's weapon in 3D gameplay, or `Mouse.current` fails to re-enable when switching to passthrough or closing the overlay.

---

### TM-13: Live Passthrough State Transitions & Multi-Layer Stuck-Key Flush
- **Objective:** Certify that holding movement keys (`W`, `A`, `S`, `D`, `Shift`, `Space`) or mouse buttons (`LMB`, `RMB`) while input passthrough is active and then transitioning into blocked mode (by toggling `BlockAllInputsWhenOpen` to `true`, focusing an `ImGui.InputText`, or hovering an ImGui window) immediately cuts off the in-game input without leaving keys or buttons stuck (`AUD-09`, `INP-02`).
- **Setup & Procedure:**
  1. Open the ImGui overlay (`Insert`) and uncheck `BlockAllInputsWhenOpen` in `InputTestPanel` so Selective Swallowing (passthrough) is active.
  2. Click on empty 3D gameplay space and hold down `W` + `Left Shift` so the player character sprints forward continuously.
  3. **Transition Case A (Global Block Toggle while Key Held):** While keeping `W` + `Left Shift` physically depressed, click the `BlockAllInputsWhenOpen` checkbox in `InputTestPanel` to re-enable full input blocking. Observe the character while `W` is still held, and then physically release `W` + `Left Shift`.
  4. **Transition Case B (Selective Keyboard Capture while Key Held):** With `BlockAllInputsWhenOpen = false`, hold `W` so the character walks forward, and while holding `W`, click inside the `##UnicodeInput` text box in `InputTestPanel` (`WantCaptureKeyboard` transitions `false` → `true`).
  5. **Transition Case C (Selective Mouse Capture while Mouse Button Held):** With `BlockAllInputsWhenOpen = false`, hold `RMB` (aim) or `LMB` (continuous fire) on empty 3D space (`WantCaptureMouse == false`), drag the cursor onto an ImGui window (`WantCaptureMouse` transitions `false` → `true`), and release `RMB`/`LMB` over the ImGui window.
- **Pass Criteria:**
  - In Cases A, B, and C, the character **immediately** stops walking/sprinting/firing on the exact frame that blocking engages (`FlushDepressedKeysOnCaptureTransition()` + `Input.ResetInputAxes()` + `InputSystem.ResetDevice()`).
  - Synthesized `WM_KEYUP` and `WM_*BUTTONUP` messages posted by `ReleaseRemainingDepressedKeys(hWnd, keepTrackedForSubclassPassThrough: true)` pass through `SubclassHandler.CheckAndClearKey` and `NewInputSystemBlocker.NotifyUpdatePrefix` (`_pendingKeyReleaseFlushFrames`) without being swallowed.
  - Subsequent physical release of `W`, `Left Shift`, or `LMB`/`RMB` does not re-trigger movement or leave sticky state when passthrough is re-enabled or the overlay is closed.
- **Fail Criteria:** Character continues walking forward after `BlockAllInputsWhenOpen` is enabled or `ImGui.InputText` is focused, or synthesized/physical `WM_KEYUP` is swallowed by `SubclassHandler` while `IsCursorVisible == true`.

---

### TM-14: IL2CPP Interop Boundary, `ClassInjector` Lifecycle & Zero-Allocation Stress
- **Objective:** Certify IL2CPP-specific runtime invariants across `Il2CppInterop` proxy lifecycles, native pointer identity, main-thread dispatching, and garbage collection (`AUD-03`–`AUD-07`).
- **Setup & Procedure:**
  1. **Idempotent `ClassInjector` Registration (`AUD-06`):** Call `UnityMainThreadDispatcher.Register()` multiple times from `TestPlugin` after `DearImGuiInjectionPlus` has already loaded.
  2. **`Il2CppObjectBase.Pointer` Device Tracking under GC Pressure (`AUD-04`):** With `Unity.InputSystem` active and the overlay open (`NewInputSystemBlocker` tracking disabled devices), trigger 20 consecutive full garbage collections (`GC.Collect(2, GCCollectionMode.Forced, true)`) via `MemoryBenchmarkTestPanel`, then close the overlay (`Insert`) and verify `Mouse.current` and `Keyboard.current` are re-enabled.
  3. **Cross-Thread Cursor Toggle (`AUD-04`):** Toggle `DearImGuiInjectionPlus.ToggleCursorVisibility()` directly from an ImGui button callback on the DirectX `Present` render thread 50 times in a row.
  4. **Scene Transition String Cache (`AUD-07`):** Load or transition between two Unity scenes while monitoring `TestPluginRunner.Update()`'s `CurrentScene` snapshot field in `ConcurrencyTestPanel`.
- **Pass Criteria:**
  - Calling `UnityMainThreadDispatcher.Register()` repeatedly is a safe no-op (`ClassInjector.IsTypeRegisteredInIl2Cpp` guard prevents duplicate registration exceptions).
  - `_disabledDevices` keyed by `Il2CppObjectBase.Pointer` (`IntPtr`) accurately restores all disabled `InputDevice` instances after forced Gen2 GC sweeps.
  - Toggling cursor visibility from the render thread dispatches `NewInputSystemBlocker.SetBlocked`, ` UnityInputBlocker.TryResetInputAxes`, and `Cursor.lockState`/`Cursor.visible` mutations strictly onto the Unity main thread via `UnityMainThreadDispatcher.Enqueue`.
  - `TestPluginRunner` detects the changed `Scene.handle` and updates `_cachedSceneName` exactly once per scene change with zero per-frame string allocations.
- **Fail Criteria:** `ArgumentException` from `ClassInjector.RegisterTypeInIl2Cpp`, devices remaining permanently disabled after GC collects transient `Il2CppObjectBase` wrappers, or cross-thread IL2CPP invocation crashes.
