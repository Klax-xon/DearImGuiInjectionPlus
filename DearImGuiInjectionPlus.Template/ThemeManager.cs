using System.Numerics;
using ImGuiNET;

namespace DearImGuiInjectionPlus.Template;

public enum GuiTheme
{
    ModernDark = 0,
    HighContrastAccent = 1,
    ClassicImGui = 2
}

/// <summary>
/// Handles runtime visual styling and dynamic theme switching.
/// Provides 3 fully cohesive styling configurations:
/// 1. Default ImGui Classic (pure vanilla unstyled Dear ImGui look)
/// 2. Modern Sleek Dark (contemporary deep slate/charcoal look with rounded controls)
/// 3. High-Contrast / Accent Theme (obsidian background with vibrant neon emerald highlights)
/// </summary>
public static class ThemeManager
{
    public static GuiTheme CurrentTheme { get; private set; } = GuiTheme.ModernDark;

    public static readonly string[] ThemeNames = new[]
    {
        "Modern Sleek Dark",
        "High-Contrast Emerald Accent",
        "Default ImGui Classic"
    };

    public static void ApplyTheme(GuiTheme theme)
    {
        CurrentTheme = theme;
        var style = ImGui.GetStyle();

        switch (theme)
        {
            case GuiTheme.ModernDark:
                ApplyModernDark(style);
                break;
            case GuiTheme.HighContrastAccent:
                ApplyHighContrastEmerald(style);
                break;
            case GuiTheme.ClassicImGui:
                ApplyClassic(style);
                break;
        }
    }

