# DearImGuiInjectionPlus

[![Release](https://img.shields.io/badge/Release-v1.0.0-blue.svg?style=flat-square)](https://github.com/Klax-xon/DearImGuiInjectionPlus/releases)
[![Target Framework](https://img.shields.io/badge/.NET-6.0%20(CoreCLR)-purple.svg?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![BepInEx](https://img.shields.io/badge/BepInEx-6.x%20IL2CPP%20(Bleeding%20Edge)-orange.svg?style=flat-square)](https://builds.bepinex.dev/projects/bepinex_be)
[![Architecture](https://img.shields.io/badge/Architecture-x64-red.svg?style=flat-square)](https://en.wikipedia.org/wiki/X86-64)
[![DirectX](https://img.shields.io/badge/DirectX-D3D11%20%7C%20D3D12-darkgreen.svg?style=flat-square)](https://docs.microsoft.com/en-us/windows/win32/direct3d)
[![Dear ImGui](https://img.shields.io/badge/Dear%20ImGui-1.89.9%20Docking-ff69b4.svg?style=flat-square)](https://github.com/ocornut/imgui)
[![Render Loop](https://img.shields.io/badge/Render%20Loop-Zero%20Steady--State%20Alloc-brightgreen.svg?style=flat-square)](#zero-allocation-render-loop--concurrency-pipelines)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)

A high-performance unmanaged/managed hybrid injection framework engineered to embed **[Dear ImGui](https://github.com/ocornut/imgui)** directly into **Unity** DirectX swapchains under **[BepInEx 6 IL2CPP](https://github.com/BepInEx/BepInEx)** (.NET 6 CoreCLR x64).

`DearImGuiInjectionPlus` delivers an ultra-stable, crash-resilient developer GUI layer for modders, game reverse engineers, and tool creators. By hooking the engine's DXGI swapchain at the native COM level, it renders high-framerate, immediate-mode interfaces without relying on intermediate C++ proxy DLLs, without interfering with Unity's internal rendering pipelines, and without allocating managed heap memory inside the render loop.

---

## Table of Contents

- [Key Architectural Features](#key-architectural-features)
  - [Native DirectX 11 & DirectX 12 Pipelines](#-native-directx-11--directx-12-pipelines)
  - [Non-Destructive Win32 Subclassing & Multi-Layer Input Swallowing](#-non-destructive-win32-subclassing--multi-layer-input-swallowing)
  - [Zero-Allocation Render Loop & Concurrency Pipelines](#-zero-allocation-render-loop--concurrency-pipelines)
  - [Fault Isolation & Stack Recovery](#-fault-isolation--stack-recovery)
- [Supported Environments](#supported-environments)
- [Installation & Setup](#installation--setup)
  - [For Players & Mod Users](#for-players--mod-users)
  - [Configuration File (`DearImGuiInjectionPlus.cfg`)](#configuration-file-dearimguiinjectionpluscfg)
- [Developer Integration Guide](#developer-integration-guide)
  - [1. Manifest & Assembly Dependencies](#1-manifest--assembly-dependencies)
  - [2. Plugin Entry Point (`BasePlugin.Load`)](#2-plugin-entry-point-basepluginload)
  - [3. IL2CPP MonoBehaviour Registration (`ClassInjector`)](#3-il2cpp-monobehaviour-registration-classinjector)
  - [4. Modular Windows (`IImGuiWindow`)](#4-modular-windows-iimguiwindow-recommended)
  - [5. Main Menu Bar Tabs (`RegisterTab`)](#5-main-menu-bar-tabs-registertab)
  - [6. Thread-Safe State Publishing (`StateSnapshotPipeline` & `StructSnapshotPipeline`)](#6-thread-safe-state-publishing-lock-free)
  - [7. Main-Thread Dispatching (`UnityMainThreadDispatcher`)](#7-main-thread-dispatching-unitymainthreaddispatcher)
  - [8. Real-Time Diagnostics (`GraphicsTelemetry`)](#8-real-time-diagnostics-graphicstelemetry)
  - [9. Legacy Render Delegate (`Render`)](#9-legacy-render-delegate-render)
- [Input Blocking Architecture (4-Layer Defense)](#input-blocking-architecture-4-layer-defense)
  - [Layer 1: Win32 Subclassing & Message Swallowing (`Comctl32`)](#layer-1-win32-subclassing--message-swallowing-comctl32)
  - [Layer 2: Direct `UnityPlayer.dll` Native ICall Hooks & Stripping-Resilient HarmonyX Detours](#layer-2-direct-unityplayerdll-native-icall-hooks--stripping-resilient-harmonyx-detours)
  - [Layer 3: New Input System Buffer Hook (`NativeInputSystem.NotifyUpdate`)](#layer-3-new-input-system-buffer-hook-nativeinputsystemnotifyupdate)
  - [Layer 4: uGUI Canvas Shielding (`EventSystemBlocker`)](#layer-4-ugui-canvas-shielding-eventsystemblocker)
  - [Stuck-Key & 3D Camera Drift Prevention](#stuck-key--3d-camera-drift-prevention)
- [Threading Model & Execution Architecture](#threading-model--execution-architecture)
  - [Thread Affinity & Safety Boundaries](#thread-affinity--safety-boundaries)
  - [Quiescent Teardown & `FrameGate`](#quiescent-teardown--framegate)
  - [COM Reference Balance & SharpDX Lifetime Safety](#com-reference-balance--sharpdx-lifetime-safety)
- [Diagnostics & Test Bench (`TestPlugin`)](#diagnostics--test-bench-testplugin)
  - [Diagnostic Dashboard Overview](#diagnostic-dashboard-overview)
  - [Runtime Verification Matrix (TM-01 through TM-14)](#runtime-verification-matrix-tm-01-through-tm-14)
- [Building from Source](#building-from-source)
  - [Prerequisites](#prerequisites)
  - [NuGet Package Sources](#nuget-package-sources)
  - [Build Commands](#build-commands)
  - [Output Structure](#output-structure)
- [Troubleshooting & FAQ](#troubleshooting--faq)
- [Credits & Attribution](#credits--attribution)
- [License](#license)

---

## Key Architectural Features

### 🎮 Native DirectX 11 & DirectX 12 Pipelines
- **Direct DXGI Hooking**: Hooks `IDXGISwapChain::Present` (VTable slot 8) and `IDXGISwapChain1::Present1` (VTable slot 22) dynamically at runtime using [`Reloaded.Hooks`](https://github.com/Reloaded-Project/Reloaded.Hooks). Completely bypasses the need for fragile C++ proxy wrapper DLLs.
- **Direct3D 11 Backend**: Custom managed D3D11 backend featuring deterministic `StateBackup` capture and restoration, immediate-context rasterizer state recovery, and direct VTable-level shader resource binding (`PSSetShaderResources` slot 8) that bypasses transient wrapper allocations.
- **Direct3D 12 Backend**: Native command queue acquisition via swapchain device query (`GetDevice` / `QueryInterface` for `IID_ID3D12CommandQueue` on `Present`), ring-buffered per-backbuffer `FrameContext` allocators, automatic fence synchronization (`WaitForGpuIdle`), and dynamic root signature / PSO format adaptation.
- **Dynamic SwapChain & Device Loss Recovery**: Seamlessly updates active swapchain pointers upon resolution and window mode changes on the same `HWND`. In the event of a GPU driver crash or TDR (`DeviceRemovedReason.Failure`), stale GPU interfaces are invalidated and automatically reconstructed without crashing the host process.
- **HDR & Extended Color Space Support**: Dynamically detects backbuffer formats (`R8G8B8A8_UNorm`, `B8G8R8A8_UNorm_SRgb`, `R10G10B10A2_UNorm`, `R16G16B16A16_Float`), automatically instantiates matching D3D12 render target PSOs, and applies gamma compensation to prevent color washout on HDR10 and scRGB displays.

### 🛡️ Non-Destructive Win32 Subclassing & Multi-Layer Input Swallowing
- **Safe Subclassing**: Window interception uses `Comctl32.SetWindowSubclass` / `RemoveWindowSubclass` marshaled strictly to the window-owning thread. Coexists harmoniously with other Win32 hooks and preserves Unicode window procedures (`IsWindowUnicode`).
- **Comprehensive 4-Layer Input Swallowing Pipeline**:
  1. **Win32 Message Pump (`Comctl32`)**: Swallows raw input (`WM_INPUT`), mouse clicks, and keystrokes based on `BlockAllInputsWhenOpen` and ImGui capture flags (`WantCaptureMouse`, `WantCaptureKeyboard`). `Alt+Enter` is explicitly whitelisted directly to `DefSubclassProc` to preserve fullscreen switching.
  2. **Native `UnityPlayer.dll` ICall Hooks & HarmonyX Detours**: Direct native detours on `UnityPlayer.dll` C++ exports (`GetKeyInt`, `get_mousePosition_Injected`, `GetMouseButton`, `Cursor` setters) bypass IL2CPP C++ `/O2` compiler inlining across Unity 2019, 2021, 2022, and Unity 6 runtimes, with per-method isolated HarmonyX fallbacks.
  3. **New Input System Raw Buffer Compaction (`NativeInputSystem.NotifyUpdate`)**: Hooks the low-level `[RequiredByNativeCode]` unmanaged entry point to zero or compact `NativeInputEventBuffer` in native memory, dropping hardware events and resetting `Mouse.current.delta` to `(0, 0)` while surviving aggressive `UnityLinker` stripping.
  4. **uGUI Canvas Shielding (`EventSystemBlocker`)**: Detours `UnityEngine.EventSystems.EventSystem.Update` to prevent mouse clicks on ImGui windows from penetrating underlying game canvas buttons.
- **Stuck-Key & Drift Prevention**: Tracks held keys upon menu opening in a 256-bit bitmask (`GetAsyncKeyState`). Synthesizes `WM_KEYUP` notifications when the menu closes or transitions from passthrough to blocked mode, preventing characters from moving or firing perpetually. Mouse cursor lock (`CursorLockMode.None`) and clipping bounds (`ClipCursor(NULL)`) are managed seamlessly.
- **Full Unicode & IME Candidate Support**: `WM_CHAR`, `WM_IME_CHAR`, and `WM_UNICHAR` (including UTF-16 surrogate pairs for supplementary emojis `0x10000`–`0x10FFFF`) are routed directly into ImGui. IME system messages dispatch to `DefWindowProcW` while typing, allowing IME composition windows to function natively.

### ⚡ Zero-Allocation Render Loop & Concurrency Pipelines
- **Zero Steady-State Managed Allocations**: Eliminates heap churn on the render thread. All render-loop collections (tabs, modular windows, delegates) use pre-cached snapshots that only rebuild upon registration changes. Dynamic vertex and index buffers grow logarithmically and reuse native memory.
- **Thread-Isolated Input Queue**: The Win32 WndProc never touches `ImGuiIO` directly. Input messages are packaged into zero-allocation `InputEvent` structs and pushed to a lock-free `ConcurrentQueue<InputEvent>`, drained exclusively on the DXGI `Present` thread.
- **Lock-Free State Pipelines**:
  - `StateSnapshotPipeline<T>` (for reference types): Atomic snapshot exchange via `Interlocked.Exchange` and `Volatile.Read`.
  - `StructSnapshotPipeline<T>` (for value types): Triple-buffered atomic slot exchange (`_slot0`, `_slot1`, `_slot2`) with flag masking, guaranteeing **zero torn reads** and **zero boxing allocations** under extreme write frequencies (tested up to 10,000 Hz).
- **UnityMainThreadDispatcher**: Non-blocking lock-free action queue allowing ImGui callbacks and background worker threads to marshal game-state mutations directly to the Unity main thread, guarded against deadlocks and post-timeout object corruption.

### 🛡️ Fault Isolation & Stack Recovery
- **Per-Callback Exception Containment**: Every modular window (`IImGuiWindow.OnRender`), menu tab (`RegisterTab`), and legacy render delegate is wrapped in an isolated `try/catch` boundary backed by `RestoreImGuiStack()` (`igErrorCheckEndFrameRecover`) and `RestoreImGuiWindowStack()` (`igErrorCheckEndWindowRecover`). A third-party mod throwing an exception cannot corrupt the ImGui stack, break other mods, or crash the host process.
- **Consecutive Error Circuit Breakers**: If an unhandled backend rendering error occurs for 5 consecutive frames, a circuit breaker trips, pausing rendering for 5 seconds to prevent frame-rate collapse and log flooding (60–240 stack traces per second).

---

## Supported Environments

| Component | Supported Version / Requirement | Architectural Notes |
| :--- | :--- | :--- |
| **Operating System** | **Windows 10 / 11 (x64)** | 64-bit architecture strictly required (uses 64-bit `ImTextureID`). |
| **Unity Engine** | **Unity 2020 through Unity 2022.3+ / Unity 6** | Certified on Unity 2019 LTS, 2020, 2021 LTS, 2022 LTS, and Unity 6 IL2CPP runtimes. |
| **Scripting Backend** | **IL2CPP (.NET 6.0 CoreCLR)** | Fully decoupled from IL2CPP runtime overhead via native VTable hooks. |
| **Modding Host** | **BepInEx 6.x IL2CPP** | Bleeding Edge builds (e.g. `6.0.0-be.790+`) using `BepInEx.Unity.IL2CPP.BasePlugin`. |
| **Graphics APIs** | **Direct3D 11** & **Direct3D 12** | Automatically detected from `SystemInfo.graphicsDeviceType`. |
| **Dear ImGui** | **v1.89.9 Docking** (`cimgui.dll` / `ImGui.NET`) | Multi-docking, custom viewports, and high-DPI font atlas baking. |

> [!NOTE]
> **Unsupported Graphics Backends**: OpenGL, Vulkan, and Metal are not supported. When launched under an unsupported backend, `DearImGuiInjectionPlus` logs an informative error and cleanly aborts initialization without installing native DXGI hooks or intercepting window procedures.

---

## Installation & Setup

### For Players & Mod Users

1. **Install BepInEx 6 IL2CPP (x64)**:
   - Ensure your Unity IL2CPP game has **BepInEx 6.x IL2CPP (64-bit)** installed (downloadable from [BepInEx Bleeding Edge Builds](https://builds.bepinex.dev/projects/bepinex_be)).
2. **Download Release**:
   - Download the latest `DearImGuiInjectionPlus-v1.0.0.zip` from [Releases](https://github.com/Klax-xon/DearImGuiInjectionPlus/releases).
3. **Extract to Plugins Directory**:
   - Extract the archive directly into your game's `BepInEx/plugins/` directory:
     ```
     <GameDirectory>/
     └── BepInEx/
         └── plugins/
             └── DearImGuiInjectionPlus/
                 ├── DearImGuiInjectionPlus.dll
                 ├── RendererFinder.dll
                 ├── ImGui.NET.dll
                 ├── cimgui.dll
                 ├── Reloaded.Hooks.dll
                 ├── Reloaded.Hooks.Definitions.dll
                 ├── Reloaded.Memory.dll
                 ├── Reloaded.Memory.Buffers.dll
                 ├── Reloaded.Assembler.dll
                 ├── Iced.dll
                 ├── FASM.DLL
                 ├── FASMX64.DLL
                 ├── SharpDX.dll
                 ├── SharpDX.DXGI.dll
                 ├── SharpDX.Direct3D11.dll
                 ├── SharpDX.Direct3D12.dll
                 └── Assets/
                     ├── Fonts/
                     │   └── Comfortaa-Medium.ttf
                     └── Shaders/
                         ├── imgui-vertex.hlsl.bytes
                         └── imgui-frag.hlsl.bytes
     ```
4. **Launch the Game**:
   - Press **`Insert`** on your keyboard to toggle the Dear ImGui cursor and developer overlay.
   - UI layout and docking configurations are saved automatically to `BepInEx/config/DearImGuiInjectionPlus_imgui.ini`.

### Configuration File (`DearImGuiInjectionPlus.cfg`)

Upon the first game startup, a configuration file is generated at `BepInEx/config/DearImGuiInjectionPlus.cfg`:

```ini
[Keybinds]

## Key for switching the Dear ImGui cursor and menu visibility.
# Setting type: VirtualKey
# Default value: Insert
CursorVisibility = Insert

[InputBlocking]

## Whether all game mouse clicks and keyboard inputs are swallowed while the menu is open.
## If set to false, inputs are only swallowed when ImGui explicitly requests capture (WantCaptureMouse/Keyboard).
# Setting type: Boolean
# Default value: true
BlockAllInputsWhenOpen = true
```

---

## Developer Integration Guide

`DearImGuiInjectionPlus` provides a modern, thread-safe, and stripping-resilient API designed specifically for BepInEx 6 IL2CPP mod developers.

### 1. Manifest & Assembly Dependencies

Reference `DearImGuiInjectionPlus.dll`, `ImGui.NET.dll`, and `BepInEx.Unity.IL2CPP.dll` in your `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <LangVersion>preview</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BepInEx.Unity.IL2CPP" Version="6.0.0-be.790" />
    <Reference Include="DearImGuiInjectionPlus">
      <HintPath>..\path\to\bin\DearImGuiInjectionPlus.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="ImGui.NET">
      <HintPath>..\path\to\bin\ImGui.NET.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
</Project>
```

---

### 2. Plugin Entry Point (`BasePlugin.Load`)

In BepInEx 6 IL2CPP, plugins inherit from `BasePlugin` (not `BaseUnityPlugin`) and initialize inside `public override void Load()`:

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace MyCustomMod;

[BepInPlugin("com.author.mycustommod", "My Custom Mod", "1.0.0")]
[BepInDependency(DearImGuiInjectionPlus.Metadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class MyCustomModPlugin : BasePlugin
{
    public static MyCustomModPlugin Instance { get; private set; }
    public StateSnapshotPipeline<GameDataSnapshot> SnapshotPipeline { get; private set; }

    private MyModWindow _settingsWindow;
    private IDisposable _menuTab;
    private GameObject _runnerGo;

    public override void Load()
    {
        Instance = this;
        SnapshotPipeline = new StateSnapshotPipeline<GameDataSnapshot>(new GameDataSnapshot());

        // 1. Register custom IL2CPP MonoBehaviour runner for main-thread engine polling
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<MyModRunner>())
        {
            ClassInjector.RegisterTypeInIl2Cpp<MyModRunner>();
        }

        _runnerGo = new GameObject("MyCustomMod_Runner");
        _runnerGo.hideFlags = HideFlags.HideInHierarchy;
        Object.DontDestroyOnLoad(_runnerGo);
        _runnerGo.AddComponent<MyModRunner>();

        // 2. Register modular window
        _settingsWindow = new MyModWindow(SnapshotPipeline);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_settingsWindow);

        // 3. Register menu bar tab
        _menuTab = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Mods", "My Custom Mod", () =>
        {
            if (ImGuiNET.ImGui.MenuItem("Toggle Settings Window", "", _settingsWindow.IsOpen))
            {
                _settingsWindow.IsOpen = !_settingsWindow.IsOpen;
            }
        });

        Log.LogInfo("MyCustomMod loaded successfully!");
    }

    public override bool Unload()
    {
        _menuTab?.Dispose();
        if (_settingsWindow != null)
        {
            DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_settingsWindow);
        }

        if (_runnerGo != null)
        {
            Object.Destroy(_runnerGo);
        }

        return base.Unload();
    }
}
```

---

### 3. IL2CPP MonoBehaviour Registration (`ClassInjector`)

Because ImGui renders on the DirectX `Present` thread, querying `UnityEngine.Object` methods (such as `transform.position` or `SceneManager.GetActiveScene()`) directly from the ImGui render loop is strictly prohibited and causes crashes.

To poll Unity engine state, spawn a dedicated `MonoBehaviour` runner on the Unity main thread. In IL2CPP, all custom `MonoBehaviour` classes **must implement the native pointer constructor**:

```csharp
using System;
using DearImGuiInjectionPlus;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyCustomMod;

public class GameDataSnapshot
{
    public Vector3 PlayerPosition;
    public float Health;
    public string CurrentScene;
}

public class MyModRunner : MonoBehaviour
{
    // REQUIRED: Native pointer constructor for IL2CPP ClassInjector
    public MyModRunner(IntPtr handle) : base(handle) { }

    private string _cachedSceneName = "Default";
    private int _cachedSceneHandle = -1;

    private void Update()
    {
        var pipeline = MyCustomModPlugin.Instance?.SnapshotPipeline;
        if (pipeline == null)
            return;

        // Eliminate per-frame string allocation (AUD-07): only read .name when handle changes!
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.handle != _cachedSceneHandle)
        {
            _cachedSceneHandle = activeScene.handle;
            _cachedSceneName = activeScene.name;
        }

        Vector3 pos = Vector3.zero;
        if (Camera.main != null)
        {
            pos = Camera.main.transform.position;
        }

        // Publish snapshot lock-free to the ImGui render thread
        pipeline.Publish(new GameDataSnapshot
        {
            PlayerPosition = pos,
            Health = 100f,
            CurrentScene = _cachedSceneName
        });
    }
}
```

> [!TIP]
> **Eliminating Per-Frame IL2CPP String Allocations (`AUD-07`)**: Avoid querying `SceneManager.GetActiveScene().name` every frame in IL2CPP `Update()`. In IL2CPP, accessing `.name` invokes `IL2CPP.Il2CppStringToManaged`, creating a managed string allocation on every single frame. Instead, cache `activeScene.handle` and only query `.name` when the handle changes.

---

### 4. Modular Windows (`IImGuiWindow`) [Recommended]

The `IImGuiWindow` interface provides isolated exception handling, unique window tracking, and automatic docking integration. If an exception is thrown in `OnRender()`, the framework automatically catches the error, restores the ImGui stack via `igErrorCheckEndFrameRecover`, and logs the error without crashing other windows or the host game:

```csharp
using System.Numerics;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using ImGuiNET;
using UnityEngine;

namespace MyCustomMod;

public class MyModWindow : IImGuiWindow
{
    public string Id => "my_custom_mod_window";
    public string Title => "My Custom Mod - Controls";
    public bool IsOpen { get; set; } = true;

    private readonly StateSnapshotPipeline<GameDataSnapshot> _snapshotPipeline;
    private float _multiplier = 1.0f;

    public MyModWindow(StateSnapshotPipeline<GameDataSnapshot> snapshotPipeline)
    {
        _snapshotPipeline = snapshotPipeline;
    }

    public void OnRender()
    {
        if (!IsOpen)
            return;

        bool open = IsOpen;
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Thread-safe lock-free read from snapshot pipeline (Render Thread safe)
            var data = _snapshotPipeline.GetLatest();
            if (data != null)
            {
                ImGui.Text($"Active Scene: {data.CurrentScene}");
                ImGui.Text($"Player Position: ({data.PlayerPosition.x:F1}, {data.PlayerPosition.y:F1}, {data.PlayerPosition.z:F1})");
            }

            ImGui.Separator();
            ImGui.SliderFloat("Speed Multiplier", ref _multiplier, 0.1f, 5.0f);

            if (ImGui.Button("Spawn Debug Cube"))
            {
                // Enqueue engine modifications to the Unity main thread asynchronously
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "ModDebugCube";
                    if (Camera.main != null)
                    {
                        cube.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 3f;
                    }
                });
            }
        }
        ImGui.End();
        IsOpen = open;
    }
}
```

---

### 5. Main Menu Bar Tabs (`RegisterTab`)

Adds dropdown menu items to the unified Dear ImGui main menu bar. `RegisterTab` deduplicates entries by category/title, isolates execution with `igErrorCheckEndWindowRecover`, and returns an `IDisposable` token for clean disposal:

```csharp
// Register under "Tools" -> "Quick Actions"
var tabHandle = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Tools", "Quick Actions", () =>
{
    if (ImGuiNET.ImGui.MenuItem("Teleport Player to Origin"))
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (Camera.main != null)
                Camera.main.transform.position = UnityEngine.Vector3.zero;
        });
    }
});

// Explicit unregistration when plugin unloads:
tabHandle.Dispose();
// Alternatively:
DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterTab("Tools", "Quick Actions");
```

---

### 6. Thread-Safe State Publishing (Lock-Free)

To bridge data between Unity's main scripting thread and the DirectX `Present` render thread without lock contention, GC pauses, or torn reads, use the built-in snapshot pipelines:

#### Reference Types (`StateSnapshotPipeline<T>`)
Best for complex view models and snapshots with variable-length arrays or strings. Uses atomic `Interlocked.Exchange` and `Volatile.Read`:

```csharp
var pipeline = new StateSnapshotPipeline<GameDataSnapshot>(new GameDataSnapshot());

// Main Thread (e.g. Update):
pipeline.Publish(new GameDataSnapshot { Health = 100f });

// Render Thread (inside OnRender):
GameDataSnapshot snapshot = pipeline.GetLatest();
```

#### Value Types (`StructSnapshotPipeline<T>`)
Best for high-frequency primitives, transforms, and matrices (e.g. 1,000–10,000 Hz physics ticks). Uses a **triple-buffered atomic slot exchange** (`_slot0`, `_slot1`, `_slot2`) with flag masking, guaranteeing **zero boxing** and **zero torn reads**:

```csharp
public struct TransformSnapshot
{
    public float X, Y, Z;
    public float Pitch, Yaw, Roll;
}

var structPipeline = new StructSnapshotPipeline<TransformSnapshot>();

// Main Thread (runs up to 10,000 Hz with zero allocations):
structPipeline.Publish(new TransformSnapshot { X = 10f, Y = 20f, Z = 30f });

// Render Thread (guaranteed atomic read, zero torn struct fields):
TransformSnapshot transform = structPipeline.GetLatest();
```

---

### 7. Main-Thread Dispatching (`UnityMainThreadDispatcher`)

Allows ImGui button clicks or background tasks to safely dispatch execution to the Unity main thread:

```csharp
// Asynchronous non-blocking dispatch (RECOMMENDED from ImGui render loop):
UnityMainThreadDispatcher.Enqueue(() =>
{
    GameObject.Find("Player")?.GetComponent<AudioSource>()?.Play();
});
```

> [!CAUTION]
> **CRITICAL DEADLOCK RULE**:
> Never call synchronous blocking dispatch methods (`InvokeOnMainThread`) from within the ImGui render loop (`OnRender` or `Present` thread). Unity's main thread frequently waits on the render thread to present before beginning the next frame; blocking the render thread while waiting for the main thread produces an unrecoverable engine pipeline deadlock. All render-to-main communication must use asynchronous non-blocking `Enqueue(Action)`.

---

### 8. Real-Time Diagnostics (`GraphicsTelemetry`)

Query active DirectX swapchain details, display resolutions, and render statistics without querying DirectX COM interfaces directly:

```csharp
string backend = GraphicsTelemetry.BackendName;      // "Direct3D 11" or "Direct3D 12"
string format = GraphicsTelemetry.BackBufferFormat;   // e.g. "R10G10B10A2_UNorm"
bool isHdr = GraphicsTelemetry.IsHdr;                // true if HDR10 / scRGB
float refreshHz = GraphicsTelemetry.RefreshRateHz;   // e.g. 144.0 Hz
int vtxCount = GraphicsTelemetry.TotalVertices;       // ImGui vertices rendered this frame
int idxCount = GraphicsTelemetry.TotalIndices;        // ImGui indices rendered this frame
```

---

### 9. Legacy Render Delegate (`Render`)

Third-party plugins can also subscribe directly to the static `Render` event:

```csharp
DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render += MyRenderCallback;

private static void MyRenderCallback()
{
    if (DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible)
    {
        ImGuiNET.ImGui.ShowDemoWindow();
    }
}
```

---

## Input Blocking Architecture (4-Layer Defense)

In modern Unity IL2CPP games, standard Win32 `WndProc` hooking alone is fundamentally incapable of preventing 3D mouse look (camera rotation) and gameplay clicks. Unity's C++ engine (`UnityPlayer.dll`) drains high-frequency mouse deltas directly from the OS kernel queue via Win32 `GetRawInputBuffer()` *before* messages reach the window procedure, and the C++ compiler inlines managed `UnityEngine.Input` wrappers into call sites inside `GameAssembly.dll`.

`DearImGuiInjectionPlus` solves this with a **synchronized 4-layer defense pipeline**:

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

### Layer 1: Win32 Subclassing & Message Swallowing (`Comctl32`)
- Uses `Comctl32.SetWindowSubclass` and `RemoveWindowSubclass` marshaled strictly to the window-owning thread. Coexists harmoniously with other injected tools and avoids breaking `CallWindowProc` chains.
- Raw input messages (`WM_INPUT`, `0x00FF`) are redirected to `DefWindowProcW`, preventing kernel raw input streams from reaching the engine.
- Swallows keyboard (`WM_KEYDOWN`, `WM_KEYUP`, `WM_SYSKEYDOWN`, `WM_SYSKEYUP`) and mouse (`WM_LBUTTONDOWN`, `WM_RBUTTONDOWN`, `WM_MBUTTONDOWN`, `WM_MOUSEWHEEL`) messages according to `BlockAllInputsWhenOpen` and ImGui's `WantCaptureMouse` / `WantCaptureKeyboard` flags.
- **Fullscreen Whitelist**: `Alt+Enter` (`VK_RETURN` with `WM_SYSKEYDOWN`) is explicitly passed directly to `DefSubclassProc` to ensure fullscreen transitions never hang.
- **Unicode & IME Integrity**: Preserves `IsWindowUnicode(hwnd)`. Routes `WM_CHAR`, `WM_IME_CHAR`, and `WM_UNICHAR` (including UTF-16 emoji surrogate pairs `0x10000..0x10FFFF`) to `io.AddInputCharacterUTF16`, routing IME system messages to `DefWindowProcW` so OS IME composition candidates display accurately.

### Layer 2: Direct `UnityPlayer.dll` Native ICall Hooks & Stripping-Resilient HarmonyX Detours
- **C++ `/O2` Inlining Bypass**: In IL2CPP release builds, methods like `Input.GetKey(KeyCode)` or `Input.mousePosition` are 1-line wrappers around internal methods `GetKeyInt` and `get_mousePosition_Injected`. The IL2CPP C++ compiler inlines the outer wrapper at call sites across `GameAssembly.dll` (leaving `CallerCount = 0`). Standard Harmony detours on `GameAssembly.dll` wrappers are completely bypassed because the game invokes the native export directly!
- The framework resolves internal ICall addresses via `IL2CPP.il2cpp_resolve_icall` and installs native detours using `Il2CppInteropRuntime.Instance.DetourProvider.Create(ptr, detour)`:
  - Hooks `GetKeyInt`, `GetKeyDownInt`, `GetKeyUpInt`, `GetMouseButton`, `GetMouseButtonDown`, `GetMouseButtonUp`, `get_anyKey`, `get_anyKeyDown`.
  - Hooks `get_mousePosition_Injected`, `get_mouseScrollDelta_Injected`, `get_mousePositionDelta_Injected`.
  - Hooks legacy Unity 2019–2022.1 `GetAxis` / `GetButton` ICalls.
  - Hooks Unity 2022.2–2023.2 `InputUnsafeUtility` `*__Unmanaged` ICalls.
  - Hooks Unity 2023.3 and Unity 6 `InputUnsafeUtility` `*_Injected` ICalls.
  - Hooks `Cursor.set_lockState` and `Cursor.set_visible` to prevent the game from locking the cursor while ImGui is active.
- **Stripping Resilience**: Before installing managed Harmony fallbacks, `ApplyHarmonyPatchesSafely` queries `Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(targetMethod)`. If a method has been stripped by `UnityLinker` (missing `NativeMethodInfoPtr_*`), it is safely skipped rather than aborting remaining patches or throwing exceptions.

### Layer 3: New Input System Buffer Hook (`NativeInputSystem.NotifyUpdate`)
- For games utilizing `Unity.InputSystem`, the framework hooks `UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate(NativeInputUpdateType, IntPtr eventBuffer)`. This method is marked `[RequiredByNativeCode]` in the Unity engine and is **never stripped** by `UnityLinker`.
- **Full Blocking Mode**: Zeroes `eventCount` (offset 8) and `sizeInBytes` (offset 12) directly inside the unmanaged `NativeInputEventBuffer` in native memory. Because `NotifyUpdate` still executes with `eventCount = 0`, `Unity.InputSystem` drops all hardware events and resets `Mouse.current.delta` to `(0, 0)`, completely eliminating 3D camera rotation and weapon fire.
- **Selective Passthrough Mode**: Inspects raw device IDs and FourCC event formats (`'MOUS'`, `'POIN'`, `'TOUC'`, `'KEYB'`) to compact the unmanaged buffer in place, dropping mouse inputs while allowing keyboard movement through.
- **Device Tracking Safety**: Disabled devices are tracked by unmanaged `Il2CppObjectBase.Pointer` (`IntPtr`) to prevent object identity churn and GC finalizer crashes.

### Layer 4: uGUI Canvas Shielding (`EventSystemBlocker`)
- Installs a Harmony prefix patch on `UnityEngine.EventSystems.EventSystem.Update`.
- Suppresses canvas raycasting and event updates whenever Dear ImGui is visible and capturing mouse input, preventing clicks on ImGui windows from accidentally activating underlying game canvas buttons.

### Stuck-Key & 3D Camera Drift Prevention
1. **Passthrough Key Tracking (`AUD-09`)**: When operating in selective passthrough mode (`BlockAllInputsWhenOpen = false`), keys and mouse buttons pressed while interacting with the game are recorded in `_downKeys` via `InputManager.SetKeyDown(vk)`.
2. **Synthesized Release Delivery**: When transitioning from passthrough to blocked mode, or when focusing an `ImGui.InputText`, `InputManager.FlushDepressedKeysOnCaptureTransition()` synthesizes release events via `PostMessageW(WM_KEYUP)` while setting `_pendingKeyReleaseFlushFrames = 2` and keeping `_downKeys` active. This ensures the window subclass and `NewInputSystemBlocker` forward the synthesized releases to Unity instead of swallowing them.
3. **Engine Axis Accumulator Reset**: `DearImGuiInjectionPlusRunner.Update()` executes `UnityInputBlocker.TryResetInputAxes()` (`Input.ResetInputAxes()`) and `InputSystem.ResetDevice()` on the main thread during capture transitions, immediately zeroing internal engine axis accumulators.

---

## Threading Model & Execution Architecture

### Thread Affinity & Safety Boundaries

| Execution Context | Thread Identity | Allowed Operations | Strictly Prohibited Operations |
| :--- | :--- | :--- | :--- |
| **Unity Main Thread** | Unity Scripting Thread (`ManagedThreadId == MainThreadId`) | `UnityEngine.Object` APIs, GameObjects, Transforms, physics queries, scene loading, Unity `Cursor.*`, publishing to `StateSnapshotPipeline.Publish()`, window subclassing / unsubclassing via `Comctl32`. | Direct ImGui rendering calls (ImGui is context-bound to the render thread). |
| **Render / Present Thread** | Graphics Device Present Thread (invoked by DXGI swapchain `Present`) | `ImGui.NewFrame()`, `ImGui.Render()`, `IImGuiWindow.OnRender()`, DirectX device draw calls, reading from `StateSnapshotPipeline.GetLatest()`. | Calling ANY `UnityEngine.Object` methods, instantiating GameObjects, or calling blocking `UnityMainThreadDispatcher.InvokeOnMainThread` (causes pipeline deadlock). |
| **Win32 Message Thread** | OS UI Message Pump | `InputManager.SubclassHandler`, mouse cursor clipping, raw input routing, pushing input events into `ConcurrentQueue<InputEvent>`. | Long-running tasks, blocking waits (stalls game message loop), direct `ImGui` or `ImGuiIO` mutations. |
| **IL2CPP Domain Context** | Native Interop Domain | Managed reflection, P/Invoke, `ClassInjector` type queries, native ICall detours. | Invoking IL2CPP object methods from unattached unmanaged native threads without `il2cpp_thread_attach`. |

### Quiescent Teardown & `FrameGate`

The destructive `Process.GetCurrentProcess().Kill()` hack has been completely eliminated. Teardown executes an **inverted quiescence sequence** in `DearImGuiInjectionPlusBaseUnityPlugin.Shutdown`:

```
[1] BasePlugin.Unload() / DearImGuiInjectionPlusRunner.OnApplicationQuit()
     │
[2] Set RendererFinder.IsShuttingDown = true
     │
[3] Unblock New Input System devices, dispose native ICall hooks, and unpatch Harmony
     │
[4] FrameGate.CloseAndDrain(1000) (Wait up to 1000 ms for in-flight Present render frames to finish)
     │
[5] Deactivate native DXGI hooks via Reloaded.Hooks.Disable()
     │
[6] Remove window subclass via Comctl32.RemoveWindowSubclass on window thread
     │
[7] Flush GPU pipelines: _deviceContext.Flush() (D3D11) / WaitForGpuIdle() fence wait (D3D12)
     │
[8] Destroy GPU resources (vertex/index buffers, PSOs, descriptor heaps, font textures)
     │
[9] Save DearImGuiInjectionPlus_imgui.ini, destroy ImGui context (ImGui.DestroyContext), free unmanaged strings
     │
[10] Destroy runner GameObject; process exits cleanly with code 0
```

- **`FrameGate` Invariant**: `FrameGate.TryEnter()` guards `Present` hook execution. When shutdown initiates, `FrameGate.CloseAndDrain(1000)` atomizes the state, blocks new frames from entering, and waits for all active render calls to complete. If the timeout expires, resource destruction is intentionally skipped to prevent use-after-free crashes at process exit.

### COM Reference Balance & SharpDX Lifetime Safety
- **Borrowed COM Reference Promotion (`RR-01`)**: Wrapping an unmanaged COM pointer from Unity (such as swapchain pointers passed into `Present` or fallback command queues) in a SharpDX object takes ownership. SharpDX's `Dispose()` calls `Release()`. The framework explicitly calls `Marshal.AddRef` before wrapping borrowed pointers, balancing against later disposal.
- **Suppressing Finalizers**: SharpDX objects created over borrowed or long-lived pointers call `GC.SuppressFinalize` to prevent non-deterministic finalizer thread execution from racing with active device teardown.
- **Deterministic State Backup Disposal (`MEM-01`, `RR-04`)**: In D3D11 mode, `BackupRenderState` captures pipeline state before binding the overlay's transient RTV and returns an `IDisposable` `StateBackup` singleton. On disposal, all queried COM wrappers are disposed, and `Array.Clear` resets scissor and viewport count arrays, allowing `IDXGISwapChain::ResizeBuffers` to succeed unconditionally.
- **Unmanaged ANSI String Tracking (`SEC-02`, `MEM-08`)**: Native strings such as `ImGuiIO.IniFilename`, `BackendPlatformName`, and `BackendRendererName` are allocated in unmanaged memory via `Marshal.StringToHGlobalAnsi`. Pointers are freed via `Marshal.FreeHGlobal` strictly *after* `ImGui.DestroyContext()` so that `ImGui.SaveIniSettingsToDisk` does not read freed memory.

---

## Diagnostics & Test Bench (`TestPlugin`)

The repository includes a production-grade diagnostic and stress-testing plugin in `TestPlugin/` implementing runtime verification scenarios `TM-01` through `TM-14`.

### Diagnostic Dashboard Overview

When running with `TestPlugin.dll` installed, a **Test Suite Master Panel** is accessible from the top menu bar (`Test Suite` -> `Master Dashboard`):

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

1. **`GraphicsTestPanel`**: Displays live telemetry, triggers dynamic vertex/index buffer bursts (>100,000 vertices/indices) to test buffer growth, validates `ImDrawCallback_ResetRenderState` (`-1`) callback recovery, and monitors HDR/SDR color space adaptation.
2. **`ConcurrencyTestPanel`**: Launches a background worker updating `StructSnapshotPipeline<T>` at up to 10,000 Hz, asserting zero torn reads across struct fields while torturing `UnityMainThreadDispatcher`.
3. **`InputTestPanel`**: Tests multilingual international text entry (Polish, German umlauts, French accents, Cyrillic, Japanese, Chinese, Emoji `WM_UNICHAR`), monitors `IsWindowUnicode(hwnd)`, and tracks held keys and capture transitions.
4. **`FaultRecoveryTestPanel`**: Injects deliberate crashes (unclosed `Begin()`, `PushID()`, `BeginChild()`, and unclosed `BeginMenu()`) to certify `RestoreImGuiStack` recovery, tests rapid tab registration/unregistration churn, and verifies shutdown timestamp writing.
5. **`MemoryBenchmarkTestPanel`**: Real-time process memory metrics via Win32 PSAPI (`WorkingSetSize`, `PrivateUsage`, `PeakWorkingSetSize`), rolling FPS histograms, and managed GC Gen0/Gen1/Gen2 heap monitors.
6. **`Il2CppTestPanel`**: Live audit of hooked native ICalls (`GetKeyInt`, `GetMouseButton`, `NotifyUpdate`, `InputUnsafeUtility`) and managed Harmony detours, interactive 3D mouse look and click suppression verification, and live passthrough transition testing.

### Runtime Verification Matrix (TM-01 through TM-14)

| Test ID | Category | Target API | Scenario & Verification Summary |
| :--- | :--- | :---: | :--- |
| **TM-01** | Bootstrap & Hook Discovery | D3D11, D3D12 | Cold start under non-ASCII directory paths (`LoadLibraryW`), `ClassInjector` pre-registration, `DXGI_PRESENT_TEST` filtering, unsupported graphics backend graceful abort. |
| **TM-02** | SwapChain Resize & Fullscreen | D3D11, D3D12 | 20 rapid `Alt+Enter` toggles, continuous 15s window border drag-resize, 1080p↔4K switching, window minimize/restore (`DisplaySize == 0`). Zero `DXGI_ERROR_INVALID_CALL`. |
| **TM-03** | SDR, sRGB & HDR Backbuffers | D3D11, D3D12 | `R8G8B8A8_UNorm`, `B8G8R8A8_UNorm_SRgb`, `R10G10B10A2_UNorm` (HDR10), `R16G16B16A16_Float` (scRGB) PSO dynamic adaptation and gamma correction without color washout. |
| **TM-04** | Concurrency & High-Rate Input | D3D11, D3D12 | 1000–8000 Hz gaming mouse movement + rapid typing + 200+ FPS uncapped `Present`. `StructSnapshotPipeline` 10,000 Hz multi-field stress with zero torn reads and zero deadlocks. |
| **TM-05** | Input Swallowing & Stuck Keys | D3D11, D3D12 | Held `W`+`Shift`+`RMB` across overlay toggles and live passthrough toggles, `Insert` auto-repeat suppression, 3D mouse look/click swallowing, and uGUI raycast penetration shielding. |
| **TM-06** | Unicode, Dead Keys & CJK IME | D3D11, D3D12 | International non-ASCII input (Cyrillic, Polish, German umlauts, Emoji `WM_UNICHAR`, Japanese/Chinese IME) across ImGui widgets and native Unity text fields. |
| **TM-07** | Consumer Fault & Stack Recovery | D3D11, D3D12 | Deliberate exception mid-`ImGui.Begin()`, unclosed `BeginMenu()`, tab churn (50 iterations), and 5-error / 5-second consecutive error circuit breaker trip. |
| **TM-08** | Dynamic Geometry & Device Loss | D3D11, D3D12 | Dynamic vertex/index buffer growth (>100k vertices), simulated TDR / device loss (`DeviceRemovedReason.Failure`), automatic resource invalidation and re-initialization. |
| **TM-09** | Steady-State Memory & Soak | D3D11, D3D12 | 60-minute continuous soak at 120+ FPS monitoring VRAM, DXGI live objects, native heap, zero-alloc scene tracking, and Gen0/Gen2 GC collection frequencies. |
| **TM-10** | Quiescent Teardown & Exit | D3D11, D3D12 | Clean `Application.Quit()` and `Alt+F4` under heavy UI load; inverted shutdown sequence execution, `imgui.ini` save, and crash-free process exit with code `0`. |
| **TM-11** | IL2CPP Patch & Stripping Resilience | D3D11, D3D12 | Per-method HarmonyX isolation, `NativeMethodInfoPtr` validation, C++-inlined `GetKeyInt`/`_Injected` detours, and survival under stripped modules. |
| **TM-12** | 3D Gameplay Camera Look & `NotifyUpdate` | D3D11, D3D12 | 3D camera look (`GetRawInputBuffer()`, `Mouse.current.delta`, `Input.GetAxis`) and click suppression via unmanaged `NativeInputSystem.NotifyUpdate` buffer compaction. |
| **TM-13** | Live Passthrough Toggle & Key Flush | D3D11, D3D12 | Holding `W`+`Shift`+`LMB` in passthrough (`BlockAllInputsWhenOpen = false`) then toggling to blocked; verifying immediate cutoff and zero stuck keys. |
| **TM-14** | IL2CPP Interop & `ClassInjector` Stress | D3D11, D3D12 | `ClassInjector` idempotency, `Il2CppObjectBase.Pointer` device identity under forced Gen2 GC, main-thread proxy enforcement, and scene transition cache. |

---

## Building from Source

### Prerequisites
- **.NET 6.0 SDK** (v6.0.400 or newer)
- **Visual Studio 2022** (v17.4+) or **JetBrains Rider** with .NET desktop build tools
- **Git**

### NuGet Package Sources

The repository relies on BepInEx 6 IL2CPP NuGet feeds. Ensure your `nuget.config` contains:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="BepInEx" value="https://nuget.bepinex.dev/v3/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="Samboy" value="https://nuget.samboy.dev/v3/index.json" />
  </packageSources>
</configuration>
```

### Build Commands

Clone the repository and build using the .NET CLI:

```powershell
# Clone the repository
git clone https://github.com/Klax-xon/DearImGuiInjectionPlus.git
cd DearImGuiInjectionPlus

# Restore NuGet packages
dotnet restore

# Build the Release configuration
dotnet build -c Release
```

### Output Structure

The build output is unified directly into the repository root `bin/` directory:

```
bin/
├── DearImGuiInjectionPlus.dll
├── RendererFinder.dll
├── TestPlugin.dll
├── ImGui.NET.dll
├── cimgui.dll
├── Reloaded.Hooks.dll
├── SharpDX.dll
├── SharpDX.DXGI.dll
├── SharpDX.Direct3D11.dll
├── SharpDX.Direct3D12.dll
└── Assets/
    ├── Fonts/
    │   └── Comfortaa-Medium.ttf
    └── Shaders/
        ├── imgui-vertex.hlsl.bytes
        └── imgui-frag.hlsl.bytes
```

---

## Troubleshooting & FAQ

### Q1: The game launches, but pressing `Insert` does not show the overlay.
- Check `BepInEx/LogOutput.log` to confirm the graphics backend. If the game is running on Vulkan or OpenGL, `DearImGuiInjectionPlus` will abort initialization. You can force DirectX by adding `-force-d3d11` or `-force-d3d12` to the game's launch options in Steam.
- Ensure `cimgui.dll` is present in the `BepInEx/plugins/DearImGuiInjectionPlus/` folder. The loader automatically preloads native libraries using `Kernel32.LoadLibraryW`.

### Q2: 3D camera look still rotates when clicking on ImGui windows in an IL2CPP game.
- Ensure you are running the v1.0.0 release. In this release, `UnityPlayer.dll` native ICalls (`GetKeyInt`, `get_mousePosition_Injected`) and `UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate` are hooked natively, which zeroes raw mouse deltas before Unity's C++ camera scripts receive them.
- If using `BlockAllInputsWhenOpen = false` (passthrough mode), camera rotation is allowed when clicking on the 3D world outside of ImGui windows, but is strictly blocked whenever hovering or clicking inside an ImGui window (`WantCaptureMouse == true`).

### Q3: Why does my game deadlock when invoking methods on the main thread?
- You are likely calling `UnityMainThreadDispatcher.InvokeOnMainThread(...)` synchronously from inside `IImGuiWindow.OnRender()` or a DirectX `Present` callback. Unity's main thread frequently waits for the render thread to present before beginning the next frame; blocking the render thread while waiting for the main thread produces an engine deadlock.
- Always use asynchronous `UnityMainThreadDispatcher.Enqueue(() => ...)` from the ImGui render loop.

### Q4: How do I handle scene changes without creating garbage in IL2CPP?
- In IL2CPP, calling `SceneManager.GetActiveScene().name` allocates a new managed string every frame via `IL2CPP.Il2CppStringToManaged`. Instead, cache `activeScene.handle` and only query `.name` when the handle integer changes, or poll scene names at a low frequency (e.g. 1 Hz).

---

## Credits & Attribution

`DearImGuiInjectionPlus` is built upon the incredible work of the open-source community:

- **[Dear ImGui](https://github.com/ocornut/imgui)** by Omar Cornut ([@ocornut](https://github.com/ocornut)) — The standard for immediate-mode graphical interfaces in games.
- **[ImGui.NET](https://github.com/ImGuiNET/ImGui.NET)** by Eric Mellino ([@mellinoe](https://github.com/mellinoe)) — High-performance .NET wrapper for Dear ImGui.
- **[cimgui](https://github.com/cimgui/cimgui)** — Thin C-compatible wrapper auto-generated for Dear ImGui.
- **[BepInEx Team](https://github.com/BepInEx/BepInEx)** — The modding framework and runtime interop suite powering modern Unity IL2CPP modding.
- **[Il2CppInterop](https://github.com/BepInEx/Il2CppInterop)** — IL2CPP bridge and type generation for CoreCLR .NET 6.
- **[Reloaded.Hooks](https://github.com/Reloaded-Project/Reloaded.Hooks)** by Sewer56 — High-performance x86/x64 function hooking library.
- **[HarmonyX](https://github.com/BepInEx/HarmonyX)** — Non-destructive runtime method detouring for IL2CPP.
- **[SharpDX](https://github.com/sharpdx/SharpDX)** by Alexandre Mutel ([@xoofx](https://github.com/xoofx)) — Direct3D 11, Direct3D 12, and DXGI managed interop.

---

## License

This project is licensed under the **[MIT License](LICENSE)**. You are free to use, modify, and distribute this software in personal, educational, or commercial projects.
