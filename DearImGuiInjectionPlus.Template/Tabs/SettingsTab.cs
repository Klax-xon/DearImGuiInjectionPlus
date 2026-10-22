using System;
using System.Numerics;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Template.Utils;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;
using UnityEngine;

namespace DearImGuiInjectionPlus.Template.Tabs;

/// <summary>
/// Settings & Preferences Tab.
/// Provides runtime UI scale and DPI adjustments, dynamic visual theme switching,
/// input confinement / hotkey behavior settings, and framework telemetry.
/// </summary>
public class SettingsTab : ITemplateTab
{
    public string TabName => "Settings & Preferences";

    private readonly GuiConsole _console;
    private readonly Action<GuiTheme> _onThemeChanged;
    private readonly Action<float> _onScaleChanged;

    public SettingsTab(GuiConsole console, Action<GuiTheme> onThemeChanged, Action<float> onScaleChanged)
    {
        _console = console;
        _onThemeChanged = onThemeChanged;
        _onScaleChanged = onScaleChanged;
    }

    public void Draw()
    {
        DrawScaleSection();
        ImGui.Separator();

        DrawThemeSection();
        ImGui.Separator();

        DrawInputAndHotkeySection();
        ImGui.Separator();

        DrawFrameworkDiagnosticsSection();
    }

    private void DrawScaleSection()
    {
        if (ImGui.CollapsingHeader("Display Scaling & High-DPI Adjustments", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var io = ImGui.GetIO();
            float currentScale = io.FontGlobalScale;

            ImGui.TextDisabled("Adjust the global font and interface scale dynamically for 1080p, 1440p, or 4K displays:");

            if (ImGui.SliderFloat("UI / Font Scale", ref currentScale, 0.75f, 2.50f, "%.2fx"))
            {
                io.FontGlobalScale = currentScale;
                _onScaleChanged?.Invoke(currentScale);
            }

            ImGui.TextUnformatted("Quick Resolution Presets:");
            ImGui.SameLine();

            if (ImGui.Button("1080p (1.0x)"))
            {
                io.FontGlobalScale = 1.0f;
                _onScaleChanged?.Invoke(1.0f);
                _console.LogInfo("UI Scale set to 1.0x (1080p standard)");
            }
            ImGui.SameLine();

            if (ImGui.Button("1440p (1.25x)"))
            {
                io.FontGlobalScale = 1.25f;
                _onScaleChanged?.Invoke(1.25f);
                _console.LogInfo("UI Scale set to 1.25x (1440p standard)");
            }
            ImGui.SameLine();

            if (ImGui.Button("4K UHD (2.0x)"))
            {
                io.FontGlobalScale = 2.00f;
                _onScaleChanged?.Invoke(2.00f);
                _console.LogInfo("UI Scale set to 2.0x (4K UHD standard)");
            }
        }
    }

    private void DrawThemeSection()
    {
        if (ImGui.CollapsingHeader("Dynamic Visual Theme", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Select between 2 fully custom designed themes and pure vanilla Dear ImGui classic:");

            int current = (int)ThemeManager.CurrentTheme;
            if (ImGui.Combo("Active Theme", ref current, ThemeManager.ThemeNames, ThemeManager.ThemeNames.Length))
            {
                var newTheme = (GuiTheme)current;
                ThemeManager.ApplyTheme(newTheme);
                _onThemeChanged?.Invoke(newTheme);
                _console.LogInfo($"Theme switched to: {ThemeManager.ThemeNames[current]}");
            }

            ImGui.Spacing();

            // Quick Theme Overview
            switch (ThemeManager.CurrentTheme)
            {
                case GuiTheme.ModernDark:
                    ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.65f, 1.00f, 1.0f), "Theme: Modern Sleek Dark");
                    ImGui.BulletText("Deep slate/charcoal backgrounds with subtle slate borders");
                    ImGui.BulletText("Ergonomic 7px window and 4px control corner rounding");
                    ImGui.BulletText("Azure accents for active controls, sliders, and tabs");
                    break;

                case GuiTheme.HighContrastAccent:
                    ImGui.TextColored(new System.Numerics.Vector4(0.00f, 1.00f, 0.62f, 1.0f), "Theme: High-Contrast Emerald Accent");
                    ImGui.BulletText("Obsidian deep void backgrounds with glowing emerald highlights");
                    ImGui.BulletText("Sharp 4px geometry with 1.5px high-contrast borders");
                    ImGui.BulletText("Maximum visibility against complex 3D game scenes");
                    break;

                case GuiTheme.ClassicImGui:
                    ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.9f, 0.9f, 1.0f), "Theme: Default ImGui Classic");
                    ImGui.BulletText("Original Omar Cornut / Dear ImGui classic styling");
                    ImGui.BulletText("Zero rounding, sharp rectangular borders, classic palette");
                    break;
            }
        }
    }

    private void DrawInputAndHotkeySection()
    {
        if (ImGui.CollapsingHeader("Input Confinement & Hotkey Preferences", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.LabelText("Window Toggle Hotkey", "Insert");
            ImGui.TextDisabled("Press Insert in-game to toggle this Base GUI menu on/off.");

            ImGui.Spacing();

            // Auto-Manage Cursor Toggle
            bool autoManage = BaseGuiPlugin.AutoManageCursorConfig?.Value ?? true;
            if (ImGui.Checkbox("Auto-Manage Cursor Visibility on Window Open/Close", ref autoManage))
            {
                if (BaseGuiPlugin.AutoManageCursorConfig != null)
                {
                    BaseGuiPlugin.AutoManageCursorConfig.Value = autoManage;
                }
                _console.LogInfo($"Auto-manage cursor set to: {autoManage}");
            }
            ImGui.TextDisabled("When enabled, closing the window releases the cursor and seamlessly unblocks game input.");

            // Block All Inputs Toggle
            bool blockAll = DearImGuiInjectionPlus.BlockAllInputsWhenOpen;
            if (ImGui.Checkbox("Block All Game Inputs When Menu Is Open", ref blockAll))
            {
                DearImGuiInjectionPlus.BlockAllInputsWhenOpen = blockAll;
                _console.LogInfo($"BlockAllInputsWhenOpen set to: {blockAll}");
            }
            ImGui.TextDisabled("When enabled, all mouse clicks, movements, and keystrokes are swallowed while the UI is open.");
        }
    }

    private static void DrawFrameworkDiagnosticsSection()
    {
        if (ImGui.CollapsingHeader("Framework Telemetry & Win32 Interop", ImGuiTreeNodeFlags.None))
        {
            IntPtr hwnd = global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.WindowHandle;
            bool isUnicode = hwnd != IntPtr.Zero && User32.IsWindowUnicode(hwnd);

            ImGui.LabelText("Subclassed HWND", $"0x{hwnd.ToInt64():X8}");
            ImGui.LabelText("Win32 IsWindowUnicode", isUnicode ? "TRUE (UTF-16 Native)" : "FALSE (ANSI)");
            ImGui.LabelText("Cursor Visible", global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible ? "TRUE" : "FALSE");
            ImGui.LabelText("WantCaptureMouse", global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.WantCaptureMouse ? "TRUE" : "FALSE");
            ImGui.LabelText("WantCaptureKeyboard", global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.WantCaptureKeyboard ? "TRUE" : "FALSE");
            ImGui.LabelText("Input Blocking Mode", global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.BlockAllInputsWhenOpen ? "BLOCK ALL WHEN OPEN" : "SELECTIVE CAPTURE");
        }
    }
}
