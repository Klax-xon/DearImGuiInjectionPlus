using System.Numerics;
using DearImGuiInjectionPlus;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Unified Master Dashboard for DearImGui Test Suite.
/// Provides tabbed navigation across all validation categories (TM-01 through TM-10)
/// and controls the visibility of individual modular floating test windows.
/// </summary>
public class TestSuiteMasterPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_master";
    public string Title => "DearImGui Test Suite - Master Dashboard";
    public bool IsOpen { get; set; } = true;

    private readonly GraphicsTestPanel _graphicsPanel;
    private readonly ConcurrencyTestPanel _concurrencyPanel;
    private readonly InputTestPanel _inputPanel;
    private readonly FaultRecoveryTestPanel _faultPanel;
    private readonly MemoryBenchmarkTestPanel _memoryPanel;

    public TestSuiteMasterPanel(
        GraphicsTestPanel graphicsPanel,
        ConcurrencyTestPanel concurrencyPanel,
        InputTestPanel inputPanel,
        FaultRecoveryTestPanel faultPanel,
        MemoryBenchmarkTestPanel memoryPanel)
    {
        _graphicsPanel = graphicsPanel;
        _concurrencyPanel = concurrencyPanel;
        _inputPanel = inputPanel;
        _faultPanel = faultPanel;
        _memoryPanel = memoryPanel;
    }

    public void OnRender()
    {
        if (!IsOpen)
            return;

        bool open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(680, 750), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;

            DrawMenuBar();
            DrawSummaryBanner();
            ImGui.Separator();

            if (ImGui.BeginTabBar("MasterTestSuiteTabs", ImGuiTabBarFlags.Reorderable))
            {
                if (ImGui.BeginTabItem("1. Graphics & Display"))
                {
                    _graphicsPanel.DrawPanelContent();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("2. Concurrency & Dispatch"))
                {
                    _concurrencyPanel.DrawPanelContent();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("3. Input & Win32"))
                {
                    _inputPanel.DrawPanelContent();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("4. Fault Recovery & Lifecycle"))
                {
                    _faultPanel.DrawPanelContent();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("5. Memory & Telemetry"))
                {
                    _memoryPanel.DrawPanelContent();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void DrawMenuBar()
    {
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu("Floating Windows"))
            {
                bool gOpen = _graphicsPanel.IsOpen;
                if (ImGui.MenuItem("1. Graphics & SwapChain Telemetry", "", ref gOpen)) _graphicsPanel.IsOpen = gOpen;

                bool cOpen = _concurrencyPanel.IsOpen;
                if (ImGui.MenuItem("2. Concurrency & Lock-Free Pipelines", "", ref cOpen)) _concurrencyPanel.IsOpen = cOpen;

                bool iOpen = _inputPanel.IsOpen;
                if (ImGui.MenuItem("3. Input, Windows & Unicode Validator", "", ref iOpen)) _inputPanel.IsOpen = iOpen;

                bool fOpen = _faultPanel.IsOpen;
                if (ImGui.MenuItem("4. Fault Recovery & Lifecycle", "", ref fOpen)) _faultPanel.IsOpen = fOpen;

                bool mOpen = _memoryPanel.IsOpen;
                if (ImGui.MenuItem("5. Profiler & Memory Benchmarks", "", ref mOpen)) _memoryPanel.IsOpen = mOpen;

                ImGui.Separator();
                if (ImGui.MenuItem("Open All Floating Windows"))
                {
                    SetAllFloatingWindows(true);
                }
                if (ImGui.MenuItem("Close All Floating Windows"))
                {
                    SetAllFloatingWindows(false);
                }

                ImGui.EndMenu();
            }
            ImGui.EndMenuBar();
        }
    }

    private void DrawSummaryBanner()
    {
        string backend = GraphicsTelemetry.BackendName != "Unknown" ? GraphicsTelemetry.BackendName : "Detecting...";
        var io = ImGui.GetIO();

        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "DearImGuiInjectionPlus In-Game Certification Suite");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), backend);
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.Text($"{io.Framerate:F0} FPS");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.Text($"HWND: 0x{DearImGuiInjectionPlus.DearImGuiInjectionPlus.WindowHandle.ToInt64():X8}");

        ImGui.Spacing();
        if (ImGui.Button("Open All Floating Panels"))
        {
            SetAllFloatingWindows(true);
        }
        ImGui.SameLine();
        if (ImGui.Button("Close All Floating Panels"))
        {
            SetAllFloatingWindows(false);
        }
    }

    private void SetAllFloatingWindows(bool state)
    {
        _graphicsPanel.IsOpen = state;
        _concurrencyPanel.IsOpen = state;
        _inputPanel.IsOpen = state;
        _faultPanel.IsOpen = state;
        _memoryPanel.IsOpen = state;
    }
}
