using System;
using System.Collections.Generic;
using CppInterop;
using Reloaded.Hooks;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace RendererFinder.Renderers;

/// <summary>
/// Contains a full list of IDXGISwapChain functions to be used
/// as an indexer into the SwapChain Virtual Function Table entries.
/// </summary>
public enum IDXGISwapChain
{
    // IUnknown
    QueryInterface = 0,
    AddRef = 1,
    Release = 2,

    // IDXGIObject
    SetPrivateData = 3,
    SetPrivateDataInterface = 4,
    GetPrivateData = 5,
    GetParent = 6,

    // IDXGIDeviceSubObject
    GetDevice = 7,

    // IDXGISwapChain
    Present = 8,
    GetBuffer = 9,
    SetFullscreenState = 10,
    GetFullscreenState = 11,
    GetDesc = 12,
    ResizeBuffers = 13,
    ResizeTarget = 14,
    GetContainingOutput = 15,
    GetFrameStatistics = 16,
    GetLastPresentCount = 17,
}

public class DX11Renderer : IRenderer
{
    private static readonly List<object> _cache = new();
    private static SwapChain _cachedSwapChain;
    private static volatile bool _loggedPresent = false;
    private const uint DXGI_PRESENT_TEST = 0x00000001;

    [ThreadStatic]
    private static bool _isInsidePresent;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainPresentDelegate(IntPtr self, uint syncInterval, uint flags);

    private static readonly CDXGISwapChainPresentDelegate _swapChainPresentHookDelegate = SwapChainPresentHook;
    private static Hook<CDXGISwapChainPresentDelegate> _swapChainPresentHook;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainPresent1Delegate(IntPtr self, uint syncInterval, uint flags, IntPtr presentParameters);

    private static readonly CDXGISwapChainPresent1Delegate _swapChainPresent1HookDelegate = SwapChainPresent1Hook;
    private static Hook<CDXGISwapChainPresent1Delegate> _swapChainPresent1Hook;

    private static readonly object _presentLock = new();

    public static event Action<SwapChain, uint, uint> OnPresent
    {
        add
        {
            lock (_presentLock)
            {
                _onPresentAction += value;
            }
        }
        remove
        {
            lock (_presentLock)
            {
                _onPresentAction -= value;
            }
        }
    }
    private static Action<SwapChain, uint, uint> _onPresentAction;

