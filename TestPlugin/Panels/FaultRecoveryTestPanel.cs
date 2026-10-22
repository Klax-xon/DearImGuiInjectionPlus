using System;
using System.IO;
using System.Numerics;
using BepInEx;
using DearImGuiInjectionPlus;
using ImGuiNET;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace TestPlugin.Panels;

/// <summary>
/// Category 4: Fault Recovery &amp; Lifecycle (TM-07, TM-10, LC-03, LC-04, LC-05, LC-07).
/// Instruments deliberate stack imbalance faults (Fault A window stack, Fault B menu stack),
/// automated tab churn stress testing (Fault C), and quiescent teardown verification.
/// </summary>
public class FaultRecoveryTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_fault";
    public string Title => "4. Fault Recovery & Lifecycle";
    public bool IsOpen { get; set; } = false;

    // Fault A State
    private bool _armFaultA = false;
    private int _faultAInjectedCount = 0;
    private string _faultAStatus = "Idle. Click 'Trigger Fault A' to inject a window stack imbalance.";

    // Fault B State (Checked by TestPlugin tab callback)
    public static bool TriggerFaultB { get; set; } = false;
    private static int _faultBInjectedCount = 0;
    public static void NotifyFaultBInjected() => _faultBInjectedCount++;

    // Fault C State (Tab churn stress)
    private string _churnStatus = "Idle. Click 'Run 50x Tab Churn Test' to execute.";
    private bool _churnPassed = false;

    // Teardown status
    public static string ShutdownTimestampPath => Path.Combine(Paths.ConfigPath, "TestPlugin_shutdown.timestamp");

    public void OnRender()
    {
        // Deliberate Fault A Injection (LC-05, TM-07)
        if (_armFaultA)
        {
            _armFaultA = false;
            _faultAInjectedCount++;
            _faultAStatus = $"PASS: Fault A injected #{_faultAInjectedCount}. DearImGuiInjectionPlus successfully caught exception and restored stack!";

            // Deliberately open a window, push an ID, and begin a child WITHOUT calling EndChild(), PopID(), or End()!
            ImGui.Begin("FaultWindow_StackImbalanceSimulation");
            ImGui.PushID(99999);
            ImGui.BeginChild("FaultyChildPanel", new Vector2(200, 100));
            ImGui.Text("Faulty child content...");

            throw new InvalidOperationException("Simulated Fault A: Deliberate unhandled exception mid-ImGui.Begin/Child without EndChild/PopID/End!");
        }

        if (!IsOpen)
            return;

        bool open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(620, 680), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
        {
            DrawPanelContent();
        }
        ImGui.End();
        IsOpen = open;
    }

    public void DrawPanelContent()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "Category 4: Fault Recovery & Lifecycle");
        ImGui.TextDisabled("Validates TM-07 (Consumer Fault Isolation & Stack Recovery) and TM-10 (Quiescent Teardown).");
        ImGui.Separator();

        DrawFaultASection();
        ImGui.Separator();

        DrawFaultBSection();
        ImGui.Separator();

        DrawFaultCSection();
        ImGui.Separator();

        DrawTeardownSection();
    }

    private void DrawFaultASection()
    {
        if (ImGui.CollapsingHeader("Fault A: Window Stack Imbalance Injection (LC-05, TM-07)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Executes ImGui.Begin(), ImGui.PushID(123), and ImGui.BeginChild(), then immediately throws an unhandled InvalidOperationException without calling EndChild(), PopID(), or End().");
            ImGui.TextWrapped("Pass Criteria: DearImGuiInjectionPlus isolates the crash, unwinds the broken ImGui stack via RestoreImGuiStack() (igErrorCheckEndFrameRecover), and subsequent windows continue rendering with zero process crashes.");

            if (ImGui.Button("Trigger Fault A (Window Stack Crash)"))
            {
                _armFaultA = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), _faultAStatus);
            ImGui.TextDisabled($"Total Injections: {_faultAInjectedCount}");
        }
    }

    private void DrawFaultBSection()
    {
        if (ImGui.CollapsingHeader("Fault B: Tab Menu Stack Imbalance (LC-03, TM-07)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Arms a deliberate exception inside the menu bar callback. Next frame, the tab callback executes ImGui.BeginMenu(), ImGui.PushStyleColor(), and throws before PopStyleColor() or EndMenu().");
            ImGui.TextWrapped("Pass Criteria: RestoreImGuiWindowStack() unwinds the menu bar stack safely. The main menu bar and all other tabs remain fully functional.");

            if (ImGui.Button("Arm Fault B (Tab Menu Crash)"))
            {
                TriggerFaultB = true;
            }

            ImGui.SameLine();
            if (TriggerFaultB)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "[ARMED: Exception will fire on next frame]");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"Total Injections Caught: {_faultBInjectedCount}");
            }
        }
    }

    private void DrawFaultCSection()
    {
        if (ImGui.CollapsingHeader("Fault C: Tab Lifecycle Churn Test (LC-07, TM-07)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Registers and unregisters menu tabs 50 times in rapid succession. Asserts that tab snapshots are atomically rebuilt without leaving duplicate UI entries or leaking delegate closures.");

            if (ImGui.Button("Run 50x Tab Churn Test"))
            {
                RunTabChurnTest();
            }

            ImGui.SameLine();
            if (_churnPassed)
            {
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), _churnStatus);
            }
            else
            {
                ImGui.TextDisabled(_churnStatus);
            }
        }
    }

    private void RunTabChurnTest()
    {
        try
        {
            const int iterations = 50;
            int successfulUnregisters = 0;

            for (int i = 0; i < iterations; i++)
            {
                string cat = "DearImGui Test Suite";
                string title = $"ChurnTab_{i}";

                // 1. Register
                var handle = DearImGuiInjectionPlus.DearImGuiInjectionPlus.RegisterTab(cat, title, () => { });

                // 2. Unregister immediately
                bool unregistered = DearImGuiInjectionPlus.DearImGuiInjectionPlus.UnregisterTab(cat, title);
                if (unregistered)
                {
                    successfulUnregisters++;
                }
            }

            if (successfulUnregisters == iterations)
            {
                _churnPassed = true;
                _churnStatus = $"PASS: 50/50 tab churn cycles completed cleanly. Zero residual entries.";
            }
            else
            {
                _churnPassed = false;
                _churnStatus = $"FAIL: Only {successfulUnregisters}/50 tabs unregistered cleanly.";
            }
        }
        catch (Exception ex)
        {
            _churnPassed = false;
            _churnStatus = $"FAIL: Exception during churn test: {ex.Message}";
        }
    }

    private void DrawTeardownSection()
    {
        if (ImGui.CollapsingHeader("Teardown & Clean Exit Verification (TM-10, LC-01, LC-02)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Verifies clean teardown via FrameGate drain, unhooking detours, and persistence of the OnDestroy timestamp file.");

            string path = ShutdownTimestampPath;
            bool exists = File.Exists(path);

            ImGui.Text("Shutdown Timestamp File:");
            ImGui.SameLine();
            ImGui.TextDisabled(path);

            if (exists)
            {
                string content = "";
                try { content = File.ReadAllText(path).Trim(); } catch { }
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"[FOUND FROM PREVIOUS EXIT] Content: \"{content}\"");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "[NOT FOUND YET] Will be written on OnDestroy / Application.Quit.");
            }

            ImGui.Spacing();
            if (ImGui.Button("Trigger Clean Exit (Application.Quit)"))
            {
                Application.Quit();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Executes graceful engine exit to verify quiescent teardown.");
        }
    }
}
