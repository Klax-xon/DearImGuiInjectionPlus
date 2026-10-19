using System;
using System.Diagnostics;
using System.Numerics;
using DearImGuiInjectionPlus;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Category 5: Profiler &amp; Memory Benchmarks (TM-09, MEM-01–MEM-08).
/// Provides rolling graphs of FPS and Delta Time, real-time tracking of managed GC heaps and collections,
/// native process memory diagnostics, and baseline leak soak tracking.
/// </summary>
public class MemoryBenchmarkTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_memory";
    public string Title => "5. Profiler & Memory Benchmarks";
    public bool IsOpen { get; set; } = true;

    // Rolling History
    private const int HistoryLength = 120;
    private readonly float[] _fpsHistory = new float[HistoryLength];
    private readonly float[] _dtHistory = new float[HistoryLength];
    private int _historyIndex = 0;

    // Running Stats
    private float _minFps = 9999f;
    private float _maxFps = 0f;
    private float _avgFps = 0f;
    private float _accumFps = 0f;
    private int _frameCount = 0;

    // Soak Baseline Tracking (TM-09)
    private readonly Stopwatch _soakStopwatch = Stopwatch.StartNew();
    private long _baselineManagedBytes = 0;
    private long _baselinePrivateBytes = 0;
    private bool _hasBaseline = false;

    // Allocation Rate Tracking
    private long _lastGcMemory = 0;
    private long _lastRateTimestamp = 0;
    private float _allocatedBytesPerSec = 0f;

    public void OnRender()
    {
        UpdateMetrics();

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

    private void UpdateMetrics()
    {
        var io = ImGui.GetIO();
        float currentFps = io.Framerate;
        float currentDt = io.DeltaTime * 1000f; // ms

        _fpsHistory[_historyIndex] = currentFps;
        _dtHistory[_historyIndex] = currentDt;
        _historyIndex = (_historyIndex + 1) % HistoryLength;

        _frameCount++;
        _accumFps += currentFps;
        _avgFps = _accumFps / _frameCount;
        if (currentFps < _minFps && currentFps > 0) _minFps = currentFps;
        if (currentFps > _maxFps) _maxFps = currentFps;

        // Allocation rate computation (sampled every 500ms)
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(now - _lastRateTimestamp) / Stopwatch.Frequency;
        if (elapsedSec >= 0.5)
        {
            long currentMem = GC.GetTotalMemory(false);
            long diff = currentMem - _lastGcMemory;
            if (diff > 0)
            {
                _allocatedBytesPerSec = (float)(diff / elapsedSec);
            }
            else
            {
                _allocatedBytesPerSec = 0f;
            }
            _lastGcMemory = currentMem;
            _lastRateTimestamp = now;
        }

        if (!_hasBaseline)
        {
            RecordBaseline();
        }
    }

    private void RecordBaseline()
    {
        _baselineManagedBytes = GC.GetTotalMemory(false);
        try
        {
            _baselinePrivateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        }
        catch { }
        _hasBaseline = true;
    }

    public void DrawPanelContent()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "Category 5: Profiler & Memory Benchmarks");
        ImGui.TextDisabled("Validates TM-09 (Steady-State Memory & 60-Minute Soak) and MEM-01 through MEM-08.");
        ImGui.Separator();

        DrawFrameRateAndTimingSection();
        ImGui.Separator();

        DrawManagedMemorySection();
        ImGui.Separator();

        DrawSoakBenchmarkSection();
    }

    private void DrawFrameRateAndTimingSection()
    {
        if (ImGui.CollapsingHeader("Frame Rate & Timing Monitor (MEM-07, TM-09)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var io = ImGui.GetIO();
            float currentFps = io.Framerate;
            float currentDt = io.DeltaTime * 1000f;

            ImGui.Columns(4, "TimingCols", false);
            ImGui.Text($"Current: {currentFps:F1} FPS");
            ImGui.NextColumn();
            ImGui.Text($"Delta: {currentDt:F2} ms");
            ImGui.NextColumn();
            ImGui.Text($"Min/Max: {_minFps:F0} / {_maxFps:F0}");
            ImGui.NextColumn();
            ImGui.Text($"Avg: {_avgFps:F1} FPS");
            ImGui.NextColumn();
            ImGui.Columns(1);

            // Rolling FPS graph
            ImGui.PlotLines("##FPSGraph", ref _fpsHistory[0], HistoryLength, _historyIndex,
                $"FPS (0 - 240) | Current: {currentFps:F1}", 0f, 240f, new Vector2(ImGui.GetContentRegionAvail().X, 60f));

            // Rolling Delta Time graph
            ImGui.PlotLines("##DTGraph", ref _dtHistory[0], HistoryLength, _historyIndex,
                $"Delta Time ms (0 - 33.3ms) | Current: {currentDt:F2} ms", 0f, 33.3f, new Vector2(ImGui.GetContentRegionAvail().X, 60f));
        }
    }

    private void DrawManagedMemorySection()
    {
        if (ImGui.CollapsingHeader("Live Managed & Process Heap Tracker (MEM-07)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            long managedBytes = GC.GetTotalMemory(false);
            float managedMb = managedBytes / (1024f * 1024f);

            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            long privateBytes = 0;
            long workingSetBytes = 0;
            try
            {
                var proc = Process.GetCurrentProcess();
                privateBytes = proc.PrivateMemorySize64;
                workingSetBytes = proc.WorkingSet64;
            }
            catch { }

            float privateMb = privateBytes / (1024f * 1024f);
            float workingSetMb = workingSetBytes / (1024f * 1024f);

            ImGui.Columns(2, "MemoryCols", true);

            ImGui.Text("Managed GC Heap Size:");
            ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{managedMb:F2} MB ({managedBytes:N0} bytes)");
            ImGui.NextColumn();

            ImGui.Text("Managed Allocation Rate:");
            ImGui.NextColumn();
            ImGui.Text($"{_allocatedBytesPerSec / 1024f:F1} KB / sec");
            ImGui.NextColumn();

            ImGui.Text("GC Gen 0 Collections:");
            ImGui.NextColumn();
            ImGui.Text($"{gen0:N0}");
            ImGui.NextColumn();

            ImGui.Text("GC Gen 1 Collections:");
            ImGui.NextColumn();
            ImGui.Text($"{gen1:N0}");
            ImGui.NextColumn();

            ImGui.Text("GC Gen 2 Collections:");
            ImGui.NextColumn();
            ImGui.Text($"{gen2:N0}");
            ImGui.NextColumn();

            ImGui.Text("Process Private Memory:");
            ImGui.NextColumn();
            ImGui.Text($"{privateMb:F2} MB");
            ImGui.NextColumn();

            ImGui.Text("Process Working Set:");
            ImGui.NextColumn();
            ImGui.Text($"{workingSetMb:F2} MB");
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            if (ImGui.Button("Force Garbage Collection (GC.Collect)"))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Forces full Gen 2 collection to observe baseline steady-state heap.");
        }
    }

    private void DrawSoakBenchmarkSection()
    {
        if (ImGui.CollapsingHeader("60-Minute Soak & Leak Benchmark (TM-09)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            TimeSpan soakElapsed = _soakStopwatch.Elapsed;
            long currentManaged = GC.GetTotalMemory(false);
            long currentPrivate = 0;
            try { currentPrivate = Process.GetCurrentProcess().PrivateMemorySize64; } catch { }

            float managedGrowthMb = (currentManaged - _baselineManagedBytes) / (1024f * 1024f);
            float privateGrowthMb = (currentPrivate - _baselinePrivateBytes) / (1024f * 1024f);

            ImGui.Text($"Soak Elapsed Time: {soakElapsed.Hours:D2}:{soakElapsed.Minutes:D2}:{soakElapsed.Seconds:D2}");

            ImGui.Columns(2, "SoakCols", true);

            ImGui.Text("Baseline Managed Memory:");
            ImGui.NextColumn();
            ImGui.Text($"{_baselineManagedBytes / (1024f * 1024f):F2} MB");
            ImGui.NextColumn();

            ImGui.Text("Net Managed Memory Growth:");
            ImGui.NextColumn();
            Vector4 colManaged = managedGrowthMb < 5f ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.2f, 1f);
            ImGui.TextColored(colManaged, $"{(managedGrowthMb >= 0 ? "+" : "")}{managedGrowthMb:F2} MB {(managedGrowthMb < 5f ? "[PASS: < 5 MB]" : "[WARNING]")}");
            ImGui.NextColumn();

            ImGui.Text("Net Unmanaged / Private Growth:");
            ImGui.NextColumn();
            Vector4 colPriv = privateGrowthMb < 5f ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.2f, 1f);
            ImGui.TextColored(colPriv, $"{(privateGrowthMb >= 0 ? "+" : "")}{privateGrowthMb:F2} MB {(privateGrowthMb < 5f ? "[PASS: < 5 MB]" : "[WARNING]")}");
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            if (ImGui.Button("Reset Soak Baseline Snapshot"))
            {
                RecordBaseline();
            }
        }
    }
}