    public unsafe bool Init()
    {
        Log.Info("[DX11Renderer] Initializing DXGI 1.2 swapchain hooks via IDXGIFactory2 (Present Slot 8, Present1 Slot 22)...");

        IntPtr windowHandle = IntPtr.Zero;
        Device device = null;
        SharpDX.DXGI.Device dxgiDevice = null;
        Adapter adapter = null;
        Factory2 factory2 = null;
        SwapChain1 swapChain1 = null;

        try
        {
            // Step 1: Create dummy window
            try
            {
                Log.Info("[DX11Renderer] Creating dummy window...");
                windowHandle = Windows.User32.CreateFakeWindow();
                if (windowHandle == IntPtr.Zero)
                {
                    Log.Error("[DX11Renderer] Failed to create dummy window for DXGI initialization.");
                    return false;
                }
                Log.Info($"[DX11Renderer] Dummy window created successfully: {windowHandle:X8}");
            }
            catch (Exception ex)
            {
                Log.Error($"[DX11Renderer] Exception creating dummy window: {ex}");
                return false;
            }

            // Step 2: Calling D3D11CreateDevice
            try
            {
                Log.Info("[DX11Renderer] Calling D3D11CreateDevice...");
                device = new Device(
                    DriverType.Hardware,
                    DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });
                Log.Info($"[DX11Renderer] D3D11CreateDevice succeeded. Device: {device.NativePointer:X8}, FeatureLevel: {device.FeatureLevel}");
            }
            catch (Exception ex)
            {
                var hrStr = ex is SharpDX.SharpDXException sdx ? $" (HRESULT: 0x{sdx.ResultCode.Code:X8})" : "";
                Log.Error($"[DX11Renderer] Exception calling D3D11CreateDevice{hrStr}: {ex}");
                return false;
            }

            // Step 3: Querying IDXGIFactory2
            try
            {
                Log.Info("[DX11Renderer] Querying IDXGIFactory2...");
                dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
                adapter = dxgiDevice.Adapter;
                factory2 = adapter.GetParent<Factory2>();
                Log.Info($"[DX11Renderer] Querying IDXGIFactory2 succeeded. Factory2: {factory2.NativePointer:X8}");
            }
            catch (Exception ex)
            {
                var hrStr = ex is SharpDX.SharpDXException sdx ? $" (HRESULT: 0x{sdx.ResultCode.Code:X8})" : "";
                Log.Error($"[DX11Renderer] Exception querying IDXGIFactory2{hrStr}: {ex}");
                return false;
            }

            // Step 4: Calling CreateSwapChainForHwnd
            try
            {
                Log.Info("[DX11Renderer] Calling CreateSwapChainForHwnd...");
                var desc1 = new SwapChainDescription1
                {
                    Width = 1,
                    Height = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    SwapEffect = SwapEffect.FlipDiscard
                };

                try
                {
                    swapChain1 = new SwapChain1(factory2, device, windowHandle, ref desc1);
                    Log.Info($"[DX11Renderer] CreateSwapChainForHwnd with FlipDiscard succeeded. SwapChain1: {swapChain1.NativePointer:X8}");
                }
                catch (Exception exFlip)
                {
                    var hrFlipStr = exFlip is SharpDX.SharpDXException sdxFlip ? $" (HRESULT: 0x{sdxFlip.ResultCode.Code:X8})" : "";
                    Log.Warning($"[DX11Renderer] FlipDiscard swapchain creation failed{hrFlipStr}: {exFlip.Message}. Falling back to FlipSequential...");
                    desc1.SwapEffect = SwapEffect.FlipSequential;
                    swapChain1 = new SwapChain1(factory2, device, windowHandle, ref desc1);
                    Log.Info($"[DX11Renderer] CreateSwapChainForHwnd with FlipSequential succeeded. SwapChain1: {swapChain1.NativePointer:X8}");
                }
            }
            catch (Exception ex)
            {
                var hrStr = ex is SharpDX.SharpDXException sdx ? $" (HRESULT: 0x{sdx.ResultCode.Code:X8})" : "";
                Log.Error($"[DX11Renderer] Exception calling CreateSwapChainForHwnd{hrStr}: {ex}");
                return false;
            }

            // Step 5: Hooking VTable slots 8 (Present) and 22 (Present1) only.
            try
            {
                Log.Info("[DX11Renderer] Hooking VTable slots 8 (Present) and 22 (Present1)...");
                const int Present1MethodTableIndex = 22;
                const int VTableEntriesCount = Present1MethodTableIndex + 1;
                var swapChain1VTable = VirtualFunctionTable.FromObject((nuint)(nint)swapChain1.NativePointer, VTableEntriesCount);

                var swapChainPresentFunctionPtr = swapChain1VTable.TableEntries[(int)IDXGISwapChain.Present].FunctionPointer; // Slot 8
                var swapChainPresent1FunctionPtr = swapChain1VTable.TableEntries[Present1MethodTableIndex].FunctionPointer; // Slot 22

                _cache.Add(_swapChainPresentHookDelegate);
                _swapChainPresentHook = new(_swapChainPresentHookDelegate, swapChainPresentFunctionPtr);
                _swapChainPresentHook.Activate();

                _cache.Add(_swapChainPresent1HookDelegate);
                _swapChainPresent1Hook = new(_swapChainPresent1HookDelegate, swapChainPresent1FunctionPtr);
                _swapChainPresent1Hook.Activate();

                Log.Info($"[DX11Renderer] Successfully hooked IDXGISwapChain1: Present (Slot 8 @ 0x{swapChainPresentFunctionPtr:X}), Present1 (Slot 22 @ 0x{swapChainPresent1FunctionPtr:X}).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DX11Renderer] Exception hooking VTable slots 8, 22: {ex}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DX11Renderer] Unexpected exception during DX11Renderer.Init: {ex}");
            return false;
        }
        finally
        {
            swapChain1?.Dispose();
            factory2?.Dispose();
            adapter?.Dispose();
            dxgiDevice?.Dispose();
            device?.Dispose();

            if (windowHandle != IntPtr.Zero)
            {
                Windows.User32.DestroyWindow(windowHandle);
            }
        }
    }

