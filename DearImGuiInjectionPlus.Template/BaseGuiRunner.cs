using System;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Template.State;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DearImGuiInjectionPlus.Template;

/// <summary>
/// IL2CPP-injected MonoBehaviour executing on the Unity main scripting thread.
/// Responsible for:
/// 1. Sampling engine state into the zero-allocation <see cref="StructSnapshotPipeline{T}"/>
/// 2. Monitoring toggle hotkeys via <see cref="Input.GetKeyDown(KeyCode)"/>
/// 3. Applying frame-level cheats (e.g. GodMode health lock, timeScale enforcement)
/// </summary>
public class BaseGuiRunner : MonoBehaviour
{
    private string _cachedSceneName = "DefaultScene";
    private float _nextSceneCheckTime;
    private float _mockAngle;
    internal static bool _isQuitting;

    public BaseGuiRunner(IntPtr handle) : base(handle) { }

    private void Awake()
    {
        _isQuitting = false;
        try
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(activeScene.name))
            {
                _cachedSceneName = activeScene.name;
            }
        }
        catch
        {
            _cachedSceneName = "ActiveScene";
        }
    }

    private void Update()
    {
        try
        {
            // 1. Hotkey Detection on the Unity Main Thread (Unified with framework cursor toggle)
            var toggleKey = BaseGuiPlugin.ToggleKeyConfig?.Value ?? KeyCode.Insert;
            if (toggleKey == KeyCode.F1)
            {
                toggleKey = KeyCode.Insert;
            }

            if (Input.GetKeyDown(toggleKey) || Input.GetKeyDown(KeyCode.Insert))
            {
                global::DearImGuiInjectionPlus.DearImGuiInjectionPlus.ToggleCursorVisibility();
            }

            // 2. Zero-Allocation Telemetry Capture
            var pipeline = BaseGuiPlugin.Instance?.TelemetryPipeline;
            if (pipeline == null)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextSceneCheckTime)
            {
                _nextSceneCheckTime = now + 1.0f; // Sample scene name at 1 Hz to eliminate steady-state GC allocs
                try
                {
                    var scene = SceneManager.GetActiveScene();
                    if (!string.IsNullOrEmpty(scene.name))
                    {
                        _cachedSceneName = scene.name;
                    }
                }
                catch
                {
                    // Fallback during scene transitions
                }
            }

            float dt = Time.unscaledDeltaTime;
            float fps = dt > 0.0001f ? (1.0f / dt) : 60.0f;

            // Sample or simulate player coordinates
            Vector3 playerPos = Vector3.zero;
            Vector3 playerVel = Vector3.zero;
            bool isGrounded = true;

            var playerGo = GameObject.FindWithTag("Player");
            if (playerGo != null)
            {
                playerPos = playerGo.transform.position;
            }
            else if (Camera.main != null)
            {
                playerPos = Camera.main.transform.position;
            }
            else
            {
                // Smooth mock coordinates so developers observe live telemetry in template testing
                _mockAngle += dt * 0.5f;
                playerPos = new Vector3(Mathf.Cos(_mockAngle) * 15f, 1.2f, Mathf.Sin(_mockAngle) * 15f);
                playerVel = new Vector3(-Mathf.Sin(_mockAngle) * 7.5f, 0f, Mathf.Cos(_mockAngle) * 7.5f);
            }

            // Sample enemies
            int enemyCount = 0;
            try
            {
                var enemies = GameObject.FindGameObjectsWithTag("Enemy");
                enemyCount = enemies != null ? enemies.Length : 0;
            }
            catch
            {
                // Tag might not exist in blank project
            }

            // Apply persistent state overrides
            var controlState = BaseGuiPlugin.Instance?.ControlState;
            if (controlState != null)
            {
                if (controlState.FreezeTime && Time.timeScale != 0f)
                {
                    Time.timeScale = 0f;
                }
            }

            var snapshot = new GameTelemetrySnapshot
            {
                Fps = fps,
                DeltaTime = dt,
                SmoothDeltaTime = Time.smoothDeltaTime,
                PlayerPosition = playerPos,
                PlayerVelocity = playerVel,
                PlayerHealth = controlState?.GodMode == true ? 100.0f : 85.0f,
                PlayerMaxHealth = 100.0f,
                ActiveEnemies = enemyCount,
                IsPlayerGrounded = isGrounded,
                IsGamePaused = Mathf.Approximately(Time.timeScale, 0f),
                FrameCount = (ulong)Time.frameCount,
                SceneName = _cachedSceneName,
                UnityVersion = Application.unityVersion,
                GraphicsBackend = SystemInfo.graphicsDeviceType.ToString()
            };

            // Write into triple-buffered value pipeline (strictly 0 allocations)
            pipeline.Publish(in snapshot);
        }
        catch (Exception ex)
        {
            Log.Error($"[BaseGuiRunner] Exception in Update: {ex}");
        }
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        BaseGuiPlugin.Instance?.Teardown();
    }

    private void OnDestroy()
    {
        if (!_isQuitting)
        {
            Log.Warning("[BaseGuiRunner] Runner destroyed outside of application quit. Respawning...");
            BaseGuiPlugin.Instance?.EnsureRunnerAlive();
            return;
        }

        BaseGuiPlugin.Instance?.Teardown();
    }
}
