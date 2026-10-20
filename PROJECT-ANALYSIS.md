# PROJECT ANALYSIS: DearImGuiInjectionPlus — Architectural & Low-Level Stability Audit

**Scope:** All C# sources in `DearImGuiInjectionPlus/`, `RendererFinder/`, `TestPlugin/` (no `.cpp`/`.h` files exist in the repository; native code is the prebuilt `cimgui.dll`). Build artifacts (`bin/`, `obj/`, `.vs/`, `.git/`, binaries) were excluded.
**Reference document:** `PROJECT-OVERVIEW.md`.
**Audit dimensions:** DirectX hooking & swapchain lifecycle, threading, memory/COM lifetime, input pipeline, process teardown & fault recovery.

> Line references are against the working tree at audit time (2026-10-10). Severity scale: **Critical** (crash, device removal, data loss, or a broken resource-lifetime invariant on common paths), **Major** (likely misbehavior or crash under realistic edge conditions), **Minor** (localized defect, latent bug, or hygiene issue), **Informational** (verified-correct behavior or documentation mismatch).

---

## 1. Executive Summary

DearImGuiInjectionPlus is built sensibly. A dummy-device vtable lookup leads to an inline detour on `IDXGISwapChain::Present`/`Present1`, and on D3D12 a temporary hook on `ID3D12CommandQueue::ExecuteCommandLists` captures the game's queue. The D3D11 and D3D12 renderers are managed ports of the upstream `imgui_impl_*` backends, Win32 input is wired in through window subclassing, and input is blocked at three layers (Win32, legacy `Input`, New Input System). Several fundamentals are right:

- **VTable indices are correct:** `Present` = 8, `Present1` = 22, `GetDevice` = 7, `ExecuteCommandLists` = 10. Hook delegates are rooted in `static readonly` fields, so the GC cannot collect the reverse-P/Invoke thunks.
- **Re-entrancy is guarded.** `[ThreadStatic]` flags stop recursive `Present → Present1` and recursive `ExecuteCommandLists` dispatch on the same thread.
- **D3D12 per-backbuffer `CommandAllocator`s are fence-guarded.** Steady-state allocator resets cannot overlap GPU execution.
- **The D3D12 work is ordered correctly.** ImGui commands are submitted on the presenting queue *before* the original `Present` runs, and reusing the single CPU-only RTV descriptor heap is safe because RTV descriptors are consumed when the command list is recorded.
- **Consumer callbacks are isolated per window/tab/delegate.**

The risk profile is still **high**. The issues cluster around one cross-cutting weakness: **resource ownership and thread affinity are never written down or enforced**. The most important consequences:

