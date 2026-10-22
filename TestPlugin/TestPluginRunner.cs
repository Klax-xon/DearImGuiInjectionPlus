using System;
using DearImGuiInjectionPlus;
using UnityEngine;
using UnityEngine.SceneManagement;
using PluginLog = TestPlugin.Log;

namespace TestPlugin;

public class TestPluginRunner : MonoBehaviour
{
    private string _cachedSceneName = "SampleScene";
    private float _nextSceneCheckTime;
    internal static bool _isQuitting;

    public TestPluginRunner(IntPtr handle) : base(handle) { }

    private void Awake()
    {
        _isQuitting = false;
    }

    private void Update()
    {
        try
        {
            var pipeline = TestPlugin.Instance?.PlayerSnapshotPipeline;
            if (pipeline == null)
                return;

            float now = Time.time;
            if (now >= _nextSceneCheckTime)
            {
                _nextSceneCheckTime = now + 1.0f; // Sample scene name at 1 Hz, avoiding per-frame string allocation
                try
                {
                    var activeScene = SceneManager.GetActiveScene();
                    var name = activeScene.name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        _cachedSceneName = name;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Debug($"[TestPluginRunner] Scene query fallback: {ex.Message}");
                }
            }

            var snapshot = new PlayerDataSnapshot
            {
                Position = transform.position,
                Health = 100f,
                MaxHealth = 100f,
                CurrentScene = _cachedSceneName
            };
            pipeline.Publish(snapshot);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[TestPluginRunner] Exception in Update: {ex}");
        }
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        TestPlugin.Instance?.Teardown(fromOnDestroy: false);
    }

    private void OnDestroy()
    {
        if (!_isQuitting)
        {
            PluginLog.Warning("[TestPlugin] TestPluginRunner destroyed outside of application quit (scene transition). Suppressing premature teardown.");
            TestPlugin.Instance?.EnsureRunnerAlive();
            return;
        }

        TestPlugin.Instance?.Teardown(fromOnDestroy: true);
    }
}
