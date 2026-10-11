using System;
using System.Runtime.InteropServices;
using RendererFinder.Renderers;

namespace RendererFinder;

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int GetDeviceDelegate(IntPtr self, [In] ref Guid riid, out IntPtr ppDevice);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int QueryInterfaceDelegate(IntPtr self, [In] ref Guid riid, out IntPtr ppvObject);

public static class RendererFinder
{
    public static RendererKind RendererKind { get; internal set; } = RendererKind.None;

    public static RendererKind TargetRendererKind { get; set; } = RendererKind.None;

    public static bool IsBackendInitialized { get; set; } = false;

    public static readonly RendererKind[] AvailableRenderers = { RendererKind.D3D11, RendererKind.D3D12 };

    public static bool IsShuttingDown { get; set; } = false;

    private static DX11Renderer _dx11Renderer;
    private static DX12Renderer _dx12Renderer;

    public static event Action<RendererKind, IntPtr> OnRendererDetected;

    public static bool Init()
    {
        Log.Info($"[RendererFinder] Initializing hooks (TargetRendererKind: {TargetRendererKind})...");

        // 1. Hook DXGI swapchain Present (Slot 8, 22)
        try
        {
            _dx11Renderer = new DX11Renderer();
            if (!_dx11Renderer.Init())
            {
                Log.Error("[RendererFinder] Failed to initialize DXGI swapchain hooks.");
                return false;
            }

            Log.Info("[RendererFinder] Successfully hooked DXGI swapchain Present (Slot 8, 22).");
        }
        catch (Exception ex)
        {
            Log.Error($"[RendererFinder] Exception hooking DXGI swapchain: {ex}");
            return false;
        }

        // 2. If target is D3D12, hook ID3D12CommandQueue.ExecuteCommandLists (Slot 10) upfront
        // to capture the game's CommandQueue when it executes command lists.
        if (TargetRendererKind == RendererKind.D3D12)
        {
            try
            {
                _dx12Renderer = new DX12Renderer();
                if (!_dx12Renderer.Init())
                {
                    Log.Warning("[RendererFinder] Failed to initialize DX12 CommandQueue hook.");
                }
                else
                {
                    Log.Info("[RendererFinder] Successfully hooked DX12 CommandQueue ExecuteCommandLists (Slot 10).");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RendererFinder] Exception initializing DX12 renderer: {ex}");
            }
        }

        return true;
    }

    /// <summary>
    /// Attempts to query the ID3D12CommandQueue directly from the swap chain via IDXGISwapChain::GetDevice (VTable 7),
    /// or returns the captured command queue from DX12Renderer.
    /// </summary>
    public static unsafe IntPtr QueryCommandQueueFromSwapChain(IntPtr swapChainPtr)
    {
        if (DX12Renderer.CapturedCommandQueue != IntPtr.Zero)
            return DX12Renderer.CapturedCommandQueue;

        if (swapChainPtr == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            IntPtr vtable = *(IntPtr*)swapChainPtr;
            IntPtr getDevicePtr = *(IntPtr*)(vtable + 7 * IntPtr.Size);
            var getDevice = Marshal.GetDelegateForFunctionPointer<GetDeviceDelegate>(getDevicePtr);

            // 1. Try querying IID_ID3D12CommandQueue directly: {0ec870a6-5d7e-4c22-8cfc-5baae07616ed}
            Guid d3d12QueueGuid = new Guid("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
            int hr = getDevice(swapChainPtr, ref d3d12QueueGuid, out IntPtr commandQueuePtr);
            if (hr == 0 && commandQueuePtr != IntPtr.Zero)
            {
                SharpDX.Direct3D12.CommandQueue q = null;
                try
                {
                    q = new SharpDX.Direct3D12.CommandQueue(commandQueuePtr);
                    if (q.Description.Type == SharpDX.Direct3D12.CommandListType.Direct)
                    {
                        Log.Info($"[RendererFinder] Acquired Direct ID3D12CommandQueue directly from swapchain: {commandQueuePtr:X8}");
                        GC.SuppressFinalize(q);
                        DX12Renderer.CapturedCommandQueue = commandQueuePtr;
                        return commandQueuePtr;
                    }
                    else
                    {
                        Log.Warning($"[RendererFinder] Swapchain returned non-direct command queue: {commandQueuePtr:X8} (Type: {q.Description.Type})");
                        q.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RendererFinder] Could not verify swapchain command queue type: {ex.Message}");
                    if (q != null)
                        q.Dispose();
                    else
                        Marshal.Release(commandQueuePtr);
                }
            }

            // 2. Try querying IUnknown from GetDevice, then QI for ID3D12CommandQueue
            Guid unknownGuid = new Guid("00000000-0000-0000-c000-000000000046");
            hr = getDevice(swapChainPtr, ref unknownGuid, out IntPtr pUnknown);
            if (hr == 0 && pUnknown != IntPtr.Zero)
            {
                try
                {
                    IntPtr pUnkVtable = *(IntPtr*)pUnknown;
                    IntPtr qiPtr = *(IntPtr*)pUnkVtable;
                    var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qiPtr);

                    int qiHr = qi(pUnknown, ref d3d12QueueGuid, out IntPtr queueFromQi);
                    if (qiHr == 0 && queueFromQi != IntPtr.Zero)
                    {
                        SharpDX.Direct3D12.CommandQueue q = null;
                        try
                        {
                            q = new SharpDX.Direct3D12.CommandQueue(queueFromQi);
                            if (q.Description.Type == SharpDX.Direct3D12.CommandListType.Direct)
                            {
                                Log.Info($"[RendererFinder] Acquired Direct ID3D12CommandQueue via QI from swapchain: {queueFromQi:X8}");
                                GC.SuppressFinalize(q);
                                DX12Renderer.CapturedCommandQueue = queueFromQi;
                                return queueFromQi;
                            }
                            else
                            {
                                Log.Warning($"[RendererFinder] QI returned non-direct command queue: {queueFromQi:X8} (Type: {q.Description.Type})");
                                q.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[RendererFinder] Could not verify QI command queue type: {ex.Message}");
                            if (q != null)
                                q.Dispose();
                            else
                                Marshal.Release(queueFromQi);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(pUnknown);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[RendererFinder] Could not query ID3D12CommandQueue from swapchain: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    internal static void NotifyRendererDetected(RendererKind kind, IntPtr swapChainPtr)
    {
        RendererKind = kind;

        if (kind == RendererKind.D3D12 && _dx12Renderer == null)
        {
            _dx12Renderer = new DX12Renderer();
            _dx12Renderer.Init();
        }

        try
        {
            OnRendererDetected?.Invoke(kind, swapChainPtr);
        }
        catch (Exception ex)
        {
            Log.Error($"[RendererFinder] Exception in OnRendererDetected callback: {ex}");
        }
    }

    public static void Dispose()
    {
        IsShuttingDown = true;
        IsBackendInitialized = false;

        _dx11Renderer?.Dispose();
        _dx11Renderer = null;

        _dx12Renderer?.Dispose();
        _dx12Renderer = null;

        RendererKind = RendererKind.None;
        TargetRendererKind = RendererKind.None;
        OnRendererDetected = null;
    }
}