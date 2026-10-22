using System;
using System.Numerics;
using System.Text;
using DearImGuiInjectionPlus.BepInEx;
using DearImGuiInjectionPlus.Template.State;
using DearImGuiInjectionPlus.Template.Utils;
using ImGuiNET;
using UnityEngine;

namespace DearImGuiInjectionPlus.Template.Tabs;

/// <summary>
/// Game Controls & Actions Tab.
/// Features one-shot action buttons, persistent state toggles,
/// parameter tuning sliders/steppers, and pre-allocated command text inputs.
/// Refactored with responsive table-based layouts to prevent widget collisions.
/// </summary>
public class ControlsTab : ITemplateTab
{
    public string TabName => "Game Controls";

    private readonly PlayerControlState _state;
    private readonly GuiConsole _console;

    // Pre-allocated UTF-8 buffer for text input (zero steady-state allocations)
    private readonly byte[] _commandBuffer = new byte[256];

    public ControlsTab(PlayerControlState state, GuiConsole console)
    {
        _state = state;
        _console = console;
    }

    public void Draw()
    {
        DrawActionButtonsSection();
        ImGui.Separator();

        DrawPersistentTogglesSection();
        ImGui.Separator();

        DrawTuningParametersSection();
        ImGui.Separator();

        DrawCommandInputSection();
    }

