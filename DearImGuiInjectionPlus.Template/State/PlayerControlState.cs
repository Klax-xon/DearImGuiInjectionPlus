using System;
using DearImGuiInjectionPlus.BepInEx;
using UnityEngine;

namespace DearImGuiInjectionPlus.Template.State;

/// <summary>
/// Manages live game control states, cheats, and debug parameters.
/// State changes triggered in the ImGui interface are safely dispatched
/// asynchronously to the Unity main thread via <see cref="UnityMainThreadDispatcher"/>.
/// </summary>
public class PlayerControlState
{
    // Persistent Toggles
    public bool GodMode { get; set; } = false;
    public bool InfiniteStamina { get; set; } = false;
    public bool FreezeTime { get; set; } = false;
    public bool NoClip { get; set; } = false;
    public bool GhostMode { get; set; } = false;

    // Parameter Sliders
    public float PlayerSpeedMultiplier { get; set; } = 1.0f;
    public float FieldOfView { get; set; } = 90.0f;
    public float GravityScale { get; set; } = 1.0f;
    public float JumpForceMultiplier { get; set; } = 1.0f;

    // Stepper & Game values
    public int CurrentLevelOrWave { get; set; } = 1;
    public int CurrencyAmount { get; set; } = 500;

    /// <summary>
    /// Applies the FreezeTime state safely to Unity's Time.timeScale on the main thread.
    /// </summary>
    public void ApplyFreezeTime(bool freeze, Action<string> onLogged = null)
    {
        FreezeTime = freeze;
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            Time.timeScale = freeze ? 0.0f : 1.0f;
            onLogged?.Invoke($"[Controls] Game time scale set to {(freeze ? "0.0 (Frozen)" : "1.0 (Normal)")}");
        });
    }

    /// <summary>
    /// Applies a player speed multiplier change safely on the main thread.
    /// </summary>
    public void ApplySpeedMultiplier(float multiplier, Action<string> onLogged = null)
    {
        PlayerSpeedMultiplier = multiplier;
        onLogged?.Invoke($"[Controls] Player speed multiplier set to {multiplier:F2}x");
    }

    /// <summary>
    /// Applies FOV change safely on the main thread.
    /// </summary>
    public void ApplyFov(float fov, Action<string> onLogged = null)
    {
        FieldOfView = fov;
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (Camera.main != null)
            {
                Camera.main.fieldOfView = fov;
            }
            onLogged?.Invoke($"[Controls] Main Camera FOV set to {fov:F1}°");
        });
    }

    /// <summary>
    /// Dispatches a spawn teleport command to the main scripting thread.
    /// </summary>
    public void TeleportToSpawn(Action<string> onLogged = null)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null)
            {
                player.transform.position = Vector3.zero;
                onLogged?.Invoke($"[Action] Teleported Player '{player.name}' to (0, 0, 0)");
            }
            else
            {
                onLogged?.Invoke("[Action] No GameObject with tag 'Player' found. Teleported mock origin.");
            }
        });
    }

    /// <summary>
    /// Dispatches a kill all enemies action to the main thread.
    /// </summary>
    public void KillAllEnemies(Action<string> onLogged = null)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            var enemies = GameObject.FindGameObjectsWithTag("Enemy");
            int count = enemies != null ? enemies.Length : 0;
            if (enemies != null)
            {
                for (int i = 0; i < enemies.Length; i++)
                {
                    UnityEngine.Object.Destroy(enemies[i]);
                }
            }
            onLogged?.Invoke($"[Action] Eliminated {count} enemy instances from active scene.");
        });
    }

    /// <summary>
    /// Dispatches a full heal action to the main thread.
    /// </summary>
    public void HealPlayer(Action<string> onLogged = null)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            onLogged?.Invoke("[Action] Player health restored to 100% (Full HP).");
        });
    }

    /// <summary>
    /// Dispatches a reload active scene command on the main thread.
    /// </summary>
    public void ReloadCurrentScene(Action<string> onLogged = null)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                UnityEngine.SceneManagement.SceneManager.LoadScene(scene.buildIndex);
                onLogged?.Invoke($"[Action] Reloaded scene '{scene.name}' (BuildIndex: {scene.buildIndex})");
            }
            catch (Exception ex)
            {
                onLogged?.Invoke($"[Action Error] Failed to reload scene: {ex.Message}");
            }
        });
    }
}
