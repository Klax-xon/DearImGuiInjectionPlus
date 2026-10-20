# IN-GAME VALIDATION & STRESS TEST MATRIX — DearImGuiInjectionPlus v1.0.0

**Target Assembly:** `DearImGuiInjectionPlus.dll`, `RendererFinder.dll`, `TestPlugin.dll`  
**Target Runtimes:** Unity Mono (2019 LTS, 2021 LTS, 2022 LTS, Unity 6) — Windows x64  
**Graphics APIs:** Direct3D 11 (`-force-d3d11`) & Direct3D 12 (`-force-d3d12`)  

This test matrix defines systematic runtime validation scenarios, execution procedures, instrumentation hooks for `TestPlugin`, and strict pass/fail criteria for certifying `DearImGuiInjectionPlus` v1.0.0 in-game.

---

## 1. Summary Test Matrix

| Test ID | Category | Target API | Related Audit IDs | Scenario Summary | Priority |
| :--- | :--- | :---: | :--- | :--- | :---: |
| **TM-01** | Bootstrap & Hook Discovery | D3D11, D3D12, Vulkan/GL | `DXH-02`, `DXH-07`, `DXH-10`, `LC-08` | Cold start, Unicode install path (`LoadLibraryW`), `DXGI_PRESENT_TEST` filtering, unsupported API abort | **P0** |
| **TM-02** | SwapChain Resize & Fullscreen | D3D11, D3D12 | `MEM-01`, `INP-02`, `DXH-06` | Rapid `Alt+Enter`, windowed drag-resize, 1080p↔4K resolution switching, minimize/restore (`DisplaySize == 0`) | **P0** |
| **TM-03** | SDR, sRGB & HDR Backbuffers | D3D11, D3D12 | `DXH-01`, `DXH-11` | `R8G8B8A8_UNorm`, `B8G8R8A8_UNorm_SRgb`, `R10G10B10A2_UNorm` (HDR10), `R16G16B16A16_Float` (scRGB) PSO & gamma | **P0** |
| **TM-04** | Concurrency & High-Rate Input | D3D11, D3D12 | `TH-02`, `TH-05`, `TH-06` | 1000–8000 Hz mouse movement + rapid typing + 200+ FPS `Present` + `StateSnapshotPipeline` / `StructSnapshotPipeline` stress | **P0** |
| **TM-05** | Input Blocking & Stuck Keys | D3D11, D3D12 | `INP-02`, `INP-05`, `INP-06`, `INP-07`, `INP-09` | Held `W`+`Shift`+`RMB` across toggle, `Insert` auto-repeat, `BlockAllInputsWhenOpen` live toggle, Raw Input & uGUI blocking | **P0** |
| **TM-06** | Unicode, Dead Keys & CJK IME | D3D11, D3D12 | `INP-01`, `INP-08`, `SEC-01` | Non-ASCII input (Cyrillic, German umlauts, Emoji `WM_UNICHAR`, Japanese/Chinese IME) in both ImGui and native Unity UI | **P1** |
| **TM-07** | Consumer Fault & Stack Recovery | D3D11, D3D12 | `LC-03`, `LC-04`, `LC-05`, `LC-07` | Deliberate exception mid-`ImGui.Begin()`, unclosed `BeginMenu()`, `RegisterTab`/`UnregisterTab` churn, circuit breaker trip | **P1** |
| **TM-08** | Device Loss & TDR Recovery | D3D11, D3D12 | `DXH-03`, `DXH-04`, `DXH-06`, `DXH-08` | Simulated GPU TDR (`dxcap -forcetdr`), vertex/index buffer dynamic growth (>50k vertices), resource invalidation & re-init | **P1** |
| **TM-09** | Steady-State Memory & Soak | D3D11, D3D12 | `MEM-01`–`MEM-08` | 60-minute continuous soak at 120+ FPS monitoring VRAM, DXGI live objects, native heap, and managed Gen0/Gen2 GC collections | **P0** |
| **TM-10** | Quiescent Teardown & Exit | D3D11, D3D12 | `TH-01`, `LC-01`, `LC-02`, `DXH-05`, `SEC-02`, `INP-03` | Clean `Application.Quit()` and `Alt+F4` under heavy UI load; co-plugin `OnDestroy` execution & `imgui.ini` persistence | **P0** |