    private static void ApplyModernDark(ImGuiStylePtr style)
    {
        // Ergonomic modern paddings and roundings
        style.WindowPadding = new Vector2(12f, 12f);
        style.FramePadding = new Vector2(8f, 5f);
        style.ItemSpacing = new Vector2(8f, 6f);
        style.ItemInnerSpacing = new Vector2(6f, 5f);
        style.TouchExtraPadding = new Vector2(0f, 0f);
        style.IndentSpacing = 20f;
        style.ScrollbarSize = 14f;
        style.GrabMinSize = 10f;

        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 1f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 1f;
        style.TabBorderSize = 0f;

        style.WindowRounding = 7f;
        style.ChildRounding = 5f;
        style.FrameRounding = 4f;
        style.PopupRounding = 5f;
        style.ScrollbarRounding = 5f;
        style.GrabRounding = 3f;
        style.TabRounding = 5f;

        style.WindowTitleAlign = new Vector2(0.5f, 0.5f);
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
        style.DisplaySafeAreaPadding = new Vector2(4f, 4f);

        // Deep Slate & Charcoal Palette with Azure Accents
        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new Vector4(0.92f, 0.94f, 0.96f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.50f, 0.55f, 0.62f, 1.00f);
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.10f, 0.12f, 0.15f, 0.95f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.12f, 0.14f, 0.18f, 0.65f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.11f, 0.13f, 0.17f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.22f, 0.26f, 0.33f, 0.65f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.16f, 0.19f, 0.25f, 0.85f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.22f, 0.26f, 0.34f, 0.95f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.28f, 0.33f, 0.43f, 1.00f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.09f, 0.10f, 0.13f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.13f, 0.16f, 0.21f, 1.00f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.08f, 0.09f, 0.12f, 0.60f);
        colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.11f, 0.13f, 0.17f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.09f, 0.10f, 0.13f, 0.60f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.25f, 0.30f, 0.39f, 0.80f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.33f, 0.39f, 0.50f, 0.95f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.40f, 0.48f, 0.60f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.35f, 0.65f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.35f, 0.65f, 1.00f, 0.85f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.48f, 0.75f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.18f, 0.22f, 0.29f, 0.90f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.26f, 0.32f, 0.42f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.32f, 0.40f, 0.52f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.24f, 0.32f, 0.80f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.26f, 0.33f, 0.44f, 0.90f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.33f, 0.41f, 0.54f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.22f, 0.26f, 0.33f, 0.70f);
        colors[(int)ImGuiCol.SeparatorHovered] = new Vector4(0.35f, 0.65f, 1.00f, 0.70f);
        colors[(int)ImGuiCol.SeparatorActive] = new Vector4(0.35f, 0.65f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.25f, 0.30f, 0.39f, 0.30f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.35f, 0.65f, 1.00f, 0.70f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.48f, 0.75f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.13f, 0.16f, 0.21f, 0.90f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.24f, 0.30f, 0.40f, 1.00f);
        colors[(int)ImGuiCol.TabActive] = new Vector4(0.20f, 0.25f, 0.34f, 1.00f);
        colors[(int)ImGuiCol.TabUnfocused] = new Vector4(0.10f, 0.12f, 0.15f, 0.90f);
        colors[(int)ImGuiCol.TabUnfocusedActive] = new Vector4(0.15f, 0.18f, 0.24f, 1.00f);
        colors[(int)ImGuiCol.PlotLines] = new Vector4(0.35f, 0.65f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.PlotLinesHovered] = new Vector4(0.55f, 0.80f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.PlotHistogram] = new Vector4(0.35f, 0.65f, 1.00f, 0.80f);
        colors[(int)ImGuiCol.PlotHistogramHovered] = new Vector4(0.55f, 0.80f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.35f, 0.65f, 1.00f, 0.35f);
        colors[(int)ImGuiCol.DragDropTarget] = new Vector4(0.35f, 0.65f, 1.00f, 0.95f);
        colors[(int)ImGuiCol.NavHighlight] = new Vector4(0.35f, 0.65f, 1.00f, 1.00f);
    }

    private static void ApplyHighContrastEmerald(ImGuiStylePtr style)
    {
        // Sharp, technical geometry with high contrast
        style.WindowPadding = new Vector2(10f, 10f);
        style.FramePadding = new Vector2(7f, 4f);
        style.ItemSpacing = new Vector2(8f, 5f);
        style.ItemInnerSpacing = new Vector2(5f, 4f);
        style.TouchExtraPadding = new Vector2(0f, 0f);
        style.IndentSpacing = 18f;
        style.ScrollbarSize = 13f;
        style.GrabMinSize = 9f;

        style.WindowBorderSize = 1.5f;
        style.ChildBorderSize = 1f;
        style.PopupBorderSize = 1.5f;
        style.FrameBorderSize = 1f;
        style.TabBorderSize = 0f;

        style.WindowRounding = 4f;
        style.ChildRounding = 3f;
        style.FrameRounding = 3f;
        style.PopupRounding = 3f;
        style.ScrollbarRounding = 3f;
        style.GrabRounding = 2f;
        style.TabRounding = 3f;

        style.WindowTitleAlign = new Vector2(0.5f, 0.5f);
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
        style.DisplaySafeAreaPadding = new Vector2(3f, 3f);

        // Obsidian Background with Vibrant Neon Emerald (#00E699 / #00FF9E)
        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new Vector4(0.95f, 0.98f, 0.96f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.40f, 0.52f, 0.46f, 1.00f);
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.05f, 0.07f, 0.06f, 0.97f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.07f, 0.10f, 0.08f, 0.85f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.06f, 0.08f, 0.07f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.00f, 0.88f, 0.55f, 0.55f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.08f, 0.14f, 0.11f, 0.90f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.12f, 0.22f, 0.17f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.16f, 0.30f, 0.23f, 1.00f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.04f, 0.06f, 0.05f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.07f, 0.14f, 0.10f, 1.00f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.04f, 0.06f, 0.05f, 0.70f);
        colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.06f, 0.09f, 0.07f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.05f, 0.07f, 0.06f, 0.70f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.00f, 0.70f, 0.44f, 0.70f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.00f, 0.88f, 0.55f, 0.90f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.20f, 1.00f, 0.65f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.00f, 1.00f, 0.62f, 1.00f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.00f, 0.88f, 0.55f, 0.95f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.20f, 1.00f, 0.65f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.08f, 0.22f, 0.16f, 0.85f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.12f, 0.35f, 0.24f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.00f, 0.75f, 0.48f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.10f, 0.25f, 0.18f, 0.80f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.14f, 0.36f, 0.26f, 0.90f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.00f, 0.75f, 0.48f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.00f, 0.88f, 0.55f, 0.40f);
        colors[(int)ImGuiCol.SeparatorHovered] = new Vector4(0.00f, 1.00f, 0.62f, 0.70f);
        colors[(int)ImGuiCol.SeparatorActive] = new Vector4(0.20f, 1.00f, 0.65f, 1.00f);
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.00f, 0.88f, 0.55f, 0.35f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.00f, 1.00f, 0.62f, 0.80f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.20f, 1.00f, 0.65f, 1.00f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.07f, 0.14f, 0.10f, 0.90f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.12f, 0.28f, 0.20f, 1.00f);
        colors[(int)ImGuiCol.TabActive] = new Vector4(0.10f, 0.24f, 0.17f, 1.00f);
        colors[(int)ImGuiCol.TabUnfocused] = new Vector4(0.05f, 0.08f, 0.06f, 0.90f);
        colors[(int)ImGuiCol.TabUnfocusedActive] = new Vector4(0.08f, 0.16f, 0.12f, 1.00f);
        colors[(int)ImGuiCol.PlotLines] = new Vector4(0.00f, 1.00f, 0.62f, 1.00f);
        colors[(int)ImGuiCol.PlotLinesHovered] = new Vector4(0.40f, 1.00f, 0.75f, 1.00f);
        colors[(int)ImGuiCol.PlotHistogram] = new Vector4(0.00f, 0.88f, 0.55f, 0.85f);
        colors[(int)ImGuiCol.PlotHistogramHovered] = new Vector4(0.00f, 1.00f, 0.62f, 1.00f);
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.00f, 0.88f, 0.55f, 0.35f);
        colors[(int)ImGuiCol.DragDropTarget] = new Vector4(0.00f, 1.00f, 0.62f, 0.95f);
        colors[(int)ImGuiCol.NavHighlight] = new Vector4(0.00f, 1.00f, 0.62f, 1.00f);
    }

    private static void ApplyClassic(ImGuiStylePtr style)
    {
        // Revert to vanilla Dear ImGui Classic colors and layout
        ImGui.StyleColorsClassic();

        style.WindowPadding = new Vector2(8f, 8f);
        style.FramePadding = new Vector2(4f, 3f);
        style.ItemSpacing = new Vector2(8f, 4f);
        style.ItemInnerSpacing = new Vector2(4f, 4f);
        style.TouchExtraPadding = new Vector2(0f, 0f);
        style.IndentSpacing = 21f;
        style.ScrollbarSize = 14f;
        style.GrabMinSize = 10f;

        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 1f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.TabBorderSize = 0f;

        style.WindowRounding = 0f;
        style.ChildRounding = 0f;
        style.FrameRounding = 0f;
        style.PopupRounding = 0f;
        style.ScrollbarRounding = 0f;
        style.GrabRounding = 0f;
        style.TabRounding = 0f;

        style.WindowTitleAlign = new Vector2(0.0f, 0.5f);
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
        style.DisplaySafeAreaPadding = new Vector2(3f, 3f);
    }
}
