using UnityEngine;

namespace DearImGuiInjectionPlus.Template.State;

/// <summary>
/// Value-type state snapshot containing live game telemetry.
/// Sampled from the Unity main scripting thread and read by the ImGui render thread.
/// Because this is a struct handled by <see cref="DearImGuiInjectionPlus.StructSnapshotPipeline{T}"/>,
/// snapshot sampling, publishing, and consumption are strictly zero-allocation.
/// </summary>
public struct GameTelemetrySnapshot
{
    public float Fps;
    public float DeltaTime;
    public float SmoothDeltaTime;

    public Vector3 PlayerPosition;
    public Vector3 PlayerVelocity;
    public float PlayerHealth;
    public float PlayerMaxHealth;

    public int ActiveEnemies;
    public bool IsPlayerGrounded;
    public bool IsGamePaused;
    public ulong FrameCount;

    public string SceneName;
    public string UnityVersion;
    public string GraphicsBackend;

    public static GameTelemetrySnapshot CreateDefault()
    {
        return new GameTelemetrySnapshot
        {
            Fps = 60.0f,
            DeltaTime = 0.0166f,
            SmoothDeltaTime = 0.0166f,
            PlayerPosition = Vector3.zero,
            PlayerVelocity = Vector3.zero,
            PlayerHealth = 100.0f,
            PlayerMaxHealth = 100.0f,
            ActiveEnemies = 0,
            IsPlayerGrounded = true,
            IsGamePaused = false,
            FrameCount = 0,
            SceneName = "Initializing...",
            UnityVersion = Application.unityVersion,
            GraphicsBackend = SystemInfo.graphicsDeviceType.ToString()
        };
    }
}