    private void DrawActionButtonsSection()
    {
        if (ImGui.CollapsingHeader("One-Shot Engine Actions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Dispatches actions asynchronously to the Unity main scripting thread:");
            ImGui.Spacing();

            // 2-Column Responsive Action Grid
            if (ImGui.BeginTable("##ActionButtonsGrid", 2, ImGuiTableFlags.None))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Button("Teleport to Spawn", new System.Numerics.Vector2(-1, 32)))
                {
                    _state.TeleportToSpawn(msg => _console.LogInfo(msg));
                }

                ImGui.TableNextColumn();
                if (ImGui.Button("Kill All Enemies", new System.Numerics.Vector2(-1, 32)))
                {
                    _state.KillAllEnemies(msg => _console.LogWarning(msg));
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Button("Heal Player Full", new System.Numerics.Vector2(-1, 32)))
                {
                    _state.HealPlayer(msg => _console.LogInfo(msg));
                }

                ImGui.TableNextColumn();
                if (ImGui.Button("Reload Scene", new System.Numerics.Vector2(-1, 32)))
                {
                    _state.ReloadCurrentScene(msg => _console.LogInfo(msg));
                }

                ImGui.EndTable();
            }
        }
    }

    private void DrawPersistentTogglesSection()
    {
        if (ImGui.CollapsingHeader("Persistent Cheats & Overrides", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // 2-Column Responsive Toggle Grid (no hardcoded pixel offsets)
            if (ImGui.BeginTable("##TogglesGrid", 2, ImGuiTableFlags.None))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool god = _state.GodMode;
                if (ImGui.Checkbox("God Mode (Invulnerability)", ref god))
                {
                    _state.GodMode = god;
                    _console.LogInfo($"God Mode toggled: {(god ? "ENABLED" : "DISABLED")}");
                }

                ImGui.TableNextColumn();
                bool stamina = _state.InfiniteStamina;
                if (ImGui.Checkbox("Infinite Stamina", ref stamina))
                {
                    _state.InfiniteStamina = stamina;
                    _console.LogInfo($"Infinite Stamina toggled: {(stamina ? "ENABLED" : "DISABLED")}");
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool freeze = _state.FreezeTime;
                if (ImGui.Checkbox("Freeze Time (timeScale = 0)", ref freeze))
                {
                    _state.ApplyFreezeTime(freeze, msg => _console.LogInfo(msg));
                }

                ImGui.TableNextColumn();
                bool noClip = _state.NoClip;
                if (ImGui.Checkbox("No Clip (Pass Through Walls)", ref noClip))
                {
                    _state.NoClip = noClip;
                    _console.LogInfo($"No Clip toggled: {(noClip ? "ENABLED" : "DISABLED")}");
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool ghost = _state.GhostMode;
                if (ImGui.Checkbox("Ghost Mode (Invisible to AI)", ref ghost))
                {
                    _state.GhostMode = ghost;
                    _console.LogInfo($"Ghost Mode toggled: {(ghost ? "ENABLED" : "DISABLED")}");
                }

                ImGui.EndTable();
            }
        }
    }

    private void DrawTuningParametersSection()
    {
        if (ImGui.CollapsingHeader("Engine Tuning & Sliders", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Responsive sliders leaving margin for label
            ImGui.SetNextItemWidth(-140f);
            float speed = _state.PlayerSpeedMultiplier;
            if (ImGui.SliderFloat("Player Speed", ref speed, 1.0f, 10.0f, "%.2fx"))
            {
                _state.ApplySpeedMultiplier(speed, msg => _console.LogInfo(msg));
            }

            ImGui.SetNextItemWidth(-140f);
            float fov = _state.FieldOfView;
            if (ImGui.SliderFloat("Field of View", ref fov, 60.0f, 120.0f, "%.1f deg"))
            {
                _state.ApplyFov(fov, msg => _console.LogInfo(msg));
            }

            ImGui.SetNextItemWidth(-140f);
            float gravity = _state.GravityScale;
            if (ImGui.SliderFloat("Gravity Scale", ref gravity, -2.0f, 3.0f, "%.2fx"))
            {
                _state.GravityScale = gravity;
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    Physics.gravity = new UnityEngine.Vector3(0, -9.81f * gravity, 0);
                });
                _console.LogInfo($"Gravity scale adjusted to {gravity:F2}x");
            }

            ImGui.SetNextItemWidth(-140f);
            float jump = _state.JumpForceMultiplier;
            if (ImGui.SliderFloat("Jump Force", ref jump, 1.0f, 5.0f, "%.2fx"))
            {
                _state.JumpForceMultiplier = jump;
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Row 1: Discrete Wave Stepper Controls (Clean row layout)
            ImGui.TextUnformatted("Wave / Level:");
            ImGui.SameLine();
            if (ImGui.Button(" - ##WaveMinus", new System.Numerics.Vector2(28, 22)))
            {
                if (_state.CurrentLevelOrWave > 1)
                {
                    _state.CurrentLevelOrWave--;
                    _console.LogInfo($"Wave set to {_state.CurrentLevelOrWave}");
                }
            }
            ImGui.SameLine();
            ImGui.Text($"Wave {_state.CurrentLevelOrWave}");
            ImGui.SameLine();
            if (ImGui.Button(" + ##WavePlus", new System.Numerics.Vector2(28, 22)))
            {
                _state.CurrentLevelOrWave++;
                _console.LogInfo($"Wave set to {_state.CurrentLevelOrWave}");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset to 1##WaveReset"))
            {
                _state.CurrentLevelOrWave = 1;
                _console.LogInfo("Wave reset to 1.");
            }

            ImGui.Spacing();

            // Row 2: Currency Manager Controls (Clean dedicated row without SameLine collision)
            ImGui.TextUnformatted($"Currency: {_state.CurrencyAmount:N0} Gold");
            ImGui.SameLine();
            if (ImGui.SmallButton("+100##Add100"))
            {
                _state.CurrencyAmount += 100;
                _console.LogInfo($"Added +100 Gold (Total: {_state.CurrencyAmount:N0})");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("+1,000##Add1k"))
            {
                _state.CurrencyAmount += 1000;
                _console.LogInfo($"Added +1,000 Gold (Total: {_state.CurrencyAmount:N0})");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("+10,000##Add10k"))
            {
                _state.CurrencyAmount += 10000;
                _console.LogInfo($"Added +10,000 Gold (Total: {_state.CurrencyAmount:N0})");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##ResetGold"))
            {
                _state.CurrencyAmount = 0;
                _console.LogWarning("Currency reset to 0.");
            }
        }
    }

    private void DrawCommandInputSection()
    {
        if (ImGui.CollapsingHeader("Developer Command Console", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Execute in-game developer commands or cheats (e.g. 'god', 'heal', 'speed 2.5', 'tp', 'kill'):");

            ImGui.SetNextItemWidth(-100f);
            bool enterPressed = ImGui.InputText(
                "##CommandInput",
                _commandBuffer,
                (uint)_commandBuffer.Length,
                ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.SameLine();
            bool submitClicked = ImGui.Button("Execute", new System.Numerics.Vector2(90, 0));

            if (enterPressed || submitClicked)
            {
                ExecuteSubmittedCommand();
            }
        }
    }

    private void ExecuteSubmittedCommand()
    {
        // Decode UTF-8 string up to null terminator
        int len = 0;
        while (len < _commandBuffer.Length && _commandBuffer[len] != 0)
        {
            len++;
        }

        if (len == 0)
            return;

        string commandText = Encoding.UTF8.GetString(_commandBuffer, 0, len).Trim();

        // Clear input buffer immediately
        Array.Clear(_commandBuffer, 0, _commandBuffer.Length);

        if (string.IsNullOrEmpty(commandText))
            return;

        _console.LogCommand(commandText);

        // Command parsing & routing
        string lower = commandText.ToLowerInvariant();
        if (lower is "god" or "godmode")
        {
            _state.GodMode = !_state.GodMode;
            _console.LogInfo($"God mode is now {(_state.GodMode ? "ENABLED" : "DISABLED")}");
        }
        else if (lower is "heal" or "healfull")
        {
            _state.HealPlayer(msg => _console.LogInfo(msg));
        }
        else if (lower is "kill" or "killall")
        {
            _state.KillAllEnemies(msg => _console.LogWarning(msg));
        }
        else if (lower is "tp" or "spawn")
        {
            _state.TeleportToSpawn(msg => _console.LogInfo(msg));
        }
        else if (lower is "freeze" or "time")
        {
            _state.ApplyFreezeTime(!_state.FreezeTime, msg => _console.LogInfo(msg));
        }
        else if (lower.StartsWith("speed "))
        {
            string arg = lower.Substring(6);
            if (float.TryParse(arg, out float val))
            {
                val = Math.Clamp(val, 0.1f, 20f);
                _state.ApplySpeedMultiplier(val, msg => _console.LogInfo(msg));
            }
            else
            {
                _console.LogError($"Invalid speed argument: '{arg}'");
            }
        }
        else if (lower.StartsWith("fov "))
        {
            string arg = lower.Substring(4);
            if (float.TryParse(arg, out float val))
            {
                val = Math.Clamp(val, 30f, 150f);
                _state.ApplyFov(val, msg => _console.LogInfo(msg));
            }
            else
            {
                _console.LogError($"Invalid FOV argument: '{arg}'");
            }
        }
        else
        {
            _console.LogWarning($"Unknown command: '{commandText}'. Type 'god', 'heal', 'kill', 'tp', 'speed <val>', or 'fov <val>'.");
        }
    }
}
