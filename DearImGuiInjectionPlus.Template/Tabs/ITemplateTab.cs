namespace DearImGuiInjectionPlus.Template.Tabs;

/// <summary>
/// Modular tab view interface for the Base GUI window.
/// Adding a new custom tool tab to this template requires only implementing
/// this interface and registering it in <see cref="BaseGuiWindow"/>.
/// </summary>
public interface ITemplateTab
{
    /// <summary>
    /// Display name rendered on the tab header item.
    /// </summary>
    string TabName { get; }

    /// <summary>
    /// ImGui draw routine executed when this tab is selected and visible.
    /// </summary>
    void Draw();
}
