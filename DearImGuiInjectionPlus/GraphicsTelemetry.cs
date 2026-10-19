using System;

namespace DearImGuiInjectionPlus;

/// <summary>
/// Provides live runtime telemetry and diagnostics for the active DirectX swapchain and graphics device.
/// Updated every frame on the render thread during swapchain presentation.
/// </summary>
public static class GraphicsTelemetry
{
    private static volatile string _backendName = "Unknown";
    private static IntPtr _windowHandle = IntPtr.Zero;
    private static IntPtr _swapChainPointer = IntPtr.Zero;
    private static volatile string _backBufferFormat = "Unknown";
    private static int _width;
    private static int _height;
    private static int _refreshRateNumerator;
    private static int _refreshRateDenominator;
    private static volatile bool _isHdr;
    private static volatile string _colorSpaceName = "Unknown";

    /// <summary>
    /// Active DirectX graphics backend ("Direct3D 11" or "Direct3D 12").
    /// </summary>
    public static string BackendName
    {
        get => _backendName;
        internal set => _backendName = value;
    }

    /// <summary>
    /// Win32 window handle (HWND) associated with the active swapchain.
    /// </summary>
    public static IntPtr WindowHandle
    {
        get => _windowHandle;
        internal set => _windowHandle = value;
    }

    /// <summary>
    /// Native pointer to the active IDXGISwapChain.
    /// </summary>
    public static IntPtr SwapChainPointer
    {
        get => _swapChainPointer;
        internal set => _swapChainPointer = value;
    }

    /// <summary>
    /// Active swapchain backbuffer DXGI format (e.g. R8G8B8A8_UNorm, R10G10B10A2_UNorm, R16G16B16A16_Float).
    /// </summary>
    public static string BackBufferFormat
    {
        get => _backBufferFormat;
        internal set => _backBufferFormat = value;
    }

    /// <summary>
    /// Backbuffer width in pixels.
    /// </summary>
    public static int Width
    {
        get => _width;
        internal set => _width = value;
    }

    /// <summary>
    /// Backbuffer height in pixels.
    /// </summary>
    public static int Height
    {
        get => _height;
        internal set => _height = value;
    }

    /// <summary>
    /// Refresh rate numerator reported by the swapchain mode description.
    /// </summary>
    public static int RefreshRateNumerator
    {
        get => _refreshRateNumerator;
        internal set => _refreshRateNumerator = value;
    }

    /// <summary>
    /// Refresh rate denominator reported by the swapchain mode description.
    /// </summary>
    public static int RefreshRateDenominator
    {
        get => _refreshRateDenominator;
        internal set => _refreshRateDenominator = value;
    }

    /// <summary>
    /// Calculated refresh rate in Hertz.
    /// </summary>
    public static float RefreshRateHz
    {
        get
        {
            int den = _refreshRateDenominator;
            return den > 0 ? (float)_refreshRateNumerator / den : 0f;
        }
    }

    /// <summary>
    /// True if the detected backbuffer format is an HDR format (HDR10 or scRGB).
    /// </summary>
    public static bool IsHdr
    {
        get => _isHdr;
        internal set => _isHdr = value;
    }

    /// <summary>
    /// Human-readable color space classification (e.g. "HDR10 (R10G10B10A2_UNorm)", "scRGB (R16G16B16A16_Float)", "sRGB", "SDR Linear").
    /// </summary>
    public static string ColorSpaceName
    {
        get => _colorSpaceName;
        internal set => _colorSpaceName = value;
    }

    /// <summary>
    /// Total rendered vertices across all ImGui draw lists from the most recent frame.
    /// </summary>
    public static int TotalVertices { get; internal set; }

    /// <summary>
    /// Total rendered indices across all ImGui draw lists from the most recent frame.
    /// </summary>
    public static int TotalIndices { get; internal set; }

    internal static void Update(
        string backendName,
        IntPtr windowHandle,
        IntPtr swapChainPointer,
        string format,
        int width,
        int height,
        int refreshNum,
        int refreshDen,
        bool isHdr,
        string colorSpace)
    {
        _backendName = backendName ?? "Unknown";
        _windowHandle = windowHandle;
        _swapChainPointer = swapChainPointer;
        _backBufferFormat = format ?? "Unknown";
        _width = width;
        _height = height;
        _refreshRateNumerator = refreshNum;
        _refreshRateDenominator = refreshDen;
        _isHdr = isHdr;
        _colorSpaceName = colorSpace ?? "Unknown";
    }
}
