using System;
using BepInEx;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.BepInEx;
using ImGuiNET;
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
    private byte[] _inputBuffer = new byte[64];

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
    private StateSnapshotPipeline<PlayerDataSnapshot> _playerSnapshotPipeline;
    private SampleModWindow _modWindow;

    private void Awake()
    {
        Log.Init(new BepInExLog(Logger));
        Log.Info("[TestPlugin] Awake called successfully.");

        _playerSnapshotPipeline = new StateSnapshotPipeline<PlayerDataSnapshot>();
        _modWindow = new SampleModWindow(_playerSnapshotPipeline);
    }

    private void OnEnable()
    {
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render += RenderLegacyOverlay;
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterWindow(_modWindow);
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab("Mods", "Toggle Sample View", () =>
        {
            if (ImGui.MenuItem("Toggle Sample Window", "", _modWindow.IsOpen))
            {
                _modWindow.IsOpen = !_modWindow.IsOpen;
            }
        });
    }

    private void OnDisable()
    {
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.Render -= RenderLegacyOverlay;
        DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterWindow(_modWindow);
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
}