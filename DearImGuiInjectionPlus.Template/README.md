# DearImGuiInjectionPlus.Template (Base GUI Template)

Production-grade, modular "Base GUI" template plugin for building all-in-one in-game testing, inspection, and debugging tools powered by **DearImGuiInjectionPlus** under **BepInEx 6 IL2CPP (`net6.0`)**.

---

## Architecture Overview

```
DearImGuiInjectionPlus.Template/
├── BaseGuiPlugin.cs             # BepInEx 6 IL2CPP entry point, config binds, lifecycle teardown
├── BaseGuiRunner.cs             # IL2CPP MonoBehaviour runner (main-thread telemetry & hotkeys)
├── BaseGuiWindow.cs             # Primary IImGuiWindow implementation & modular tab routing
├── ThemeManager.cs              # Dynamic runtime styling (Modern Dark, High-Contrast Emerald, Classic)
├── State/
│   ├── GameTelemetrySnapshot.cs # Value-type struct for zero-allocation triple-buffered telemetry
│   └── PlayerControlState.cs    # Live game cheats, overrides, and parameters
├── Tabs/
│   ├── ITemplateTab.cs          # Pluggable tab interface
│   ├── TelemetryTab.cs          # Live telemetry readouts, status badges, and FPS sparkline
│   ├── ControlsTab.cs           # Action buttons, persistent toggles, sliders, and cheat console
│   ├── ConsoleTab.cs            # Categorized scrollable log console with filtering & clipboard copy
│   └── SettingsTab.cs           # UI / Font scale, theme switcher, hotkey & input preferences
└── Utils/
    └── GuiConsole.cs            # Thread-safe, bounded circular log buffer (zero steady-state allocs)
```

---

## Key Features

1. **Zero Steady-State Allocations**:
   - Uses `StructSnapshotPipeline<GameTelemetrySnapshot>` for lock-free, zero-allocation telemetry transfer from Unity's main thread to the DirectX ImGui render thread.
   - Pre-allocated buffers for text input (`byte[]`), plot history (`float[]`), and bounded ring-buffer logging (`ConsoleEntry[]`).
   - Pre-cached color vectors and string handles (scene sampling throttled to 1 Hz).

2. **Dynamic Visual Themes**:
   - **Modern Sleek Dark**: Deep charcoal/slate background (`#1A1F26`), ergonomic 7px window rounding, softened borders, and clean azure control accents.
   - **High-Contrast Emerald**: Ultra-dark obsidian void (`#0D1210`), glowing neon emerald (`#00E699`) highlights, and sharp 1.5px borders for maximum readability against 3D game geometry.
   - **Default ImGui Classic**: Pure vanilla Dear ImGui styling for developers who prefer the original look.

3. **Seamless Input Confinement & Unified Toggle**:
   - Synchronized toggle hotkey (`KeyCode.Insert` by default, aligned with the framework).
   - Menu visibility is directly bound to `DearImGuiInjectionPlus.IsCursorVisible`.
   - Closing the window restores cursor lock mode, cursor visibility, unblocks the New Input System, and cleans up synthesized mouse/keyboard inputs via `DearImGuiInjectionPlus.SetCursorVisibility(false)`.
   - Sleek game-trainer layout with a fixed left sidebar (`175px`) and dedicated right content region without generic desktop window borders, collapse triangles, or top menu bars.

---

## How-To Guides

### 1. How to Add a New Custom Tab

Implement `ITemplateTab` and register it with `BaseGuiWindow`:

```csharp
using DearImGuiInjectionPlus.Template.Tabs;
using ImGuiNET;

namespace DearImGuiInjectionPlus.Template.Tabs;

public class CustomInventoryTab : ITemplateTab
{
    public string TabName => "Inventory Inspector";

    public void Draw()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.65f, 1.0f, 1.0f), "Player Inventory Items:");
        ImGui.Separator();

        if (ImGui.Button("Grant 10x Health Potions"))
        {
            DearImGuiInjectionPlus.BepInEx.UnityMainThreadDispatcher.Enqueue(() =>
            {
                // Call your game's item addition API on the Unity main thread
            });
        }
    }
}
```

Then in `BaseGuiWindow.cs`, instantiate and add it:

```csharp
_tabs.Add(new CustomInventoryTab());
```

---

### 2. How to Add a New Action Button

To add an action button that executes game logic safely on the Unity main scripting thread:

```csharp
if (ImGui.Button("My Action Button", new System.Numerics.Vector2(160, 30)))
{
    DearImGuiInjectionPlus.BepInEx.UnityMainThreadDispatcher.Enqueue(() =>
    {
        // Safe access to UnityEngine.Object, GameObject, SceneManager, etc.
        var player = UnityEngine.GameObject.FindWithTag("Player");
        if (player != null)
        {
            // Perform game logic
        }
    });

    _console.LogInfo("My Action Button executed successfully.");
}
```
