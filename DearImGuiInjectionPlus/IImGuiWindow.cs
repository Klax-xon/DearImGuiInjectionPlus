namespace DearImGuiInjectionPlus;

/// <summary>
/// Interface representing an extensible ImGui window view.
/// Allows plugins and mods to register modular UI windows with isolated exception handling.
/// </summary>
public interface IImGuiWindow
{
    /// <summary>
    /// Unique identifier for this window (e.g. plugin GUID or window slug).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display title rendered on window headers or navigation tabs.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Whether this window is currently visible and rendering.
    /// </summary>
    bool IsOpen { get; set; }

    /// <summary>
    /// ImGui rendering callback executed once per frame within the active ImGui frame context.
    /// Execution is wrapped in an exception boundary to isolate faults from other mods.
    /// </summary>
    void OnRender();
}
