using System;
using System.Collections.Generic;
using System.Numerics;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Template.State;
using DearImGuiInjectionPlus.Template.Tabs;
using DearImGuiInjectionPlus.Template.Utils;
using ImGuiNET;
using UnityEngine;

namespace DearImGuiInjectionPlus.Template;

/// <summary>
/// Primary Base GUI Window implementation.
/// Redesigned with a sleek game-cheat / trainer-style two-pane layout:
/// - Left Sidebar: Logo/title, full-width vertical tab buttons with active theme accent, and status footer.
/// - Right Content Area: Active tab panel rendering.
/// - Unified visibility: Window visibility is directly synchronized with DearImGuiInjectionPlus.IsCursorVisible.
/// </summary>
public class BaseGuiWindow : IImGuiWindow
{
    public string Id => "dearimgui_base_gui_window";
    public string Title => "DearImGuiInjectionPlus Base Menu";

    /// <summary>
    /// Synchronized directly with the framework's cursor visibility and input capture state.
    /// When open, cursor is visible and input is intercepted.
    /// When closed, cursor is hidden and all game input is immediately restored.
    /// </summary>
    public bool IsOpen
    {
        get => global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible;
        set => global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.SetCursorVisibility(value);
    }

    private readonly StructSnapshotPipeline<GameTelemetrySnapshot> _pipeline;
    private readonly PlayerControlState _controlState;
    private readonly GuiConsole _console;

    private readonly List<ITemplateTab> _tabs = new();
    private int _selectedTabIndex = 0;

    public BaseGuiWindow(
        StructSnapshotPipeline<GameTelemetrySnapshot> pipeline,
        PlayerControlState controlState,
        GuiConsole console,
        Action<GuiTheme> onThemeChanged,
        Action<float> onScaleChanged)
    {
        _pipeline = pipeline;
        _controlState = controlState;
        _console = console;

        // Register default trainer tabs
        _tabs.Add(new TelemetryTab(_pipeline));
        _tabs.Add(new ControlsTab(_controlState, _console));
        _tabs.Add(new ConsoleTab(_console));
        _tabs.Add(new SettingsTab(_console, onThemeChanged, onScaleChanged));
    }

    /// <summary>
    /// Registers an additional custom tab into the Base GUI window sidebar.
    /// </summary>
    public void AddTab(ITemplateTab tab)
    {
        if (tab != null && !_tabs.Contains(tab))
        {
            _tabs.Add(tab);
        }
    }

    public void OnRender()
    {
        if (!IsOpen)
            return;

        // Check toggle hotkey while window has ImGui input focus
        if (CheckToggleHotkeyPressed())
        {
            IsOpen = false;
            return;
        }

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(820, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(580, 380), new System.Numerics.Vector2(3840, 2160));

        // Strip MenuBar, NoCollapse to remove triangle, NoScrollbar to delegate scrolling to panes
        const ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar;

        // Begin without passing ref open - strips the desktop titlebar 'X' button
        if (ImGui.Begin(Title, windowFlags))
        {
            DrawSidebarPane();
            ImGui.SameLine();
            DrawContentPane();
        }

        ImGui.End();
    }

    private void DrawSidebarPane()
    {
        // Measure all tab titles to dynamically size the sidebar without clipping
        float maxTextWidth = 0f;
        for (int i = 0; i < _tabs.Count; i++)
        {
            float textWidth = ImGui.CalcTextSize(_tabs[i].TabName).X;
            if (textWidth > maxTextWidth)
            {
                maxTextWidth = textWidth;
            }
        }

        // Dynamic sidebar width with comfortable padding (36px for frame padding + margins), clamped between 160px and 240px
        float sidebarWidth = Math.Clamp(maxTextWidth + 36f, 160f, 240f);
        var style = ImGui.GetStyle();

        // Left sidebar child pane with border enabled
        ImGui.BeginChild("##SidebarPane", new System.Numerics.Vector2(sidebarWidth, 0), true, ImGuiWindowFlags.NoScrollbar);

        // 1. Stylized Logo / Title Header
        ImGui.Spacing();
        var accentColor = style.Colors[(int)ImGuiCol.HeaderActive];
        ImGui.PushStyleColor(ImGuiCol.Text, accentColor);
        ImGui.TextUnformatted("  [ DII+ MENU ]");
        ImGui.PopStyleColor();

        ImGui.TextDisabled("   Trainer Overlay");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 2. Vertical Stack of Tab Buttons
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            bool isSelected = (i == _selectedTabIndex);

            if (isSelected)
            {
                // Active tab highlighted with theme's active accent color
                var activeBg = style.Colors[(int)ImGuiCol.HeaderActive];
                var activeHover = style.Colors[(int)ImGuiCol.HeaderHovered];
                ImGui.PushStyleColor(ImGuiCol.Button, activeBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, activeHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeBg);
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 1f, 1f));
            }
            else
            {
                // Inactive tabs with clean subtle background
                var inactiveBg = new System.Numerics.Vector4(style.Colors[(int)ImGuiCol.FrameBg].X, style.Colors[(int)ImGuiCol.FrameBg].Y, style.Colors[(int)ImGuiCol.FrameBg].Z, 0.40f);
                ImGui.PushStyleColor(ImGuiCol.Button, inactiveBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, style.Colors[(int)ImGuiCol.FrameBgHovered]);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, style.Colors[(int)ImGuiCol.FrameBgActive]);
                ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
            }

            if (ImGui.Button(tab.TabName, new System.Numerics.Vector2(-1, 36)))
            {
                _selectedTabIndex = i;
            }

            ImGui.PopStyleColor(4);
            ImGui.Spacing();
        }

        // 3. Sidebar Footer (docked at the bottom)
        const float footerHeight = 56f;
        float availY = ImGui.GetContentRegionAvail().Y;
        if (availY > footerHeight)
        {
            ImGui.Dummy(new System.Numerics.Vector2(0, availY - footerHeight));
        }

        ImGui.Separator();
        var snapshot = _pipeline.GetLatest();
        ImGui.TextColored(new System.Numerics.Vector4(0.25f, 0.90f, 0.45f, 1f), $"{snapshot.Fps:F0} FPS");
        ImGui.SameLine();
        ImGui.TextDisabled($"({snapshot.DeltaTime * 1000f:F1}ms)");
        ImGui.TextDisabled("[Insert] to Toggle");

        ImGui.EndChild();
    }

    private void DrawContentPane()
    {
        // Right content child pane taking remaining width
        ImGui.BeginChild("##ContentArea", new System.Numerics.Vector2(0, 0), false);

        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            _tabs[_selectedTabIndex].Draw();
        }

        ImGui.EndChild();
    }

    private static bool CheckToggleHotkeyPressed()
    {
        // Standardized strictly on Insert key for unified toggle behavior
        return ImGui.IsKeyPressed(ImGuiKey.Insert, false);
    }
}
