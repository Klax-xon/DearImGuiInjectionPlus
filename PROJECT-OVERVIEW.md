# PROJECT OVERVIEW: DearImGuiInjectionPlus

An unmanaged/managed hybrid injection framework engineered to embed Dear ImGui directly into Unity engine DirectX swapchains under BepInEx 6 (IL2CPP / CoreCLR .NET 6). Delivers custom managed Direct3D 11 and Direct3D 12 backends, non-destructive Win32 window subclassing (`Comctl32`), a resilient 4-layer synchronized input swallow architecture (Win32 message filtering, direct `UnityPlayer.dll` native ICall detours, `NativeInputSystem.NotifyUpdate` raw buffer compaction, and uGUI shielding), zero-allocation steady-state render loop execution, live graphics telemetry, and thread-safe main-thread dispatching.

---

### 1. System Identity & Runtime Environment

- **Core Languages & Versions**:
  - **C#**: Preview language version (`<LangVersion>preview</LangVersion>`), unsafe blocks enabled (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`), nullable disabled (`<Nullable>disable</Nullable>`).
  - **Native C/C++**: Precompiled `cimgui.dll` (cimgui wrapper for Dear ImGui v1.89.9 Docking branch, compiled with 64-bit `ImTextureID`). Multi-platform binaries reside in `libs/cimgui/`:
    - Windows: `win-x64/cimgui.dll`, `win-x86/cimgui.dll`, `win-arm64/cimgui.dll`
    - Linux: `linux-x64/libcimgui.so`
    - macOS: `osx/libcimgui.dylib`
    - Dynamically preloaded at runtime via `Kernel32.LoadLibrary` with Unicode search paths to ensure stability in non-ASCII directory structures.
  - **HLSL Bytecode**: Precompiled vertex and pixel shaders (`Assets/Shaders/imgui-vertex.hlsl.bytes`, `Assets/Shaders/imgui-frag.hlsl.bytes`) compiled for Direct3D 11/12 bytecode formats.
- **Target Framework(s) & SDKs**:
  - **SDK**: `Microsoft.NET.Sdk`.
  - **Target Framework**: `.NET 6.0` (`net6.0`, CoreCLR) across all projects (`DearImGuiInjectionPlus`, `RendererFinder`, `TestPlugin`).
  - **Preprocessor Defines**: `IL2CPP`.
  - **Assembly Output**: Unified to `../bin/` with `AppendTargetFrameworkToOutputPath=false`, `DebugType=embedded`, `Optimize=true`.
  - **Primary NuGet Packages & Assembly References**:
    - `BepInEx.Unity.IL2CPP` (v6.0.0-be.790)
    - `Reloaded.Hooks` (v4.3.0)
    - `netstandard2.0.SharpDX` / `.DXGI` / `.Direct3D11` / `.Direct3D12` (v4.2.1-beta0-g7abb72df54)
    - `System.Runtime.CompilerServices.Unsafe` (v6.0.0)
    - `ImGui.NET` (`libs/netstandard2.0/ImGui.NET.dll`, updated to v1.89.9 Docking)
    - Stripped Unity IL2CPP assemblies (`libs/net6/`): `Il2Cppmscorlib.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.UI.dll`, `UnityEngine.UIModule.dll`, `UnityEngine.IMGUIModule.dll`, `UnityEngine.InputLegacyModule.dll`, `UnityEngine.InputModule.dll`
- **Modding Framework Target**:
  - **BepInEx**: Version 6.x (`BasePlugin` architecture from `BepInEx.Unity.IL2CPP`).
  - **Unity Runtime Target**: Unity IL2CPP on .NET 6.0 CoreCLR x64 runtime; certified across Unity 2019 LTS, 2021 LTS, 2022 LTS, and Unity 6 IL2CPP runtimes on Windows x64.
  - **Plugin Metadata**:
    - GUID: `com.custom.dearimguiinjectionplus`
    - Name: `DearImGuiInjectionPlus`
    - Version: `1.0.0`
    - Author: `Klax-xon`
- **Supported Graphics Backends / Injection Targets**:
  - **Direct3D 11**: Full support (`RendererKind.D3D11`, `UnityEngine.Rendering.GraphicsDeviceType.Direct3D11`).
  - **Direct3D 12**: Full support (`RendererKind.D3D12`, `UnityEngine.Rendering.GraphicsDeviceType.Direct3D12`).
  - **Auto-Detection**: Auto-resolved from `UnityEngine.SystemInfo.graphicsDeviceType` during plugin bootstrap.
  - **Unsupported Backends**: OpenGL, Vulkan, Metal (logs an error and immediately aborts bootstrap without hooking DXGI).
- **Core Repository References & Specifications**:
  - [`PROJECT-ANALYSIS.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/PROJECT-ANALYSIS.md): Comprehensive low-level architectural audit and defect register (`MEM-01..08`, `TH-01..07`, `DXH-01..12`, `INP-01..09`, `LC-01..08`, `SEC-01..02`).
  - [`AUDIT-VERDICT.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/AUDIT-VERDICT.md): Formal stability, security, and release certification report (`RR-01..06`).
  - [`IL2CPP-AUDIT.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/IL2CPP-AUDIT.md): Forensic post-migration audit covering BepInEx 6 IL2CPP, C++ `/O2` inlining circumvention, and multi-version input hooking (`AUD-01..09`).
  - [`TEST-MATRIX.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TEST-MATRIX.md): Complete in-game validation scenarios (`TM-01` through `TM-14`) and stress harnesses.

---

### 2. High-Level Architecture & Injection Pipeline

