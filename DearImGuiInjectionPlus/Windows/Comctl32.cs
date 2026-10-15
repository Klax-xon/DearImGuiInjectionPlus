using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjectionPlus.Windows;

public static class Comctl32
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr SubclassProc(
        IntPtr hWnd,
        WindowMessage uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        WindowMessage uMsg,
        IntPtr wParam,
        IntPtr lParam);
}
