using System;
using DearImGuiInjectionPlus;
using UnityEngine;

namespace TestPlugin;

public class TestPluginRunner : MonoBehaviour
{
    private StateSnapshotPipeline<PlayerDataSnapshot> _playerSnapshotPipeline;

    public TestPluginRunner(IntPtr handle) : base(handle) { }

    internal void Initialize(StateSnapshotPipeline<PlayerDataSnapshot> snapshotPipeline)
    {
        _playerSnapshotPipeline = snapshotPipeline;
    }

    private void Update()
    {
        if (_playerSnapshotPipeline == null)
            return;

        var snapshot = new PlayerDataSnapshot
        {
            Position = transform.position,
            Health = 100f,
            MaxHealth = 100f,
            CurrentScene = gameObject.scene.name ?? "SampleScene"
        };
        _playerSnapshotPipeline.Publish(snapshot);
    }

    private void OnDestroy()
    {
        TestPlugin.Instance?.Teardown();
    }
}