---

## 2. Detailed Test Scenarios & Pass/Fail Criteria

### TM-01: Bootstrap, Unicode Path Preload & Hook Discovery
- **Objective:** Verify that `DearImGuiInjectionPlus` initializes cleanly on D3D11 and D3D12 even when installed in a non-ASCII directory path, filters `DXGI_PRESENT_TEST`, and cleanly aborts on Vulkan/OpenGL without hooking DXGI.
- **Setup & Procedure:**
  1. Place the game or BepInEx plugin folder under a path containing non-ASCII characters (e.g., `D:\Games\Тест_日本語_🎮\BepInEx\plugins\DearImGuiInjectionPlus\`).
  2. Launch with `-force-d3d11`, then `-force-d3d12`, and finally `-force-vulkan` (or `-force-glcore`).
- **Pass Criteria:**
  - **D3D11 & D3D12:** `BepInEx/LogOutput.log` records `[DearImGuiInjectionPlus] Preloaded native cimgui.dll from: ...` and `Step 5 complete: ImGui DX11/DX12 bootstrap finished successfully` on Frame 1.
  - **D3D12 Queue Discovery:** Log records `[RendererFinder] Acquired Direct ID3D12CommandQueue directly from swapchain` (or via QI/fallback matching `ExpectedDevicePointer` and `Present` thread ID).
  - **Vulkan / OpenGL:** Log records `[DearImGuiInjectionPlus] Unsupported graphics backend: Vulkan. Aborting initialization.` No `DX11Renderer` or `DX12Renderer` hooks are installed.
- **Fail Criteria:** `DllNotFoundException: cimgui`, hooking DXGI when running under Vulkan/OpenGL, or capturing a Compute/Copy command queue on D3D12.

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
  1. Launch a Unity title configured with Linear color space (using `R8G8B8A8_UNorm_SRgb` or `B8G8R8A8_UNorm_SRgb` swapchainviews) and with Windows HD Color (HDR) enabled (`R10G10B10A2_UNorm` / `R16G16B16A16_Float`).
  2. Toggle HDR on and off in-game while the ImGui overlay is open under D3D12.
- **Pass Criteria:**
  - Under D3D12, `LogOutput.log` reports `InitImGuiDX12Internal completed with format ...` matching the active backbuffer format, and if changed mid-session, logs `Backbuffer format changed from ... to ... Recreating PSO.`
  - Under D3D11 `_SRgb` formats, the non-sRGB RTV alias (`R8G8B8A8_UNorm` / `B8G8R8A8_UNorm`) prevents double-gamma washout.
  - Under HDR formats, `DearImGuiTheme.ApplyGammaCorrection(true)` applies gamma correction and no D3D12 Debug Layer PSO/RTV format mismatch errors occur.
- **Fail Criteria:** Blank/invisible overlay on D3D12 when backbuffer format is `B8G8R8A8_UNorm` or `R16G16B16A16_Float`, or washed-out/clipped colors on sRGB/HDR swapchains.

---

### TM-04: Concurrency Torture — High-Frequency Input Queue vs. Uncapped Render Thread
- **Objective:** Prove that `ImGuiContext` and `ImGuiIO` are never mutated concurrently (`TH-02`) and that `StateSnapshotPipeline<T>`, `StructSnapshotPipeline<T>`, and `UnityMainThreadDispatcher` operate without deadlocks or torn reads (`TH-05`, `TH-06`).
- **Setup & Procedure:**
  1. Disable VSync (`QualitySettings.vSyncCount = 0`, `Application.targetFrameRate = -1`) so `Present` runs at 200–500+ FPS on the render thread.
  2. Use a 1000 Hz–8000 Hz gaming mouse to sweep continuously across ImGui widgets while typing rapidly into `ImGui.InputText` and scrolling the mouse wheel.
  3. In `TestPlugin`, publish a multi-field `StructSnapshotPipeline<TransformMatrixSnapshot>` (where all fields equal an incrementing sequence number `seq`) every `Update()` on the main thread, and assert in `OnRender()` on the render thread that all fields of `GetLatest()` match (`fieldA == fieldB == fieldC == fieldD`).
  4. Click `"Spawn Cube (via Main Thread Dispatcher)"` 100 times rapidly from the ImGui render thread, and invoke `UnityMainThreadDispatcher.InvokeOnMainThread` from a background `Task.Run` worker thread.
- **Pass Criteria:**
  - Zero native crashes (`0xC0000005` / `0xC0000374` heap corruption in `cimgui.dll`) over 10 minutes of continuous high-frequency input.
  - Zero torn-struct assertions triggered in `StructSnapshotPipeline<T>.GetLatest()`.
  - Zero main↔render thread deadlocks; all 100 cubes spawn on the Unity main thread with zero `UnityEngine.UnityException` threading errors.
- **Fail Criteria:** Any native access violation in `cimgui.dll`, any torn read detected across struct fields, or any 2000 ms timeout stall on the render thread.

---

### TM-05: Multi-Layer Input Swallowing, Stuck-Key Prevention & `BlockAllInputsWhenOpen`
- **Objective:** Validate input blocking across Win32 (`WM_INPUT`, mouse/keyboard messages), Legacy `UnityEngine.Input`, `Unity.InputSystem`, and `UnityEngine.EventSystems.EventSystem`, as well as held-key release synthesis.
- **Setup & Procedure:**
  1. **Stuck-Key Test (`INP-02`):** In-game (overlay closed), hold down `W` (move forward), `Left Shift` (sprint), and `Right Mouse Button` (aim). While holding all three, press `Insert` to open the overlay. Release `W`, `Left Shift`, and `RMB` while the overlay is open, then press `Insert` to close the overlay. Repeat a second time where you keep holding `W` while opening the overlay, release `W` *while typing in an ImGui text box*, and close the overlay.
  2. **Auto-Repeat Test (`INP-07`):** Hold down the `Insert` key continuously for 3 seconds.
  3. **Mid-Drag Close Test (`INP-07`):** Click and drag an ImGui window title bar or slider (`LMB` held), and while dragging, press `Insert` to close the overlay.
  4. **Selective vs. Full Blocking (`INP-06`):**
     - With `BlockAllInputsWhenOpen = true`: click and type both *inside* an ImGui window and *on empty screen space outside* any ImGui window.
     - Set `BlockAllInputsWhenOpen = false` in `DearImGuiInjectionPlus.cfg` (or live via ConfigurationManager): click on empty space outside ImGui windows (should reach the game), then click/type inside an ImGui window (should be swallowed because `WantCaptureMouse` / `WantCaptureKeyboard` is true).
  5. **Cursor Fighting & uGUI Raycast Penetration (`INP-09`):** Position an ImGui window directly over a clickable Unity uGUI `Button` in a FPS game that calls `Cursor.lockState = CursorLockMode.Locked` every `Update()`. Click the ImGui window 20 times.
- **Pass Criteria:**
  - Character stops moving/aiming immediately when keys/buttons are released while the overlay is open; zero stuck movement after closing the overlay.
  - Holding `Insert` toggles the overlay **once** per physical press (no rapid strobe).
  - Closing mid-drag releases Win32 mouse capture (`GetCapture() == IntPtr.Zero`) and clears `_mouseButtonsDown`.
  - Underlying uGUI buttons never fire when clicking on an overlapping ImGui window, and the OS cursor stays unlocked, visible, and unclipped (`ClipCursor(NULL)`) across the entire monitor.
  - Window border resize cursors (`IDC_SIZENWSE`, `IDC_SIZEWE`) still appear when hovering the Win32 window frame in windowed mode.
- **Fail Criteria:** Player keeps walking/firing after closing the overlay, camera rotates via `WM_INPUT` while moving mouse over ImGui, or uGUI buttons beneath ImGui windows are triggered.

---

### TM-06: Unicode Window Integrity, Dead Keys, Emoji (`WM_UNICHAR`) & CJK IME Input
- **Objective:** Verify that `InputManager` preserves Unity's Unicode window status (`IsWindowUnicode(hwnd) != 0`) and that international text input works in both ImGui and the game itself (`INP-01`, `INP-08`, `SEC-01`).
- **Setup & Procedure:**
  1. Inspect `User32.IsWindowUnicode(InputManager.WindowHandle)` before and after `InputManager.Subclass` — must remain `true`.
  2. Open the overlay and focus `ImGui.InputText` in `SampleModWindow`.
  3. Enter dead-key combinations (e.g., German `¨` + `o` → `ö`, French `^` + `e` → `ê`), Cyrillic (`Привет`), CJK characters via Microsoft Pinyin / Japanese IME (`日本語テスト`), and supplementary-plane characters / emojis via `Win+.` or `WM_UNICHAR`.
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
  1. Add three fault-injection toggles to `TestPlugin`:
     - **Fault A (Window Stack Imbalance):** Inside `IImGuiWindow.OnRender()`, call `ImGui.Begin("FaultyWindow")`, `ImGui.PushID(123)`, `ImGui.BeginChild("Child")`, and throw `new InvalidOperationException("Simulated plugin crash")` before calling `EndChild()`, `PopID()`, or `End()`.
     - **Fault B (Tab Menu Stack Imbalance):** Inside a `RegisterTab` callback, call `ImGui.BeginMenu("SubMenu")`, `ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF)`, and throw an exception before `PopStyleColor()` / `EndMenu()`.
     - **Fault C (Tab Lifecycle Churn):** Enable and disable `TestPlugin` (`enabled = false; enabled = true;`) 50 times in a loop.
  2. Observe `SampleModWindow` and `ImGui.ShowDemoWindow()` rendering alongside the throwing window/tab.
- **Pass Criteria:**
  - Fault A and Fault B are caught per-callback; `RestoreImGuiStack()` (`igErrorCheckEndFrameRecover`) and `RestoreImGuiWindowStack()` (`igErrorCheckEndWindowRecover`) unwind the broken ImGui stacks cleanly.
  - All other registered windows (`SampleModWindow`, Demo Window) continue rendering normally on the same and subsequent frames with zero native assertions in `cimgui.dll`.
  - Fault C (`OnEnable`/`OnDisable` 50×) results in **exactly one** `"Toggle Sample View"` entry under the `"Mods"` menu when enabled, and **zero** entries when disabled.
  - If a backend-level render error is forced for 5 consecutive frames, the circuit breaker trips and logs a single warning, pausing render attempts for 5 seconds instead of logging 60–240 stack traces per second.
- **Fail Criteria:** Unhandled exception terminates the Unity process, subsequent windows fail to render due to corrupted ImGui window/ID stack, or duplicate menu tabs accumulate.

---

### TM-08: Dynamic Geometry Growth & GPU Device-Loss / TDR Recovery
- **Objective:** Validate dynamic vertex/index upload buffer reallocation under heavy geometry load (`DXH-04`) and automatic backend recovery after `DXGI_ERROR_DEVICE_REMOVED` / `DXGI_ERROR_DEVICE_RESET` (`DXH-06`, `DXH-08`).
- **Setup & Procedure:**
  1. **Geometry Burst:** In `TestPlugin`, open a stress window that renders 25,000+ textured/colored rectangles (`ImGui.GetWindowDrawList().AddRectFilled(...)` → >100,000 vertices and >150,000 indices) in a single frame, then shrinks back to normal, repeating every 2 seconds across all swapchain backbuffers.
  2. **Simulated TDR / Device Reset:** With the game running in a test environment supporting recovery (or synthetic device removal), trigger a TDR via Windows SDK `dxcap.exe -forcetdr` (or invoke `InvalidateDevice()` in a debug harness).
- **Pass Criteria:**
  - Vertex and index buffers in D3D11 (`_vertexBuffer`, `_indexBuffer`) and D3D12 (`currentFrameContext.VertexBuffer`, `currentFrameContext.IndexBuffer`) grow seamlessly without visual corruption, `IndexOutOfRangeException`, or use-after-free on in-flight D3D12 upload heaps.
  - Upon device loss (`DeviceRemovedReason.Failure`), `LogOutput.log` reports `Device loss detected. Invalidating device objects.` All stale COM objects are disposed without crashing the process, and if the engine recreates the device on the target window, the backend reinitializes automatically.
- **Fail Criteria:** GPU hang, corrupted vertex triangles on D3D12 triple-buffered swapchains, or unhandled `SharpDXException` crashing the process.

---

### TM-09: 60-Minute Steady-State Memory & Resource Leak Soak
- **Objective:** Confirm zero COM reference leaks (`MEM-01`–`MEM-06`) and negligible managed GC pressure (`MEM-07`) during extended gameplay.
- **Setup & Procedure:**
  1. Launch the game with the overlay open (`SampleModWindow` + `ShowDemoWindow` active, updating `StateSnapshotPipeline` every frame).
  2. Record baseline metrics after 2 minutes of warmup:
     - Process Private Working Set & Unmanaged Heap (`Process.GetCurrentProcess().PrivateMemorySize64`)
     - Dedicated GPU Memory usage (via Task Manager / PIX / Process Explorer)
     - Managed heap size (`GC.GetTotalMemory(false)`) and `GC.CollectionCount(0/1/2)`
  3. Let the game run at 120+ FPS for 60 minutes (~432,000 frames).
- **Pass Criteria:**
  - Unmanaged memory and Dedicated VRAM plateau after warmup (net growth $< 5\text{ MB}$ over 60 minutes, confirming zero leaked `ID3D11RenderTargetView` or pipeline state references per frame).
  - If running with the DirectX Debug Layer (`IDXGIDebug1::ReportLiveObjects`), live RTV/SRV/Buffer counts remain constant between minute 2 and minute 60.
- **Fail Criteria:** Monotonic linear growth in VRAM or process private bytes proportional to frame count.

---

### TM-10: Quiescent Teardown, `FrameGate` Drain & Clean Process Exit
- **Objective:** Verify that exiting the game under both D3D11 and D3D12 completes cleanly through the inverted quiescence sequence without `Process.Kill()`, access violations, or skipped plugin `OnDestroy` handlers (`TH-01`, `LC-01`, `LC-02`, `DXH-05`, `SEC-02`, `INP-03`).
- **Setup & Procedure:**
  1. Register a companion plugin (`TestPlugin`) that logs `[TestPlugin] OnApplicationQuit / OnDestroy save completed` and writes a timestamp file to disk during its `OnDestroy()`.
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
  - `TestPlugin`'s `OnDestroy` timestamp file is written on 100% of runs.
  - `BepInEx/config/DearImGuiInjectionPlus_imgui.ini` is saved cleanly to disk with updated window positions.
  - Process exits with exit code `0` and **zero** Windows Event Viewer Application Error (`0xC0000005` in `dxgi.dll`, `d3d12core.dll`, `cimgui.dll`, or `mono-2.0-bdwgc.dll`) entries.
- **Fail Criteria:** Truncated `LogOutput.log` tail, missing co-plugin `OnDestroy` execution, corrupted/missing `DearImGuiInjectionPlus_imgui.ini`, or any post-exit crash dump.
