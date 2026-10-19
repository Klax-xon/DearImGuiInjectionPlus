using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Concurrency test payload struct with multiple fields for validating zero torn reads (TH-06, TM-04).
/// </summary>
public struct ConcurrencyTestPayload
{
    public ulong SequenceNumber;
    public ulong FieldA;
    public ulong FieldB;
    public ulong FieldC;
    public ulong FieldD;
    public long TimestampTicks;
}

/// <summary>
/// Category 2: Concurrency &amp; Lock-Free Pipelines (TM-04, TH-05, TH-06).
/// Validates high-frequency triple-buffered StructSnapshotPipeline against torn reads,
/// and stresses UnityMainThreadDispatcher batch draining, blocking invoke latency, and timeout resilience.
/// </summary>
public class ConcurrencyTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_concurrency";
    public string Title => "2. Concurrency & Lock-Free Pipelines";
    public bool IsOpen { get; set; } = true;

    // StructSnapshotPipeline Stress State (TH-06)
    private readonly StructSnapshotPipeline<ConcurrencyTestPayload> _pipeline = new();
    private Thread _workerThread;
    private volatile bool _workerRunning = false;
    private int _targetUpdateRateHz = 5000;
    private long _workerSequence = 0;
    private long _totalWrites = 0;
    private long _successfulReads = 0;
    private long _tornReads = 0;
    private ConcurrencyTestPayload _lastReadPayload;
    private ConcurrencyTestPayload _lastTornPayload;

    // Rates calculation
    private long _lastRateCalcTimestamp = 0;
    private long _lastRateWrites = 0;
    private long _lastRateReads = 0;
    private float _currentWriteRateHz = 0f;
    private float _currentReadRateHz = 0f;

    // MainThreadDispatcher Stress State (TH-05)
    private int _singleTasksEnqueued = 0;
    private int _singleTasksExecuted = 0;
    private int _burstTasksEnqueued = 0;
    private int _burstTasksExecuted = 0;

    // InvokeOnMainThread State
    private string _invokeResult = "Idle. Click 'InvokeOnMainThread' to test.";
    private float _lastInvokeLatencyMs = 0f;
    private bool _isInvokePending = false;

    // Timeout Test State
    private string _timeoutResult = "Idle. Click 'Trigger Deliberate Timeout' to test.";
    private bool _isTimeoutPending = false;

    public void OnRender()
    {
        // 1. Consume from StructSnapshotPipeline every frame on the render thread (TM-04)
        ConsumePipelineOnRenderThread();

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

    private int _lastConsumedFrame = -1;

    public void ConsumePipelineOnRenderThread()
    {
        if (!_workerRunning)
        {
            _currentWriteRateHz = 0f;
            _currentReadRateHz = 0f;
            return;
        }

        int currentFrame = ImGui.GetFrameCount();
        if (_lastConsumedFrame == currentFrame)
            return;
        _lastConsumedFrame = currentFrame;

        var latest = _pipeline.GetLatest();
        if (latest.SequenceNumber > 0)
        {
            _lastReadPayload = latest;

            // Integrity check: All fields must strictly equal the SequenceNumber to prove zero torn reads (TH-06)
            bool intact = (latest.FieldA == latest.SequenceNumber) &&
                          (latest.FieldB == latest.SequenceNumber) &&
                          (latest.FieldC == latest.SequenceNumber) &&
                          (latest.FieldD == latest.SequenceNumber);

            if (intact)
            {
                _successfulReads++;
            }
            else
            {
                _tornReads++;
                _lastTornPayload = latest;
            }
        }

        // Periodic frequency calculation (every 500ms)
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(nowTicks - _lastRateCalcTimestamp) / Stopwatch.Frequency;
        if (elapsedSec >= 0.5)
        {
            _currentWriteRateHz = (float)((_totalWrites - _lastRateWrites) / elapsedSec);
            _currentReadRateHz = (float)((_successfulReads - _lastRateReads) / elapsedSec);
            _lastRateWrites = _totalWrites;
            _lastRateReads = _successfulReads;
            _lastRateCalcTimestamp = nowTicks;
        }
    }

    public void DrawPanelContent()
    {
        // Ensure pipeline is consumed even when viewed directly inside the Master Dashboard tab
        ConsumePipelineOnRenderThread();

        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "Category 2: Concurrency & Lock-Free Pipelines");
        ImGui.TextDisabled("Validates TM-04 (High-Frequency Concurrency), TH-05 (Dispatcher Lifetimes), and TH-06 (Zero Torn Reads).");
        ImGui.Separator();

        DrawSnapshotPipelineSection();
        ImGui.Separator();

        DrawMainThreadDispatcherSection();
    }

    private void DrawSnapshotPipelineSection()
    {
        if (ImGui.CollapsingHeader("StructSnapshotPipeline<T> Torture Test (TH-06, TM-04)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Triple-buffered lock-free struct exchange. Background thread writes at up to 10,000 updates/sec while render thread verifies payload integrity (FieldA == FieldB == FieldC == FieldD == Seq).");

            if (_workerRunning)
            {
                if (ImGui.Button("Stop Worker Thread"))
                {
                    StopWorker();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"[RUNNING at ~{_currentWriteRateHz:F0} Hz writes]");
            }
            else
            {
                if (ImGui.Button("Start Worker Thread"))
                {
                    StartWorker();
                }
                ImGui.SameLine();
                ImGui.TextDisabled("[STOPPED]");
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset Counters"))
            {
                ResetCounters();
            }
            ImGui.SameLine();
            if (ImGui.Button("Stop & Reset"))
            {
                StopAndReset();
            }

            ImGui.SliderInt("Target Writer Rate (Hz)", ref _targetUpdateRateHz, 100, 10000);

            ImGui.Spacing();
            ImGui.Columns(2, "ConcurrencyStatsCols", true);

            ImGui.Text("Total Background Writes:");
            ImGui.NextColumn();
            ImGui.Text($"{_totalWrites:N0} writes ({_currentWriteRateHz:F0} Hz)");
            ImGui.NextColumn();

            ImGui.Text("Render Thread Successful Reads:");
            ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{_successfulReads:N0} reads ({_currentReadRateHz:F0} Hz)");
            ImGui.NextColumn();

            ImGui.Text("Torn Reads / Checksum Failures:");
            ImGui.NextColumn();
            if (_tornReads == 0)
            {
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), "0 [PASS: ZERO TORN READS]");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.2f, 0.2f, 1f), $"{_tornReads} [FAIL: TORN READ DETECTED]");
            }
            ImGui.NextColumn();

            ImGui.Text("Latest Sequence Number:");
            ImGui.NextColumn();
            ImGui.Text($"{_lastReadPayload.SequenceNumber:N0}");
            ImGui.NextColumn();

            ImGui.Columns(1);

            if (_tornReads > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                    $"Torn Read Details: Seq={_lastTornPayload.SequenceNumber}, A={_lastTornPayload.FieldA}, B={_lastTornPayload.FieldB}, C={_lastTornPayload.FieldC}, D={_lastTornPayload.FieldD}");
            }
        }
    }

    private void DrawMainThreadDispatcherSection()
    {
        if (ImGui.CollapsingHeader("UnityMainThreadDispatcher Stress Tester (TH-05, TM-04)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Tests asynchronous Enqueue(Action), batch draining (MaxBatchPerFrame = 500), synchronous InvokeOnMainThread<T>, and deadlock timeout protection.");

            // 1. Single Task
            if (ImGui.Button("Enqueue Single Task"))
            {
                _singleTasksEnqueued++;
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    Interlocked.Increment(ref _singleTasksExecuted);
                });
            }
            ImGui.SameLine();
            ImGui.Text($"Single Tasks: Enqueued={_singleTasksEnqueued}, Executed={_singleTasksExecuted}");

            // 2. Burst of 500 Tasks
            if (ImGui.Button("Enqueue Burst of 500 Tasks"))
            {
                _burstTasksEnqueued += 500;
                for (int i = 0; i < 500; i++)
                {
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        Interlocked.Increment(ref _burstTasksExecuted);
                    });
                }
            }
            ImGui.SameLine();
            ImGui.Text($"Burst Tasks: Enqueued={_burstTasksEnqueued}, Executed={_burstTasksExecuted}");

            ImGui.Separator();

            // 3. Synchronous InvokeOnMainThread<T> from Background Thread
            ImGui.Text("Synchronous InvokeOnMainThread<T> (Executed via Task.Run to prevent render thread deadlock):");
            if (ImGui.Button("Run InvokeOnMainThread<T> Benchmark") && !_isInvokePending)
            {
                _isInvokePending = true;
                _invokeResult = "Invoking on main thread...";
                Task.Run(() =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        int frameCount = UnityMainThreadDispatcher.InvokeOnMainThread(() => UnityEngine.Time.frameCount, 2000);
                        sw.Stop();
                        _lastInvokeLatencyMs = (float)sw.Elapsed.TotalMilliseconds;
                        _invokeResult = $"PASS: Returned frameCount={frameCount} in {_lastInvokeLatencyMs:F2}ms";
                    }
                    catch (Exception ex)
                    {
                        _invokeResult = $"FAIL: {ex.GetType().Name}: {ex.Message}";
                    }
                    finally
                    {
                        _isInvokePending = false;
                    }
                });
            }

            if (!string.IsNullOrEmpty(_invokeResult))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.9f, 1f, 1f));
                ImGui.TextWrapped(_invokeResult);
                ImGui.PopStyleColor();
            }

            // 4. Deliberate Timeout Test
            ImGui.Text("Deliberate Dispatcher Timeout Test (Verifies DispatchCompletionHandle safe disposal):");
            if (ImGui.Button("Trigger Deliberate Timeout (50ms limit)") && !_isTimeoutPending)
            {
                _isTimeoutPending = true;
                _timeoutResult = "Executing timeout test...";
                Task.Run(() =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        // Submit a task with 50ms timeout where the main thread takes longer or is simulated
                        UnityMainThreadDispatcher.InvokeOnMainThread(() =>
                        {
                            Thread.Sleep(200);
                            return 1234;
                        }, timeoutMs: 50);

                        sw.Stop();
                        _timeoutResult = $"FAIL: Expected TimeoutException but completed in {sw.ElapsedMilliseconds}ms";
                    }
                    catch (TimeoutException tex)
                    {
                        _timeoutResult = $"PASS: Caught expected TimeoutException in ~50ms: {tex.Message}";
                    }
                    catch (Exception ex)
                    {
                        _timeoutResult = $"FAIL: Unexpected exception: {ex.GetType().Name}: {ex.Message}";
                    }
                    finally
                    {
                        _isTimeoutPending = false;
                    }
                });
            }

            if (!string.IsNullOrEmpty(_timeoutResult))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, _timeoutResult.StartsWith("PASS") 
                    ? new Vector4(0.3f, 1f, 0.4f, 1f) 
                    : new Vector4(1f, 0.8f, 0.2f, 1f));
                ImGui.TextWrapped(_timeoutResult);
                ImGui.PopStyleColor();
            }
        }
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _totalWrites, 0);
        Interlocked.Exchange(ref _workerSequence, 0);
        _successfulReads = 0;
        _tornReads = 0;
        _lastRateWrites = 0;
        _lastRateReads = 0;
        _lastRateCalcTimestamp = Stopwatch.GetTimestamp();
        _currentWriteRateHz = 0f;
        _currentReadRateHz = 0f;
        _lastReadPayload = default;
        _lastTornPayload = default;

        // Flush all three slots in the triple buffer to clear stale sequence numbers
        _pipeline.Publish(default);
        _pipeline.Publish(default);
        _pipeline.Publish(default);
    }

    public void StopAndReset()
    {
        StopWorker();
        ResetCounters();
    }

    public void StartWorker()
    {
        if (_workerRunning)
            return;

        _lastRateCalcTimestamp = Stopwatch.GetTimestamp();
        _lastRateWrites = _totalWrites;
        _lastRateReads = _successfulReads;
        _currentWriteRateHz = 0f;
        _currentReadRateHz = 0f;

        _workerRunning = true;
        _workerThread = new Thread(WorkerLoop)
        {
            Name = "DearImGui_ConcurrencyWorker",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _workerThread.Start();
    }

    public void StopWorker()
    {
        _workerRunning = false;
        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(500);
            _workerThread = null;
        }
        _currentWriteRateHz = 0f;
        _currentReadRateHz = 0f;
    }

    private void WorkerLoop()
    {
        var sw = Stopwatch.StartNew();
        double ticksPerUpdate = (double)Stopwatch.Frequency / Math.Max(100, _targetUpdateRateHz);
        long nextTick = sw.ElapsedTicks;

        while (_workerRunning)
        {
            ulong seq = (ulong)Interlocked.Increment(ref _workerSequence);
            var payload = new ConcurrencyTestPayload
            {
                SequenceNumber = seq,
                FieldA = seq,
                FieldB = seq,
                FieldC = seq,
                FieldD = seq,
                TimestampTicks = Stopwatch.GetTimestamp()
            };

            _pipeline.Publish(payload);
            Interlocked.Increment(ref _totalWrites);

            // Precise pacing
            ticksPerUpdate = (double)Stopwatch.Frequency / Math.Max(100, _targetUpdateRateHz);
            nextTick += (long)ticksPerUpdate;
            if (sw.ElapsedTicks - nextTick > (long)ticksPerUpdate * 10)
            {
                nextTick = sw.ElapsedTicks;
            }

            while (sw.ElapsedTicks < nextTick && _workerRunning)
            {
                if (nextTick - sw.ElapsedTicks > Stopwatch.Frequency / 1000)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        }
    }
}
