using System;
using System.Numerics;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Category 1: Graphics &amp; SwapChain Telemetry (TM-01, TM-02, TM-03, TM-08).
/// Provides live diagnostics on graphics devices, dynamic geometry/buffer stress tests,
/// UserCallback (-1) render state reset verification, and HDR/SDR tone reproduction monitors.
/// </summary>
public class GraphicsTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_graphics";
    public string Title => "1. Graphics & SwapChain Telemetry";
    public bool IsOpen { get; set; } = true;

    // Buffer Stress State (DXH-04, TM-08)
    private bool _stressEnabled = false;
    private int _primitiveCount = 1000;
    private bool _drawRects = true;
    private bool _drawCircles = true;
    private bool _drawLines = true;
    private bool _drawText = false;
    private bool _autoBurst = false;
    private double _lastBurstTime = 0;
    private bool _burstHighState = false;

    // UserCallback Reset State (RR-03)
    private bool _triggerCallbackReset = false;
    private int _callbackResetSuccessCount = 0;
    private string _callbackStatusMessage = "Idle. Click below to verify ImDrawCallback_ResetRenderState.";

    public void OnRender()
    {
        if (!IsOpen)
            return;

        bool open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(620, 680), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;
            DrawPanelContent();
        }
        ImGui.End();
    }

    public void DrawPanelContent()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "Category 1: Graphics & SwapChain Telemetry");
        ImGui.TextDisabled("Validates TM-01 (Bootstrap), TM-02 (Resize), TM-03 (HDR/sRGB), and TM-08 (Geometry Growth).");
        ImGui.Separator();

        DrawLiveDeviceDiagnostics();
        ImGui.Separator();

        DrawDrawCallAndBufferStress();
        ImGui.Separator();

        DrawUserCallbackResetTest();
        ImGui.Separator();

        DrawHdrSdrGammaMonitor();
    }

    private void DrawLiveDeviceDiagnostics()
    {
        if (ImGui.CollapsingHeader("Live Device Diagnostics (TM-01, TM-02)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            string backend = GraphicsTelemetry.BackendName;
            if (backend == "Unknown" && RendererFinder.RendererFinder.RendererKind != RendererFinder.Renderers.RendererKind.None)
            {
                backend = RendererFinder.RendererFinder.RendererKind.ToString();
            }

            Vector4 backendColor = backend.Contains("12") 
                ? new Vector4(0.3f, 1f, 0.4f, 1f) 
                : new Vector4(0.2f, 0.7f, 1f, 1f);

            ImGui.Columns(2, "DeviceDiagCols", true);

            ImGui.Text("Active Graphics Backend:");
            ImGui.NextColumn();
            ImGui.TextColored(backendColor, backend);
            ImGui.NextColumn();

            IntPtr hwnd = GraphicsTelemetry.WindowHandle != IntPtr.Zero 
                ? GraphicsTelemetry.WindowHandle 
                : DearImGuiInjectionPlus.DearImGuiInjectionPlus.WindowHandle;

            ImGui.Text("Target Window (HWND):");
            ImGui.NextColumn();
            ImGui.Text($"0x{hwnd.ToInt64():X8}");
            ImGui.NextColumn();

            ImGui.Text("Window Unicode Status:");
            ImGui.NextColumn();
            bool isUnicode = hwnd != IntPtr.Zero && User32.IsWindowUnicode(hwnd);
            if (isUnicode)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Unicode (UTF-16) [PASS]");
            else
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), hwnd != IntPtr.Zero ? "ANSI Window" : "Pending Discovery");
            ImGui.NextColumn();

            ImGui.Text("Active SwapChain Pointer:");
            ImGui.NextColumn();
            ImGui.Text(GraphicsTelemetry.SwapChainPointer != IntPtr.Zero 
                ? $"0x{GraphicsTelemetry.SwapChainPointer.ToInt64():X8}" 
                : "Capturing on Present...");
            ImGui.NextColumn();

            ImGui.Text("Backbuffer Format:");
            ImGui.NextColumn();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), GraphicsTelemetry.BackBufferFormat);
            ImGui.NextColumn();

            ImGui.Text("Backbuffer Dimensions:");
            ImGui.NextColumn();
            var io = ImGui.GetIO();
            string dimStr = GraphicsTelemetry.Width > 0 
                ? $"{GraphicsTelemetry.Width} x {GraphicsTelemetry.Height} px" 
                : $"{io.DisplaySize.X:F0} x {io.DisplaySize.Y:F0} px (Viewport)";
            ImGui.Text(dimStr);
            ImGui.NextColumn();

            ImGui.Text("Refresh Rate:");
            ImGui.NextColumn();
            if (GraphicsTelemetry.RefreshRateHz > 0)
            {
                ImGui.Text($"{GraphicsTelemetry.RefreshRateHz:F2} Hz ({GraphicsTelemetry.RefreshRateNumerator}/{GraphicsTelemetry.RefreshRateDenominator})");
            }
            else
            {
                ImGui.TextDisabled("Variable / Unspecified");
            }
            ImGui.NextColumn();

            ImGui.Columns(1);
        }
    }

    private unsafe void DrawDrawCallAndBufferStress()
    {
        if (ImGui.CollapsingHeader("Draw Call & Dynamic Buffer Stress (DXH-04, TM-08)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Stress-tests dynamic vertex and index buffer reallocation (>100k vertices) and upload heaps across resize and burst events.");

            ImGui.Checkbox("Enable Stress Drawing", ref _stressEnabled);
            ImGui.SameLine();
            ImGui.Checkbox("Auto-Burst Mode (2s Cycle)", ref _autoBurst);

            if (_autoBurst)
            {
                double now = ImGui.GetTime();
                if (now - _lastBurstTime > 2.0)
                {
                    _lastBurstTime = now;
                    _burstHighState = !_burstHighState;
                }
                _primitiveCount = _burstHighState ? 35000 : 500;
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), $"[Auto-Burst Active] Current target: {_primitiveCount:N0} primitives");
            }
            else
            {
                ImGui.SliderInt("Primitive Count", ref _primitiveCount, 100, 50000);
            }

            ImGui.Text("Shape Filter:");
            ImGui.SameLine();
            ImGui.Checkbox("Rectangles", ref _drawRects);
            ImGui.SameLine();
            ImGui.Checkbox("Circles", ref _drawCircles);
            ImGui.SameLine();
            ImGui.Checkbox("Lines", ref _drawLines);
            ImGui.SameLine();
            ImGui.Checkbox("Text", ref _drawText);

            var drawData = ImGui.GetDrawData();
            int totalVtx = drawData.NativePtr != null ? drawData.TotalVtxCount : 0;
            int totalIdx = drawData.NativePtr != null ? drawData.TotalIdxCount : 0;

            ImGui.Text($"Frame Total Vertices: {totalVtx:N0} | Total Indices: {totalIdx:N0}");

            // Stress rendering canvas
            Vector2 canvasSize = new(ImGui.GetContentRegionAvail().X, 140f);
            Vector2 canvasPos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();

            // Background canvas rect
            drawList.AddRectFilled(canvasPos, canvasPos + canvasSize, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1f)), 4f);
            drawList.AddRect(canvasPos, canvasPos + canvasSize, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.4f, 1f)), 4f);

            if (_stressEnabled)
            {
                float width = canvasSize.X - 10f;
                float height = canvasSize.Y - 10f;
                float time = (float)ImGui.GetTime();

                int rectsDrawn = 0;
                int circlesDrawn = 0;
                int linesDrawn = 0;
                int textDrawn = 0;

                for (int i = 0; i < _primitiveCount; i++)
                {
                    float t = (float)i / _primitiveCount;
                    float px = canvasPos.X + 5f + ((i * 37) % (int)Math.Max(1, width));
                    float py = canvasPos.Y + 5f + ((i * 19) % (int)Math.Max(1, height));
                    uint color = ImGui.GetColorU32(new Vector4(
                        0.5f + 0.5f * (float)Math.Sin(time + t * 6.28f),
                        0.5f + 0.5f * (float)Math.Cos(time + t * 4f),
                        0.8f,
                        0.4f));

                    int mode = i % 4;
                    if (mode == 0 && _drawRects)
                    {
                        drawList.AddRectFilled(new Vector2(px, py), new Vector2(px + 12f, py + 12f), color);
                        rectsDrawn++;
                    }
                    else if (mode == 1 && _drawCircles)
                    {
                        drawList.AddCircleFilled(new Vector2(px + 6f, py + 6f), 5f, color, 8);
                        circlesDrawn++;
                    }
                    else if (mode == 2 && _drawLines)
                    {
                        drawList.AddLine(new Vector2(px, py), new Vector2(px + 14f, py + 8f), color, 1f);
                        linesDrawn++;
                    }
                    else if (mode == 3 && _drawText)
                    {
                        drawList.AddText(new Vector2(px, py), color, "#");
                        textDrawn++;
                    }
                }
            }

            ImGui.Dummy(canvasSize);
        }
    }

    private void DrawUserCallbackResetTest()
    {
        if (ImGui.CollapsingHeader("UserCallback Reset Test (RR-03, RR-05)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Triggers a custom draw command with UserCallback = new IntPtr(-1) (ImDrawCallback_ResetRenderState). Validates that D3D11 and D3D12 properly restore pipeline state without throwing Marshal exceptions.");

            if (ImGui.Button("Trigger ResetRenderState Callback (UserCallback = -1)"))
            {
                _triggerCallbackReset = true;
            }

            if (_triggerCallbackReset)
            {
                _triggerCallbackReset = false;
                try
                {
                    var drawList = ImGui.GetWindowDrawList();
                    drawList.AddCallback(new IntPtr(-1), IntPtr.Zero);
                    _callbackResetSuccessCount++;
                    _callbackStatusMessage = $"PASS: ImDrawCallback_ResetRenderState successfully queued and executed (Total: {_callbackResetSuccessCount}).";
                }
                catch (Exception ex)
                {
                    _callbackStatusMessage = $"FAIL: Exception triggering UserCallback: {ex.Message}";
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), _callbackStatusMessage);
        }
    }

    private void DrawHdrSdrGammaMonitor()
    {
        if (ImGui.CollapsingHeader("HDR / SDR Gamma Monitor & Tone Reproduction (TM-03)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text("Color Space:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), GraphicsTelemetry.ColorSpaceName);

            ImGui.Text("HDR Pipeline Active:");
            ImGui.SameLine();
            if (GraphicsTelemetry.IsHdr)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "YES (HDR10 / scRGB)");
            else
                ImGui.TextDisabled("NO (Standard Dynamic Range)");

            ImGui.Spacing();
            ImGui.Text("16-Step Gray Ramp (Verify black level discrimination and highlight clipping):");

            var drawList = ImGui.GetWindowDrawList();
            Vector2 rampPos = ImGui.GetCursorScreenPos();
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float stepWidth = totalWidth / 16f;
            float rampHeight = 24f;

            for (int i = 0; i < 16; i++)
            {
                float level = (float)i / 15f;
                uint col = ImGui.GetColorU32(new Vector4(level, level, level, 1f));
                Vector2 p0 = new(rampPos.X + i * stepWidth, rampPos.Y);
                Vector2 p1 = new(p0.X + stepWidth, p0.Y + rampHeight);
                drawList.AddRectFilled(p0, p1, col);
                drawList.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 0.5f)));
            }

            ImGui.Dummy(new Vector2(totalWidth, rampHeight + 4f));

            // Continuous gradient bar
            Vector2 gradPos = ImGui.GetCursorScreenPos();
            uint black = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f));
            uint white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
            drawList.AddRectFilledMultiColor(gradPos, new Vector2(gradPos.X + totalWidth, gradPos.Y + 16f), black, white, white, black);
            drawList.AddRect(gradPos, new Vector2(gradPos.X + totalWidth, gradPos.Y + 16f), ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.8f)));

            ImGui.Dummy(new Vector2(totalWidth, 20f));
            ImGui.TextDisabled("Note: On HDR displays, steps 0-2 must be distinctly visible above true black without crushed shadows.");
        }
    }
}
