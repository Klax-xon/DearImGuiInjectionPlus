using DearImGuiInjectionPlus.Windows;
using ImGuiNET;

namespace DearImGuiInjectionPlus.Backends;

internal enum InputEventType : byte
{
    MouseMove,
    MouseButton,
    MouseWheel,
    Key,
    KeyModifiers,
    Char,
    Focus,
    CursorVisibility
}

internal struct InputEvent
{
    public InputEventType Type;
    public ImGuiMouseSource MouseSource;
    public float X;
    public float Y;
    public int Int1;
    public int Int2;
    public int Int3;
    public bool Bool1;
    public bool Bool2;
    public bool Bool3;
    public bool Bool4;

    public static InputEvent CreateMouseMove(float x, float y, ImGuiMouseSource source) => new()
    {
        Type = InputEventType.MouseMove,
        X = x,
        Y = y,
        MouseSource = source
    };

    public static InputEvent CreateMouseButton(int button, bool down, ImGuiMouseSource source) => new()
    {
        Type = InputEventType.MouseButton,
        Int1 = button,
        Bool1 = down,
        MouseSource = source
    };

    public static InputEvent CreateMouseWheel(float wheelX, float wheelY) => new()
    {
        Type = InputEventType.MouseWheel,
        X = wheelX,
        Y = wheelY
    };

    public static InputEvent CreateKeyModifiers(bool ctrl, bool shift, bool alt, bool super) => new()
    {
        Type = InputEventType.KeyModifiers,
        Bool1 = ctrl,
        Bool2 = shift,
        Bool3 = alt,
        Bool4 = super
    };

    public static InputEvent CreateKey(ImGuiKey key, bool down, User32.VirtualKey nativeKeycode, int nativeScancode) => new()
    {
        Type = InputEventType.Key,
        Int1 = (int)key,
        Bool1 = down,
        Int2 = (int)nativeKeycode,
        Int3 = nativeScancode
    };

    public static InputEvent CreateChar(ushort character) => new()
    {
        Type = InputEventType.Char,
        Int1 = character
    };

    public static InputEvent CreateFocus(bool focused) => new()
    {
        Type = InputEventType.Focus,
        Bool1 = focused
    };

    public static InputEvent CreateCursorVisibility(bool visible, float mouseX, float mouseY) => new()
    {
        Type = InputEventType.CursorVisibility,
        Bool1 = visible,
        X = mouseX,
        Y = mouseY
    };
}