```
+----------------------------------------------------------------------------------------------------+
|                                    Plugin Lifecycle & Injection Flow                                |
+----------------------------------------------------------------------------------------------------+
                                                  |
                               [1] BepInEx Plugin Load (BasePlugin.Load)
                                - Detect SystemInfo.graphicsDeviceType (D3D11 vs D3D12; abort if unsupported)
                                - Preload native cimgui.dll via Kernel32.LoadLibrary (Unicode-safe)
                                - Read BepInEx Config (DearImGuiInjectionPlus.cfg: CursorVisibility, BlockAllInputsWhenOpen)
                                - Register IL2CPP MonoBehaviour types: UnityMainThreadDispatcher.Register() (captures MainThreadId)
                                  and ClassInjector.RegisterTypeInIl2Cpp<DearImGuiInjectionPlusRunner>() (Fixes AUD-06)
                                - Spawn runner GameObject ("DearImGuiInjectionPlus_Runner") with HideAndDontSave + DontDestroyOnLoad
                                - Apply direct UnityPlayer.dll native ICall hooks (defeats C++ /O2 inlining; Fixes AUD-08)
                                - Apply isolated Harmony patches (validates NativeMethodInfoPtr, merges prefixes/postfixes; Fixes AUD-01)
                                - Hook UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate for unmanaged event buffer compaction
                                - Hook UnityEngine.EventSystems.EventSystem.Update via Harmony prefix (EventSystemBlocker)
                                - Initialize DearImGuiInjectionPlus.Init(configPath, assetsFolder, cursorKeyConfig)
                                                  |
                               [2] Hook Setup (RendererFinder.Init)
                                - Create hidden Win32 dummy window (User32.CreateFakeWindow)
                                - Initialize dummy D3D11 Device + SwapChain1
                                - Hook DXGI SwapChain VTable Slot 8 (Present) & Slot 22 (Present1) via Reloaded.Hooks
                                - If D3D12: Prepare DX12Renderer (Querying CommandQueue from SwapChain via
                                  GetDevice Slot 7 / QI on Present is prioritized; Slot 10 ExecuteCommandLists is fallback with balanced AddRef)
                                                  |
                               [3] Frame 1 First-Hit (Present / Present1 Hook)
                                - Filter DXGI_PRESENT_TEST (0x00000001) and occlusion probes
                                - FrameGate.TryEnter() guards execution against teardown races
                                - Native hook catches active game SwapChain pointer & tracks target HWND
                                - D3D11: Extract ID3D11Device & ImmediateContext from SwapChain; detect HDR/sRGB formats
                                - D3D12: Capture ID3D12CommandQueue (direct SwapChain QI or fallback hook with balanced AddRef; RR-01)
                                                  |
                               [4] ImGui Context & Subclass Setup
                                - ImGui.CreateContext(), configure ImGuiIO (DockingEnable, HasMouseCursors, etc.)
                                - Apply theme & bake font atlas (Comfortaa-Medium.ttf)
                                - Initialize D3D11/D3D12 pipeline objects (Shaders, PSOs, CBVs, descriptor heaps)
                                - Subclass HWND via Comctl32.SetWindowSubclass (marshaled to window thread)
                                - Initialize ImGuiWin32Impl (QPC timer, ConcurrentQueue<InputEvent>, high-DPI scaling)
                                                  |
                               [5] Per-Frame Render Hook (Present Thread)
                                - Device loss check (DeviceRemovedReason.Failure): auto-invalidate & reinitialize
                                - Dynamic SwapChain check: update pointer or invalidate on device change (RR-02)
                                - Drain thread-safe input event queue into ImGuiWin32Impl
                                - ImGuiWin32Impl.NewFrame() -> ImGuiDX11Impl/DX12Impl.NewFrame() -> ImGui.NewFrame()
                                - Render MainMenuBar Tabs (RegisterTab) [Zero-alloc snapshot, RestoreImGuiWindowStack shield]
                                - Render Modular Windows (IImGuiWindow.OnRender) [Zero-alloc snapshot, RestoreImGuiStack shield]
                                - Invoke Legacy Action Delegates (Render) [Zero-alloc snapshot, RestoreImGuiStack shield]
                                - Safety net: RestoreImGuiStack() (igErrorCheckEndFrameRecover)
                                - ImGui.EndFrame() -> ImGui.Render()
                                - Publish IO.WantCaptureMouse / WantCaptureKeyboard atomically with memory barriers
                                - If capture flags transition false -> true while overlay open, flush depressed keys on main thread (AUD-09)
                                - Publish live GraphicsTelemetry (backend, swapchain, format, resolution, HDR, vertices, indices)
                                - D3D11: StateBackup (IDisposable, clears scissors/viewports) -> Bind non-sRGB RTV
                                         -> PSSetShaderResources (slot 8 direct) -> Restore state -> Dispose transient RTV
                                - D3D12: Transition backbuffer to RenderTarget -> SetDescriptorHeaps (pre-cached array)
                                         -> DrawIndexedInstanced -> Transition to Present -> ExecuteCommandList -> Advance fence
                                - Call Original Present / Present1 Function Pointer via Reloaded.Hooks
                                - FrameGate.Exit()
                                - Circuit breaker: 5 consecutive errors trigger 5-second rendering cooldown
                                                  |
                               [6] Teardown & Quiescent Cleanup (DearImGuiInjectionPlusBaseUnityPlugin.Shutdown)
                                - Triggered via DearImGuiInjectionPlusRunner.OnApplicationQuit or BasePlugin.Unload()
                                - Guard against premature destruction in OnDestroy during scene transitions (EnsureRunnerAlive)
                                - Set RendererFinder.IsShuttingDown = true
                                - Unblock New Input System devices, dispose native ICall hooks, and unpatch Harmony
                                - Quiesce Protocol: FrameGate.CloseAndDrain(1000) drains in-flight render frames
                                - Disable native hooks via Reloaded.Hooks.Disable() (RendererFinder.Dispose)
                                - Remove window subclass via Comctl32.RemoveWindowSubclass on window thread
                                - Wait for GPU idle: _deviceContext.Flush() (D3D11) / WaitForGpuIdle() fence wait (D3D12)
                                - Destroy D3D11/D3D12 GPU buffers, PSOs, descriptor heaps, font textures
                                - Save ini settings, destroy ImGui context (ImGui.DestroyContext), free unmanaged strings
                                - Destroy runner GameObject; graceful shutdown finishes with exit code 0
```