    public void Dispose()
    {
        RendererFinder.IsShuttingDown = true;
        lock (_presentLock)
        {
            _onPresentAction = null;
        }
        _cachedSwapChain = null;

        try
        {
            if (_swapChainPresentHook != null && _swapChainPresentHook.IsHookActivated && _swapChainPresentHook.IsHookEnabled)
            {
                _swapChainPresentHook.Disable();
                Log.Info("[DX11Renderer] Successfully disabled SwapChainPresentHook.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DX11Renderer] Error disabling SwapChainPresentHook: {ex}");
        }

        try
        {
            if (_swapChainPresent1Hook != null && _swapChainPresent1Hook.IsHookActivated && _swapChainPresent1Hook.IsHookEnabled)
            {
                _swapChainPresent1Hook.Disable();
                Log.Info("[DX11Renderer] Successfully disabled SwapChainPresent1Hook.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DX11Renderer] Error disabling SwapChainPresent1Hook: {ex}");
        }
    }

    private static IntPtr SwapChainPresentHook(IntPtr self, uint syncInterval, uint flags)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
        {
            return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
        }

        if (RendererFinder.IsShuttingDown)
        {
            return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
        }

        if (_isInsidePresent)
        {
            return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
        }

        _isInsidePresent = true;
        try
        {
            if (FrameGate.TryEnter())
            {
                try
                {
                    HandlePresent(self, syncInterval, flags);
                }
                finally
                {
                    FrameGate.Exit();
                }
            }
            return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
        }
        finally
        {
            _isInsidePresent = false;
        }
    }

    private static IntPtr SwapChainPresent1Hook(IntPtr self, uint syncInterval, uint flags, IntPtr presentParameters)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
        {
            return _swapChainPresent1Hook.OriginalFunction(self, syncInterval, flags, presentParameters);
        }

        if (RendererFinder.IsShuttingDown)
        {
            return _swapChainPresent1Hook.OriginalFunction(self, syncInterval, flags, presentParameters);
        }

        if (_isInsidePresent)
        {
            return _swapChainPresent1Hook.OriginalFunction(self, syncInterval, flags, presentParameters);
        }

        _isInsidePresent = true;
        try
        {
            if (FrameGate.TryEnter())
            {
                try
                {
                    HandlePresent(self, syncInterval, flags);
                }
                finally
                {
                    FrameGate.Exit();
                }
            }
            return _swapChainPresent1Hook.OriginalFunction(self, syncInterval, flags, presentParameters);
        }
        finally
        {
            _isInsidePresent = false;
        }
    }

    private static void HandlePresent(IntPtr self, uint syncInterval, uint flags)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
        {
            return;
        }

        if (RendererFinder.IsShuttingDown)
        {
            return;
        }

        if (!_loggedPresent)
        {
            _loggedPresent = true;
            Log.Info($"[Hook] SwapChainPresentHook invoked! SwapChain: {self:X8}");
        }

        // One-time initialization on Frame 1 using authoritative TargetRendererKind
        if (!RendererFinder.IsBackendInitialized)
        {
            if (RendererFinder.TargetRendererKind == RendererKind.D3D11)
            {
                RendererFinder.RendererKind = RendererKind.D3D11;
                RendererFinder.IsBackendInitialized = true;
                Log.Info("[RendererFinder] Successfully initialized Direct3D 11 backend on Frame 1.");
                RendererFinder.NotifyRendererDetected(RendererKind.D3D11, self);
            }
            else if (RendererFinder.TargetRendererKind == RendererKind.D3D12)
            {
                var queue = DX12Renderer.CapturedCommandQueue;
                if (queue == IntPtr.Zero)
                {
                    // 1. Prioritize querying the command queue directly from the swapchain (DXH-02)
                    queue = RendererFinder.QueryCommandQueueFromSwapChain(self);
                    if (queue != IntPtr.Zero)
                    {
                        DX12Renderer.CapturedCommandQueue = queue;
                    }
                    else
                    {
                        // 2. Check deferred queue binding from ExecuteCommandLists hook matching present thread & device affinity
                        queue = DX12Renderer.GetOrBindQueueForPresent(self);
                    }
                }

                if (queue != IntPtr.Zero)
                {
                    RendererFinder.RendererKind = RendererKind.D3D12;
                    RendererFinder.IsBackendInitialized = true;
                    Log.Info($"[RendererFinder] Successfully initialized Direct3D 12 backend on Frame 1 with CommandQueue: {queue:X8}.");
                    RendererFinder.NotifyRendererDetected(RendererKind.D3D12, self);

                    // DXH-09: Do not dynamically call Hook.Disable() on hot graphics paths during active rendering.
                    // Leaving the hook activated and setting _captureQueue = false avoids rewriting executable memory pages
                    // while worker threads may concurrently execute the prologue.
                    DX12Renderer._captureQueue = false;
                    Log.Info("[DX12Renderer] Bypassed ExecuteCommandLists hook via pass-through flag after capturing queue.");
                }
                else
                {
                    // CommandQueue not captured yet; enable fallback detour with swapchain device affinity and defer initialization
                    DX12Renderer.EnsureFallbackHookEnabled(self);
                    return;
                }
            }
            else
            {
                return;
            }
        }

        if (RendererFinder.RendererKind == RendererKind.D3D11)
        {
            var action = _onPresentAction;
            if (action != null)
            {
                var swapChain = (_cachedSwapChain != null && _cachedSwapChain.NativePointer == self)
                    ? _cachedSwapChain
                    : (_cachedSwapChain = new SwapChain(self));

                try
                {
                    action.Invoke(swapChain, syncInterval, flags);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }
        else if (RendererFinder.RendererKind == RendererKind.D3D12)
        {
            DX12Renderer.NotifyPresentFromDXGISwapChain(self, syncInterval, flags);
        }
    }
}