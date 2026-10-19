using System;
using System.Numerics;
using System.Text;
using DearImGuiInjectionPlus;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;

namespace TestPlugin.Panels;

/// <summary>
/// Category 3: Input, Windows &amp; Unicode Validator (TM-05, TM-06, INP-01, INP-02, INP-04, INP-06, INP-08, SEC-01).
/// Validates multilingual text input (Polish, CJK, Emojis, dead keys), IsWindowUnicode status,
/// real-time capture flags (WantCaptureMouse/Keyboard), and live key/mouse hardware monitors.
/// </summary>
public class InputTestPanel : IImGuiWindow
{
    public string Id => "dearimgui_test_input";
    public string Title => "3. Input, Windows & Unicode Validator";
    public bool IsOpen { get; set; } = true;

    // Unicode Input Buffer (1024 bytes UTF-8)
    private readonly byte[] _inputBuffer = new byte[1024];
    private string _cachedDisplayString = "";

    public InputTestPanel()
    {
        SetInputText("Zażółć gęślą jaźń 日本語テスト 🎮");
    }

    private void SetInputText(string text)
    {
        Array.Clear(_inputBuffer, 0, _inputBuffer.Length);
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int len = Math.Min(bytes.Length, _inputBuffer.Length - 1);
            Array.Copy(bytes, 0, _inputBuffer, 0, len);
            _inputBuffer[len] = 0;
        }
        _cachedDisplayString = text ?? "";
    }

    private void AppendInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        int currentLen = 0;
        while (currentLen < _inputBuffer.Length && _inputBuffer[currentLen] != 0)
        {
            currentLen++;
        }

        var newBytes = Encoding.UTF8.GetBytes(text);
        if (currentLen + newBytes.Length < _inputBuffer.Length)
        {
            Array.Copy(newBytes, 0, _inputBuffer, currentLen, newBytes.Length);
            _inputBuffer[currentLen + newBytes.Length] = 0;
            _cachedDisplayString = Encoding.UTF8.GetString(_inputBuffer, 0, currentLen + newBytes.Length);
        }
    }

    public void OnRender()
    {
        if (!IsOpen)
            return;

        bool open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(640, 720), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;
            DrawPanelContent();
        }
        ImGui.End();
    }

    public void DrawPanelContent()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "Category 3: Input, Windows & Unicode Validator");
        ImGui.TextDisabled("Validates TM-05 (Input Swallowing & Stuck Keys) and TM-06 (Unicode & CJK IME Input).");
        ImGui.Separator();

        DrawMultilingualUnicodeSection();
        ImGui.Separator();

        DrawInputCaptureHUDSection();
        ImGui.Separator();

        DrawLiveHardwareMonitorSection();
    }

    private void DrawMultilingualUnicodeSection()
    {
        if (ImGui.CollapsingHeader("Multilingual Unicode & IME Text Box (TM-06, INP-01, INP-08)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            IntPtr hwnd = DearImGuiInjectionPlus.DearImGuiInjectionPlus.WindowHandle;
            bool isUnicode = hwnd != IntPtr.Zero && User32.IsWindowUnicode(hwnd);

            ImGui.Text("Native Win32 Window Unicode Status:");
            ImGui.SameLine();
            if (isUnicode)
            {
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), "[PASS] IsWindowUnicode(HWND) == TRUE (UTF-16 Subclass Intact)");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "[FAIL] IsWindowUnicode(HWND) == FALSE (ANSI Degradation!)");
            }

            ImGui.Spacing();
            ImGui.Text("Interactive Text Input (Type directly or use quick-paste buttons):");
            ImGui.InputText("##UnicodeInput", _inputBuffer, (uint)_inputBuffer.Length);

            // Compute length
            int byteLen = 0;
            while (byteLen < _inputBuffer.Length && _inputBuffer[byteLen] != 0) byteLen++;
            string currentStr = Encoding.UTF8.GetString(_inputBuffer, 0, byteLen);

            ImGui.TextDisabled($"Length: {currentStr.Length} chars | {byteLen} UTF-8 bytes");

            ImGui.Spacing();
            ImGui.Text("Quick-Paste Test Strings:");

            // Polish
            if (ImGui.Button("Polish: Zażółć gęślą jaźń"))
            {
                SetInputText("Zażółć gęślą jaźń");
            }
            ImGui.SameLine();
            if (ImGui.Button("Japanese: 日本語テスト"))
            {
                SetInputText("日本語テスト");
            }
            ImGui.SameLine();
            if (ImGui.Button("Korean: 한국어"))
            {
                SetInputText("한국어");
            }

            // Chinese, Emojis, German
            if (ImGui.Button("Chinese: 测试中文输入法"))
            {
                SetInputText("测试中文输入法");
            }
            ImGui.SameLine();
            if (ImGui.Button("Emojis: 🎮 🚀 ✨ 🧪 🕹️ 🎯"))
            {
                SetInputText("🎮 🚀 ✨ 🧪 🕹️ 🎯");
            }
            ImGui.SameLine();
            if (ImGui.Button("German: Größenmaßstäbe & Übergrößen"))
            {
                SetInputText("Größenmaßstäbe & Übergrößen");
            }

            // Cyrillic, French
            if (ImGui.Button("Cyrillic: Привет, мир!"))
            {
                SetInputText("Привет, мир!");
            }
            ImGui.SameLine();
            if (ImGui.Button("French: Crème brûlée & fête"))
            {
                SetInputText("Crème brûlée & fête");
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                SetInputText("");
            }
        }
    }

    private void DrawInputCaptureHUDSection()
    {
        if (ImGui.CollapsingHeader("Real-Time Input Capture HUD (TM-05, INP-02, INP-06)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Visualizes live capture flags published by the render thread and toggles global vs selective input swallowing.");

            bool wantMouse = DearImGuiInjectionPlus.DearImGuiInjectionPlus.WantCaptureMouse;
            bool wantKb = DearImGuiInjectionPlus.DearImGuiInjectionPlus.WantCaptureKeyboard;
            bool isCursor = DearImGuiInjectionPlus.DearImGuiInjectionPlus.IsCursorVisible;

            ImGui.Columns(3, "CaptureHUDCols", false);

            ImGui.Text("WantCaptureMouse:");
            DrawBadge(wantMouse ? "CAPTURED [TRUE]" : "PASSTHROUGH [FALSE]", wantMouse);
            ImGui.NextColumn();

            ImGui.Text("WantCaptureKeyboard:");
            DrawBadge(wantKb ? "CAPTURED [TRUE]" : "PASSTHROUGH [FALSE]", wantKb);
            ImGui.NextColumn();

            ImGui.Text("IsCursorVisible:");
            DrawBadge(isCursor ? "VISIBLE [TRUE]" : "HIDDEN [FALSE]", isCursor);
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            ImGui.Separator();

            bool blockAll = DearImGuiInjectionPlus.DearImGuiInjectionPlus.BlockAllInputsWhenOpen;
            if (ImGui.Checkbox("BlockAllInputsWhenOpen (Global Input Swallowing)", ref blockAll))
            {
                DearImGuiInjectionPlus.DearImGuiInjectionPlus.BlockAllInputsWhenOpen = blockAll;
            }

            if (blockAll)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                    "Mode: FULL SWALLOWING. All game inputs (mouse clicks, mouse look, keystrokes) are completely blocked while overlay is open.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f),
                    "Mode: SELECTIVE SWALLOWING. Clicking outside ImGui windows forwards inputs to the game engine; clicking inside ImGui swallows input.");
            }
        }
    }

    private void DrawBadge(string text, bool active)
    {
        Vector4 col = active ? new Vector4(0.2f, 0.9f, 0.3f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.TextColored(col, text);
    }

    private void DrawLiveHardwareMonitorSection()
    {
        if (ImGui.CollapsingHeader("Live Key, Mouse & Hardware Monitor (TH-02, INP-04)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var io = ImGui.GetIO();

            ImGui.Columns(2, "HardwareCols", true);

            ImGui.Text("Mouse Coordinates:");
            ImGui.NextColumn();
            ImGui.Text($"X: {io.MousePos.X:F1}, Y: {io.MousePos.Y:F1}");
            ImGui.NextColumn();

            ImGui.Text("Mouse Delta:");
            ImGui.NextColumn();
            ImGui.Text($"dX: {io.MouseDelta.X:F2}, dY: {io.MouseDelta.Y:F2}");
            ImGui.NextColumn();

            ImGui.Text("Mouse Wheel:");
            ImGui.NextColumn();
            ImGui.Text($"V: {io.MouseWheel:F2}, H: {io.MouseWheelH:F2}");
            ImGui.NextColumn();

            ImGui.Text("Mouse Buttons Mask:");
            ImGui.NextColumn();
            bool lmb = io.MouseDown[0];
            bool rmb = io.MouseDown[1];
            bool mmb = io.MouseDown[2];
            ImGui.Text($"LMB: {(lmb ? "[DOWN]" : "UP")} | RMB: {(rmb ? "[DOWN]" : "UP")} | MMB: {(mmb ? "[DOWN]" : "UP")}");
            ImGui.NextColumn();

            ImGui.Text("Modifiers:");
            ImGui.NextColumn();
            ImGui.Text($"Ctrl: {io.KeyCtrl} | Shift: {io.KeyShift} | Alt: {io.KeyAlt} | Super: {io.KeySuper}");
            ImGui.NextColumn();

            ImGui.Text("Viewport DisplaySize:");
            ImGui.NextColumn();
            ImGui.Text($"{io.DisplaySize.X:F0} x {io.DisplaySize.Y:F0} px");
            ImGui.NextColumn();

            ImGui.Columns(1);

            ImGui.Spacing();
            ImGui.Text("Currently Depressed Keys:");
            var sb = new StringBuilder();
            for (int key = 0; key < 512; key++)
            {
                if (io.KeysDown[key])
                {
                    sb.Append((ImGuiKey)key).Append(" (").Append(key).Append(") ");
                }
            }

            if (sb.Length == 0)
            {
                ImGui.TextDisabled("None");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), sb.ToString());
            }
        }
    }
}
