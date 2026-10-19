using System;
using System.IO;
using BepInEx;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using ImGuiNET;
using TestPlugin.Panels;
using UnityEngine;

namespace TestPlugin;

public class PlayerDataSnapshot
{
    public Vector3 Position;
    public float Health;
    public float MaxHealth;
    public string CurrentScene;
}

public class SampleModWindow : IImGuiWindow
{
    public string Id => "sample_mod_window";
    public string Title => "DearImGuiInjectionPlus Mod View";
    public bool IsOpen { get; set; } = true;

    private readonly StateSnapshotPipeline<PlayerDataSnapshot> _snapshotPipeline;
    private readonly byte[] _inputBuffer = new byte[64];

    public SampleModWindow(StateSnapshotPipeline<PlayerDataSnapshot> snapshotPipeline)
    {
        _snapshotPipeline = snapshotPipeline;
    }

    public void OnRender()
    {
        bool open = IsOpen;
        if (ImGui.Begin("DearImGuiInjectionPlus - Developer Overlay", ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;

            ImGui.TextColored(new System.Numerics.Vector4(0.2f, 1f, 0.4f, 1f), "Status: Zero-Crash Architecture Active");
            ImGui.Separator();

            // Thread-safe lock-free read from snapshot pipeline without calling UnityEngine.Object APIs!
            var snapshot = _snapshotPipeline.GetLatest();
            if (snapshot != null)
            {
                ImGui.Text($"Scene: {snapshot.CurrentScene}");
                ImGui.Text($"Player Position: ({snapshot.Position.x:F2}, {snapshot.Position.y:F2}, {snapshot.Position.z:F2})");
                ImGui.Text($"Player Health: {snapshot.Health:F0} / {snapshot.MaxHealth:F0}");
            }
            else
            {
                ImGui.Text("Waiting for state snapshot from Unity main thread...");
            }

            ImGui.Separator();
            ImGui.InputText("Input Test", _inputBuffer, (uint)_inputBuffer.Length);

            if (ImGui.Button("Spawn Cube (via Main Thread Dispatcher)"))
            {
                // Safely interact with UnityEngine.Object on the main thread from ImGui render loop
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "InjectedCube_" + DateTime.Now.Ticks;
                    Log.Info($"[TestPlugin] Created game object on main thread: {cube.name}");
                });
            }
        }
        ImGui.End();
    }
}

[BepInDependency(DearImGuiInjectionPlus.Metadata.GUID)]
[BepInPlugin("com.custom.testplugin", "TestPlugin", "1.0.0")]
public class TestPlugin : BaseUnityPlugin
{
    // Snapshot pipeline for sample mod window
    private StateSnapshotPipeline<PlayerDataSnapshot> _playerSnapshotPipeline;
    private SampleModWindow _sampleModWindow;

    // Test Suite Panels (TM-01 through TM-10)
    private GraphicsTestPanel _graphicsPanel;
    private ConcurrencyTestPanel _concurrencyPanel;
    private InputTestPanel _inputPanel;
    private FaultRecoveryTestPanel _faultPanel;
    private MemoryBenchmarkTestPanel _memoryPanel;
    private TestSuiteMasterPanel _masterPanel;

    // Registered Tab handles for clean teardown (LC-07)
    private IDisposable _testSuiteTabHandle;
    private IDisposable _sampleModTabHandle;

    private void Awake()
    {
        Log.Init(new BepInExLog(Logger));
        Log.Info("[TestPlugin] Awake: Initializing DearImGui In-Game Test Suite...");

        // 1. Initialize sample mod
        _playerSnapshotPipeline = new StateSnapshotPipeline<PlayerDataSnapshot>();
        _sampleModWindow = new SampleModWindow(_playerSnapshotPipeline);

        // 2. Initialize modular test suite panels
        _graphicsPanel = new GraphicsTestPanel();
        _concurrencyPanel = new ConcurrencyTestPanel();
        _inputPanel = new InputTestPanel();
        _faultPanel = new FaultRecoveryTestPanel();
        _memoryPanel = new MemoryBenchmarkTestPanel();
        _masterPanel = new TestSuiteMasterPanel(
            _graphicsPanel,
            _concurrencyPanel,
            _inputPanel,
            _faultPanel,
            _memoryPanel);

        Log.Info("[TestPlugin] Test panels initialized successfully.");
    }