1. **The D3D11 transient-RTV pattern doesn't work** ([MEM-01](#mem-01)). The render-state backup calls every SharpDX `Get*` accessor. Each one returns an `AddRef`'d COM wrapper, and none are disposed. Every frame this leaks a reference to the overlay's own backbuffer RTV, plus every pipeline object the game had bound. The backbuffer stays referenced forever, so `IDXGISwapChain::ResizeBuffers` will fail with `DXGI_ERROR_INVALID_CALL`. That is exactly the failure `PROJECT-OVERVIEW.md` says this pattern prevents.
2. **The D3D12 bootstrap over-releases the game's swapchain** ([MEM-02](#mem-02)). `using var tempSc = new SwapChain(swapChainPtr)` wraps a borrowed pointer without `AddRef` and then calls `Release` on it.
3. **The ImGui context has no thread ownership** ([TH-02](#th-02)). The window procedure (Unity main/window thread) pushes events into `ImGuiIO`'s queue while `ImGui.NewFrame()` drains it on the graphics thread. Dear ImGui is not thread-safe, so this risks heap corruption inside `cimgui.dll`.
4. **Teardown destroys resources while a frame may still be in flight** ([TH-01](#th-01), [DXH-05](#dxh-05)). The ImGui context, D3D12 command lists, heaps and fences are destroyed *before* the hooks are disabled, there is no barrier against a frame already inside `Present`, and nothing waits for the GPU (`WaitForGpuIdle` is dead code).
5. **`Process.GetCurrentProcess().Kill()` on D3D12** ([LC-01](#lc-01)) stops other plugins' `OnDestroy` handlers from running (save-data loss) and kills the game if the BepInEx manager object is destroyed mid-session. It most likely hides the real root cause, which is the combination of MEM-02, DXH-05 and TH-01 — not a Unity/Agility SDK GC bug.

**A secondary cluster comes from the Win32 subclass.** It is installed through `SetWindowLongPtrA`/`CallWindowProcA`, because the `DllImport`s don't specify a character set ([INP-01](#inp-01)). That turns Unity's Unicode window into an ANSI window for the whole session, breaks non-ASCII text entry in the game even when the overlay is closed, and makes the ANSI `WM_CHAR` path reachable. That path has a heap buffer overflow ([SEC-01](#sec-01)).

**Overall:** the hooking core is sound and the bugs can be fixed without redesigning the architecture. You need four disciplines:

- explicit COM ownership (always `AddRef` borrowed pointers, or never wrap them),
- a single-threaded ImGui ownership model with a cross-thread input queue,
- a frame-gate/quiesce protocol for teardown,
- strict exception boundaries at every native→managed transition.

---

## 2. Detailed Findings Table

| ID | Category | Severity | File & Line Reference | Summary of Issue |
| :--- | :--- | :--- | :--- | :--- |
| <a id="t-mem-01"></a>MEM-01 | Memory | **Critical** | [ImGuiDX11Impl.cs#L192](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L192), [#L241-L308](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L241-L308), [#L310-L364](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L310-L364) | D3D11 state backup leaks dozens of `AddRef`'d COM references per frame, including the overlay's own backbuffer RTV. The backbuffer is pinned permanently, `ResizeBuffers` fails, and transient game resources are never freed. |
| MEM-02 | Memory | **Critical** | [ImGuiDX12.cs#L40-L41](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L40-L41) | `using var tempSc = new SwapChain(swapChainPtr)` releases a reference the code never acquired, over-releasing the game's DXGI swapchain. |
| TH-01 | Threading / Lifecycle | **Critical** | [DearImGuiInjectionPlusBaseUnityPlugin.cs#L175-L223](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L175-L223), [DearImGuiInjectionPlus.cs#L227-L269](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L227-L269), [ImGuiDX12.cs#L85-L117](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L85-L117) | The main thread destroys the ImGui context and D3D resources while the graphics thread may be inside the `Present` hook. Hooks are disabled only *after* destruction, and there is no frame gate. |
| TH-02 | Threading | **Critical** | [InputManager.cs#L71-L134](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L71-L134), [ImGuiWin32Impl.cs#L462-L637](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L462-L637), [DearImGuiInjectionPlus.cs#L324-L349](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L324-L349) | `ImGuiIO` input queue and config flags are mutated from the window-procedure thread while the graphics thread runs `ImGui.NewFrame()`. Concurrent access to a non-thread-safe native context. |
| LC-01 | Lifecycle | **Critical** | [DearImGuiInjectionPlusBaseUnityPlugin.cs#L215-L222](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L215-L222) | `Process.GetCurrentProcess().Kill()` in `OnDestroy` (D3D12) skips other plugins' teardown and saves, and kills the game if the manager object is destroyed outside application quit. |
| DXH-01 | DirectX Hooking | Major | [ImGuiDX12.cs#L277-L281](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L277-L281), [#L353](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L353), [ImGuiDX12Impl.cs#L489](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L489) | The D3D12 PSO RTV format is hard-coded to `R8G8B8A8_UNorm`, while the RTV is created with the backbuffer's real format (`B8G8R8A8`, `R10G10B10A2`, `R16G16B16A16_Float`). This is a PSO/RTV format mismatch, which is undefined behavior. HDR color space is also ignored on both APIs. |
| DXH-02 | DirectX Hooking | Major | [DX12Renderer.cs#L163-L225](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L163-L225), [RendererFinder.cs#L81-L168](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L81-L168) | The first Direct-type queue to call `ExecuteCommandLists` from *any* thread or device is captured as "the game's queue". Nothing checks that it belongs to the swapchain's device, or that it is the queue the swapchain presents on. |
| DXH-03 | DirectX Hooking | Major | [ImGuiDX12.cs#L334-L372](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L334-L372), [#L350-L351](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L350-L351), [#L400-L403](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L400-L403) | An exception or early `return` between `CommandList.Reset` and `Close` leaves the list open. Every later `Reset` fails, so the overlay is permanently broken and the log is flooded. |
| DXH-04 | DirectX Hooking / Memory | Major | [ImGuiDX12Impl.cs#L125-L128](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L125-L128), [#L601-L611](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L601-L611), [ImGuiDX12.cs#L304-L316](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L304-L316) | The vertex/index upload ring uses its own counter and a fixed `numFramesInFlight`, separate from the per-backbuffer fences. If the buffer count grows, upload heaps that are still in flight get overwritten or destroyed. `InvalidateDeviceObjects` empties the ring but leaves the modulus non-zero, which throws `IndexOutOfRangeException`. |
| DXH-05 | DirectX Hooking / Lifecycle | Major | [ImGuiDX12.cs#L85-L141](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L85-L141), [ImGuiDX12Impl.cs#L581-L669](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L581-L669) | D3D12 resources (PSO, root signature, upload heaps, font texture, allocators, command list, heaps, fence) are released without waiting for the GPU. `WaitForGpuIdle()` is never called. |
| DXH-06 | DirectX Hooking | Major | [ImGuiDX11.cs#L89-L133](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L89-L133), [ImGuiDX12.cs#L163-L201](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L163-L201), [#L406-L418](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L406-L418) | No swapchain/device identity tracking. `IsTargetWindowHandle` accepts any non-zero HWND, so every swapchain in the process gets an ImGui frame (and can do so concurrently). A device or swapchain recreated after device-loss or a fullscreen transition is used with stale device objects. |
| DXH-07 | DirectX Hooking | Major | [DX11Renderer.cs#L256-L302](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L256-L302) | `DXGI_PRESENT_TEST` (and occlusion-probe presents) aren't filtered. ImGui renders and advances a frame even when nothing is presented, which double-blends the overlay and wastes GPU work. |
| DXH-08 | DirectX Hooking / Memory | Major | [ImGuiDX11Impl.cs#L479-L570](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L479-L570), [#L629-L635](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L629-L635), [ImGuiDX12Impl.cs#L424-L579](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L424-L579), [#L671-L675](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L671-L675) | A failure in `CreateDeviceObjects` (missing shader file, PSO creation failure) is retried every frame. Each attempt leaks the objects created before the failure and logs a full stack trace. |
| DXH-09 | DirectX Hooking | Major | [DX11Renderer.cs#L186-L192](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L186-L192), [DX12Renderer.cs#L105-L107](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L105-L107), [DX11Renderer.cs#L346-L357](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L346-L357) | Inline detours are written/toggled on hot DXGI/D3D12 code while other threads may be executing those function prologues. Threads aren't suspended and there is no safe point. |
| DXH-10 | DirectX Hooking | Minor | [DearImGuiInjectionPlusBaseUnityPlugin.cs#L39-L42](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L39-L42), [DearImGuiInjectionPlus.cs#L167-L186](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L167-L186) | Unsupported backends (Vulkan/OpenGL) are logged as errors and then silently re-targeted to D3D11, so DXGI is still hooked. Vulkan/GL drivers that present through DXGI interop swapchains then hit the D3D11 path. |
| DXH-11 | DirectX Hooking | Minor | [ImGuiDX12Impl.cs#L452-L465](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L452-L465) | The D3D12 static sampler uses `ComparisonMinMagMipLinear` with a non-comparison `Sample()` in the shader. That is invalid sampler/shader pairing and gives driver-dependent results. |
| DXH-12 | DirectX Hooking | Minor | [ImGuiDX12Impl.cs#L251-L422](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L251-L422) | Font upload creates a private queue, allocator, list and fence, then blocks with `INFINITE` inside `Present`. It has no exception safety, and creating the event can fail silently. |
| DXH-13 | DirectX Hooking | Informational | [DX11Renderer.cs#L54-L66](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L54-L66), [DX12Renderer.cs#L34-L36](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L34-L36) | ABI review: VTable slot indices and Microsoft x64 convention are correct. `HRESULT` is declared as `IntPtr` (harmless on x64; it passes RAX's upper 32 bits through). |
| MEM-03 | Memory | Major | [RendererFinder.cs#L97-L153](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L97-L153), [DX12Renderer.cs#L182-L216](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L182-L216), [ImGuiDX12.cs#L68](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L68), [#L186](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L186) | Command-queue ownership is inconsistent. `GetDevice`/`QI` paths leak one reference (once per frame on the retry path). The hook's failure path disposes a wrapper that was never `AddRef`'d (over-release). The stored queue pointer is a weak, unowned reference. |
| MEM-04 | Memory | Major | [ImGuiDX11Impl.cs#L53](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L53), [#L230](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L230), [#L644-L645](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L644-L645), [ImGuiDX12Impl.cs#L627](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L627), [#L633](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L633), [DX11Renderer.cs#L375-L377](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L375-L377), [DX12Renderer.cs#L148-L150](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L148-L150) | Many SharpDX wrappers are built around *borrowed* pointers without `AddRef`. They're safe only while `SharpDX.Configuration.EnableReleaseOnFinalizer == false`, a process-global setting any other plugin can change. If it changes, the finalizer thread mass-releases game objects. |
| MEM-05 | Memory | Minor | [ImGuiDX11.cs#L22-L42](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L22-L42), [ImGuiDX12.cs#L242](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L242), [#L114](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L114) | Owned device/context references (`GetDevice`, `ImmediateContext`) are never released. D3D11 backend shutdown is skipped entirely during engine shutdown. |
| MEM-06 | Memory | Minor | [ImGuiDX12Impl.cs#L278-L408](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L278-L408) | D3D12 font upload is not exception-safe. Upload buffer, queue, allocator, list, fence, Win32 event and texture all leak on any throw. |
| MEM-07 | Memory / Performance | Minor | [ImGuiDX11Impl.cs#L245-L305](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L245-L305), [DearImGuiInjectionPlus.cs#L365-L375](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L365-L375), [ImGuiDX11Impl.cs#L209](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L209) | Hundreds of managed allocations per frame on the graphics thread (state-backup arrays, wrappers, a per-frame `Dictionary`, `GetDelegateForFunctionPointer`). This contradicts the "zero allocations" claim and adds GC pauses to the render thread. |
| MEM-08 | Memory | Minor | [ImGuiWin32Impl.cs#L45](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L45), [#L109](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L109), [ImGuiDX11Impl.cs#L622-L626](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L622-L626), [ImGuiDX12Impl.cs#L662-L666](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L662-L666), [DearImGuiInjectionPlus.cs#L218-L221](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L218-L221) | Unmanaged backend-name strings are leaked (`BackendPlatformName`), or freed while ImGui still points at them (`BackendRendererName`). `IniFilename` is re-allocated and leaked on every init retry. |
| SEC-01 | Memory Safety | Major | [ImGuiWin32Impl.cs#L611-L622](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L611-L622) | Heap buffer overflow in the ANSI `WM_CHAR` path: a 1-byte `AllocHGlobal` receives a 2-byte `WCHAR`. INP-01 makes this path reachable in practice. |
| SEC-02 | Memory Safety | Major | [DearImGuiInjectionPlus.cs#L255-L265](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L255-L265) | `IniFilename` is freed *before* `ImGui.DestroyContext`, which reads it to save settings: a use-after-free on shutdown. |
| TH-03 | Threading | Minor | [ImGuiDX12.cs#L143-L238](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L143-L238), [DearImGuiInjectionPlus.cs#L271-L291](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L271-L291) | D3D12 initialization can run from both the `ExecuteCommandLists` hook thread and the `Present` thread, with no lock or once-guard. The path is latent today but nothing prevents it. |
| TH-04 | Threading | Minor | [DX11Renderer.cs#L68-L73](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L68-L73), [DX12Renderer.cs#L16](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L16), [#L27-L32](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L27-L32), [RendererFinder.cs#L15-L23](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L15-L23) | Cross-thread static state (`_onPresentAction`, `IsShuttingDown`, `CapturedCommandQueue`, `IsBackendInitialized`, `Initialized`, `IsCursorVisible`) has no `volatile`/`Interlocked`/lock discipline, and event add/remove is non-atomic. |
| TH-05 | Threading | Major | [UnityMainThreadDispatcher.cs#L80-L165](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/UnityMainThreadDispatcher.cs#L80-L165) | Calling `InvokeOnMainThread` from the render thread can deadlock against Unity's main↔graphics thread sync (2 s stall per call). On timeout the action still runs later, and `Set()` is called on a disposed event. |
| TH-06 | Threading | Major | [StateSnapshotPipeline.cs#L43-L81](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/StateSnapshotPipeline.cs#L43-L81) | `StructSnapshotPipeline<T>` allows torn reads: a writer can overwrite the slot a reader is still copying after two publishes. |
| TH-07 | Threading | Minor | [ImGuiDX12.cs#L325-L332](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L325-L332), [ImGuiDX12Impl.cs#L384](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L384) | Unbounded fence waits (`WaitOne()` / `INFINITE`) inside `Present`. A hung or wrong queue freezes the game permanently, with no diagnostics. |
| INP-01 | Input | Major | [User32.cs#L800-L821](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Windows/User32.cs#L800-L821), [InputManager.cs#L44](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L44) | `SetWindowLongPtr`/`CallWindowProc`/`DefWindowProc` bind to the **ANSI** (`…A`) exports. Unity's window becomes ANSI for the whole session, and non-ASCII game text input breaks even with the overlay closed. |
| INP-02 | Input | Major | [InputManager.cs#L120-L130](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L120-L130) | Key-up and button-up messages are swallowed while the overlay is open. Keys and buttons held when it opens stay "down" in the game after it closes. |
| INP-03 | Input / Lifecycle | Major | [InputManager.cs#L37-L65](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L37-L65) | Unsubclassing blindly restores the saved WndProc and drops the delegate root. Any subclasser installed after this one is unlinked, and if it still calls this thunk, that calls into a collected delegate (crash). |
| INP-04 | Input | Major | [ImGuiWin32Impl.cs#L157-L191](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L157-L191), [#L114-L155](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L114-L155), [#L270-L299](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L270-L299) | Thread-affine Win32 APIs (`GetKeyState`, `SetCursor`) are called from the graphics thread, which has no message queue attached to the window. Shift/Win keys are force-released every frame, and cursor shape updates don't apply. |
| INP-05 | Input | Major | [UnityInputBlocker.cs#L6-L109](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Patches/UnityInputBlocker.cs#L6-L109), [NewInputSystemBlocker.cs#L16-L87](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs#L16-L87), [InputManager.cs#L112-L118](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L112-L118) | Gaps in input-blocking coverage: `Input.inputString`, `mousePosition`, touches, gamepads/XInput, `GetRawInputBuffer` polling, Rewired/Steam Input, non-`current` devices, devices the game re-enables, and inlined JIT callers. |
| INP-06 | Input | Minor | [DearImGuiInjectionPlus.cs#L43](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L43), [DearImGuiInjectionPlusBaseUnityPlugin.cs#L161](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L161) | The `BlockAllInputsWhenOpen` config is honored only by the uGUI `EventSystem` hook. Win32 swallowing and the Harmony patches ignore it. |
| INP-07 | Input | Minor | [InputManager.cs#L86-L95](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L86-L95), [ImGuiWin32Impl.cs#L507-L546](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L507-L546), [InputManager.cs#L107-L110](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L107-L110) | Toggle-key auto-repeat flips the overlay repeatedly. Mouse capture and button bitmask leak when it is closed mid-drag. `WM_SETCURSOR` is consumed for non-client areas, so resize cursors don't appear. |
| INP-08 | Input | Minor | [InputManager.cs#L147-L154](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L147-L154) | IME (`WM_IME_*`) and `WM_UNICHAR` aren't handled. IME composition leaks to the game and CJK input never reaches ImGui. |
| INP-09 | Input | Minor | [CursorManager.cs#L6-L44](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/CursorManager.cs#L6-L44), [InputManager.cs#L156-L250](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L156-L250) | Cursor state sync edge cases: non-main-thread Unity API calls from WndProc, `EnforceCursorStateOnMainThread` is dead code, and direct `ClipCursor`/`SetCursorPos` calls by games bypass it. `ClipCursor(NULL)` runs only once. |
| LC-02 | Lifecycle | Major | [DearImGuiInjectionPlusBaseUnityPlugin.cs#L182-L211](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L182-L211), [ImGuiDX11.cs#L29-L39](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L29-L39), [DX11Renderer.cs#L223-L254](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L223-L254) | Teardown order is inverted (resources first, hooks last) and asymmetric between D3D11 and D3D12. Reloaded `Disable()` does not restore the original bytes, despite the comment claiming it does. |
| LC-03 | Lifecycle / Fault Recovery | Major | [DX11Renderer.cs#L256-L302](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L256-L302), [DX12Renderer.cs#L163-L225](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L163-L225), [InputManager.cs#L71-L134](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L71-L134) | Exception boundaries at native→managed entry points are incomplete. `Present` hooks use `try/finally` without `catch`, the `ExecuteCommandLists` hook can skip the original on a throw, and the WndProc has no boundary at all. An escaping exception crashes the process under Mono. |
| LC-04 | Lifecycle / Fault Recovery | Minor | [DX11Renderer.cs#L383-L386](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L383-L386), [ImGuiDX12.cs#L400-L403](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L400-L403) | No circuit breaker. Persistent failures log a full stack trace every frame (≈60–240 entries/s), so the log grows without bound and the frame rate drops. |
| LC-05 | Lifecycle / Fault Recovery | Major | [DearImGuiInjectionPlus.cs#L354-L435](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L354-L435) | A consumer exception between `Begin`/`End` (or `Push`/`Pop`) leaves ImGui's window/ID stacks unbalanced, and nothing calls ImGui's error-recovery API. Result: an assertion abort (asserting cimgui builds) or progressive UI corruption. |
| LC-06 | Lifecycle | Minor | [ImGuiWin32Impl.cs#L33-L37](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L33-L37), [#L95-L112](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L95-L112), [RendererFinder.cs#L190-L204](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L190-L204) | Backends can't be re-initialized: `_windowHandle` is never reset, `IsShuttingDown` is sticky, and the init retry path duplicates fonts. |
| LC-07 | Lifecycle / API | Minor | [DearImGuiInjectionPlus.cs#L127-L142](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L127-L142), [TestPlugin.cs#L89-L106](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/TestPlugin/TestPlugin.cs#L89-L106) | There's no `UnregisterTab`. `TestPlugin` re-registers its tab on every `OnEnable`, creating duplicates, and the closures keep disabled plugins alive. |
| LC-08 | Lifecycle | Minor | [DearImGuiInjectionPlusBaseUnityPlugin.cs#L95-L126](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L95-L126), [Kernel32.cs#L100-L101](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Windows/Kernel32.cs#L100-L101) | `cimgui.dll` is preloaded through `LoadLibraryA` (ANSI), which fails for install paths containing characters outside the active code page. |
| DOC-01 | Informational | Informational | [PROJECT-OVERVIEW.md](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/PROJECT-OVERVIEW.md) | Several claims in the overview contradict the code: the transient RTV preventing resize errors, "zero allocations", hooks "restoring original bytes", `BlockAllInputsWhenOpen` semantics, and the root cause of the D3D12 shutdown crash. |

---

## 3. In-Depth Technical Analysis

### 3.1 Critical Findings

<a id="mem-01"></a>
#### MEM-01 — D3D11 render-state backup leaks COM references every frame (pins the backbuffer)

**Technical Description.**
`ImGuiDX11Impl.RenderDrawData` calls `BackupRenderState(ctx)` ([L192](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L192)). That function reads the whole pipeline through SharpDX accessors such as `ctx.InputAssembler.InputLayout`, `GetIndexBuffer`, `GetVertexBuffers`, `Rasterizer.State`, `OutputMerger.GetBlendState`/`GetDepthStencilState`/`GetRenderTargets`, `VertexShader.Get()`, `GetSamplers`, `GetConstantBuffers`, `GetShaderResources` (for VS/HS/DS/GS/PS/CS), and `ComputeShader.GetUnorderedAccessViews`.

Every one of those maps to a D3D11 `*Get*` call (for example `ID3D11DeviceContext::OMGetRenderTargets`). Those calls **`AddRef` each returned interface**, and SharpDX wraps each one in a fresh `ComObject` that takes over that reference. `RestoreRenderState` ([L310-L364](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L310-L364)) re-binds them but never calls `Dispose()`. The `StateBackup` object then becomes garbage. SharpDX's default `Configuration.EnableReleaseOnFinalizer = false` means the finalizer **does not** call `Release`, so every reference leaks permanently. If a co-loaded plugin has turned that flag on, the references are instead released on the finalizer thread at arbitrary times (see MEM-04).

The order of operations makes it worse. [`ImGuiDX11.RenderImGui`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L106-L118) binds the overlay's **own transient RTV** *before* calling `RenderDrawData`. So the backup captures `OMGetRenderTargets` → **our RTV with +1 reference**. The `using` block releases only the reference that `new RenderTargetView(...)` created. The extra reference keeps the RTV alive, and the RTV holds an internal reference on the swapchain's backbuffer `ID3D11Texture2D`.

**Impact Assessment.**
- **Resize/fullscreen failure.** `IDXGISwapChain::ResizeBuffers` requires that *all* backbuffer references are released. Because a new RTV is leaked every frame and each one holds the backbuffer, `ResizeBuffers` returns `DXGI_ERROR_INVALID_CALL`. Depending on the Unity version, that shows up as a black or stretched image after a resize, a failed Alt+Enter transition, `"Failed to resize swapchain"` errors, or a forced device reset/crash. This is exactly the failure `PROJECT-OVERVIEW.md` says the transient RTV prevents.
- **Unbounded memory growth.** One leaked `ID3D11RenderTargetView` per frame (216,000 per hour at 60 FPS), plus a pinned reference on every shader, buffer, SRV, sampler and state the game had bound at end of frame. Transient render textures the game frees (post-processing chains, resolution changes, scene loads) can never be destroyed while they were bound at `Present`, which causes VRAM growth and eventually `E_OUTOFMEMORY` or a device-removed error.
- **Driver object limits.** Some drivers cap live view objects, so a long session can hit allocation failures.

**Recommended Solution.**
1. Make `StateBackup` deterministic. Implement `IDisposable` and dispose *every* captured object (including each element of the arrays) right after `RestoreRenderState`. This mirrors upstream `imgui_impl_dx11.cpp`, which `Release()`s every backed-up interface:
   ```csharp
   private sealed class StateBackup : IDisposable
   {
       // ...fields...
       public void Dispose()
       {
           InputLayout?.Dispose(); IndexBuffer?.Dispose(); RS?.Dispose();
           BlendState?.Dispose(); DepthStencilState?.Dispose(); DepthStencilView?.Dispose();
           DisposeAll(VertexBuffers); DisposeAll(RenderTargetViews);
           VS?.Dispose(); DisposeAll(VSSamplers); DisposeAll(VSConstantBuffers); DisposeAll(VSResourceViews);
           // ...HS, DS, GS, PS, CS (incl. CSUAVs)...
       }
       private static void DisposeAll<T>(T[] a) where T : class, IDisposable
       { if (a == null) return; for (int i = 0; i < a.Length; i++) { a[i]?.Dispose(); a[i] = null; } }
   }
   // usage
   using (var old = BackupRenderState(ctx)) { /* setup + draw */ RestoreRenderState(ctx, old); }
   ```
2. Back up the state **before** binding the overlay's RTV, so the game's original OM bindings are what gets restored. Then release the transient RTV *after* restoring.
3. Reduce the backup to what upstream actually saves (scissors/viewports with their *real* counts, RS, OM blend/depth, IA, VS/PS with slot 0 SRV/sampler/CB, GS). Backing up 128 SRV slots × 6 stages is unnecessary at `Present` and multiplies the leak surface. Use `RSGetViewports(ref count, …)` to restore the real viewport count instead of 16 zero-sized viewports.
4. Better still, avoid the problem entirely. Use an `ID3DDeviceContextState` (`ID3D11DeviceContext1::SwapDeviceContextState`) with a pre-built state object. That swaps the whole pipeline atomically, with no per-object `Get*` calls and no leaks. It needs Win8+/D3D11.1, which every supported Unity version already requires.

---

<a id="mem-02"></a>
#### MEM-02 — Over-release of the game's swapchain during D3D12 bootstrap

**Technical Description.**
In [`ImGuiDX12.Init`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L36-L47):
```csharp
using var tempSc = new SwapChain(swapChainPtr);
windowHandle = tempSc.Description.OutputHandle;
```
`SharpDX.ComObject(IntPtr)` takes the pointer **without calling `AddRef`**. `Dispose()` calls `IUnknown::Release`. `swapChainPtr` is the `self` argument of the hooked `Present`, a *borrowed* reference owned by Unity. The `using` block therefore removes one of Unity's references to its swapchain.

**Impact Assessment.**
- The swapchain's refcount is one lower than Unity thinks. When Unity drops its last reference (resolution change, `ResizeBuffers`-based recreation, fullscreen mode change, quit), the object is destroyed one `Release` early, while DXGI or the D3D12 runtime still has internal users. The result is a use-after-free inside `dxgi.dll` or `d3d12core.dll`. Typical symptoms are access violations in `dxgi!CDXGISwapChain::*`, or crashes during shutdown's final release.
- **This is the most likely real cause of the "Unity 6 D3D12 Agility SDK shutdown crash"** that `OnDestroy` works around with `Process.Kill()` (see LC-01). The crash appears at teardown because that is when the final `Release` sequence runs.

**Recommended Solution.**
- Either `Marshal.AddRef(swapChainPtr)` before wrapping (then the `using` is balanced), or skip the wrapper and read the HWND via `IDXGISwapChain::GetDesc` / `IDXGISwapChain1::GetHwnd` through a raw vtable call.
- Adopt a single helper for borrowed pointers across the codebase:
  ```csharp
  static T Borrow<T>(IntPtr p) where T : SharpDX.ComObject
  { Marshal.AddRef(p); return (T)Activator.CreateInstance(typeof(T), p); } // caller MUST Dispose
  ```
- Use the D3D12 Debug Layer and `DXGI_DEBUG_ALL` live-object reporting (`IDXGIDebug1::ReportLiveObjects`) in development builds to catch refcount imbalance regressions.

---

<a id="th-01"></a>
#### TH-01 — Teardown races with an in-flight render frame

**Technical Description.**
Unity runs `OnDestroy` on the **main thread**. With multithreaded rendering, which is the default on desktop (and native graphics jobs only add to it), `Present` runs on the **graphics/render thread**. The shutdown sequence in [`OnDestroy`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L175-L223) is:
1. `IsShuttingDown = true` (a plain static property with no memory barrier, see TH-04).
2. `InputManager.Unsubclass()`.
3. `DearImGuiInjectionPlus.Dispose()`. That calls `ImGuiDX12.Dispose()`, which disposes `_commandList`, both descriptor heaps, `_fence`, `_fenceEvent` and the allocators, and runs `ImGuiDX12Impl.Shutdown()` (PSO, root signature, font texture, upload buffers). It then frees `IniFilename` and calls `ImGui.DestroyContext`.
4. Only then `RendererFinder.Dispose()` disables the hooks.

The hooks check `IsShuttingDown` only on **entry**. A render thread that has already passed the check, or is part-way through `ImGui.NewFrame()`/`RenderDrawData`/`ExecuteCommandList`, keeps using objects the main thread is destroying. Nothing signals "a frame is in progress" (no counter, lock or event), so the main thread cannot wait for it to finish.

**Impact Assessment.**
- Access violation in `cimgui.dll`: `DestroyContext` frees `ImGuiContext` while `NewFrame`/`Render` dereferences it.
- `ObjectDisposedException` or a native AV in SharpDX when using a disposed `GraphicsCommandList`/`DescriptorHeap`. Any managed exception that escapes the hook crashes the process (LC-03).
- `SafeWaitHandle` disposed during `_fenceEvent.WaitOne()` gives an `ObjectDisposedException` on the render thread.
- These are intermittent exit crashes that depend on timing. They are hard to reproduce, and fixing them is what would make `Process.Kill()` unnecessary.

**Recommended Solution.** Add a **frame gate** with a quiesce protocol:
```csharp
static class FrameGate
{
    static int _state;             // 0 = open, 1 = closing
    static int _inFlight;
    public static bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _state) != 0) { Interlocked.Decrement(ref _inFlight); return false; }
        return true;
    }
    public static void Exit() => Interlocked.Decrement(ref _inFlight);
    public static bool CloseAndDrain(int timeoutMs)
    {
        Volatile.Write(ref _state, 1);
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) != 0)
        { if (sw.ElapsedMilliseconds > timeoutMs) return false; Thread.Sleep(1); }
        return true;
    }
}
```
- The `Present` hook wraps `HandlePresent` in `if (FrameGate.TryEnter()) try { … } finally { FrameGate.Exit(); }`, and always calls the original.
- Shutdown order: **(a)** `CloseAndDrain` → **(b)** disable hooks (`Disable()` is now harmless because nothing new enters) → **(c)** D3D12: signal and wait on the fence for the *presenting queue* (DXH-05) → **(d)** release GPU objects → **(e)** restore WndProc (INP-03) → **(f)** `ImGui.SaveIniSettingsToDisk`, null out `IniFilename`, `DestroyContext`, free strings (SEC-02).
- If draining times out (render thread stuck in the driver), **skip** destruction and leak deliberately. A leak at process exit is harmless; a use-after-free is not.

---

<a id="th-02"></a>
#### TH-02 — The ImGui context is mutated from two threads

**Technical Description.**
Dear ImGui keeps one global `ImGuiContext` with no internal locking. All `io.Add*Event` calls append to `g.InputEventsQueue` (an `ImVector` that may reallocate), and `ImGui::NewFrame()` reads and clears it.

- **Window-procedure thread.** [`InputManager.WndProcHandler`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L104) → [`ImGuiWin32Impl.WndProcHandler`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L462-L637) calls `AddMousePosEvent`, `AddMouseButtonEvent`, `AddKeyEvent`, `SetKeyEventNativeData`, `AddInputCharacter*` and `AddFocusEvent`. [`ToggleCursor`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L315-L349) and [`OnCursorVisibilityToggled`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L193-L200) also write `ConfigFlags` and `WantSetMousePos` and add mouse events. In the Unity Windows player the message pump runs on the main thread in most versions; some versions and launch modes use a dedicated window thread. Either way, it is **not** the graphics thread.
- **Graphics thread.** `ImGuiWin32Impl.NewFrame` → `ImGui.NewFrame()` → user callbacks → `ImGui.Render()`.
- **Main thread.** The `EventSystem.Update` detour reads `IO.WantCaptureMouse` ([L166](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L166)). That read is benign but racy.

**Impact Assessment.**
- An `ImVector::push_back` reallocation that overlaps `NewFrame`'s iteration causes heap corruption inside `cimgui.dll`. The crash surfaces later in unrelated allocations (CRT heap corruption `0xC0000374`, or AVs in `ImGui::UpdateInputEvents`).
- Lost or duplicated input events: missed clicks, stuck keys inside ImGui.
- Torn `ConfigFlags` updates (`NoMouse` not applied or not cleared).

This becomes more likely with high-poll-rate mice (1000–8000 Hz `WM_MOUSEMOVE`) combined with a high frame rate.

**Recommended Solution.**
- **Single-owner rule:** only the graphics thread touches the ImGui context.
- In the WndProc, translate messages into a compact managed struct and enqueue it into a `ConcurrentQueue<InputEvent>` (or a pre-allocated SPSC ring buffer to avoid allocations). Return the swallow/forward decision synchronously from *managed* state. `WantCaptureMouse`/`WantCaptureKeyboard` should be mirrored into `volatile bool`s that the render thread publishes after each frame.
- At the top of the render-thread frame, before `ImGui.NewFrame()`, drain the queue and replay it through the `io.Add*Event` APIs.
- Route `ToggleCursor`'s ImGui-side effects (`ConfigFlags`, initial mouse position) through the same queue as a "visibility changed" event.

---

<a id="lc-01"></a>
#### LC-01 — `Process.Kill()` during `OnDestroy` (D3D12)

**Technical Description.**
When the renderer is D3D12, [`OnDestroy`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L219-L222) calls `Process.GetCurrentProcess().Kill()`, which is `TerminateProcess` on itself. The comment assumes "all save data, Steam/EOS, and plugins have finished teardown". That assumption is wrong:
- All BepInEx 5 plugins are components on the same `BepInEx_Manager` GameObject. Unity gives **no ordering guarantee** for `OnDestroy` across components, so any plugin whose `OnDestroy`/`OnApplicationQuit` saves data and has not run yet is skipped.
- `OnDestroy` is not exclusive to application quit. Games that destroy all root objects (some loaders call `Destroy` on every scene root, and BepInEx's `HideManagerGameObject`/scene-reload scenarios exist), or other mods that clean up stray objects, trigger it mid-session. The game is then **terminated during play**.
- `TerminateProcess` skips Unity's shutdown entirely: `PlayerPrefs` flush, the Steamworks `SteamAPI_Shutdown` (cloud sync), the crash handler, buffered BepInEx log writes (the `LogOutput.log` tail is lost, including the "shutdown completed safely" line just written), and EOS/analytics flush.

**Impact Assessment.** Save corruption or loss in the game and in other mods, missing logs, unexplained mid-session exits, and Steam Cloud conflicts. It also hides genuine lifetime bugs (MEM-02, DXH-05, TH-01) that are probably the real cause of the crash the workaround targets.

**Recommended Solution.**
1. Remove `Kill()`. Fix MEM-02, MEM-03, DXH-05 and TH-01, then re-test the Unity 6 D3D12 exit path under the D3D12 Debug Layer with GPU-based validation.
2. If a fallback is still needed, gate it strictly. Run it only when application quit is genuinely in progress (subscribe to `Application.quitting`, which is raised before `OnDestroy` on quit, and set a flag), and only as the *last* action in `Application.quitting`'s handler or a late `AppDomain.ProcessExit` handler. Never run it from `OnDestroy`.
3. Better: if GPU teardown cannot be made safe, *don't release* GPU objects at quit. Leaking them deliberately once the frame gate is closed and the hooks are disabled is safe, because the OS reclaims everything.

---

### 3.2 DirectX Hooking & SwapChain Lifecycle Findings

#### DXH-01 — Swapchain buffer format mismatch (hardcoded `R8G8B8A8_UNorm`) and missing color space / HDR handling
- **File & Line Reference:** [`ImGuiDX12.cs#L277-L281`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L277-L281), [`#L353`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L353), [`ImGuiDX12Impl.cs#L489`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L489)
- **Technical Description:**
  In Direct3D 12, the pipeline state object (PSO) is compiled against a strictly specified render target format array (`psoDesc.RenderTargetFormats[0] = RTVFormat`). In `ImGuiDX12.InitImGuiDX12Internal`, the backend passes a hardcoded format:
  ```csharp
  ImGuiDX12Impl.Init((void*)_device.NativePointer, bufferCount,
      Format.R8G8B8A8_UNorm,
      (void*)_shaderDescriptorHeap.NativePointer, ...);
  ```
  However, in `RenderImGui`:
  ```csharp
  _device.CreateRenderTargetView(backBufferResource, null, _rtvDescriptorHeap.CPUDescriptorHandleForHeapStart);
  ```
  Passing `null` as the `RenderTargetViewDescription` causes Direct3D 12 to inherit the exact underlying resource format of `backBufferResource`. Modern Unity games targeting DirectX 11 and 12 frequently configure their DXGI swapchains with formats other than standard `DXGI_FORMAT_R8G8B8A8_UNORM`, such as:
  - `DXGI_FORMAT_B8G8R8A8_UNORM` (standard for modern DXGI Flip model presentation),
  - `DXGI_FORMAT_R10G10B10A2_UNORM` (HDR10 displays or wide-color-gamut presentation),
  - `DXGI_FORMAT_R16G16B16A16_FLOAT` (scRGB linear HDR backbuffers).
  
  When the backbuffer format differs from `R8G8B8A8_UNorm`, the D3D12 command list validation fails when `OMSetRenderTargets` binds an RTV whose descriptor format does not match `psoDesc.RenderTargetFormats[0]`. In addition, in DX11 mode (`ImGuiDX11.cs#L107`), `new RenderTargetView(_device, backBuffer)` without an explicit description creates a view matching the swapchain format. If the backbuffer is an sRGB format (e.g. `R8G8B8A8_UNorm_SRgb`), ImGui's non-sRGB output will undergo automatic hardware sRGB conversion, resulting in washed-out colors.
- **Impact Assessment:**
  On D3D12, attempting to draw with a PSO whose `RenderTargetFormats[0]` differs from the bound RTV's format triggers D3D12 runtime validation errors: `CORRUPTED_PARAMETER` or `INVALID_CALL`, culminating in `DXGI_ERROR_DEVICE_REMOVED` or silent drop of ImGui draw commands. On HDR or sRGB swapchains, ImGui text and controls appear significantly desaturated, washed out, or clipped.
- **Recommended Solution:**
  1. Dynamically inspect the swapchain backbuffer format during initialization:
     ```csharp
     var desc = swapChain.Description;
     var backBufferFormat = desc.ModeDescription.Format;
     ```
  2. In DX12, initialize `ImGuiDX12Impl` with `backBufferFormat` instead of `Format.R8G8B8A8_UNorm`.
  3. If the swapchain uses an sRGB or HDR format (`R10G10B10A2_UNorm`, `R16G16B16A16_Float`), handle color space conversion: in DX11, create the transient RTV with an explicit `Format.R8G8B8A8_UNorm` or convert vertex/texture color outputs to linear space within the pixel shader bytecode or via custom ImGui style gamma correction.

---

#### DXH-02 — Ambiguous D3D12 CommandQueue capture without SwapChain affinity check
- **File & Line Reference:** [`DX12Renderer.cs#L163-L225`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L163-L225), [`RendererFinder.cs#L81-L168`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L81-L168)
- **Technical Description:**
  In `DX12Renderer.CommandQueueExecuteCommandListHook`, the hook intercepts `ID3D12CommandQueue::ExecuteCommandLists` and captures the first queue pointer where `queueType == CommandListType.Direct`:
  ```csharp
  var queueType = checkQueue.Description.Type;
  if (queueType == CommandListType.Direct)
  {
      CapturedCommandQueue = self;
      ...
  }
  ```
  Unity's graphics engine under DirectX 12 frequently creates multiple `Direct` command queues (e.g., asynchronous compute engines emulated via direct queues, secondary worker thread queues, video decoding queues, or VR / XR submission queues). 
  DXGI swapchains under Direct3D 12 require that commands rendering into backbuffer resources and executing right before `Present` are submitted to the **exact command queue associated with the swapchain** during `CreateSwapChainForHwnd`. Capturing whatever direct queue happens to execute first on any background job thread leads to capturing the wrong command queue.
- **Impact Assessment:**
  If a secondary or worker queue is captured, submitting ImGui's command list on that queue while the swapchain is presented on Unity's primary rendering queue causes cross-queue resource hazard violations. The GPU attempts to present a backbuffer that is in the middle of being written by an unsynchronized queue, causing screen tearing, visual artifacts, device hang, or `DXGI_ERROR_DEVICE_HUNG` / `DXGI_ERROR_DEVICE_REMOVED`.
- **Recommended Solution:**
  1. Prioritize querying the command queue directly from the swapchain using `RendererFinder.QueryCommandQueueFromSwapChain(swapChainPtr)` before enabling the global `ExecuteCommandLists` detour.
  2. In `ExecuteCommandLists`, compare device pointers: verify that `checkQueue.Device.NativePointer == swapChainDevice.NativePointer`.
  3. Defer queue binding until `Present` is invoked; cross-reference thread affinity or verify that the queue has submitted a command list transitioning the swapchain's current backbuffer.

---

#### DXH-03 — CommandList leak and permanent pipeline failure on rendering exception
- **File & Line Reference:** [`ImGuiDX12.cs#L334-L372`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L334-L372), [`#L350-L351`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L350-L351), [`#L400-L403`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L400-L403)
- **Technical Description:**
  In `ImGuiDX12.RenderImGui`, the graphics command list is reset:
  ```csharp
  currentFrameContext.CommandAllocator.Reset();
  _commandList.Reset(currentFrameContext.CommandAllocator, null);
  ```
  After this point, if an unhandled exception occurs (e.g. inside `ImGuiDX12Impl.RenderDrawData`, font texture binding, or if `swapChain.GetBackBuffer<Resource>` returns null):
  ```csharp
  using (var backBufferResource = swapChain.GetBackBuffer<Resource>(backBufferIndex))
  {
      if (backBufferResource == null)
          return; // CommandList is LEFT OPEN in recording state!
      ...
  }
  _commandList.Close();
  ```
  If an exception is thrown or `backBufferResource == null` triggers an early return, `_commandList.Close()` is never reached. On the next frame, `_commandList.Reset(...)` is called on a command list that is still in the recording state. Direct3D 12 requires that a command list must be closed before it can be reset. Calling `Reset` on an open command list throws an HRESULT exception (`E_FAIL` / `0x80004005`).
- **Impact Assessment:**
  Once any single frame encounters an exception or a null backbuffer resource, the command list remains permanently open. Every subsequent frame throws an exception in `_commandList.Reset`, completely killing ImGui overlay rendering for the remainder of the game session and spamming the BepInEx log at 60–240 FPS.
- **Recommended Solution:**
  Enclose the command list recording sequence within a strict `try/finally` block:
  ```csharp
  currentFrameContext.CommandAllocator.Reset();
  _commandList.Reset(currentFrameContext.CommandAllocator, null);
  bool listClosed = false;
  try
  {
      // Recording and rendering operations
      ...
      _commandList.Close();
      listClosed = true;
      _commandQueue.ExecuteCommandList(_commandList);
  }
  finally
  {
      if (!listClosed)
      {
          try { _commandList.Close(); } catch { }
      }
  }
  ```

---

#### DXH-04 — D3D12 vertex/index upload ring unsynchronized with GPU fences & buffer resizing race
- **File & Line Reference:** [`ImGuiDX12Impl.cs#L125-L128`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L125-L128), [`#L601-L611`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L601-L611), [`ImGuiDX12.cs#L304-L316`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L304-L316)
- **Technical Description:**
  `ImGuiDX12Impl` maintains its own `RenderBuffer[] pFrameResources` array of size `numFramesInFlight`. It cycles through them using:
  ```csharp
  frameIndex = frameIndex + 1;
  var fr = pFrameResources[frameIndex % numFramesInFlight];
  ```
  Meanwhile, `ImGuiDX12` manages backbuffers and fences indexed by `swapChain.CurrentBackBufferIndex`:
  ```csharp
  int backBufferIndex = swapChain.CurrentBackBufferIndex;
  var currentFrameContext = _frameContexts[backBufferIndex];
  ```
  These two indexing mechanisms are completely disconnected:
  1. `swapChain.CurrentBackBufferIndex` is driven by DXGI flip scheduling (which does not advance in a strict linear sequential modulo order if frames are dropped, throttled, or presented out of turn).
  2. `frameIndex` simply increments on every `RenderDrawData` call.
  3. When vertex or index buffer capacity is exceeded, `fr.VertexBuffer?.Dispose()` is called immediately, and a new committed resource is created. If `pFrameResources` is recycled while the previous frame using that slot is still being read by the GPU (because `pFrameResources` does not track GPU fence completion values), active vertex memory is unmapped or disposed while in flight.
  4. In `InvalidateDeviceObjects()`, `pFrameResources = Array.Empty<RenderBuffer>()` is set, but `numFramesInFlight` is NOT reset to 0. A subsequent render call before reinitialization will evaluate `pFrameResources[frameIndex % numFramesInFlight]` on an empty array, throwing `IndexOutOfRangeException`.
- **Impact Assessment:**
  GPU crashes (`DXGI_ERROR_DEVICE_REMOVED` with reason `DXGI_ERROR_DEVICE_HUNG`) caused by reading freed or overwritten upload heap memory. Dynamic vertex/index buffer expansion under heavy UI drawing loads (such as extensive debug logs or complex custom windows) crashes the D3D12 pipeline.
- **Recommended Solution:**
  Tie `RenderBuffer` directly to `FrameContext` in `ImGuiDX12.cs` so that upload buffers share the exact same fence synchronization boundary as `CommandAllocator`:
  ```csharp
  internal class FrameContext
  {
      internal CommandAllocator CommandAllocator;
      internal long FenceValue;
      internal Resource VertexBuffer;
      internal Resource IndexBuffer;
      internal int VertexBufferSize;
      internal int IndexBufferSize;
  }
  ```
  Only reset or reallocate `VertexBuffer` / `IndexBuffer` after the fence for that specific `FrameContext` has completed on the CPU (`_fence.CompletedValue >= currentFrameContext.FenceValue`).

---

#### DXH-05 — D3D12 GPU-CPU synchronization omitted on teardown
- **File & Line Reference:** [`ImGuiDX12.cs#L85-L141`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L85-L141), [`ImGuiDX12Impl.cs#L581-L669`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L581-L669)
- **Technical Description:**
  In `ImGuiDX12.cs`, a method `WaitForGpuIdle()` is implemented:
  ```csharp
  private static void WaitForGpuIdle()
  {
      ...
      _fenceValue++;
      _commandQueue.Signal(_fence, _fenceValue);
      if (_fence.CompletedValue < _fenceValue)
      {
          _fence.SetEventOnCompletion(_fenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
          _fenceEvent.WaitOne();
      }
  }
  ```
  However, **`WaitForGpuIdle()` is never called anywhere in the codebase.**
  When `ImGuiDX12.Dispose()` runs during plugin shutdown or backend disposal, it immediately calls `_commandList?.Dispose()`, `_rtvDescriptorHeap?.Dispose()`, `_shaderDescriptorHeap?.Dispose()`, `_fence?.Dispose()`, and disposes all `CommandAllocator`s without waiting for commands recorded on the GPU in the previous frame to finish executing.
- **Impact Assessment:**
  Disposing D3D12 command allocators, root signatures, PSOs, or descriptor heaps while command lists are actively executing on the GPU causes an immediate native hardware exception and device crash (`DXGI_ERROR_DEVICE_REMOVED`). This is another direct root contributor to the teardown crashes observed under D3D12.
- **Recommended Solution:**
  At the beginning of `ImGuiDX12.Dispose()`, call `WaitForGpuIdle()` before disposing any native D3D12 device objects:
  ```csharp
  internal static void Dispose()
  {
      WaitForGpuIdle();
      ...
  }
  ```

---

#### DXH-06 — Missing SwapChain & Device identity tracking across resolution changes and multi-window contexts
- **File & Line Reference:** [`ImGuiDX11.cs#L89-L133`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L89-L133), [`ImGuiDX12.cs#L163-L201`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L163-L201), [`#L406-L418`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L406-L418)
- **Technical Description:**
  The injection system assumes that the game contains exactly one swapchain and that its underlying `ID3D11Device` / `ID3D12Device` pointer remains static for the entire lifetime of the process. In `IsTargetWindowHandle`:
  ```csharp
  private static bool IsTargetWindowHandle(IntPtr windowHandle)
  {
      if (windowHandle != IntPtr.Zero)
      {
          if (_windowHandle == IntPtr.Zero)
              _windowHandle = windowHandle;
          return true; // Returns true for ANY non-zero window handle!
      }
      return false;
  }
  ```
  If Unity creates multiple swapchains (e.g. secondary viewports, splash screens, editor windows, or auxiliary camera outputs), `RenderImGui` executes against *every* presenting swapchain in the process. Furthermore, if the game's graphics device is recreated after a driver reset (`DXGI_ERROR_DEVICE_RESET`) or display resolution change, `_device` and `_deviceContext` retain references to the dead, invalidated device.
- **Impact Assessment:**
  Attempting to render using device objects bound to a dead device throws SharpDX COM exceptions every frame. In multi-window environments, ImGui renders into secondary swapchains out-of-order, corrupting state and causing severe flickering.
- **Recommended Solution:**
  1. Store the exact `swapChain.NativePointer` in `_currentSwapChainPtr` and only render to that primary game swapchain.
  2. Implement device loss detection: if `device.DeviceRemovedReason` indicates device loss, invalidate all device objects via `InvalidateDeviceObjects()`, reset `_device = null`, and allow reinitialization on the newly created device.

---

#### DXH-07 — `DXGI_PRESENT_TEST` and occlusion presents unhandled
- **File & Line Reference:** [`DX11Renderer.cs#L256-L302`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L256-L302)
- **Technical Description:**
  In `SwapChainPresentHook` and `SwapChainPresent1Hook`:
  ```csharp
  private static IntPtr SwapChainPresentHook(IntPtr self, uint syncInterval, uint flags)
  {
      ...
      HandlePresent(self, syncInterval, flags);
      return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
  }
  ```
  Games and graphics engines frequently invoke `Present` with special flags such as `DXGI_PRESENT_TEST` (`0x00000001u`), `DXGI_PRESENT_DO_NOT_SEQUENCE` (`0x00000002u`), or `DXGI_PRESENT_USE_DURATION` (`0x00000100u`). When `DXGI_PRESENT_TEST` is specified, the application is performing an occlusion status check or querying whether the display is ready to receive a frame; the runtime does *not* flip backbuffers or present any rendered content to the screen.
  `HandlePresent` executes full ImGui rendering, updates ImGui internal frame clocks, and writes into the backbuffer even when `flags & DXGI_PRESENT_TEST != 0`.
- **Impact Assessment:**
  Unnecessary GPU overhead, double-stepping ImGui animations and input clocks, and potential backbuffer corruption during presentation state probes.
- **Recommended Solution:**
  Immediately bypass ImGui rendering when `DXGI_PRESENT_TEST` is detected:
  ```csharp
  const uint DXGI_PRESENT_TEST = 0x00000001;
  if ((flags & DXGI_PRESENT_TEST) != 0)
  {
      return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
  }
  ```

---

#### DXH-08 — Infinite retry loop on `CreateDeviceObjects` failure leaks resources and floods logs
- **File & Line Reference:** [`ImGuiDX11Impl.cs#L479-L570`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L479-L570), [`#L629-L635`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L629-L635), [`ImGuiDX12Impl.cs#L424-L579`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L424-L579), [`#L671-L675`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L671-L675)
- **Technical Description:**
  In `NewFrame()` of both backends:
  ```csharp
  // DX11:
  if (_fontSampler == null)
      CreateDeviceObjects();

  // DX12:
  if (pPipelineState == null)
      CreateDeviceObjects();
  ```
  If `CreateDeviceObjects()` fails (e.g. because shader bytecode files in `Assets/Shaders/` are missing, corrupted, or incompatible, or PSO creation fails), the method returns `false`, leaving `_fontSampler` or `pPipelineState` as `null`.
  Because `NewFrame()` is called every single frame inside `Present`, `CreateDeviceObjects()` is re-executed on every frame. In `ImGuiDX11Impl.CreateDeviceObjects`:
  - `_vertexShader = new VertexShader(...)`
  - `_inputLayout = new InputLayout(...)`
  - `_vertexConstantBuffer = new Buffer(...)`
  If an error occurs downstream (such as loading `imgui-frag.hlsl.bytes`), `CreateDeviceObjects` exits without cleaning up the earlier instantiated COM wrappers.
- **Impact Assessment:**
  Tens of thousands of native DirectX objects leaked per minute, severe stuttering, disk I/O thrashing from continuous `File.ReadAllBytes`, and log file explosion.
- **Recommended Solution:**
  Implement an initialization failure backoff / circuit-breaker flag (`_deviceObjectsCreationFailed`). If creation fails, log the error once, mark the state as failed, and do not attempt creation again until explicitly requested or until device objects are invalidated.

---

#### DXH-09 — Dynamic hook activation / deactivation concurrency safety
- **File & Line Reference:** [`DX11Renderer.cs#L186-L192`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L186-L192), [`DX12Renderer.cs#L105-L107`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L105-L107), [`DX11Renderer.cs#L346-L357`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L346-L357)
- **Technical Description:**
  `Reloaded.Hooks` performs inline function detouring by rewriting the first several bytes of target functions with jump instructions. In `DX11Renderer.HandlePresent`:
  ```csharp
  if (DX12Renderer.CommandQueueHook != null && DX12Renderer.CommandQueueHook.IsHooked)
  {
      DX12Renderer.CommandQueueHook.Disable();
  }
  ```
  Calling `Hook.Disable()` modifies process executable memory pages (`VirtualProtect` to `PAGE_EXECUTE_READWRITE`, overwriting prologue opcodes, and resetting protections) while the game engine's worker threads may concurrently be executing instructions within that exact prologue.
- **Impact Assessment:**
  If a thread executes across the modified prologue during byte modification, an invalid instruction exception (`STATUS_ILLEGAL_INSTRUCTION` / `0xC000001D`) or access violation occurs, crashing the game instantly.
- **Recommended Solution:**
  Never dynamically disable or re-enable hooks on hot graphics paths during active rendering. Instead of calling `Hook.Disable()` on `ExecuteCommandLists`, leave the hook activated and use a fast volatile pass-through boolean flag (`_captureQueue = false`) to bypass overhead without rewriting memory pages.

---

#### DXH-10 — Vulkan and OpenGL silently fall back to Direct3D 11
- **File & Line Reference:** [`DearImGuiInjectionPlusBaseUnityPlugin.cs#L39-L42`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L39-L42), [`DearImGuiInjectionPlus.cs#L167-L186`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/DearImGuiInjectionPlus.cs#L167-L186)
- **Technical Description:**
  In `DearImGuiInjectionPlusBaseUnityPlugin.Awake`:
  ```csharp
  if (deviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
      RendererFinder.RendererFinder.TargetRendererKind = RendererKind.D3D11;
  else if (deviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
      RendererFinder.RendererFinder.TargetRendererKind = RendererKind.D3D12;
  else
      Log.Error($"[DearImGuiInjectionPlus] Unsupported graphics backend: {deviceType}");
  ```
  However, in `DearImGuiInjectionPlus.Init`:
  ```csharp
  if (RendererFinder.RendererFinder.TargetRendererKind == RendererKind.None)
  {
      try {
          ...
      } catch {
          RendererFinder.RendererFinder.TargetRendererKind = RendererKind.D3D11;
      }
  }
  ```
  If `TargetRendererKind` remains `None` (because the device was Vulkan or OpenGL), `RendererFinder.Init()` proceeds to create a dummy DX11 device and hooks DXGI swapchain Present slots. On Windows, Vulkan and OpenGL runtimes sometimes create internal DXGI swapchains for window presentation interop. When triggered, the hook attempts to extract an `ID3D11Device` from a non-D3D11 swapchain.
- **Impact Assessment:**
  Fatal crash in `swapChain.GetDevice<Device>()` with `E_NOINTERFACE` or driver access violation.
- **Recommended Solution:**
  Explicitly abort bootstrap and skip `RendererFinder.Init()` if `SystemInfo.graphicsDeviceType` is neither Direct3D 11 nor Direct3D 12.

---

#### DXH-11 — Direct3D 12 static sampler mismatch
- **File & Line Reference:** [`ImGuiDX12Impl.cs#L452-L465`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L452-L465)
- **Technical Description:**
  In `ImGuiDX12Impl.CreateDeviceObjects`:
  ```csharp
  StaticSamplerDescription staticSampler = new();
  staticSampler.Filter = SharpDX.Direct3D12.Filter.ComparisonMinMagMipLinear;
  ...
  staticSampler.ComparisonFunc = SharpDX.Direct3D12.Comparison.Always;
  ```
  A comparison filter (`ComparisonMinMagMipLinear`) is specified in the root signature, but the ImGui pixel shader bytecode (`imgui-frag.hlsl.bytes`) uses regular non-comparison texture sampling (`texture.Sample(sampler, uv)` rather than `SampleCmp` / `SampleCmpLevelZero`).
- **Impact Assessment:**
  D3D12 debug layer generates warning: `D3D12_MESSAGE_ID_CORRUPTED_SAMPLER` or undefined sampling results on strict IHV hardware drivers (such as Intel Arc or AMD RDNA).
- **Recommended Solution:**
  Change filter to standard bilinear:
  ```csharp
  staticSampler.Filter = SharpDX.Direct3D12.Filter.MinMagMipLinear;
  staticSampler.ComparisonFunc = SharpDX.Direct3D12.Comparison.Never;
  ```

---

#### DXH-12 — Unprotected synchronous font upload in Present thread
- **File & Line Reference:** [`ImGuiDX12Impl.cs#L251-L422`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L251-L422)
- **Technical Description:**
  In `ImGuiDX12Impl.CreateFontsTexture`, font upload is executed synchronously within the `Present` hook:
  ```csharp
  var @event = Kernel32.CreateEvent(IntPtr.Zero, false, false, null);
  ...
  cmdQueue.ExecuteCommandList(cmdList);
  cmdQueue.Signal(fence, 1);
  fence.SetEventOnCompletion(1, @event);
  Kernel32.WaitForSingleObject(@event, Kernel32.INFINITE);
  ```
  If `Kernel32.CreateEvent` fails (e.g. out of handles), the code logs an error but proceeds anyway, passing `IntPtr.Zero` to `SetEventOnCompletion` and `WaitForSingleObject`. `WaitForSingleObject(IntPtr.Zero, INFINITE)` immediately fails or enters an indefinite hang.
- **Impact Assessment:**
  Game freezes completely on frame 1 during font texture compilation.
- **Recommended Solution:**
  Verify that `@event != IntPtr.Zero`. Use a managed `AutoResetEvent` or check fence completion via `fence.CompletedValue` polling with a reasonable timeout.

---

#### DXH-13 — Calling conventions, VTable indices, and 64-bit ABI review
- **File & Line Reference:** [`DX11Renderer.cs#L54-L66`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L54-L66), [`DX12Renderer.cs#L34-L36`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L34-L36)
- **Technical Description:**
  Verification of native function signatures against Microsoft x64 Calling Convention:
  - `IDXGISwapChain::Present` (VTable Index 8): `HRESULT Present(UINT SyncInterval, UINT Flags)`
  - `IDXGISwapChain1::Present1` (VTable Index 22): `HRESULT Present1(UINT SyncInterval, UINT PresentFlags, const DXGI_PRESENT_PARAMETERS *pPresentParameters)`
  - `ID3D12CommandQueue::ExecuteCommandLists` (VTable Index 10): `void ExecuteCommandLists(UINT NumCommandLists, ID3D12CommandList *const *ppCommandLists)`
- **Impact Assessment:**
  **Status: Compliant.** The delegate definitions declare `[Reloaded.Hooks.Definitions.X64.Function(CallingConventions.Microsoft)]` and `[Reloaded.Hooks.Definitions.X86.Function(CallingConventions.Stdcall)]`. Return types and parameter bitwidths correctly conform to the x64 Windows ABI. Note that `HRESULT` is returned as `IntPtr` (64-bit on x64), which safely passes RAX register contents back to the caller without stack truncation.

---

### 3.3 Thread Synchronization & Concurrency Findings

#### TH-03 — Concurrent D3D12 initialization from CommandQueue hook and Present hook
- **File & Line Reference:** [`ImGuiDX12.cs#L143-L238`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L143-L238), [`DearImGuiInjectionPlus.cs#L271-L291`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L271-L291)
- **Technical Description:**
  In `ImGuiDX12.cs`, `TryInitImGuiDX12()` is called from two distinct event handlers:
  1. `RetrieveCommandQueue` (triggered from `OnExecuteCommandList` hook on whichever thread Unity uses for command list execution).
  2. `OnPresent` (triggered from the Present hook on the render/present thread).
  ```csharp
  private static void TryInitImGuiDX12()
  {
      if (DearImGuiInjectionPlus.Initialized)
          return;

      if (_currentSwapChain == null || _commandQueue == null)
          return;
      ...
      InitImGuiDX12Internal(_currentSwapChain);
      DearImGuiInjectionPlus.Initialized = true;
  }
  ```
  There is no synchronization lock, `Interlocked` flag, or memory barrier protecting `TryInitImGuiDX12`. If worker thread A calls `RetrieveCommandQueue` at the exact same moment the render thread calls `OnPresent`, both threads can simultaneously evaluate `!DearImGuiInjectionPlus.Initialized`, proceed into `DearImGuiInjectionPlus.InitImGui()` and `InitImGuiDX12Internal()`, and allocate duplicate descriptor heaps, fences, and command allocators.
- **Impact Assessment:**
  Race condition resulting in leaked native heaps, duplicate initialization of the `ImGuiContext`, or unmanaged memory corruption during simultaneous texture uploads.
- **Recommended Solution:**
  Guard initialization using a thread-safe atomic lock or `CompareExchange`:
  ```csharp
  private static readonly object _initLock = new();
  private static void TryInitImGuiDX12()
  {
      if (DearImGuiInjectionPlus.Initialized)
          return;

      lock (_initLock)
      {
          if (DearImGuiInjectionPlus.Initialized || _currentSwapChain == null || _commandQueue == null)
              return;
          ...
          DearImGuiInjectionPlus.Initialized = true;
      }
  }
  ```

---

#### TH-04 — Non-atomic cross-thread shared state and racy delegate subscription
- **File & Line Reference:** [`DX11Renderer.cs#L68-L73`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L68-L73), [`DX12Renderer.cs#L16`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L16), [`#L27-L32`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L27-L32), [`RendererFinder.cs#L15-L23`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L15-L23)
- **Technical Description:**
  Multiple cross-thread state variables accessed concurrently by the Unity main thread, render thread, and Win32 message loop lack memory barriers, `volatile` keywords, or synchronization primitives:
  - `RendererFinder.IsShuttingDown`: read on the render thread during `Present`, written on the main thread during `OnDestroy`.
  - `DX12Renderer.CapturedCommandQueue`: assigned on the command queue hook thread, read on the present thread.
  - `RendererFinder.IsBackendInitialized`: assigned on frame 1 present, read concurrently.
  - `DearImGuiInjectionPlus.IsCursorVisible`: toggled from the Win32 message thread, read in `Present`, and read in Unity `Update`.
  - `DX11Renderer.OnPresent` and `DX12Renderer.OnPresent`:
    ```csharp
    public static event Action<SwapChain, uint, uint> OnPresent
    {
        add => _onPresentAction += value;
        remove => _onPresentAction -= value;
    }
    ```
    The delegate combination `+=` and `-=` is NOT atomic. If a mod adds or removes a listener while `Present` is invoking `_onPresentAction.Invoke()`, an inconsistent delegate invocation list can be created or an update lost.
- **Impact Assessment:**
  Weak memory architectures (or aggressive compiler register optimizations) can cache stale boolean values across threads, causing shutdown flags to be missed or concurrent delegate mutation to throw `NullReferenceException` during presentation.
- **Recommended Solution:**
  1. Declare all cross-thread status flags as `volatile` or read/write them using `Volatile.Read` / `Volatile.Write`.
  2. Protect event subscription accessors with thread synchronization locks:
     ```csharp
     private static readonly object _presentLock = new();
     public static event Action<SwapChain, uint, uint> OnPresent
     {
         add { lock (_presentLock) _onPresentAction += value; }
         remove { lock (_presentLock) _onPresentAction -= value; }
     }
     ```

---

#### TH-05 — `UnityMainThreadDispatcher` deadlock vulnerability and post-timeout object corruption
- **File & Line Reference:** [`UnityMainThreadDispatcher.cs#L80-L165`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/UnityMainThreadDispatcher.cs#L80-L165)
- **Technical Description:**
  `UnityMainThreadDispatcher.InvokeOnMainThread<T>` provides synchronous dispatch from background/render threads to the Unity scripting thread:
  ```csharp
  using var completionEvent = new ManualResetEventSlim(false);
  ...
  Enqueue(() =>
  {
      try { result = func(); }
      catch (Exception ex) { caughtException = ex; }
      finally { completionEvent.Set(); }
  });

  if (!completionEvent.Wait(timeoutMs))
  {
      throw new TimeoutException(...);
  }
  ```
  Two significant concurrency failure modes exist:
  1. **Engine Pipeline Deadlock:** In Unity's multithreaded rendering engine, the main scripting thread frequently blocks at the end of `Update` waiting for the graphics render thread to finish presenting the current frame before starting the next frame. If consumer ImGui code calls `InvokeOnMainThread` from within an ImGui window callback (which executes inside `Present` on the render thread), the render thread blocks waiting for the main thread, while the main thread is blocked waiting for the render thread. The 2-second timeout triggers, producing severe 2000ms frame hitching and stutter.
  2. **Disposed Event Object Access:** If the timeout fires and the calling thread exits `InvokeOnMainThread`, `completionEvent` is disposed by the `using` block. When the Unity main thread eventually dequeues and executes the action during a later frame, the `finally` block executes `completionEvent.Set()` on a disposed `ManualResetEventSlim`, throwing an `ObjectDisposedException` that aborts the dispatcher queue loop.
- **Impact Assessment:**
  2000ms frame freezes when modders call `InvokeOnMainThread` inside `IImGuiWindow.OnRender()`, followed by `ObjectDisposedException` crashing the dispatcher queue on the main thread.
- **Recommended Solution:**
  1. Do NOT dispose `ManualResetEventSlim` in the caller if a timeout occurs; manage its lifetime using a reference-counted handle or allocate a persistent pool.
  2. Document clearly that `InvokeOnMainThread` (blocking) must NEVER be called from within the ImGui render loop. Enforce asynchronous non-blocking dispatch (`Enqueue(Action)`) or snapshot pipelines (`StateSnapshotPipeline<T>`) for all render-thread to main-thread communication.

---

#### TH-06 — `StructSnapshotPipeline<T>` torn reads across multiple publishes
- **File & Line Reference:** [`StateSnapshotPipeline.cs#L43-L81`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/StateSnapshotPipeline.cs#L43-L81)
- **Technical Description:**
  `StructSnapshotPipeline<T>` is designed to provide lock-free double-buffered snapshot exchange for value types:
  ```csharp
  public void Publish(in T snapshot)
  {
      lock (_writeLock)
      {
          int writeIndex = 1 - _activeReadIndex;
          if (writeIndex == 0)
              _slot0 = snapshot;
          else
              _slot1 = snapshot;

          _activeReadIndex = writeIndex;
      }
  }

  public T GetLatest()
  {
      return _activeReadIndex == 0 ? _slot0 : _slot1;
  }
  ```
  `GetLatest()` reads `_activeReadIndex`, selects `_slot0` or `_slot1`, and copies the struct to the caller without holding a lock. If the struct `T` exceeds 64 bits (such as a 32-byte game telemetry struct containing position, velocity, and health), copying the struct is not an atomic CPU operation.
  If the Unity main thread publishes *twice* in rapid succession while the render thread is part-way through copying `_slot0`:
  - Main thread publish #1 writes `_slot1`, sets `_activeReadIndex = 1`.
  - Main thread publish #2 writes `_slot0` (overwriting the slot the reader is currently copying!), sets `_activeReadIndex = 0`.
- **Impact Assessment:**
  Torn struct memory reads resulting in corrupted coordinates, invalid enum values, or impossible floating-point numbers displayed in the UI.
- **Recommended Solution:**
  Use a triple-buffering pattern (3 pre-allocated slots with atomic integer index exchange) or standard sequence-locking (seqlock pattern) with memory barriers to guarantee read consistency without locks.

---

#### TH-07 — Unbounded fence wait in Present loop
- **File & Line Reference:** [`ImGuiDX12.cs#L325-L332`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L325-L332), [`ImGuiDX12Impl.cs#L384`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L384)
- **Technical Description:**
  In `ImGuiDX12.RenderImGui`:
  ```csharp
  if (currentFrameContext.FenceValue != 0 && _fence != null && _fenceEvent != null)
  {
      if (_fence.CompletedValue < currentFrameContext.FenceValue)
      {
          _fence.SetEventOnCompletion(currentFrameContext.FenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
          _fenceEvent.WaitOne(); // Infinite wait!
      }
  }
  ```
  `_fenceEvent.WaitOne()` specifies no timeout. If the GPU hangs, if a driver TDR occurs, or if the captured command queue was a secondary queue that stalled or ceased execution, the rendering thread blocks forever in `WaitOne()`.
- **Impact Assessment:**
  The game process becomes completely unresponsive (deadlock / hard hang) without generating any diagnostic log output or crash dump.
- **Recommended Solution:**
  Specify a reasonable watchdog timeout (e.g. `_fenceEvent.WaitOne(2000)`). If the timeout expires, log a critical error indicating GPU device timeout/hang and gracefully skip rendering for the affected frame.

---

### 3.4 Memory Safety & Resource Lifetime Findings

#### MEM-03 — Inconsistent CommandQueue ownership & refcount leakage in `QueryCommandQueueFromSwapChain`
- **File & Line Reference:** [`RendererFinder.cs#L97-L153`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L97-L153), [`DX12Renderer.cs#L182-L216`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L182-L216), [`ImGuiDX12.cs#L68`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L68), [`#L186`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L186)
- **Technical Description:**
  In `RendererFinder.QueryCommandQueueFromSwapChain`:
  ```csharp
  int hr = getDevice(swapChainPtr, ref d3d12QueueGuid, out IntPtr commandQueuePtr);
  if (hr == 0 && commandQueuePtr != IntPtr.Zero)
  {
      Marshal.AddRef(commandQueuePtr); // AddRef #1
      using var q = new SharpDX.Direct3D12.CommandQueue(commandQueuePtr);
      if (q.Description.Type == SharpDX.Direct3D12.CommandListType.Direct)
      {
          return commandQueuePtr; // LEAKED REFERENCE!
      }
  }
  ```
  COM rules dictate that `IDXGISwapChain::GetDevice` returns an interface with an **already incremented reference count (+1)**. The code then calls `Marshal.AddRef(commandQueuePtr)` (+2). `using var q` calls `Release` on disposal (-1), leaving the queue with a net +1 leaked reference count every time `QueryCommandQueueFromSwapChain` is called. If this method is called repeatedly on frames where `CapturedCommandQueue` is not yet set, references accumulate.
  Conversely, in `DX12Renderer.CommandQueueExecuteCommandListHook`:
  ```csharp
  var commandQueue = new CommandQueue(self);
  try
  {
      if (!action.Invoke(commandQueue, (uint)numCommandLists, ppCommandLists))
      {
          commandQueue.Dispose(); // OVER-RELEASE!
      }
  }
  ```
  Here, `self` is a borrowed pointer from the hook argument. Instantiating `new CommandQueue(self)` does NOT call `AddRef`. Calling `commandQueue.Dispose()` executes `IUnknown::Release`, deducting a reference count from Unity's command queue and precipitating an premature destruction crash.
- **Impact Assessment:**
  Reference count imbalance causing memory leaks on the query path and severe heap corruption / use-after-free crashes on the hook disposal path.
- **Recommended Solution:**
  Strictly balance COM lifetimes:
  - Do NOT call `Marshal.AddRef` if `GetDevice` already returned an AddRef'd pointer.
  - Store the acquired pointer in a managed wrapper that owns that reference, and call `Dispose()` exactly once during shutdown.
  - On borrowed pointers in hook callbacks, NEVER call `.Dispose()`.

---

#### MEM-04 — SharpDX wrapper instantiation without `AddRef` and finalizer thread hazards
- **File & Line Reference:** [`ImGuiDX11Impl.cs#L53`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L53), [`#L230`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L230), [`#L644-L645`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L644-L645), [`ImGuiDX12Impl.cs#L627`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L627), [`#L633`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L633), [`DX11Renderer.cs#L375-L377`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L375-L377), [`DX12Renderer.cs#L148-L150`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L148-L150)
- **Technical Description:**
  Throughout the codebase, existing native COM pointers are wrapped in SharpDX managed classes:
  - `_device = new Device((IntPtr)device);`
  - `_deviceContext = new DeviceContext((IntPtr)deviceContext);`
  - `new ShaderResourceView(new IntPtr(pcmd.TextureId));`
  - `new SwapChain(self);`
  `SharpDX.ComObject` constructors taking raw `IntPtr` assume that the caller is passing an already `AddRef`'d pointer that the managed wrapper will adopt. By default in SharpDX, finalizers do not release unmanaged pointers unless `SharpDX.Configuration.EnableReleaseOnFinalizer = true`.
  However, in modded Unity environments where multiple plugins share the AppDomain, if ANY plugin sets `SharpDX.Configuration.EnableReleaseOnFinalizer = true`, the CLR garbage collector's finalizer thread will begin invoking `Release()` on all garbage-collected SharpDX objects (such as the transient `new ShaderResourceView` allocated in `RenderDrawData` for user textures). Because no `AddRef` occurred upon creation, the finalizer over-releases the game's internal textures and devices out-of-order on a background thread.
- **Impact Assessment:**
  Intermittent, non-deterministic access violations occurring during garbage collection sweeps across unrelated scenes or loading screens.
- **Recommended Solution:**
  1. Explicitly suppress finalization on non-owned wrapper instances: `GC.SuppressFinalize(wrapper)`.
  2. For per-command user texture binding in `RenderDrawData`, do not allocate `new ShaderResourceView(new IntPtr(pcmd.TextureId))` every draw call. Instead, call native `ID3D11DeviceContext::PSSetShaderResources` directly via raw vtable or hold a cached wrapper.

---

#### MEM-05 — Missing `Dispose` on Device/Context and skipped shutdown in D3D11
- **File & Line Reference:** [`ImGuiDX11.cs#L22-L42`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L22-L42), [`ImGuiDX12.cs#L242`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L242), [`#L114`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L114)
- **Technical Description:**
  In `ImGuiDX11.Dispose()`:
  ```csharp
  if (DearImGuiInjectionPlus.Initialized && !RendererFinder.RendererFinder.IsShuttingDown)
  {
      ...
      ImGuiDX11Impl.Shutdown();
      _deviceContext?.Dispose();
      _device?.Dispose();
  }
  ```
  When `DearImGuiInjectionPlusBaseUnityPlugin.OnDestroy()` executes, it sets `RendererFinder.IsShuttingDown = true` before calling `DearImGuiInjectionPlus.Dispose()`. Because `!RendererFinder.IsShuttingDown` evaluates to `false`, **the entire cleanup block in `ImGuiDX11.Dispose()` is skipped.** `ImGuiDX11Impl.Shutdown()` is never called, and vertex/index buffers, constant buffers, and font resources are never disposed.
- **Impact Assessment:**
  Complete failure to release D3D11 GPU resources upon plugin unloading or teardown.
- **Recommended Solution:**
  Remove the `!RendererFinder.IsShuttingDown` condition from `ImGuiDX11.Dispose()`. Shutdown MUST execute cleanup precisely when the plugin is shutting down.

---

#### MEM-06 — Exception unsafety in D3D12 font upload resource allocation
- **File & Line Reference:** [`ImGuiDX12Impl.cs#L278-L408`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L278-L408)
- **Technical Description:**
  In `ImGuiDX12Impl.CreateFontsTexture`:
  Seven distinct unmanaged and GPU resources are created in sequence:
  `pTexture`, `uploadBuffer`, `fence`, `@event` (Win32 handle), `cmdQueue`, `cmdAlloc`, and `cmdList`.
  None of these allocations are protected by `try/finally` blocks. If an exception occurs during `uploadBuffer.Map`, `System.Buffer.MemoryCopy`, or `cmdList.CopyTextureRegion`, all previously allocated GPU resources and the Win32 event handle leak permanently.
- **Impact Assessment:**
  Substantial VRAM and GDI handle leakage if font creation fails.
- **Recommended Solution:**
  Wrap the upload routine in a deterministic cleanup block or wrap intermediate resources in `using` statements so that failure guarantees deallocation.

---

#### MEM-07 — Excessive managed GC allocations in high-frequency render loops
- **File & Line Reference:** [`ImGuiDX11Impl.cs#L245-L305`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L245-L305), [`DearImGuiInjectionPlus.cs#L365-L375`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L365-L375), [`ImGuiDX11Impl.cs#L209`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L209)
- **Technical Description:**
  `PROJECT-OVERVIEW.md` claims: *"Executes a single Dear ImGui frame with zero allocations"*.
  In reality, the render path allocates significant managed heap memory every frame:
  1. `BackupRenderState`: allocates 6 new arrays (`Rectangle[16]`, `RawViewportF[16]`, `Buffer[16]`, `int[16]`, `int[16]`, `RenderTargetView[8]`) plus the `StateBackup` class object on every frame.
  2. `DearImGuiInjectionPlus.NewFrame`: allocates `new Dictionary<string, List<RegisteredTab>>()` and multiple `new List<RegisteredTab>()` on every frame when the menu bar is rendered.
  3. `Marshal.GetDelegateForFunctionPointer<ImDrawUserCallBack>`: dynamically creates delegate instances inside the draw command loop.
- **Impact Assessment:**
  Generates megabytes of short-lived managed objects per second on the rendering thread, forcing frequent garbage collection sweeps, micro-stutters, and frame drops.
- **Recommended Solution:**
  1. Pre-allocate and reuse a single `StateBackup` instance with fixed-size arrays.
  2. Rebuild the menu bar category hierarchy only when `RegisterTab` is called, rather than reconstructing a dictionary every frame.
  3. Cache delegate thunks for draw callbacks.

---

#### MEM-08 — Unmanaged string pointer lifecycle & memory leaks
- **File & Line Reference:** [`ImGuiWin32Impl.cs#L45`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L45), [`#L109`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L109), [`ImGuiDX11Impl.cs#L622-L626`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11Impl.cs#L622-L626), [`ImGuiDX12Impl.cs#L662-L666`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12Impl.cs#L662-L666), [`DearImGuiInjectionPlus.cs#L218-L221`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L218-L221)
- **Technical Description:**
  Unmanaged strings allocated via `Marshal.StringToHGlobalAnsi` have mismatched lifecycles:
  1. In `ImGuiWin32Impl.ImGui_ImplWin32_InitEx`:
     `io.NativePtr->BackendPlatformName = (byte*)Marshal.StringToHGlobalAnsi("imgui_impl_win32");`
     In `ImGuiWin32Impl.Shutdown()`:
     `io.NativePtr->BackendPlatformName = null;`
     The allocated HGlobal pointer is **never freed** via `Marshal.FreeHGlobal`.
  2. In `ImGuiDX11Impl.Shutdown()` / `ImGuiDX12Impl.Shutdown()`:
     `_renderNamePtr` is freed, but `io.NativePtr->BackendRendererName` is NOT set to null. If ImGui subsequently renders or queries backend capabilities, it dereferences a dangling pointer.
- **Impact Assessment:**
  Native memory leaks on platform backend strings; potential use-after-free read during shutdown diagnostic logging.
- **Recommended Solution:**
  Store string pointers in static fields and ensure that every `Marshal.StringToHGlobalAnsi` is paired with setting the native ImGui pointer to null followed by `Marshal.FreeHGlobal`.

---

#### SEC-01 — Heap buffer overflow in ANSI `WM_CHAR` handler
- **File & Line Reference:** [`ImGuiWin32Impl.cs#L611-L622`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L611-L622)
- **Technical Description:**
  In `ImGuiWin32Impl.WndProcHandler`:
  ```csharp
  case WindowMessage.WM_CHAR:
      if (User32.IsWindowUnicode(hwnd))
      {
          if ((int)wParam > 0 && (int)wParam < 0x10000)
              io.AddInputCharacterUTF16((ushort)wParam);
      }
      else
      {
          byte[] lolxd = new byte[1] { 0 };
          lolxd[0] = *(byte*)&wParam;

          const int wideBufferSize = 1;
          var wideBuffer = Marshal.AllocHGlobal(wideBufferSize); // ALLOCATES 1 BYTE!
          Kernel32.MultiByteToWideChar(Kernel32.CP_ACP, Kernel32.MB_PRECOMPOSED, lolxd, 1, wideBuffer, wideBufferSize);
          var wideBufferChar = *(char*)wideBuffer; // READS 2 BYTES (sizeof(char))!
          io.AddInputCharacter(wideBufferChar);
          Marshal.FreeHGlobal(wideBuffer);
      }
      return IntPtr.Zero;
  ```
  1. `wideBufferSize` is defined as `1`. `Marshal.AllocHGlobal(1)` allocates a buffer of exactly **1 byte** from the native process heap.
  2. In Windows API, `MultiByteToWideChar` expects the output buffer capacity in **characters** (`cchWideChar`), but `wideBuffer` was only allocated as 1 byte. A UTF-16 wide character (`WCHAR` / `char`) requires **2 bytes**. Writing a 2-byte wide character overflows the 1-byte heap chunk by 1 byte into heap chunk headers.
  3. `*(char*)wideBuffer` dereferences 2 bytes from a 1-byte allocation.
  Because issue **INP-01** (below) mistakenly subclasses the window using the ANSI `SetWindowLong` export, Windows converts the window procedure to ANSI, forcing execution down this exact vulnerable branch whenever characters are typed into ImGui controls.
- **Impact Assessment:**
  Heap corruption of native process heap metadata (`STATUS_HEAP_CORRUPTION` / `0xC0000374`) leading to random crashes during subsequent unmanaged memory allocations.
- **Recommended Solution:**
  Allocate sufficient memory: `Marshal.AllocHGlobal(sizeof(char))` (2 bytes) or eliminate the unmanaged allocation completely by using managed `Encoding.Default.GetString(lolxd)[0]`.

---

#### SEC-02 — Use-after-free of `IniFilename` on shutdown
- **File & Line Reference:** [`DearImGuiInjectionPlus.cs#L255-L265`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L255-L265)
- **Technical Description:**
  In `DearImGuiInjectionPlus.Dispose()`:
  ```csharp
  if (ImGui.GetIO().NativePtr != null && ImGui.GetIO().NativePtr->IniFilename != null)
  {
      Marshal.FreeHGlobal((IntPtr)ImGui.GetIO().NativePtr->IniFilename);
  }
  IO = null;

  if (Context != IntPtr.Zero)
  {
      ImGui.DestroyContext(Context);
      Context = IntPtr.Zero;
  }
  ```
  `IniFilename` pointer is freed in unmanaged memory **before calling `ImGui.DestroyContext(Context)`**.
  Internally, when `ImGui::DestroyContext()` executes, Dear ImGui checks if settings are dirty and invokes `ImGui::SaveIniSettingsToDisk(ctx->IO.IniFilename)` to serialize window positions to disk. Because `IniFilename` was already freed, `SaveIniSettingsToDisk` passes a pointer to deallocated memory to the C runtime file I/O function (`fopen`).
- **Impact Assessment:**
  Use-after-free read during process teardown, resulting in corrupted configuration files, file creation errors, or memory protection access violations.
- **Recommended Solution:**
  Explicitly invoke `ImGui.SaveIniSettingsToDisk(ImGuiIniConfigPath)` first, then null out `IniFilename` on the native IO struct, destroy the context, and finally free the unmanaged string:
  ```csharp
  if (Context != IntPtr.Zero)
  {
      ImGui.SaveIniSettingsToDisk(ImGuiIniConfigPath);
      var nativeIo = ImGui.GetIO().NativePtr;
      var iniPtr = (IntPtr)nativeIo->IniFilename;
      nativeIo->IniFilename = null;
      ImGui.DestroyContext(Context);
      Context = IntPtr.Zero;
      if (iniPtr != IntPtr.Zero)
          Marshal.FreeHGlobal(iniPtr);
  }
  ```

---

### 3.5 Input Pipeline & Window Procedure Hooking Findings

#### INP-01 — User32 P/Invoke ANSI bindings force Unicode window into ANSI mode
- **File & Line Reference:** [`User32.cs#L800-L821`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Windows/User32.cs#L800-L821), [`InputManager.cs#L44`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L44)
- **Technical Description:**
  In `User32.cs`:
  ```csharp
  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
  public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

  [DllImport("user32.dll")]
  public static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr windowHandle, WindowMessage message, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern IntPtr DefWindowProc(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam);
  ```
  In Win32 API, `user32.dll` exports two distinct variants of these APIs:
  - ANSI: `SetWindowLongA` / `SetWindowLongPtrA`, `CallWindowProcA`, `DefWindowProcA`
  - Unicode: `SetWindowLongW` / `SetWindowLongPtrW`, `CallWindowProcW`, `DefWindowProcW`
  
  In C# `.NET Standard`, `[DllImport("user32.dll")]` defaults to `CharSet.Ansi`. Windows resolves `SetWindowLongPtr` to `SetWindowLongPtrA`.
  When a window is subclassed via `SetWindowLongPtrA`:
  1. The Win32 subsystem marks the window procedure as **ANSI**.
  2. Windows begins converting all window messages (`WM_CHAR`, `WM_IME_*`, text events) from native UTF-16 to the system ANSI code page before delivering them to `WndProcHandler`.
  3. `IsWindowUnicode(hWnd)` begins returning `false`, causing `ImGuiWin32Impl` to route all keyboard input through the vulnerable ANSI path (**SEC-01**).
  4. Forwarding messages via `CallWindowProcA` converts messages a second time when forwarding to Unity's native window procedure.
- **Impact Assessment:**
  Non-ASCII text entry (Cyrillic, CJK, accented European characters, emoji) typed into the game or ImGui widgets is permanently corrupted or replaced with question marks (`?`). Global window character conversion introduces unnecessary marshal translation overhead.
- **Recommended Solution:**
  Explicitly specify `CharSet = CharSet.Unicode` and bind to the wide-character API entry points:
  ```csharp
  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
  public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

  [DllImport("user32.dll", EntryPoint = "CallWindowProcW", CharSet = CharSet.Unicode)]
  public static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr windowHandle, WindowMessage message, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
  public static extern IntPtr DefWindowProc(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam);
  ```

---

#### INP-02 — Swallowing key/button UP events creates stuck inputs in game
- **File & Line Reference:** [`InputManager.cs#L120-L130`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L120-L130)
- **Technical Description:**
  In `InputManager.WndProcHandler`:
  ```csharp
  // When menu is OPEN:
  ...
  // Swallow all mouse messages (clicks, moves, wheels)
  if (IsMouseMessage(msg))
      return IntPtr.Zero;

  // Swallow all keyboard messages (letters, numbers, modifiers)
  if (IsKeyboardMessage(msg))
      return IntPtr.Zero;
  ```
  When the user opens the Dear ImGui menu (e.g. by pressing `Insert`), they may already be holding down gameplay keys or mouse buttons (e.g., `W` to walk, `Shift` to sprint, or Left Mouse Button to fire).
  While the menu is open, the user releases the key or mouse button. `InputManager` receives `WM_KEYUP` or `WM_LBUTTONUP` and **swallows it** (`return IntPtr.Zero`). The game engine never receives the release event.
  When the overlay is subsequently closed, the game engine believes the keys or mouse buttons are still held down. The character continues walking or firing continuously until the user presses and releases each key a second time.
- **Impact Assessment:**
  Extremely disruptive "stuck key" / "auto-walking" bug occurring whenever the menu is toggled during gameplay motion.
- **Recommended Solution:**
  Track keys and mouse buttons that were depressed when the overlay became visible. When closing the menu, synthesize or permit through `WM_KEYUP` / `WM_LBUTTONUP` events, or broadcast explicit key releases to the game.

---

#### INP-03 — Blind `WndProc` restoration unlinks subsequent subclassers & delegate collection risk
- **File & Line Reference:** [`InputManager.cs#L37-L65`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L37-L65)
- **Technical Description:**
  In `InputManager.Subclass` and `Unsubclass`:
  ```csharp
  _wndProcDelegate = WndProcHandler;
  _originalWndProc = User32.SetWindowLong(hWnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
  ...
  internal static void Unsubclass()
  {
      if (_windowHandle != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
      {
          User32.SetWindowLong(_windowHandle, GWL_WNDPROC, _originalWndProc);
          _originalWndProc = IntPtr.Zero;
          _windowHandle = IntPtr.Zero;
          _wndProcDelegate = null;
      }
  }
  ```
  1. **Unlinking Chained Subclassers:** If another mod, overlay tool (such as Steam Overlay, Discord Overlay, RTSS, or another BepInEx mod), or Unity itself subclassed the window *after* `DearImGuiInjectionPlus`, calling `SetWindowLong(..., _originalWndProc)` indiscriminately overwrites the active window procedure pointer with the old pointer, unlinking and bypassing the other tools.
  2. **Delegate Collection:** Calling `_wndProcDelegate = null` immediately allows the CLR garbage collector to collect the native delegate thunk. If an unmanaged hook or chained window procedure still holds the function pointer, the next window message delivered jumps to unmapped or reclaimed memory (`0xC0000005` Access Violation).
- **Impact Assessment:**
  Overwriting other mods' window hooks or instant hard crashes when unhooking on shutdown while window messages are being pumped.
- **Recommended Solution:**
  Use the modern Windows Subclassing API (`comctl32.dll`'s `SetWindowSubclass`, `RemoveWindowSubclass`, and `DefSubclassProc`). This API manages arbitrary subclassing hierarchies automatically without pointer collision or chain corruption.

---

#### INP-04 — Thread-affine Win32 APIs called from graphics rendering thread
- **File & Line Reference:** [`ImGuiWin32Impl.cs#L157-L191`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L157-L191), [`#L114-L155`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L114-L155), [`#L270-L299`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L270-L299)
- **Technical Description:**
  In `ImGuiWin32Impl.NewFrame()`, which executes inside `Present` on the render thread:
  ```csharp
  // Updates key modifiers:
  ImGui_ImplWin32_ProcessKeyEventsWorkarounds();
  ...
  ImGui_ImplWin32_UpdateMouseCursor();
  ```
  `IsVkDown` calls `User32.GetKeyState(vk)`. In the Win32 API, `GetKeyState()` accesses the **calling thread's message queue keyboard state table**, NOT the global hardware keyboard state (which is queried via `GetAsyncKeyState`).
  Because the rendering thread does not process window messages, its message queue has no attached input state. `GetKeyState()` called on the render thread frequently returns stale or zero values.
  Furthermore, `User32.SetCursor(...)` called on a background thread that does not own the window's message queue has no effect; the OS resets the cursor to the window class cursor upon the next message on the main thread.
- **Impact Assessment:**
  `Shift` and `Win` key workarounds incorrectly assume keys are released, clearing modifier flags prematurely. Mouse cursor shape updates (resizing arrows, hand, I-beam) fail to update reliably.
- **Recommended Solution:**
  Handle `WM_SETCURSOR` strictly inside the window procedure on the main UI thread. Use `GetAsyncKeyState()` or query keyboard modifiers from the tracked state within `WndProcHandler`.

---

#### INP-05 — Input blocking bypasses and gaps
- **File & Line Reference:** [`UnityInputBlocker.cs#L6-L109`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Patches/UnityInputBlocker.cs#L6-L109), [`NewInputSystemBlocker.cs#L16-L87`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Patches/NewInputSystemBlocker.cs#L16-L87), [`InputManager.cs#L112-L118`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L112-L118)
- **Technical Description:**
  The input blocking architecture has several bypass gaps:
  1. **Legacy Input:** `UnityEngine.Input.inputString` and `UnityEngine.Input.mousePosition` are NOT patched. Games that read text input or check mouse position directly continue to receive mouse coordinates and text characters.
  2. **Raw Input (`GetRawInputBuffer`):** `InputManager` redirects `WM_INPUT` to `DefWindowProc`, but games using raw input devices that query `GetRawInputBuffer` or register for raw input sink notifications still receive hardware motion packets.
  3. **New Input System:** `NewInputSystemBlocker` disables only `Mouse.current` and `Keyboard.current`. Games that cache device references (`InputDevice device = InputSystem.devices[...]`), use action maps (`PlayerInput` / `InputAction`), or poll gamepads are unaffected by disabling `current`. Additionally, if the game's code calls `InputSystem.EnableDevice(mouse)` during its `Update`, the blocker is bypassed until the menu is toggled again.
  4. **Third-Party Input Plugins:** Third-party input managers such as Rewired, InControl, or Steam Input bypass `UnityEngine.Input` entirely via native DLL calls (e.g. direct XInput or SDL2).
- **Impact Assessment:**
  Player camera drift, weapon firing, or character movement occurring in specific Unity titles while interacting with ImGui controls.
- **Recommended Solution:**
  1. Patch `UnityEngine.Input.mousePosition` and `UnityEngine.Input.inputString`.
  2. Implement a per-frame enforcement pass on `NewInputSystemBlocker` to ensure devices remain disabled while the menu is open, or patch `InputAction.ReadValue` / `InputDevice.ExecuteCommand`.

---

#### INP-06 — `BlockAllInputsWhenOpen` configuration flag ignored by Win32 and Harmony blockers
- **File & Line Reference:** [`DearImGuiInjectionPlus.cs#L43`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L43), [`DearImGuiInjectionPlusBaseUnityPlugin.cs#L161`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L161)
- **Technical Description:**
  In `DearImGuiInjectionPlusBaseUnityPlugin.Awake`:
  A configuration option `BlockAllInputsWhenOpen` is exposed to users in `DearImGuiInjectionPlus.cfg`.
  However, this flag is only checked in `DearImGuiInjectionPlusBaseUnityPlugin.IgnoreUIObjectsWhenImGuiCursorIsVisible` (the uGUI event blocker).
  Neither `InputManager.WndProcHandler` nor `UnityInputBlocker` check `DearImGuiInjectionPlus.BlockAllInputsWhenOpen`. Both blockers unconditionally swallow inputs whenever `DearImGuiInjectionPlus.IsCursorVisible` is true.
- **Impact Assessment:**
  Setting `BlockAllInputsWhenOpen = false` in the configuration file has no effect; game inputs are still blocked.
- **Recommended Solution:**
  Add `if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen) return ...` checks to `InputManager.WndProcHandler` and `UnityInputBlocker`.

---

#### INP-07 — Menu toggle auto-repeat, mouse capture leak, and `WM_SETCURSOR` non-client suppression
- **File & Line Reference:** [`InputManager.cs#L86-L95`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L86-L95), [`ImGuiWin32Impl.cs#L507-L546`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L507-L546), [`InputManager.cs#L107-L110`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L107-L110)
- **Technical Description:**
  1. **Toggle Key Auto-Repeat:** In `WndProcHandler`, `msg == WindowMessage.WM_KEYDOWN && (int)wParam == (int)ToggleKey` toggles visibility on every keystroke. When a key is held down, Windows sends repeated `WM_KEYDOWN` messages at the keyboard repeat rate (30 Hz). Holding the `Insert` key causes the menu to rapidly flicker on and off.
  2. **Mouse Capture Leak:** If the user drags an ImGui window and presses the toggle key to close the overlay, `User32.ReleaseCapture()` is never called. The window retains mouse capture, routing all OS mouse clicks to the window even when clicking on other applications.
  3. **Non-Client Cursor Suppression:** In `WndProcHandler`:
     ```csharp
     if (msg == WindowMessage.WM_SETCURSOR)
         return (IntPtr)1;
     ```
     Returning `1` unconditionally swallows `WM_SETCURSOR` for all areas of the window, including the title bar, sizing borders (`HTLEFT`, `HTRIGHT`, `HTTOP`, `HTBOTTOM`), and system menu. As a result, the OS resize cursor never appears when hovering over window edges in windowed mode.
- **Impact Assessment:**
  Flickering UI when pressing toggle keys; lost mouse capture; inability to resize windowed games.
- **Recommended Solution:**
  1. Check bit 30 of `lParam` in `WM_KEYDOWN` (previous key state: 0 = first press, 1 = repeat) to ignore auto-repeat.
  2. Ensure `User32.ReleaseCapture()` is called whenever visibility is toggled off.
  3. In `WM_SETCURSOR`, only swallow if `LOWORD(lParam) == HTCLIENT`. For non-client hit-test values, pass through to `DefWindowProc`.

---

#### INP-08 — Missing IME and `WM_UNICHAR` handling
- **File & Line Reference:** [`InputManager.cs#L147-L154`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L147-L154)
- **Technical Description:**
  In `InputManager.IsKeyboardMessage`, only standard `WM_KEYDOWN`, `WM_KEYUP`, `WM_CHAR`, `WM_DEADCHAR`, and `WM_SYS*` messages are recognized.
  `WM_IME_SETCONTEXT`, `WM_IME_NOTIFY`, `WM_IME_CONTROL`, `WM_IME_COMPOSITION`, `WM_IME_CHAR`, and `WM_UNICHAR` are completely absent.
- **Impact Assessment:**
  IME composition windows cannot be used within ImGui text input fields, and Asian language input leaks through to the game engine.
- **Recommended Solution:**
  Include `WM_IME_*` and `WM_UNICHAR` in message filtering and forward character events to `ImGuiIO.AddInputCharacterUTF16`.

---

#### INP-09 — Cursor lock state synchronization edge cases
- **File & Line Reference:** [`CursorManager.cs#L6-L44`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/CursorManager.cs#L6-L44), [`InputManager.cs#L156-L250`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L156-L250)
- **Technical Description:**
  1. In `InputManager.OnCursorVisibilityToggled`, Unity cursor APIs (`Cursor.lockState = ...`, `Cursor.visible = ...`) are invoked directly. Because `OnCursorVisibilityToggled` is called from `ToggleCursor()` inside `WndProcHandler`, these Unity API calls run on the Win32 message loop thread. In Unity versions with strict thread checking, mutating `Cursor.lockState` from off the main scripting thread throws `UnityException: get_lockState can only be called from the main thread`.
  2. `InputManager.EnforceCursorStateOnMainThread()` was written to combat games that reset cursor state in `LateUpdate`, but it is **never called anywhere in the project**.
  3. Games that enforce cursor confinement via native Win32 `ClipCursor(rect)` directly inside their game loop override `User32.ClipCursor(IntPtr.Zero)`, locking the mouse cursor to a 1x1 pixel box in the screen center while the ImGui menu is open.
- **Impact Assessment:**
  Occasional main-thread assertion exceptions when toggling the menu; mouse pointer locked to center screen in games using native Win32 clipping.
- **Recommended Solution:**
  Dispatch cursor state changes to the main thread via `UnityMainThreadDispatcher.Enqueue()`. Call `ClipCursor(IntPtr.Zero)` continuously while `IsCursorVisible` is true.

---

### 3.6 Process Teardown & Fault Recovery Findings

#### LC-02 — Inverted teardown order and asymmetric backend disposal
- **File & Line Reference:** [`DearImGuiInjectionPlusBaseUnityPlugin.cs#L182-L211`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L182-L211), [`ImGuiDX11.cs#L29-L39`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX11.cs#L29-L39), [`DX11Renderer.cs#L223-L254`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L223-L254)
- **Technical Description:**
  In `OnDestroy()`:
  ```csharp
  // 1. Unsubclass WndProc
  InputManager.Unsubclass();
  ...
  // 2. Destroy ImGui backends and resources
  DearImGuiInjectionPlus.Dispose();

  // 3. Disable native DXGI hooks
  RendererFinder.RendererFinder.Dispose();
  ```
  The teardown order is completely inverted relative to safe unmanaged hooking practices:
  1. **Hooks are disabled LAST.** The native DXGI `Present` and `ExecuteCommandLists` hooks remain active and hooked in executable memory while `DearImGuiInjectionPlus.Dispose()` is actively destroying the ImGui context, shader buffers, and device objects. Any frame presenting during this window immediately crashes.
  2. **Asymmetric disposal:** In `ImGuiDX11.Dispose()`, cleanup is skipped if `IsShuttingDown` is true (see **MEM-05**), while in `ImGuiDX12.Dispose()`, cleanup proceeds unconditionally without GPU synchronization (see **DXH-05**).
  3. **Reloaded.Hooks Disable Behavior:** `Hook.Disable()` writes an unconditioned jump to the original function or restores modified bytes; however, if threads are suspended or waiting in the hook, disposing the hook wrapper can crash native execution.
- **Impact Assessment:**
  Frequent process crashes during exit or scene reloads.
- **Recommended Solution:**
  Invert the teardown order:
  1. Signal the frame gate to drain in-flight rendering frames (**TH-01**).
  2. Disable and deactivate all native hooks (`RendererFinder.Dispose()`).
  3. Restore original window procedures (`InputManager.Unsubclass()`).
  4. Wait for GPU idle (`WaitForGpuIdle()`).
  5. Destroy graphics resources and ImGui context.

---

#### LC-03 — Missing exception catch handlers at unmanaged hook boundaries
- **File & Line Reference:** [`DX11Renderer.cs#L256-L302`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L256-L302), [`DX12Renderer.cs#L163-L225`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX12Renderer.cs#L163-L225), [`InputManager.cs#L71-L134`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/InputManager.cs#L71-L134)
- **Technical Description:**
  In `SwapChainPresentHook`:
  ```csharp
  _isInsidePresent = true;
  try
  {
      HandlePresent(self, syncInterval, flags);
      return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
  }
  finally
  {
      _isInsidePresent = false;
  }
  ```
  There is **NO `catch` block**. If `HandlePresent` throws any unhandled managed exception (e.g. `SharpDXException`, `NullReferenceException`, or `OutOfMemoryException`), the exception escapes the managed delegate boundary.
  Under the Mono runtime on Windows, an unhandled managed exception escaping across a native reverse-P/Invoke boundary into native code (such as DXGI or DirectX graphics drivers) cannot be caught by native SEH frames. The Windows kernel triggers a fail-fast crash: `STATUS_FATAL_USER_CALLBACK_EXCEPTION` (`0xC000041D`).
  Furthermore, if an exception escapes `HandlePresent`, `OriginalFunction` is never called, starving DXGI of its presentation call.
  In `DX12Renderer.CommandQueueExecuteCommandListHook`, if an exception occurs outside the inner block, `OriginalFunction` is also skipped, permanently dropping command lists recorded by the game.
- **Impact Assessment:**
  Immediate fatal native crash (`0xC000041D`) on any managed error within the injection pipeline.
- **Recommended Solution:**
  Wrap every native hook entry point in an airtight `try / catch (Exception ex)` block that logs the error and **always calls the original native function**:
  ```csharp
  private static IntPtr SwapChainPresentHook(IntPtr self, uint syncInterval, uint flags)
  {
      if (RendererFinder.IsShuttingDown || _isInsidePresent)
          return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);

      _isInsidePresent = true;
      try
      {
          try { HandlePresent(self, syncInterval, flags); }
          catch (Exception ex) { Log.Error($"[Hook] Exception in HandlePresent: {ex}"); }

          return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
      }
      catch (Exception fatal)
      {
          Log.Fatal($"[Hook] Fatal exception in Present hook: {fatal}");
          return IntPtr.Zero;
      }
      finally
      {
          _isInsidePresent = false;
      }
  }
  ```

---

#### LC-04 — Missing circuit breaker / failure logging flood
- **File & Line Reference:** [`DX11Renderer.cs#L383-L386`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/Renderers/DX11Renderer.cs#L383-L386), [`ImGuiDX12.cs#L400-L403`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiDX12.cs#L400-L403)
- **Technical Description:**
  In `HandlePresent` and `RenderImGui`:
  Exceptions are caught and logged:
  ```csharp
  catch (Exception ex)
  {
      Log.Error($"[ImGuiDX12] Render error on frame {frame}: {ex}");
  }
  ```
  If a persistent error condition occurs (such as a device loss, null buffer, or corrupted shader state), this catch block executes **every single frame** (60 to 240 times per second). Writing full multi-line stack traces to disk synchronously stalls the render thread and expands `LogOutput.log` to tens of gigabytes within minutes.
- **Impact Assessment:**
  Catastrophic frame rate collapse (dropping to 1–5 FPS) and disk fill exhaustion.
- **Recommended Solution:**
  Implement a consecutive error circuit breaker: if more than 5 consecutive frames throw exceptions, disable overlay rendering for 5 seconds or deactivate the backend cleanly.

---

#### LC-05 — ImGui stack imbalance on user callback exception
- **File & Line Reference:** [`DearImGuiInjectionPlus.cs#L354-L435`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/DearImGuiInjectionPlus.cs#L354-L435)
- **Technical Description:**
  In `DearImGuiInjectionPlus.NewFrame()`:
  ```csharp
  for (int i = 0; i < windows.Length; i++)
  {
      var win = windows[i];
      if (win != null && win.IsOpen)
      {
          try { win.OnRender(); }
          catch (Exception ex) { Log.Error($"[DearImGuiInjectionPlus] Error rendering window '{win.Title}': {ex}"); }
      }
  }
  ```
  While the catch block isolates other mods from a crashing mod, it does NOT restore Dear ImGui's internal stack state. If `win.OnRender()` threw an exception after calling `ImGui.Begin("Window")`, `ImGui.PushStyleColor(...)`, or `ImGui.PushItemWidth(...)`, but *before* calling the corresponding `ImGui.End()`, `PopStyleColor()`, or `PopItemWidth()`, ImGui's internal window and ID stacks remain imbalanced.
  When `ImGui.EndFrame()` or `ImGui.Render()` is invoked at line 433, Dear ImGui triggers an unmanaged assertion failure: `IM_ASSERT(g.CurrentWindowStack.Size == 1)` or `IM_ASSERT(g.StyleColorStack.Size == 0)`. In debug or standard builds of `cimgui.dll`, assertion failures call `abort()`, terminating the process instantly.
- **Impact Assessment:**
  A single minor exception inside any third-party mod's `IImGuiWindow.OnRender()` immediately crashes the entire game via native CRT abort.
- **Recommended Solution:**
  Use `ImGui.GetErrorLog()` or record the stack depths (`ImGui.GetIDStackSize()`) before invoking user callbacks. In the catch block, safely restore the stack by calling `ImGui.End()` until the window stack depth returns to its baseline.

---

#### LC-06 — Reinitialization impossible due to sticky state flags
- **File & Line Reference:** [`ImGuiWin32Impl.cs#L33-L37`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L33-L37), [`#L95-L112`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Backends/ImGuiWin32Impl.cs#L95-L112), [`RendererFinder.cs#L190-L204`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/RendererFinder/RendererFinder.cs#L190-L204)
- **Technical Description:**
  In `ImGuiWin32Impl`:
  ```csharp
  if (_windowHandle != IntPtr.Zero)
  {
      Log.Error("Already initialized a platform backend!");
      return false;
  }
  ```
  In `Shutdown()`, `_windowHandle` is **never reset to `IntPtr.Zero`**.
  In `RendererFinder.Dispose()`, `IsShuttingDown` is set to `true`, but is never reset if `RendererFinder.Init()` is called again.
  If a mod manager, hot-reload tool, or game mode attempts to re-initialize the injection framework without restarting the process, initialization fails completely.
- **Impact Assessment:**
  Hot-reloading is completely broken.
- **Recommended Solution:**
  Ensure all static variables (`_windowHandle`, `IsShuttingDown`, `_implementationInitialized`) are reset to default values inside `Dispose()` / `Shutdown()`.

---

#### LC-07 — Missing `UnregisterTab` causing delegate leaks & duplicate UI
- **File & Line Reference:** [`DearImGuiInjectionPlus.cs#L127-L142`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/TestPlugin/TestPlugin.cs#L89-L106)
- **Technical Description:**
  `DearImGuiInjectionPlus.RegisterTab` allows plugins to register menu bar items. However, there is no corresponding `UnregisterTab` method.
  In `TestPlugin.cs`:
  ```csharp
  private void OnEnable()
  {
      DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Mods", "Toggle Sample View", ...);
  }
  ```
  Every time a plugin component is disabled and re-enabled (such as scene changes or mod reloading), a duplicate tab is appended to `_registeredTabs`. The delegate closures also keep unreferenced plugin instances permanently pinned in memory.
- **Impact Assessment:**
  Duplicate menu items accumulating over time; managed memory leaks.
- **Recommended Solution:**
  Implement `UnregisterTab(string category, string title)` or have `RegisterTab` return an `IDisposable` registration token.

---

#### LC-08 — `PreloadNativeLibraries` uses ANSI `LoadLibrary`
- **File & Line Reference:** [`DearImGuiInjectionPlusBaseUnityPlugin.cs#L95-L126`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/BepInEx/DearImGuiInjectionPlusBaseUnityPlugin.cs#L95-L126), [`Kernel32.cs#L100-L101`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/DearImGuiInjectionPlus/Windows/Kernel32.cs#L100-L101)
- **Technical Description:**
  `PreloadNativeLibraries` calls `Kernel32.LoadLibrary(path)`.
  In `Kernel32.cs`:
  ```csharp
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
  public static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);
  ```
  The P/Invoke binds to `LoadLibraryA` (ANSI). If the game or user directory path contains non-ANSI Unicode characters (such as non-Latin user profile names or non-English Steam library paths), `LoadLibraryA` fails to find `cimgui.dll`.
- **Impact Assessment:**
  `DllNotFoundException: cimgui.dll` on systems with non-ASCII directory paths.
- **Recommended Solution:**
  Bind to `LoadLibraryW`:
  ```csharp
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPWStr)] string lpFileName);
  ```

---

#### DOC-01 — Documentation discrepancies in `PROJECT-OVERVIEW.md`
- **File & Line Reference:** [`PROJECT-OVERVIEW.md`](file:///d:/BepInEx%20stuff/DearImGuiInjectionPlus/PROJECT-OVERVIEW.md)
- **Technical Description:**
  Multiple claims in the architecture overview contradict the actual code implementation:
  1. *Claim:* "In D3D11 mode, acquiring Texture2D and transient RTV in a using block prevents held COM references that would cause DXGI_ERROR_INVALID_CALL during resizing."
     *Reality:* As proven in **MEM-01**, `BackupRenderState` leaks the RTV and backbuffer references on every frame, invalidating this claim.
  2. *Claim:* "Executes a single Dear ImGui frame with zero allocations".
     *Reality:* Allocates hundreds of objects per frame on the render thread (**MEM-07**).
  3. *Claim:* "Restores original dxgi.dll bytes".
     *Reality:* `Reloaded.Hooks.Disable()` changes detour redirection flags; it does not uninstall native hooks or restore original prologue bytes (**DXH-09**, **LC-02**).
  4. *Claim:* `BlockAllInputsWhenOpen` controls input swallowing.
     *Reality:* Win32 and Harmony input blockers ignore this flag completely (**INP-06**).
  5. *Claim:* Unity 6 D3D12 crash is caused by "Mono's post-shutdown ComputeBuffer GC sweep".
     *Reality:* As proven in **MEM-02**, **DXH-05**, and **TH-01**, the crash is directly caused by swapchain over-release, un-synchronized GPU teardown, and destroyed context races.
- **Impact Assessment:**
  Misleading architectural documentation obscures critical root-cause bugs and complicates maintenance.
- **Recommended Solution:**
  Update `PROJECT-OVERVIEW.md` to accurately reflect the corrected synchronization and lifecycle model.

---

## 4. Hardening Recommendations

To ensure rock-solid production stability across hundreds of diverse Unity titles, modding setups, and hardware configurations, the following architectural enhancements are strongly recommended:

### 1. Robust SwapChain `ResizeBuffers` (Slot 13) Interception
Currently, the injection relies entirely on the transient render target pattern to evade `ResizeBuffers` failures. However, in complex modding environments, other graphics mods, overlay software, or game post-processing pipelines can trigger resize operations at unexpected intervals.
- **Recommendation:** Hook `IDXGISwapChain::ResizeBuffers` (Slot 13). Prior to calling the original `ResizeBuffers`, unconditionally release all held backbuffer references, render target views, and depth buffers. Re-acquire backbuffer views on the subsequent `Present` call. This provides a 100% guarantee against `DXGI_ERROR_INVALID_CALL` during window resizing or fullscreen transitions.

### 2. Native C++ Trampoline / Lightweight Detour Layer
Currently, native virtual function tables are hooked directly using managed C# delegates via `Reloaded.Hooks`. While convenient, reverse-P/Invoke delegates introduce managed execution transitions on the graphics thread on every `Present` call.
- **Recommendation:** For maximum resilience and performance, package a thin, native C++ proxy DLL (`DearImGuiInjectionNative.dll`). The native proxy handles:
  - VTable redirection and original function invocation with zero CLR transitions when the overlay is closed.
  - Native Structured Exception Handling (`__try / __except`) to shield the game engine from any managed faults.
  - Immediate, thread-safe input event queueing.

### 3. Integrated DXGI / Direct3D Debug Layer Diagnostics
Debugging low-level COM leaks and descriptor hazards currently relies on trial and error.
- **Recommendation:** Add a diagnostic configuration switch in `DearImGuiInjectionPlus.cfg` (`EnableGraphicsDebugLayer = true`). When enabled in development builds:
  - Load `dxgidebug.dll` and query `IDXGIDebug1`.
  - Upon `OnDestroy()`, execute `ReportLiveObjects(DXGI_DEBUG_ALL, DXGI_DEBUG_RLO_ALL)`.
  - Log any leaked DirectX resources with their native allocation callstacks to immediately identify offending mod windows or leaky textures.

### 4. Production Input Multiplexer & Raw Input Integration
Rather than using dual-layer hooking with scattered Harmony patches across Unity input methods:
- **Recommendation:** Implement an unified Input Multiplexer:
  - Register for Win32 Raw Input (`RegisterRawInputDevices` with `RIDEV_INPUTSINK`).
  - Use `comctl32.dll`'s `SetWindowSubclass` for completely safe, chain-friendly window procedure interception.
  - Provide a single unified `InputFilter` interface that allows mods to selectively whitelist specific shortcuts (such as mod hotkeys, screenshot keys, or push-to-talk) while keeping gameplay controls blocked.

### 5. Architectural Exception Shield & Circuit Breaker Pattern
Protect the host process from faulty consumer mods:
- **Recommendation:** Wrap each consumer window's `OnRender()` in an isolated stack-depth guard. If an exception occurs:
  - Automatically pop unmatched ImGui IDs and windows back to root level.
  - Temporarily disable the offending mod window and display an inline red error box: *"Window [X] encountered an error and was paused to protect engine stability"*.
  - This eliminates CRT abort crashes and guarantees zero game crashes regardless of how poorly written a third-party mod's ImGui code may be.
