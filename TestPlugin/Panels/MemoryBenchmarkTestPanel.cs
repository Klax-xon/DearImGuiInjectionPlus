using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Category 5: Profiler &amp; Memory Benchmarks (TM-09, MEM-01–MEM-08).
/// Provides rolling graphs of FPS and Delta Time, real-time tracking of managed GC heaps and collections,
/// native process memory diagnostics via Win32 PSAPI, and baseline leak soak tracking.
/// </summary>
public class MemoryBenchmarkTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_memory";
    public string Title => "5. Profiler & Memory Benchmarks";
    public bool IsOpen { get; set; } = false;

    // Win32 Process Memory via psapi.dll
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

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
    private long _lastPostGcManagedBytes = 0;
    private long _peakManagedBytes = 0;
    private long _troughManagedBytes = 0;
    private int _lastGen2Count = 0;
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
        ImGui.SetNextWindowSize(new Vector2(620, 720), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
        {
            DrawPanelContent();
        }
        ImGui.End();
        IsOpen = open;
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

        long currentMem = GC.GetTotalMemory(false);
        if (currentMem > _peakManagedBytes) _peakManagedBytes = currentMem;
        if (currentMem < _troughManagedBytes || _troughManagedBytes == 0) _troughManagedBytes = currentMem;

        // Detect GC collection events to record clean post-collection steady-state heap
        int currentGen2 = GC.CollectionCount(2);
        if (currentGen2 > _lastGen2Count)
        {
            _lastGen2Count = currentGen2;
            _lastPostGcManagedBytes = currentMem;
        }

        // Allocation rate computation (sampled every 500ms)
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(now - _lastRateTimestamp) / Stopwatch.Frequency;
        if (elapsedSec >= 0.5)
        {
            long diff = currentMem - _lastGcMemory;
            _allocatedBytesPerSec = diff > 0 ? (float)(diff / elapsedSec) : 0f;
            _lastGcMemory = currentMem;
            _lastRateTimestamp = now;
        }

        if (!_hasBaseline)
        {
            RecordBaseline(forceGc: false);
        }
    }

    private void RecordBaseline(bool forceGc)
    {
        if (forceGc)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        _baselineManagedBytes = GC.GetTotalMemory(false);
        _lastPostGcManagedBytes = _baselineManagedBytes;
        _peakManagedBytes = _baselineManagedBytes;
        _troughManagedBytes = _baselineManagedBytes;
        _lastGen2Count = GC.CollectionCount(2);

        GetProcessMemory(out _baselinePrivateBytes, out _);
        _soakStopwatch.Restart();
        _hasBaseline = true;
    }

    private static void GetProcessMemory(out long privateBytes, out long workingSetBytes)
    {
        privateBytes = 0;
        workingSetBytes = 0;

        try
        {
            var mem = new PROCESS_MEMORY_COUNTERS_EX();
            mem.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
            if (GetProcessMemoryInfo(new IntPtr(-1), out mem, mem.cb))
            {
                privateBytes = (long)mem.PrivateUsage.ToUInt64();
                workingSetBytes = (long)mem.WorkingSetSize.ToUInt64();
                return;
            }
        }
        catch { }

        try
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            privateBytes = proc.PrivateMemorySize64;
            workingSetBytes = proc.WorkingSet64;
        }
        catch { }
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

            GetProcessMemory(out long privateBytes, out long workingSetBytes);
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

            ImGui.Text("Process Private Memory (Win32):");
            ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{privateMb:F2} MB ({privateBytes:N0} bytes)");
            ImGui.NextColumn();

            ImGui.Text("Process Working Set (Win32):");
            ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{workingSetMb:F2} MB ({workingSetBytes:N0} bytes)");
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            if (ImGui.Button("Force Garbage Collection (GC.Collect)"))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                _lastPostGcManagedBytes = GC.GetTotalMemory(false);
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
            GetProcessMemory(out long currentPrivate, out _);

            float currentManagedGrowthMb = (currentManaged - _baselineManagedBytes) / (1024f * 1024f);
            float steadyStateGrowthMb = (_lastPostGcManagedBytes - _baselineManagedBytes) / (1024f * 1024f);
            float privateGrowthMb = (currentPrivate - _baselinePrivateBytes) / (1024f * 1024f);

            ImGui.Text($"Soak Elapsed Time: {soakElapsed.Hours:D2}:{soakElapsed.Minutes:D2}:{soakElapsed.Seconds:D2}");

            ImGui.Columns(2, "SoakCols", true);

            ImGui.Text("Baseline Managed Memory:");
            ImGui.NextColumn();
            ImGui.Text($"{_baselineManagedBytes / (1024f * 1024f):F2} MB");
            ImGui.NextColumn();

            ImGui.Text("In-Flight Net Growth (Current):");
            ImGui.NextColumn();
            ImGui.Text($"{(currentManagedGrowthMb >= 0 ? "+" : "")}{currentManagedGrowthMb:F2} MB");
            ImGui.NextColumn();

            ImGui.Text("Steady-State Net Growth (Post-GC):");
            ImGui.NextColumn();
            bool steadyPass = steadyStateGrowthMb < 5f;
            Vector4 colSteady = steadyPass ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.2f, 1f);
            ImGui.TextColored(colSteady, $"{(steadyStateGrowthMb >= 0 ? "+" : "")}{steadyStateGrowthMb:F2} MB {(steadyPass ? "[PASS: < 5 MB]" : "[WARNING]")}");
            ImGui.NextColumn();

            ImGui.Text("Heap Min / Max Extents:");
            ImGui.NextColumn();
            ImGui.Text($"Min: {_troughManagedBytes / (1024f * 1024f):F2} MB | Peak: {_peakManagedBytes / (1024f * 1024f):F2} MB");
            ImGui.NextColumn();

            ImGui.Text("Net Unmanaged / Private Growth:");
            ImGui.NextColumn();
            bool privPass = Math.Abs(privateGrowthMb) < 5f;
            Vector4 colPriv = privPass ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.2f, 1f);
            ImGui.TextColored(colPriv, $"{(privateGrowthMb >= 0 ? "+" : "")}{privateGrowthMb:F2} MB {(privPass ? "[PASS: < 5 MB]" : "[WARNING]")}");
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            if (ImGui.Button("Sample Clean Post-GC Baseline (Force GC & Reset)"))
            {
                RecordBaseline(forceGc: true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset Baseline (Current Memory)"))
            {
                RecordBaseline(forceGc: false);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("Note: Cyclical heap fluctuations (up to +30 MB during map/scene loading) are normal in Unity Mono. As long as post-GC steady-state growth plateaus < 5 MB over time, no memory leak exists (TM-09 Pass).");
        }
    }
}