    private void OnEnable()
    {
        Log.Info("[TestPlugin] OnEnable: Registering test suite windows and menu tabs...");

        // 1. Register Legacy Overlay
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render += RenderLegacyOverlay;

        // 2. Register Sample Mod Window
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_sampleModWindow);
        _sampleModTabHandle = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Mods", "Toggle Sample View", () =>
        {
            if (ImGui.MenuItem("Toggle Sample Window", "", _sampleModWindow.IsOpen))
            {
                _sampleModWindow.IsOpen = !_sampleModWindow.IsOpen;
            }
        });

        // 3. Register Modular Test Windows (IImGuiWindow)
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_masterPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_graphicsPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_concurrencyPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_inputPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_faultPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_memoryPanel);

        // 4. Register Unified "DearImGui Test Suite" Menu Bar Tab (LC-07, TM-07)
        _testSuiteTabHandle = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("DearImGui Test Suite", "Test Suite Navigation", RenderTestSuiteMenu);
    }

    private void OnDisable()
    {
        Log.Info("[TestPlugin] OnDisable: Unregistering all test suite windows and tabs...");

        // 1. Unregister Legacy Overlay
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render -= RenderLegacyOverlay;

        // 2. Unregister Sample Mod Window & Tab
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_sampleModWindow);
        _sampleModTabHandle?.Dispose();
        _sampleModTabHandle = null;

        // 3. Unregister Test Windows
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_masterPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_graphicsPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_concurrencyPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_inputPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_faultPanel);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_memoryPanel);

        // 4. Unregister Test Suite Tab
        _testSuiteTabHandle?.Dispose();
        _testSuiteTabHandle = null;

        // 5. Stop background concurrency worker
        _concurrencyPanel?.StopWorker();
    }

    private void OnDestroy()
    {
        Log.Info("[TestPlugin] OnDestroy: Performing clean teardown and persisting timestamp...");

        // Ensure clean teardown if OnDisable was skipped
        OnDisable();

        // TM-10 Quiescent Teardown: Persist timestamp to disk and log completion
        try
        {
            string path = FaultRecoveryTestPanel.ShutdownTimestampPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, $"Teardown completed cleanly at {DateTime.UtcNow:o}");
            Log.Info($"[TestPlugin] OnApplicationQuit / OnDestroy save completed (Timestamp: {path})");
        }
        catch (Exception ex)
        {
            Log.Error($"[TestPlugin] Error writing teardown timestamp: {ex.Message}");
        }
    }

    private void Update()
    {
        // Unity Main Thread publishes state snapshots at game framerate
        var snapshot = new PlayerDataSnapshot
        {
            Position = transform.position,
            Health = 100f,
            MaxHealth = 100f,
            CurrentScene = gameObject.scene.name ?? "SampleScene"
        };
        _playerSnapshotPipeline.Publish(snapshot);
    }

    private void RenderLegacyOverlay()
    {
        if (DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible)
        {
            ImGui.ShowDemoWindow();
        }
    }

    private void RenderTestSuiteMenu()
    {
        // Fault B Injection Hook (TM-07, LC-03)
        if (FaultRecoveryTestPanel.TriggerFaultB)
        {
            FaultRecoveryTestPanel.TriggerFaultB = false;
            FaultRecoveryTestPanel.NotifyFaultBInjected();
            ImGui.BeginMenu("FaultyMenu");
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
            throw new InvalidOperationException("Simulated Fault B: Exception inside BeginMenu without PopStyleColor or EndMenu!");
        }

        // Master Dashboard toggle
        if (ImGui.MenuItem("Master Dashboard", "", _masterPanel.IsOpen))
        {
            _masterPanel.IsOpen = !_masterPanel.IsOpen;
        }

        ImGui.Separator();

        // Individual category panels
        if (ImGui.MenuItem("1. Graphics & SwapChain Telemetry", "", _graphicsPanel.IsOpen))
        {
            _graphicsPanel.IsOpen = !_graphicsPanel.IsOpen;
        }

        if (ImGui.MenuItem("2. Concurrency & Lock-Free Pipelines", "", _concurrencyPanel.IsOpen))
        {
            _concurrencyPanel.IsOpen = !_concurrencyPanel.IsOpen;
        }

        if (ImGui.MenuItem("3. Input, Windows & Unicode Validator", "", _inputPanel.IsOpen))
        {
            _inputPanel.IsOpen = !_inputPanel.IsOpen;
        }

        if (ImGui.MenuItem("4. Fault Recovery & Lifecycle", "", _faultPanel.IsOpen))
        {
            _faultPanel.IsOpen = !_faultPanel.IsOpen;
        }

        if (ImGui.MenuItem("5. Profiler & Memory Benchmarks", "", _memoryPanel.IsOpen))
        {
            _memoryPanel.IsOpen = !_memoryPanel.IsOpen;
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Open All Test Windows"))
        {
            SetAllPanelsState(true);
        }

        if (ImGui.MenuItem("Close All Test Windows"))
        {
            SetAllPanelsState(false);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Fault B: Trigger Menu Imbalance Crash"))
        {
            FaultRecoveryTestPanel.TriggerFaultB = true;
        }
    }

    private void SetAllPanelsState(bool open)
    {
        _masterPanel.IsOpen = open;
        _graphicsPanel.IsOpen = open;
        _concurrencyPanel.IsOpen = open;
        _inputPanel.IsOpen = open;
        _faultPanel.IsOpen = open;
        _memoryPanel.IsOpen = open;
    }
}