#### Native / Managed Interop Boundaries
1. **Dear ImGui Interop**: Interfaced via `ImGui.NET` managed wrapper binding to `cimgui.dll` (v1.89.9 Docking) through standard P/Invoke calling conventions. All string paths passed to native ImGui (such as `IniFilename`) and backend names (`BackendPlatformName`, `BackendRendererName`) are pinned unmanaged ANSI strings allocated via `Marshal.StringToHGlobalAnsi`. Strings are tracked and freed via `Marshal.FreeHGlobal` strictly *after* `ImGui.DestroyContext` to prevent use-after-free crashes during ini serialization.
2. **DirectX API Interop**: Pure managed invocation via `SharpDX` / `Reloaded.Hooks`. No intermediate custom C++ injection proxy DLL is required.
3. **COM Ownership Rules**: Native COM pointers borrowed from Unity (such as swapchain pointers passed into `Present` or fallback command queues) are never wrapped in owning SharpDX objects without calling `Marshal.AddRef`. Transient SharpDX wrappers are guarded with `GC.SuppressFinalize` to prevent non-deterministic finalizer thread race conditions from over-releasing engine interfaces.
4. **IL2CPP & CoreCLR Runtime Boundaries**:
   - Custom `MonoBehaviour` types ([`UnityMainThreadDispatcher`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/UnityMainThreadDispatcher.cs), [`DearImGuiInjectionPlusRunner`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusRunner.cs), [`TestPluginRunner`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TestPlugin/TestPluginRunner.cs)) implement the required native pointer constructor `public MyType(IntPtr handle) : base(handle)` and are registered with `ClassInjector.RegisterTypeInIl2Cpp<T>()` prior to instantiation.
   - IL2CPP devices tracked in [`NewInputSystemBlocker`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs) are keyed by unmanaged `Il2CppObjectBase.Pointer` (`IntPtr`) to prevent object identity churn and finalizer crashes under GC pressure (`AUD-04`).
   - Memory synchronization in [`StateSnapshotPipeline<T>`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/StateSnapshotPipeline.cs#L15-L61), [`StructSnapshotPipeline<T>`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/DearImGuiInjectionPlus/StateSnapshotPipeline.cs#L68-L137), and [`FrameGate`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/RendererFinder/FrameGate.cs) utilizes `Volatile.Read`/`Volatile.Write` and `Interlocked` primitives designed for the `x64` RyuJIT memory model.
5. **C++ `/O2` Inlining Bypass (Native ICall Detours)**:
   - In Unity IL2CPP release builds, 1-line wrapper methods on `UnityEngine.Input` (`GetKeyInt`, `get_mousePosition_Injected`, `GetMouseButton`) and `UnityEngine.Cursor` are inlined by the C++ compiler (`/O2`) into call sites within `GameAssembly.dll` (leaving `CallerCount = 0`). Detouring the managed wrapper via standard Harmony detours fails because call sites invoke the underlying `UnityPlayer.dll` ICall export directly.
   - The framework resolves native ICall addresses via `IL2CPP.il2cpp_resolve_icall` and installs native detours via `Il2CppInteropRuntime.Instance.DetourProvider.Create(ptr, detourDelegate)`, hooking the underlying native C++ engine exports directly and bypassing `GameAssembly.dll` inlining (`AUD-08`).
6. **VTable Resolving**: Uses `CppInterop.VirtualFunctionTable` to dereference unmanaged interface pointers (`*(IntPtr*)interfacePtr`) and resolve function pointers at fixed index offsets:
   - `IDXGISwapChain::Present` -> VTable Offset `8 * sizeof(void*)`
   - `IDXGISwapChain1::Present1` -> VTable Offset `22 * sizeof(void*)`
   - `IDXGISwapChain::GetDevice` -> VTable Offset `7 * sizeof(void*)`
   - `ID3D12CommandQueue::ExecuteCommandLists` -> VTable Offset `10 * sizeof(void*)`
   - `ID3D11DeviceContext::PSSetShaderResources` -> VTable Offset `8 * sizeof(void*)`

---

### 3. Key Namespaces, Directory Structure & Component Roles

```
DearImGuiInjectionPlus/
├── DearImGuiInjectionPlus/
│   ├── Backends/
│   │   ├── ImGuiDX11.cs              # D3D11 SwapChain render loop, HDR/sRGB handling, transient RTV lifecycle
│   │   ├── ImGuiDX11Impl.cs          # D3D11 device resources, shaders, dynamic buffers, StateBackup singleton
│   │   ├── ImGuiDX12.cs              # D3D12 SwapChain render loop, FrameContext ring, fence synchronization
│   │   ├── ImGuiDX12Impl.cs          # D3D12 root signature, PSO management, dynamic format adaptation
│   │   ├── ImGuiWin32Impl.cs         # Win32 platform backend, input queue drain, IME & high-DPI scaling
│   │   └── InputEvent.cs             # Thread-safe input event struct for cross-thread queue
│   ├── BepInEx/
│   │   ├── BepInExConfigEntry.cs     # BepInEx configuration wrapper
│   │   ├── BepInExLog.cs             # BepInEx logger implementation
│   │   ├── DearImGuiInjectionPlusBaseUnityPlugin.cs # Core BepInEx 6 BasePlugin lifecycle, safe patching & teardown
│   │   ├── DearImGuiInjectionPlusRunner.cs          # Runner MonoBehaviour, per-frame input blocking, respawn guard
│   │   └── UnityMainThreadDispatcher.cs # High-performance main thread dispatcher with deadlock safeguards
│   ├── Patches/
│   │   ├── EventSystemBlocker.cs     # Harmony prefix patch on UnityEngine.EventSystems.EventSystem.Update
│   │   ├── NewInputSystemBlocker.cs  # NativeInputSystem.NotifyUpdate raw buffer hook & reflection device blocker
│   │   └── UnityInputBlocker.cs      # Direct UnityPlayer.dll native ICall hooks & managed Harmony fallbacks
│   ├── Windows/
│   │   ├── Comctl32.cs               # SetWindowSubclass, RemoveWindowSubclass, DefSubclassProc
│   │   ├── Dwmapi.cs, Gdi32.cs, Kernel32.cs, Ntdll.cs, RECT.cs
│   │   ├── Shellscalingapi.cs, User32.cs, VirtualKey.cs, WindowMessage.cs
│   │   └── VirtualKey.cs, WindowMessage.cs
│   ├── Assets/
│   │   ├── Fonts/Comfortaa-Medium.ttf
│   │   └── Shaders/imgui-frag.hlsl.bytes, imgui-vertex.hlsl.bytes
│   ├── CursorManager.cs              # Mouse cursor visibility and lock state tracking
│   ├── DearImGuiInjectionPlus.cs     # Core orchestrator, context lifecycle, window/tab registries, NewFrame
│   ├── DearImGuiTheme.cs             # Styling, palette configuration, and dynamic HDR gamma correction
│   ├── GraphicsTelemetry.cs          # Real-time swapchain, device, resolution, and rendering telemetry API
│   ├── IConfigEntry.cs               # Config abstraction interface
│   ├── IImGuiWindow.cs               # Extensible modular window interface
│   ├── InputManager.cs               # Non-destructive Win32 subclass, stuck-key prevention, passthrough tracking
│   ├── Log.cs                        # Centralized logging facade
│   ├── Metadata.cs                   # Plugin GUID, name, version, and author metadata
│   └── StateSnapshotPipeline.cs      # Lock-free reference and triple-buffered struct snapshot pipelines
├── RendererFinder/
│   ├── CppInterop/
│   │   └── VirtualFunctionTable.cs   # Unmanaged VTable pointer resolver
│   ├── Renderers/
│   │   ├── DX11Renderer.cs           # DXGI Present/Present1 hook provider, circuit breaker, dummy device
│   │   ├── DX12Renderer.cs           # D3D12 fallback command queue capture hook with balanced AddRef
│   │   └── IRenderer.cs              # Renderer backend abstraction interface
│   ├── Windows/
│   │   └── User32.cs                 # Win32 dummy window helpers
│   ├── FrameGate.cs                  # Interlocked entry/exit frame gate and teardown drain synchronization
│   ├── Log.cs                        # RendererFinder logging facade
│   └── RendererFinder.cs             # Backend discovery engine, direct QI query, lifecycle state
└── TestPlugin/                       # Production-Grade In-Game Validation & Stress Test Suite
    ├── Panels/
    │   ├── ConcurrencyTestPanel.cs   # TM-04: Multi-field StructSnapshotPipeline stress & dispatcher torture
    │   ├── FaultRecoveryTestPanel.cs # TM-07, TM-10: Deliberate window/tab stack faults & teardown verification
    │   ├── GraphicsTestPanel.cs      # TM-01..03, TM-08: Geometry bursts, buffer stress, UserCallback -1 reset
    │   ├── Il2CppTestPanel.cs        # TM-11..14: IL2CPP ICall detours, 3D look swallowing, live passthrough flush
    │   ├── InputTestPanel.cs         # TM-05, TM-06: Unicode text, dead keys, CJK IME, emojis, capture flags
    │   ├── MemoryBenchmarkTestPanel.cs # TM-09: Win32 PSAPI private memory, rolling FPS/delta, GC heaps
    │   └── TestSuiteMasterPanel.cs   # Master Dashboard controlling all test panels
    ├── BepInExLog.cs, Log.cs, Metadata.cs
    ├── TestPluginRunner.cs           # Runner MonoBehaviour, 1 Hz scene query, snapshot publisher, quit guard
    └── TestPlugin.cs                 # Sample mod window, modular panel orchestration, lifecycle hooks
```

#### Operational Responsibilities & Hook Targets

| Class / File | Namespace | Operational Responsibility & Hook Targets |
| :--- | :--- | :--- |
| `DearImGuiInjectionPlusBaseUnityPlugin` | `DearImGuiInjectionPlus.BepInEx` | **BepInEx 6 Entry Point (`BasePlugin`)**. Bootstrap lifecycle (`Load`/`Unload`), config binding (`DearImGuiInjectionPlus.cfg`), native `cimgui.dll` preloading via `Kernel32.LoadLibrary`, runner instantiation, safe native ICall and Harmony patch deployment, and quiescent teardown coordinator via `Shutdown()`. |
| `DearImGuiInjectionPlusRunner` | `DearImGuiInjectionPlus.BepInEx` | **Runner MonoBehaviour**. Registered via `ClassInjector.RegisterTypeInIl2Cpp<T>()`. Enforces per-frame input blocking in `Update()`, tracks capture transitions to flush depressed keys/axes immediately (`AUD-09`), enforces cursor state on main thread, and protects against premature teardown on scene reloads via `EnsureRunnerAlive()`. |
| `DearImGuiInjectionPlus` | `DearImGuiInjectionPlus` | **Core Orchestrator**. Context creation (Dear ImGui 1.89.9 Docking), IO setup, registries for `IImGuiWindow`, `RegisteredTab` (`RegisterTab`/`UnregisterTab`), and legacy `Render` delegate. Drives `NewFrame()` with zero managed allocations and ImGui stack recovery shields (`ErrorCheckEndWindowRecover` / `ErrorCheckEndFrameRecover`). |
| `GraphicsTelemetry` | `DearImGuiInjectionPlus` | **Live Diagnostics API**. Public thread-safe telemetry publishing active backend name, HWND, swapchain pointer, backbuffer format, resolution, refresh rate, HDR color space, and per-frame vertex/index counts. |
| `RendererFinder` | `RendererFinder` | **Hook Discovery Engine**. Coordinates detection across backends. Queries Direct D3D12 command queues from swapchain VTable Slot 7 (`GetDevice`) or QI (`IUnknown` -> `ID3D12CommandQueue`) on Present. Coordinates hook lifecycle and shutdown flags. |
| `FrameGate` | `RendererFinder` | **Quiescence Synchronization Gate**. Atomic interlocked frame gate (`TryEnter`, `Exit`, `CloseAndDrain`). Drains all in-flight rendering frames during teardown before hooks, graphics resources, or ImGui contexts are destroyed. |
| `DX11Renderer` | `RendererFinder.Renderers` | **DXGI Hook Provider**. Creates fake Win32 window + swapchain to extract DXGI VTable pointers. Uses `Reloaded.Hooks` on `IDXGISwapChain::Present` (Slot 8) and `IDXGISwapChain1::Present1` (Slot 22). Detour disabling deactivates redirection without rewriting prologue bytes. Includes 5-error consecutive circuit breaker. |
| `DX12Renderer` | `RendererFinder.Renderers` | **D3D12 Fallback CommandQueue Hook**. Fallback detour on `ID3D12CommandQueue::ExecuteCommandLists` (Slot 10) with thread-affinity filtering and balanced `Marshal.AddRef` promotion (`RR-01`). Includes consecutive error circuit breaker. |
| `ImGuiDX11` | `DearImGuiInjectionPlus.Backends` | **D3D11 Pipeline Glue**. Hooks swapchain `OnPresent`, manages transient backbuffer `Texture2D` and `RenderTargetView` with guaranteed disposal, detects device loss (`DeviceRemovedReason.Failure`), handles dynamic swapchain recreation on the same HWND (`RR-02`), manages HDR/sRGB formats, and triggers frame render. |
| `ImGuiDX11Impl` | `DearImGuiInjectionPlus.Backends` | **D3D11 Managed Backend**. Allocates and maps dynamic vertex/index buffers, compiles shaders/states, uploads font atlas, binds textures directly via native `PSSetShaderResources` (slot 8), and executes `StateBackup` (an `IDisposable` state snapshot that clears scissor/viewport arrays and releases queried COM objects; `MEM-01`, `RR-04`). |
| `ImGuiDX12` | `DearImGuiInjectionPlus.Backends` | **D3D12 Pipeline Glue**. Manages per-backbuffer `FrameContext` (CommandAllocators, dynamic buffers, fences), fence synchronization (`WaitForGpuIdle`), backbuffer RTVs, resource state transitions (`Present` <-> `RenderTarget`), dynamic PSO format adaptation, dynamic swapchain recreation, and safe COM lifetime (`Marshal.AddRef` / `GC.SuppressFinalize`). |
| `ImGuiDX12Impl` | `DearImGuiInjectionPlus.Backends` | **D3D12 Managed Backend**. Creates root signatures, pipeline state objects (PSOs), upload ring buffers, committed resource buffers, descriptor tables, font texture resources, and caches unmanaged `UserCallback` delegates with `-1` (`ImDrawCallback_ResetRenderState`) priority (`RR-03`). |
| `ImGuiWin32Impl` | `DearImGuiInjectionPlus.Backends` | **Win32 Platform Backend**. Thread-safe `ConcurrentQueue<InputEvent>`, high-DPI scaling, tracks mouse capture/leave, handles `WM_CHAR`/`WM_UNICHAR` UTF-16/UTF-32 IME input, queries QPC timer, and caches mouse cursor shapes on the render thread to avoid cross-thread reads during `WM_SETCURSOR`. |
| `InputEvent` | `DearImGuiInjectionPlus.Backends` | **Input Event Payload**. Zero-allocation struct payload capturing event type, mouse coordinates, virtual keys, characters, modifiers, and visibility flags across the thread boundary. |
| `InputManager` | `DearImGuiInjectionPlus` | **Win32 Window Subclass**. Non-destructive subclassing via `Comctl32.SetWindowSubclass` / `RemoveWindowSubclass` marshaled to the window thread. Intercepts toggle key (`Insert`), implements `BlockAllInputsWhenOpen` and `WantCaptureMouse`/`WantCaptureKeyboard` swallowing, redirects raw input `WM_INPUT` (0x00FF) to `DefWindowProc`, whitelists `Alt+Enter` to `DefSubclassProc`, tracks keys pressed during passthrough, coordinates `_pendingKeyReleaseFlushFrames = 2`, and synthesizes key-up releases. |
| `CursorLockStatePatch` / `CursorVisiblePatch` | `DearImGuiInjectionPlus` | **Harmony Patches**. Intercepts `UnityEngine.Cursor.lockState` and `Cursor.visible` setters to capture the game's desired state while forcing `CursorLockMode.None` and `visible = true` when ImGui is visible. |
| `UnityInputBlocker` | `DearImGuiInjectionPlus.Patches` | **Native ICall & Legacy Input Blocker**. Hooks direct `UnityPlayer.dll` native ICalls (`GetKeyInt`, `GetMouseButton*`, `get_anyKey*`, `get_mousePosition_Injected`, Unity 2019–Unity 6 `InputUnsafeUtility` variants, `Cursor` setters) via `IL2CPP.il2cpp_resolve_icall` and `IDetour` to defeat C++ `/O2` inlining. Provides managed Harmony fallbacks with per-method isolation and exposes `TryResetInputAxes()`. |
| `NewInputSystemBlocker` | `DearImGuiInjectionPlus.Patches` | **New Input System Raw Buffer Blocker**. Hooks `UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate` (`[RequiredByNativeCode]`) to zero or compact `NativeInputEventBuffer` in native memory via FourCC format inspection (`'MOUS'`, `'POIN'`, `'TOUC'`, `'KEYB'`). Disables/resets devices dynamically keyed by native `Il2CppObjectBase.Pointer`. |
| `EventSystemBlocker` | `DearImGuiInjectionPlus.Patches` | **uGUI Canvas Blocker**. Harmony prefix patch on `UnityEngine.EventSystems.EventSystem.Update`. Drops uGUI event updates and raycasts when ImGui cursor is active and capturing mouse. |
| `UnityMainThreadDispatcher` | `DearImGuiInjectionPlus.BepInEx` | **Cross-Thread Dispatcher**. `MonoBehaviour` (`DontDestroyOnLoad`) draining up to 500 actions/frame in `Update`. Registered via `ClassInjector.RegisterTypeInIl2Cpp<T>()` and caches main thread ID (`Register()`). Provides lock-free `Enqueue(Action)` and synchronous `InvokeOnMainThread<T>` with `IsMainThread` fast-path and reference-counted `DispatchCompletionHandle` timeout safeguards. |
| `StateSnapshotPipeline<T>` / `StructSnapshotPipeline<T>` | `DearImGuiInjectionPlus` | **Lock-Free Concurrency Pipelines**. `StateSnapshotPipeline<T>` (for reference types) uses atomic `Interlocked.Exchange` / `Volatile.Read`. `StructSnapshotPipeline<T>` (for value types) uses a lock-free, zero-allocation triple-buffered atomic slot pipeline with flag exchange to completely prevent torn reads under high-frequency writes. |
| `VirtualFunctionTable` | `CppInterop` | **Native Memory VTable Reader**. Reads memory pointer offsets from C++ interface virtual tables. |

---

### 4. Consumer / Plugin Integration Model

Other BepInEx plugins consume `DearImGuiInjectionPlus` by referencing `DearImGuiInjectionPlus.dll`, `ImGui.NET.dll`, and `BepInEx.Unity.IL2CPP.dll`.

#### 1. Manifest Dependency & Plugin Definition
```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInDependency(DearImGuiInjectionPlus.Metadata.GUID)]
[BepInPlugin("com.author.myplugin", "MyPlugin", "1.0.0")]
public class MyPlugin : BasePlugin
{
    public override void Load()
    {
        // Plugin bootstrap logic...
    }
}
```

#### 2. Modular Window Integration (`IImGuiWindow`) [Recommended]
Consumers implement `IImGuiWindow` to gain automated exception containment and modular registration. All window rendering is wrapped in isolated `try/catch` blocks followed by `RestoreImGuiStack()` (`igErrorCheckEndFrameRecover`):
```csharp
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using ImGuiNET;
using UnityEngine;

public class MyModWindow : IImGuiWindow
{
    public string Id => "my_mod_window";
    public string Title => "My Mod Settings";
    public bool IsOpen { get; set; } = true;

    private readonly StateSnapshotPipeline<GameDataSnapshot> _snapshotPipeline;

    public MyModWindow(StateSnapshotPipeline<GameDataSnapshot> pipeline)
    {
        _snapshotPipeline = pipeline;
    }

    public void OnRender()
    {
        bool open = IsOpen;
        if (ImGui.Begin(Title, ref open))
        {
            IsOpen = open;

            // Thread-safe lock-free read from snapshot pipeline (Render Thread safe)
            var data = _snapshotPipeline.GetLatest();
            if (data != null)
            {
                ImGui.Text($"Player Position: {data.PlayerPosition}");
            }

            if (ImGui.Button("Spawn Entity"))
            {
                // MUST marshal Unity engine modifications to the main thread!
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    GameObject.CreatePrimitive(PrimitiveType.Cube);
                });
            }
        }
        ImGui.End();
    }
}

// Registration in BasePlugin lifecycle:
DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(myWindowInstance);
DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(myWindowInstance);
```

#### 3. Main Thread Runner Pattern in IL2CPP
When gathering Unity engine state (such as transforms, active scene, or physics data) to feed into `StateSnapshotPipeline<T>`, consumer plugins spawn a dedicated `MonoBehaviour` runner registered with `ClassInjector`:
```csharp
public class MyModRunner : MonoBehaviour
{
    // Required native pointer constructor for IL2CPP ClassInjector
    public MyModRunner(IntPtr handle) : base(handle) { }

    private void Update()
    {
        // Query engine data safely on the Unity main thread
        var snapshot = new GameDataSnapshot
        {
            PlayerPosition = Camera.main != null ? Camera.main.transform.position : Vector3.zero
        };
        MyPlugin.Instance.SnapshotPipeline.Publish(snapshot);
    }
}

// Registration inside BasePlugin.Load():
if (!ClassInjector.IsTypeRegisteredInIl2Cpp<MyModRunner>())
{
    ClassInjector.RegisterTypeInIl2Cpp<MyModRunner>();
}
var runnerGo = new GameObject("MyMod_Runner");
runnerGo.hideFlags = HideFlags.HideAndDontSave;
Object.DontDestroyOnLoad(runnerGo);
runnerGo.AddComponent<MyModRunner>();
```

> [!TIP]
> **Eliminating Per-Frame String Allocations (`AUD-07`)**: Avoid querying `SceneManager.GetActiveScene().name` every frame in IL2CPP `Update()`. In IL2CPP, accessing `.name` invokes `IL2CPP.Il2CppStringToManaged`, creating a new managed string allocation every frame. Instead, cache `activeScene.handle` and only query `.name` when the handle changes, or poll scene names at a low frequency (1 Hz).

#### 4. Global Menu Bar Integration (`RegisterTab` / `UnregisterTab`)
Adds dropdown menu items to the unified Dear ImGui main menu bar. `RegisterTab` deduplicates entries by category/title and returns a `RegisteredTab` handle implementing `IDisposable` for easy teardown. Tab callbacks are guarded with `RestoreImGuiWindowStack()` (`igErrorCheckEndWindowRecover`):
```csharp
var tab = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Mods", "My Mod", () =>
{
    if (ImGui.MenuItem("Toggle Window", "", myWindow.IsOpen))
    {
        myWindow.IsOpen = !myWindow.IsOpen;
    }
});

// Explicit unregistration when plugin is disabled:
DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterTab(tab);
// Or simply:
tab.Dispose();
```

#### 5. Legacy Event Delegate (`Render`)
Subscribes directly to the per-frame render callback (also protected with isolated exception recovery):
```csharp
DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render += MyDirectDrawRoutine;

private static void MyDirectDrawRoutine()
{
    if (DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible)
    {
        ImGui.ShowDemoWindow();
    }
}
```

#### 6. Live Graphics Telemetry (`GraphicsTelemetry`)
Enables companion plugins and debug overlays to monitor active DirectX swapchains, resolution, and performance without querying DirectX interfaces directly:
```csharp
string backend = GraphicsTelemetry.BackendName;      // "Direct3D 11" or "Direct3D 12"
string format = GraphicsTelemetry.BackBufferFormat;   // e.g. "R10G10B10A2_UNorm"
bool isHdr = GraphicsTelemetry.IsHdr;                // true if HDR10 / scRGB
float refreshHz = GraphicsTelemetry.RefreshRateHz;   // e.g. 144.0 Hz
int vtxCount = GraphicsTelemetry.TotalVertices;       // ImGui vertices rendered this frame
```

---

### 5. Invariants, Threading & Native Constraints

#### Threading Model & Thread Affinity Rules

| Execution Context | Thread Identity | Allowed Operations | Strictly Prohibited Operations |
| :--- | :--- | :--- | :--- |
| **Unity Main Thread** | Unity Scripting Thread (`ManagedThreadId == MainThreadId`) | `UnityEngine.Object` APIs, GameObjects, Transforms, physics queries, scene loading, Unity `Cursor.*`, publishing to `StateSnapshotPipeline.Publish()`, window subclassing / unsubclassing via `Comctl32`. | Direct ImGui rendering calls (ImGui is context-bound to the render thread). |
| **Render / Present Thread** | Graphics Device Present Thread (invoked by DXGI swapchain `Present`) | `ImGui.NewFrame()`, `ImGui.Render()`, `IImGuiWindow.OnRender()`, DirectX device draw calls, reading from `StateSnapshotPipeline.GetLatest()`. | Calling ANY `UnityEngine.Object` methods, instantiating GameObjects, or calling blocking `UnityMainThreadDispatcher.InvokeOnMainThread` (causes pipeline deadlock). |
| **Win32 Message Thread** | OS UI Message Pump | `InputManager.SubclassHandler`, mouse cursor clipping, raw input routing, pushing input events into `ConcurrentQueue<InputEvent>`. | Long-running tasks, blocking waits (stalls game message loop), direct `ImGui` or `ImGuiIO` mutations. |
| **IL2CPP Domain Context** | Native Interop Domain | Managed reflection, P/Invoke, `ClassInjector` type queries, native ICall detours. | Invoking IL2CPP object methods from unattached unmanaged native threads without `il2cpp_thread_attach`. |

#### Concurrency & Lock-Free Synchronization
1. **Thread-Safe Input Ingestion (`TH-02`)**:
   - The Win32 window procedure never mutates `ImGuiIO` directly. All mouse moves, clicks, scrolls, key presses, text chars, and focus events are packaged into `InputEvent` structs and enqueued to a thread-safe `ConcurrentQueue<InputEvent>`.
   - The `Present` thread drains `_eventQueue` inside `ImGuiWin32Impl.NewFrame()` before initiating `ImGui.NewFrame()`.
2. **Lock-Free State Pipelines (`TH-05`, `TH-06`)**:
   - `StateSnapshotPipeline<T>` (for reference types) uses atomic `Interlocked.Exchange` and `Volatile.Read` for zero-overhead snapshot sharing.
   - `StructSnapshotPipeline<T>` (for value types) uses three pre-allocated slots (`_slot0`, `_slot1`, `_slot2`) with atomic index exchange (`_readIndex`, `_sharedIndex`, `_writeIndex`, `NewDataFlag = 0x4`, `IndexMask = 0x3`). This eliminates boxing allocations and guarantees zero torn reads even when updated thousands of times per second.
3. **UnityMainThreadDispatcher Deadlock Safeguards**:
   - Includes an `IsMainThread` fast-path that executes immediately without queueing when invoked on the main thread.
   - Synchronous blocking dispatch (`InvokeOnMainThread`) uses a reference-counted `DispatchCompletionHandle` (`Interlocked.CompareExchange` state machine) to guarantee that timed-out actions never execute stale work or invoke `Set()` on a disposed `ManualResetEventSlim`.
   - **Deadlock Warning**: Synchronous blocking dispatch must **never** be called from the ImGui render loop (`OnRender` or `Present` thread). Unity's main thread frequently waits on the render thread to present; blocking the render thread while waiting for the main thread produces an unrecoverable engine pipeline deadlock. All render-to-main communication must use asynchronous non-blocking `Enqueue(Action)`.

#### Native Memory Management & COM Safety Rules
- **Unmanaged ANSI String Pointers (`SEC-02`, `MEM-08`)**:
  - Native properties such as `ImGuiIO.IniFilename`, `BackendPlatformName`, and `BackendRendererName` must be allocated in unmanaged memory via `Marshal.StringToHGlobalAnsi`.
  - Pointers are tracked and freed via `Marshal.FreeHGlobal` strictly *after* `ImGui.DestroyContext()` to ensure that `ImGui.SaveIniSettingsToDisk` does not read freed memory during teardown.
- **Texture ID Semantics**:
  - **Direct3D 11**: `ImTextureID` represents the raw pointer to `ID3D11ShaderResourceView` (`_fontResourceView.NativePointer`). Bound directly via native VTable slot 8 (`PSSetShaderResources`) with `&textureId`, eliminating transient SharpDX wrapper allocations and finalizer leaks (`MEM-04`).
  - **Direct3D 12**: `ImTextureID` represents the 64-bit integer cast of `D3D12_GPU_DESCRIPTOR_HANDLE.ptr` (`(nint)hFontSrvGpuDescHandle.Ptr`). 32-bit builds are unsupported.
- **COM Ownership & SharpDX Safety**:
  - **Borrowed vs Owned References**: Wrapping an unmanaged COM pointer with a SharpDX constructor (e.g. `new SwapChain(ptr)`) takes ownership of the pointer. SharpDX's `Dispose()` calls `Release()`. Therefore, whenever wrapping a borrowed pointer from Unity, `Marshal.AddRef(ptr)` must be called before wrapping to avoid over-releasing the engine's COM object (`MEM-02`, `RR-01`).
  - **D3D12 Fallback Queue COM Balance (`RR-01`)**: When capturing a candidate queue via the fallback `ExecuteCommandLists` hook, `DX12Renderer.GetOrBindQueueForPresent` explicitly calls `Marshal.AddRef` before copying into `CapturedCommandQueue`, balancing against `_commandQueue?.Dispose()` on shutdown or device reset.
  - **Suppressing Finalizers**: SharpDX objects created over borrowed or long-lived pointers must call `GC.SuppressFinalize` to prevent non-deterministic finalizer thread execution from racing with active device teardown (`MEM-04`).
  - **Deterministic State Backup Disposal (`MEM-01`, `RR-04`)**: In D3D11 mode, `BackupRenderState` captures pipeline state before binding the overlay's transient RTV and returns an `IDisposable` `StateBackup` singleton. On disposal, all queried COM wrappers are disposed, and `Array.Clear` resets scissor and viewport count arrays, allowing `IDXGISwapChain::ResizeBuffers` to succeed unconditionally.
- **Zero-Allocation Render Loop Hardening (`MEM-07`, `RR-03`, `RR-06`)**:
  - All per-frame collections (`_tabCategoriesSnapshot`, `_windowsSnapshot`, `_renderSnapshot`) are pre-cached snapshots rebuilt only upon registration changes.
  - In D3D11, `_vertexBinding` is pre-cached to eliminate per-frame `VertexBufferBinding` allocations, and `SetScissorRectangle` integer overloads eliminate array allocations.
  - In D3D12, `_shaderDescriptorHeaps` is a pre-allocated 1-element array (`DescriptorHeap[1]`) reused every frame in `SetDescriptorHeaps`.
  - User callbacks in both D3D11 and D3D12 are cached in `_userCallbackCache`, and special callback `-1` (`ImDrawCallback_ResetRenderState`) is evaluated before delegate lookup (`RR-03`).
- **Hook Detour Semantics (`Reloaded.Hooks`)**:
  - `Reloaded.Hooks` installs inline detours via code trampolines. Calling `.Disable()` deactivates detour redirection flags in the hook stub so subsequent calls pass straight to the original function without invoking our callback; it does not uninstall hooks or restore original prologue bytes. Consequently, teardown requires draining in-flight frames via `FrameGate.CloseAndDrain` before disabling detours.

#### Multi-Layer Input Swallowing & Camera Look Elimination

The input pipeline implements a synchronized 4-layer defense preventing keyboard leakage, cursor fighting, stuck keys, and 3D camera drift:

```
+----------------------------------------------------------------------------------------------------+
|                                    4-Layer Input Swallowing Pipeline                               |
+----------------------------------------------------------------------------------------------------+
                                                  |
           [Layer 1: Win32 Subclass (Comctl32)]  <-- Ingests OS messages, queues ImGui events
            - WM_INPUT (0x00FF) redirected to DefWindowProc (Stops 3D mouse look drift)
            - Alt+Enter forwarded to DefSubclassProc (Preserves fullscreen toggle)
            - Swallows WM_KEY*, WM_MOUSE* based on BlockAllInputsWhenOpen / WantCapture flags
            - Tracks held keys & permits single release through (_downKeys bitmask)
            - Coordinates _pendingKeyReleaseFlushFrames = 2 during release synthesis
                                                  |
           [Layer 2: Native UnityPlayer.dll ICalls] <-- Defeats C++ /O2 inlining in GameAssembly.dll
            - Detours GetKeyInt, GetMouseButton*, get_anyKey*, get_mousePosition_Injected
            - Hooks Unity 2019-2022.1 Legacy GetAxis/GetButton ICalls
            - Hooks Unity 2022.2-2023.2 InputUnsafeUtility *__Unmanaged ICalls
            - Hooks Unity 2023.3 / Unity 6 InputUnsafeUtility *_Injected ICalls
            - Detours Cursor set_lockState & set_visible ICalls
            - Managed Harmony fallbacks for stripped methods (validating NativeMethodInfoPtr)
                                                  |
           [Layer 3: New Input System Buffer Hook] <-- NativeInputSystem.NotifyUpdate ([RequiredByNativeCode])
            - Block all: zeroes eventCount & sizeInBytes in unmanaged NativeInputEventBuffer
            - Block selective: in-place compaction filtering FourCC formats ('MOUS', 'POIN', 'KEYB')
            - Dynamic device reset/disable keyed by native Il2CppObjectBase.Pointer
                                                  |
           [Layer 4: uGUI EventSystem Shield]      <-- EventSystemBlocker
            - Harmony prefix on UnityEngine.EventSystems.EventSystem.Update
            - Drops canvas raycasts & UI button triggers while Dear ImGui captures mouse
```

- **Root Cause & Resolution of 3D Gameplay Mouse Look & Click Bleed-Through (`AUD-08`)**:
  1. **Win32 `GetRawInputBuffer()` Bypasses `WndProc`:** On Windows, `UnityPlayer.dll` drains high-frequency mouse deltas (`lLastX`, `lLastY`) and button states directly from the kernel queue via `GetRawInputBuffer()` inside its C++ pump before `DispatchMessageW` invokes `InputManager.SubclassHandler`. Therefore, Win32 subclassing alone cannot stop 3D camera rotation.
  2. **IL2CPP C++ Inlining of `Input` Methods:** In `GameAssembly.dll`, public methods like `Input.GetKey(KeyCode)` or `Input.mousePosition` are simple wrappers around private native methods `GetKeyInt` and `get_mousePosition_Injected`. During IL2CPP `/O2` compilation, the compiler inlines the outer wrapper at call sites (leaving `CallerCount = 0`). Standard Harmony detours on `GameAssembly.dll` wrappers are bypassed completely because the game calls the internal `UnityPlayer.dll` ICall pointer directly.
  3. **Direct Native ICall Hooking:** `UnityInputBlocker.ApplyNativeICallHooks()` resolves underlying `UnityPlayer.dll` ICall pointers via `IL2CPP.il2cpp_resolve_icall` and installs native detours using `Il2CppInteropRuntime.Instance.DetourProvider.Create()`. This intercepts input queries directly at the engine level across Unity 2019, 2021, 2022, and Unity 6 runtimes.
  4. **Unmanaged `NativeInputEventBuffer` Compaction:** For games using `Unity.InputSystem`, `NewInputSystemBlocker` hooks `UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate(NativeInputUpdateType, IntPtr eventBuffer)`. When `BlockAllInputsWhenOpen` is active, it zeroes `eventCount` (offset 8) and `sizeInBytes` (offset 12) directly in native memory, causing `Unity.InputSystem` to drop all hardware events while resetting `Mouse.current.delta` to `(0, 0)`. When operating in selective passthrough mode, `FilterEventBuffer` inspects device IDs and FourCC state formats (`'MOUS'`, `'POIN'`, `'TOUC'`, `'KEYB'`) to compact the unmanaged buffer in place.
- **Root Cause & Resolution of Stuck Keys Across Passthrough (`AUD-09`)**:
  - **Passthrough Key Tracking**: When operating with `BlockAllInputsWhenOpen == false`, keys and mouse buttons pressed while interacting with the game are tracked in `_downKeys` via `InputManager.SetKeyDown(vk)`.
  - **Synthesis & Flushed Delivery**: When the overlay transitions from passthrough to blocked mode (or when focusing an `ImGui.InputText`), `InputManager.FlushDepressedKeysOnCaptureTransition()` synthesizes release events via `PostMessageW(WM_KEYUP)` while keeping `_downKeys` active (`keepTrackedForSubclassPassThrough: true`) and setting `_pendingKeyReleaseFlushFrames = 2`. This guarantees `SubclassHandler` and `NewInputSystemBlocker` forward the synthesized release messages to Unity instead of swallowing them.
  - **Engine Axis Reset**: `DearImGuiInjectionPlusRunner.Update()` triggers `UnityInputBlocker.TryResetInputAxes()` (`Input.ResetInputAxes()`) and `InputSystem.ResetDevice()` on the main thread during capture transitions to flush internal engine axis accumulators immediately.
- **Fullscreen Toggles (`Alt+Enter`)**:
  - `InputManager.SubclassHandler` explicitly checks for `WM_SYSKEYDOWN`/`WM_SYSKEYUP` with key code `0x0D` (`VK_RETURN`). This combination bypasses the input swallowing filter and is forwarded directly to Unity/Windows via `Comctl32.DefSubclassProc` to ensure fullscreen switching does not hang.
- **Dynamic SwapChain Recreation & Device Loss (`RR-02`, `DXH-08`)**:
  - If a game recreates its swapchain on the same target `HWND` without reporting device failure (e.g. during certain HDR or resolution transitions), both backends query the new swapchain's device (`swapChain.GetDevice<Device>()`). If it matches the existing healthy device, `_currentSwapChainPtr` is seamlessly updated without dropping the overlay. If the device differs or `DeviceRemovedReason.Failure` is detected, `InvalidateDevice()` cleanly destroys stale resources and triggers automatic reinitialization on the new device.
- **Circuit Breaker Protection (`LC-04`)**:
  - Both `DX11Renderer` and `DX12Renderer` monitor consecutive present errors. If 5 consecutive frames fail, the circuit breaker trips, pausing overlay rendering attempts for 5 seconds to prevent frame-rate collapse and log flooding (60–240 stack traces per second).
- **Auto-Repeat Suppression (`INP-07`)**: The menu toggle key (`Insert`) checks bit 30 of `lParam` to ignore auto-repeat key-down messages, preventing rapid visibility strobing.
- **Cursor Confinement & Raw Input (`INP-09`)**: Games enforcing mouse lock via `Cursor.lockState` or `Cursor.visible` are overridden by `CursorLockStatePatch`, `CursorVisiblePatch`, and native ICall detours. Windows cursor clipping is freed continuously via `User32.ClipCursor(IntPtr.Zero)`. Raw Input (`WM_INPUT` / `0x00FF`) is routed to `DefWindowProc`, preventing camera look-drift while interacting with ImGui controls.
- **Unicode, IME & `WM_UNICHAR` Support (`SEC-01`, `INP-08`)**: Non-destructive subclassing via `Comctl32` preserves the window's native Unicode procedure. `WM_CHAR`, `WM_IME_CHAR`, and `WM_UNICHAR` (including supplementary-plane emoji surrogate decomposition `0x10000..0x10FFFF`) feed `io.AddInputCharacterUTF16`. IME system messages route to `DefWindowProcW` while keyboard input is captured so OS IME candidate windows function cleanly.
- **Teardown Quiescence & Safe Shutdown Protocol (`LC-01`, `TH-01`, `LC-02`, `AUD-05`)**:
  - The destructive `Process.GetCurrentProcess().Kill()` hack has been completely eliminated.
  - The shutdown protocol executes an inverted quiescence sequence in `DearImGuiInjectionPlusBaseUnityPlugin.Shutdown`:
    1. Distinguish application exit from scene reloads: `DearImGuiInjectionPlusRunner.OnApplicationQuit` marks `_isQuitting = true`. If `OnDestroy` triggers while `_isQuitting == false`, shutdown is suppressed and `EnsureRunnerAlive()` respawns the runner.
    2. Mark `RendererFinder.IsShuttingDown = true`.
    3. Unblock New Input System devices, dispose native ICall hooks, and unpatch Harmony.
    4. Signal `FrameGate.CloseAndDrain(1000)` to drain all in-flight render calls on the `Present` thread. If the timeout expires, resource destruction is intentionally skipped to prevent use-after-free crashes at process exit.
    5. Deactivate native hooks via `RendererFinder.Dispose()` (`_hook.Disable()`).
    6. Remove window subclassing via `Comctl32.RemoveWindowSubclass` marshaled to the window thread.
    7. Flush GPU pipeline: `_deviceContext.Flush()` in D3D11; fence signal and wait (`WaitForGpuIdle()`) in D3D12.
    8. Destroy GPU resources (buffers, PSOs, descriptor heaps, font textures).
    9. Save ini settings (`DearImGuiInjectionPlus_imgui.ini`), null out `IniFilename`, and call `ImGui.DestroyContext()`.
    10. Free unmanaged ANSI strings (`_iniFilenamePtr`, `_platformNamePtr`).
  - Guarantees crash-free teardown across all DirectX backends, clean execution of co-plugin `OnDestroy` saves, and exit code `0`.

---

### 6. In-Game Validation & Test Suite Architecture

The repository includes a production-grade validation suite in `TestPlugin/` implementing the runtime test scenarios defined in [`TEST-MATRIX.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TEST-MATRIX.md):

```
+----------------------------------------------------------------------------------------------------+
|                         DearImGui Test Suite — Architecture & Test Panels                          |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                       [TestSuiteMasterPanel]
                              Unified Master Dashboard & Panel Manager
                                                  |
         +-----------------------+-----------------------+-----------------------+
         |                       |                       |                       |
[GraphicsTestPanel]     [ConcurrencyTestPanel]    [InputTestPanel]      [Il2CppTestPanel]
  - Telemetry Monitor     - StructSnapshot stress   - Unicode/IME test    - Native ICall detours
  - Buffer burst stress   - Main thread dispatch    - Stuck key test      - 3D look swallowing
  - UserCallback -1 reset - High-Hz writer worker   - Raw input monitor   - Live passthrough flush
  - HDR tone reproduction - Torn-read detection     - Capture flags       - ClassInjector stress
                                                                                 |
                                                                    [FaultRecoveryTestPanel]
                                                                      - Fault A: Window stack
                                                                      - Fault B: Menu stack
                                                                      - Fault C: Tab churn
                                                                      - Teardown timestamp
                                                                                 |
                                                                    [MemoryBenchmarkTestPanel]
                                                                      - Win32 PSAPI private bytes
                                                                      - Rolling FPS / Delta time
                                                                      - GC Gen0/1/2 heap monitors
```

#### Test Suite Component Breakdown

1. **`TestSuiteMasterPanel`**: Master dashboard providing high-level category navigation, quick status banners, and bulk show/hide controls across all floating test panels.
2. **`GraphicsTestPanel` (`TM-01`, `TM-02`, `TM-03`, `TM-08`)**:
   - Live swapchain and graphics telemetry display driven by `GraphicsTelemetry`.
   - Real-time dynamic buffer burst test generating 1,000 to 25,000+ primitives (>100,000 vertices/indices) to stress dynamic buffer reallocation on D3D11 and D3D12.
   - UserCallback `-1` reset verification asserting `ImDrawCallback_ResetRenderState` stability (`RR-03`).
   - HDR and SDR color space tone reproduction monitors.
3. **`ConcurrencyTestPanel` (`TM-04`, `TH-05`, `TH-06`)**:
   - High-frequency background writer thread (up to 10,000 Hz) publishing a multi-field `ConcurrencyTestPayload` into `StructSnapshotPipeline<T>`.
   - Continuous render-thread consumer validating that all fields match (`FieldA == FieldB == FieldC == FieldD`), confirming zero torn reads.
   - Torture testing for `UnityMainThreadDispatcher` (batch enqueueing and synchronous blocking invokes from background tasks).
4. **`InputTestPanel` (`TM-05`, `TM-06`, `INP-01`, `INP-08`)**:
   - Multilingual text entry validation with pre-populated test phrases (Polish, German umlauts, French accents, Cyrillic, Japanese, Chinese, Emojis).
   - Real-time monitor for `User32.IsWindowUnicode(hwnd)`.
   - Live display of `WantCaptureMouse`, `WantCaptureKeyboard`, and physical key/mouse button states.
5. **`FaultRecoveryTestPanel` (`TM-07`, `TM-10`, `LC-03`, `LC-05`, `LC-07`)**:
   - **Fault A Injection**: Deliberately throws an exception inside `IImGuiWindow.OnRender()` with unclosed `BeginWindow`, `PushID`, and `BeginChild` to prove `RestoreImGuiStack()` recovery.
   - **Fault B Injection**: Deliberately throws an exception inside a `RegisterTab` menu callback with unclosed `BeginMenu` to prove `RestoreImGuiWindowStack()` recovery.
   - **Fault C Churn**: Rapidly registers and unregisters menu tabs 50 times in a loop to certify zero duplicate tabs and zero memory leaks.
   - **Quiescent Teardown Verification**: Writes a shutdown timestamp to `BepInEx/config/TestPlugin_shutdown.timestamp` during `OnDestroy` to prove clean teardown without `Process.Kill()`.
6. **`MemoryBenchmarkTestPanel` (`TM-09`, `MEM-01`–`MEM-08`)**:
   - Real-time process memory metrics via Win32 PSAPI (`WorkingSetSize`, `PrivateUsage`, `PeakWorkingSetSize`).
   - Rolling graphical histograms for FPS and frame delta times.
   - Managed GC heap diagnostics (`GC.GetTotalMemory`, `GC.CollectionCount(0, 1, 2)`).
7. **`Il2CppTestPanel` (`TM-11`, `TM-12`, `TM-13`, `TM-14`, `AUD-01`–`AUD-09`)**:
   - Real-time audit of hooked native ICalls (`GetKeyInt`, `GetMouseButton`, `NotifyUpdate`, `InputUnsafeUtility`) and managed Harmony detours.
   - Interactive 3D mouse look and click suppression monitor verifying zero delta bleed-through into `UnityPlayer.dll`.
   - Live passthrough transition tester verifying immediate movement cutoff and zero stuck keys when toggling `BlockAllInputsWhenOpen` or focusing text inputs while holding movement keys.
   - IL2CPP boundary and `ClassInjector` stress harness certifying native pointer identity across forced Gen2 garbage collections.

---

### 7. Test Scenario Coverage Matrix

The scenarios implemented in `TestPlugin/` directly map to the verification requirements in [`TEST-MATRIX.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus-BE6/TEST-MATRIX.md):

| Test ID | Category | Target API | Related Audits | Invariant & Scenario Summary |
| :--- | :--- | :---: | :--- | :--- |
| **TM-01** | Bootstrap & Hook Discovery | D3D11, D3D12, Vulkan/GL | `DXH-02`, `DXH-07`, `DXH-10`, `LC-08`, `AUD-06` | Cold start, Unicode install path (`LoadLibrary`), `ClassInjector` pre-registration, `DXGI_PRESENT_TEST` filtering, unsupported API abort. |
| **TM-02** | SwapChain Resize & Fullscreen | D3D11, D3D12 | `MEM-01`, `INP-02`, `DXH-06` | Rapid `Alt+Enter`, windowed drag-resize, 1080p↔4K resolution switching, minimize/restore (`DisplaySize == 0`). |
| **TM-03** | SDR, sRGB & HDR Backbuffers | D3D11, D3D12 | `DXH-01`, `DXH-11` | `R8G8B8A8_UNorm`, `B8G8R8A8_UNorm_SRgb`, `R10G10B10A2_UNorm` (HDR10), `R16G16B16A16_Float` (scRGB) PSO & gamma. |
| **TM-04** | Concurrency & High-Rate Input | D3D11, D3D12 | `TH-02`, `TH-05`, `TH-06`, `AUD-04`, `AUD-06` | 1000–8000 Hz mouse movement + rapid typing + 200+ FPS `Present` + `StateSnapshotPipeline` / `StructSnapshotPipeline` & CoreCLR memory ordering stress. |
| **TM-05** | Input Blocking & Stuck Keys | D3D11, D3D12 | `INP-02`, `INP-05`, `INP-06`, `INP-07`, `INP-09`, `AUD-08`, `AUD-09` | Held `W`+`Shift`+`RMB` across overlay & passthrough toggles, `Insert` auto-repeat, 3D mouse look/click swallowing, Raw Input & uGUI blocking. |
| **TM-06** | Unicode, Dead Keys & CJK IME | D3D11, D3D12 | `INP-01`, `INP-08`, `SEC-01` | Non-ASCII input (Cyrillic, Polish, German umlauts, Emoji `WM_UNICHAR`, Japanese/Chinese IME) in both ImGui and native Unity UI. |
| **TM-07** | Consumer Fault & Stack Recovery | D3D11, D3D12 | `LC-03`, `LC-04`, `LC-05`, `LC-07` | Deliberate exception mid-`ImGui.Begin()`, unclosed `BeginMenu()`, `RegisterTab`/`UnregisterTab` churn, circuit breaker trip. |
| **TM-08** | Dynamic Geometry & Device Loss | D3D11, D3D12 | `DXH-03`, `DXH-04`, `DXH-06`, `DXH-08` | Dynamic vertex/index buffer growth (>100k vertices), simulated TDR / device loss (`DeviceRemovedReason.Failure`), auto re-init. |
| **TM-09** | Steady-State Memory & Soak | D3D11, D3D12 | `MEM-01`–`MEM-08`, `AUD-07` | 60-minute continuous soak at 120+ FPS monitoring VRAM, DXGI live objects, native heap, zero-alloc scene tracking, and Gen0/Gen2 GC. |
| **TM-10** | Quiescent Teardown & Exit | D3D11, D3D12 | `TH-01`, `LC-01`, `LC-02`, `DXH-05`, `SEC-02`, `INP-03`, `AUD-05` | Clean `Application.Quit()` and `Alt+F4` under heavy UI load; `OnApplicationQuit` / `Shutdown` execution & `imgui.ini` save with exit code 0. |
| **TM-11** | IL2CPP Patch & Stripping Resilience | D3D11, D3D12 | `AUD-01`, `AUD-02`, `AUD-08` | Per-method HarmonyX isolation, `NativeMethodInfoPtr` validation, C++-inlined `GetKeyInt`/`_Injected` detours, stripped module survival. |
| **TM-12** | 3D Gameplay Camera Look & `NotifyUpdate` | D3D11, D3D12 | `AUD-03`, `AUD-04`, `AUD-08` | 3D camera look (`GetRawInputBuffer()`, `Mouse.current.delta`, `Input.GetAxis`) & click suppression via `NativeInputSystem.NotifyUpdate`. |
| **TM-13** | Live Passthrough Toggle & Key Flush | D3D11, D3D12 | `AUD-09`, `INP-02`, `INP-06` | Holding `W`+`Shift`+`LMB` in passthrough (`BlockAllInputsWhenOpen = false`) then toggling to blocked; verifying immediate cutoff & zero stuck keys. |
| **TM-14** | IL2CPP Interop & `ClassInjector` Stress | D3D11, D3D12 | `AUD-03`–`AUD-07` | `ClassInjector` idempotency, `Il2CppObjectBase.Pointer` identity under forced GC, main-thread proxy enforcement, scene transition cache. |
