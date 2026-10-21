using System;
using DearImGuiInjectionPlus;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TestPlugin;

public class TestPluginRunner : MonoBehaviour
{
    private StateSnapshotPipeline<PlayerDataSnapshot> _playerSnapshotPipeline;
    private int _lastSceneHandle = -1;
    private string _cachedSceneName = "SampleScene";

    public TestPluginRunner(IntPtr handle) : base(handle) { }

    internal void Initialize(StateSnapshotPipeline<PlayerDataSnapshot> snapshotPipeline)
    {
        _playerSnapshotPipeline = snapshotPipeline;
    }

    private void Update()
    {
        if (_playerSnapshotPipeline == null)
            return;

        // Avoid per-frame IL2CPP string marshaling (IL2CPP.Il2CppStringToManaged) unless the active scene handle changed
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.handle != _lastSceneHandle)
        {
            _lastSceneHandle = activeScene.handle;
            var name = activeScene.name;
            _cachedSceneName = string.IsNullOrEmpty(name) ? "SampleScene" : name;
        }

        var snapshot = new PlayerDataSnapshot
        {
            Position = transform.position,
            Health = 100f,
            MaxHealth = 100f,
            CurrentScene = _cachedSceneName
        };
        _playerSnapshotPipeline.Publish(snapshot);
    }

    private void OnApplicationQuit()
    {
        TestPlugin.Instance?.Teardown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        TestPlugin.Instance?.Teardown(fromOnDestroy: true);
    }
}
