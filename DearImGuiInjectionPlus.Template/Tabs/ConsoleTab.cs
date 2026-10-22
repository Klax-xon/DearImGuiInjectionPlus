using System.Numerics;
using DearImGuiInjectionPlus.Template.Utils;
using ImGuiNET;

namespace DearImGuiInjectionPlus.Template.Tabs;

/// <summary>
/// Logging & Output Console Tab.
/// Features a scrollable, auto-scrolling log terminal with categorized color coding,
/// active filter buttons, clipboard export, and zero steady-state rendering allocations.
/// </summary>
public class ConsoleTab : ITemplateTab
{
    public string TabName => "Logging & Console";

    private readonly GuiConsole _console;
    private LogCategory? _filter;

    // Pre-allocated static colors for zero GC allocations
    private static readonly Vector4 ColorInfo = new(0.45f, 0.75f, 1.00f, 1.00f);
    private static readonly Vector4 ColorWarning = new(1.00f, 0.78f, 0.25f, 1.00f);
    private static readonly Vector4 ColorError = new(1.00f, 0.35f, 0.35f, 1.00f);
    private static readonly Vector4 ColorCommand = new(0.25f, 1.00f, 0.55f, 1.00f);

    public ConsoleTab(GuiConsole console)
    {
        _console = console;
    }

    public void Draw()
    {
        DrawFilterBar();
        ImGui.Separator();

        DrawConsoleBox();
    }

    private void DrawFilterBar()
    {
        // Filter Buttons
        bool isAll = _filter == null;
        if (isAll) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.35f, 0.50f, 0.75f, 0.8f));
        if (ImGui.SmallButton("All")) _filter = null;
        if (isAll) ImGui.PopStyleColor();

        ImGui.SameLine();

        bool isInfo = _filter == LogCategory.Info;
        if (isInfo) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.40f, 0.65f, 0.8f));
        if (ImGui.SmallButton($"Info ({_console.TotalInfoCount})")) _filter = LogCategory.Info;
        if (isInfo) ImGui.PopStyleColor();

        ImGui.SameLine();

        bool isWarn = _filter == LogCategory.Warning;
        if (isWarn) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.40f, 0.15f, 0.8f));
        if (ImGui.SmallButton($"Warn ({_console.TotalWarningCount})")) _filter = LogCategory.Warning;
        if (isWarn) ImGui.PopStyleColor();

        ImGui.SameLine();

        bool isErr = _filter == LogCategory.Error;
        if (isErr) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.60f, 0.20f, 0.20f, 0.8f));
        if (ImGui.SmallButton($"Error ({_console.TotalErrorCount})")) _filter = LogCategory.Error;
        if (isErr) ImGui.PopStyleColor();

        ImGui.SameLine();

        bool isCmd = _filter == LogCategory.Command;
        if (isCmd) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.50f, 0.30f, 0.8f));
        if (ImGui.SmallButton($"Commands ({_console.TotalCommandCount})")) _filter = LogCategory.Command;
        if (isCmd) ImGui.PopStyleColor();

        ImGui.Spacing();

        // Row 2: Controls & Actions (Clean responsive row)
        bool autoScroll = _console.AutoScroll;
        if (ImGui.Checkbox("Auto-scroll", ref autoScroll))
        {
            _console.AutoScroll = autoScroll;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Clear"))
        {
            _console.Clear();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Copy All"))
        {
            string logText = _console.ExportToString();
            ImGui.SetClipboardText(logText);
            _console.LogInfo("All logs copied to system clipboard.");
        }
    }

    private void DrawConsoleBox()
    {
        float footerHeight = ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##ConsoleLogRegion", new Vector2(0, -footerHeight), true, ImGuiWindowFlags.HorizontalScrollbar);

        _console.ForEach(entry =>
        {
            // Timestamp
            ImGui.TextDisabled($"[{entry.Timestamp}]");
            ImGui.SameLine();

            // Colored Category tag
            Vector4 color = entry.Category switch
            {
                LogCategory.Info => ColorInfo,
                LogCategory.Warning => ColorWarning,
                LogCategory.Error => ColorError,
                LogCategory.Command => ColorCommand,
                _ => ColorInfo
            };

            string tag = entry.Category switch
            {
                LogCategory.Info => "[INFO]   ",
                LogCategory.Warning => "[WARN]   ",
                LogCategory.Error => "[ERROR]  ",
                LogCategory.Command => "[CMD]    ",
                _ => "[LOG]    "
            };

            ImGui.TextColored(color, tag);
            ImGui.SameLine();

            // Message text
            ImGui.TextUnformatted(entry.Message);
        }, _filter);

        if (_console.AutoScroll && _console.HasNewEntry)
        {
            ImGui.SetScrollHereY(1.0f);
            _console.HasNewEntry = false;
        }

        ImGui.EndChild();
    }
}
