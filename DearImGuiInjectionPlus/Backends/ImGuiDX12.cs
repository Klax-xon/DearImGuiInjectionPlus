using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DearImGuiInjectionPlus.Backends;

internal static class ImGuiDX12
{
    private static IntPtr _windowHandle;
    private static IntPtr _currentSwapChainPtr;
    private static SwapChain3 _currentSwapChain;
    private static CommandQueue _commandQueue;

    internal class FrameContext
    {
        internal CommandAllocator CommandAllocator;
        internal long FenceValue;
        internal Resource VertexBuffer;
        internal Resource IndexBuffer;
        internal int VertexBufferSize;
        internal int IndexBufferSize;
    }
    private static FrameContext[] _frameContexts;
    private static GraphicsCommandList _commandList;
    private static DescriptorHeap _shaderDescriptorHeap;
    private static readonly DescriptorHeap[] _shaderDescriptorHeaps = new DescriptorHeap[1];
    private static DescriptorHeap _rtvDescriptorHeap;
    private static Device _device;

    private static Fence _fence;
    private static long _fenceValue;
    private static System.Threading.AutoResetEvent _fenceEvent;
    private static int _renderFrameCount = 0;
    private static readonly object _initLock = new();

    // LC-04: Consecutive error circuit breaker fields
    private const int MaxConsecutiveErrors = 5;
    private static readonly long CircuitBreakerCooldownTicks = 5 * Stopwatch.Frequency;
    private static int _consecutiveRenderErrors;
    private static long _circuitBreakerTripUntil;

    internal static void Init(IntPtr swapChainPtr = default)
    {
        IntPtr windowHandle = IntPtr.Zero;
        if (swapChainPtr != IntPtr.Zero)
        {
            _currentSwapChainPtr = swapChainPtr;
            try
            {
                Marshal.AddRef(swapChainPtr);
                using var tempSc = new SwapChain(swapChainPtr);
                windowHandle = tempSc.Description.OutputHandle;
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX12] Could not get OutputHandle from swapChainPtr: {ex.Message}");
            }
        }

        Log.Info($"[ImGuiDX12] Initializing ImGui DX12 with SwapChain: {swapChainPtr:X8}, Window: {windowHandle:X8}");

        RendererFinder.Renderers.DX12Renderer.OnPresent += OnPresent;
        RendererFinder.Renderers.DX12Renderer.OnExecuteCommandList += RetrieveCommandQueue;

        if (windowHandle != IntPtr.Zero)
        {
            _windowHandle = windowHandle;
        }

        if (swapChainPtr != IntPtr.Zero)
        {
            var queuePtr = RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue;
            if (queuePtr == IntPtr.Zero)
            {
                queuePtr = RendererFinder.RendererFinder.QueryCommandQueueFromSwapChain(swapChainPtr);
            }
            if (queuePtr == IntPtr.Zero)
            {
                queuePtr = RendererFinder.Renderers.DX12Renderer.GetOrBindQueueForPresent(swapChainPtr);
            }
            if (queuePtr != IntPtr.Zero && _commandQueue == null)
            {
                _commandQueue = new CommandQueue(queuePtr);
                GC.SuppressFinalize(_commandQueue);
                Log.Info($"[ImGuiDX12] Acquired CommandQueue on frame 1: {queuePtr:X8}");
            }

            try
            {
                _currentSwapChain = new SwapChain3(swapChainPtr);
                GC.SuppressFinalize(_currentSwapChain);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX12] Could not create SwapChain3: {ex.Message}");
            }

            TryInitImGuiDX12();
        }
    }

    internal static void Dispose()
    {
        RendererFinder.Renderers.DX12Renderer.OnExecuteCommandList -= RetrieveCommandQueue;
        RendererFinder.Renderers.DX12Renderer.OnPresent -= OnPresent;

        // (c) D3D12: signal and wait on the fence for the presenting queue (DXH-05)
        WaitForGpuIdle();

        // (d) release GPU objects
        try { ImGuiDX12Impl.Shutdown(); } catch { }

        InvalidateDevice();

        _windowHandle = IntPtr.Zero;
        _consecutiveRenderErrors = 0;
        _circuitBreakerTripUntil = 0;
    }

    private static void InvalidateDevice()
    {
        try { ImGuiDX12Impl.InvalidateDeviceObjects(); } catch { }

        try { _commandList?.Dispose(); } catch { }
        _commandList = null;

        try { _rtvDescriptorHeap?.Dispose(); } catch { }
        _rtvDescriptorHeap = null;

        try { _shaderDescriptorHeap?.Dispose(); } catch { }
        _shaderDescriptorHeap = null;
        _shaderDescriptorHeaps[0] = null;

        try { _fence?.Dispose(); } catch { }
        _fence = null;

        try { _fenceEvent?.Dispose(); } catch { }
        _fenceEvent = null;

        try { _commandQueue?.Dispose(); } catch { }
        _commandQueue = null;
        RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue = IntPtr.Zero;
        RendererFinder.Renderers.DX12Renderer.CaptureQueue = true;

        if (_frameContexts != null)
        {
            for (int i = 0; i < _frameContexts.Length; i++)
            {
                try { _frameContexts[i]?.CommandAllocator?.Dispose(); } catch { }
                try { _frameContexts[i]?.VertexBuffer?.Dispose(); } catch { }
                try { _frameContexts[i]?.IndexBuffer?.Dispose(); } catch { }
            }
            _frameContexts = null;
        }

        try { _device?.Dispose(); } catch { }
        _device = null;

        if (_currentSwapChain != null)
        {
            try { GC.SuppressFinalize(_currentSwapChain); } catch { }
            _currentSwapChain = null;
        }

        _currentSwapChainPtr = IntPtr.Zero;
    }

    private static void WaitForGpuIdle()
    {
        if (_commandQueue == null || _fence == null || _fenceEvent == null || _device == null || _device.IsDisposed)
            return;

        try
        {
            if (_device.DeviceRemovedReason.Failure)
                return;

            _fenceValue++;
            _commandQueue.Signal(_fence, _fenceValue);
            if (_fence.CompletedValue < _fenceValue)
            {
                _fence.SetEventOnCompletion(_fenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                _fenceEvent.WaitOne(2000);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ImGuiDX12] Exception waiting for GPU idle: {ex}");
        }
    }

    private static bool RetrieveCommandQueue(CommandQueue commandQueue, uint arg2, IntPtr ptr)
    {
        if (_commandQueue != null)
        {
            return true;
        }

        if (commandQueue != null && commandQueue.Description.Type == CommandListType.Direct)
        {
            lock (_initLock)
            {
                if (_commandQueue == null)
                {
                    Log.Info($"[ImGuiDX12] Successfully captured Direct CommandQueue: {commandQueue.NativePointer:X8}");
                    if (RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue != commandQueue.NativePointer)
                    {
                        System.Runtime.InteropServices.Marshal.AddRef(commandQueue.NativePointer);
                        RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue = commandQueue.NativePointer;
                    }
                    _commandQueue = new CommandQueue(commandQueue.NativePointer);
                    GC.SuppressFinalize(_commandQueue);
                }
            }

            // Trigger initialization immediately if swapchain is already known!
            TryInitImGuiDX12();
            return true;
        }

        return false;
    }

    private static void OnPresent(SwapChain3 swapChain, uint syncInterval, uint flags, IntPtr presentParameters)
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
            return;

        if (swapChain == null || swapChain.NativePointer == IntPtr.Zero)
            return;

        var windowHandle = swapChain.Description.OutputHandle;
        if (!IsTargetWindowHandle(windowHandle))
            return;

        if (_currentSwapChainPtr == IntPtr.Zero)
        {
            _currentSwapChainPtr = swapChain.NativePointer;
        }
        else if (swapChain.NativePointer != _currentSwapChainPtr)
        {
            bool sameDevice = false;
            try
            {
                using (var newDevice = swapChain.GetDevice<Device>())
                {
                    if (_device != null && !_device.IsDisposed && newDevice != null && newDevice.NativePointer == _device.NativePointer)
                    {
                        sameDevice = true;
                    }
                }
            }
            catch
            {
                sameDevice = false;
            }

            if (sameDevice)
            {
                _currentSwapChainPtr = swapChain.NativePointer;
            }
            else
            {
                InvalidateDevice();
            }
        }

        _currentSwapChain = swapChain;

        // Device loss detection
        if (_device != null)
        {
            bool isDeviceLost = false;
            try
            {
                if (_device.IsDisposed || _device.DeviceRemovedReason.Failure)
                {
                    isDeviceLost = true;
                }
            }
            catch
            {
                isDeviceLost = true;
            }

            if (isDeviceLost)
            {
                Log.Warning("[ImGuiDX12] Device loss detected. Invalidating device objects.");
                InvalidateDevice();
            }
        }

        // If command queue is not yet known, check swapchain directly or CapturedCommandQueue
        if (_commandQueue == null)
        {
            lock (_initLock)
            {
                if (_commandQueue == null)
                {
                    var queuePtr = RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue;
                    if (queuePtr == IntPtr.Zero)
                    {
                        queuePtr = RendererFinder.RendererFinder.QueryCommandQueueFromSwapChain(swapChain.NativePointer);
                    }
                    if (queuePtr == IntPtr.Zero)
                    {
                        queuePtr = RendererFinder.Renderers.DX12Renderer.GetOrBindQueueForPresent(swapChain.NativePointer);
                    }
                    if (queuePtr != IntPtr.Zero)
                    {
                        _commandQueue = new CommandQueue(queuePtr);
                        GC.SuppressFinalize(_commandQueue);
                        RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue = queuePtr;
                        Log.Info($"[ImGuiDX12] Acquired CommandQueue on present: {queuePtr:X8}");
                    }
                }
            }
        }

        // Trigger initialization or reinitialization if command queue is known
        if (!DearImGuiInjectionPlus.Initialized)
        {
            TryInitImGuiDX12();
        }
        else if (_device == null && _commandQueue != null)
        {
            lock (_initLock)
            {
                if (_device == null && _commandQueue != null)
                {
                    try
                    {
                        _currentSwapChain = swapChain;
                        _currentSwapChainPtr = swapChain.NativePointer;
                        InitImGuiDX12Internal(swapChain);
                        Log.Info($"[ImGuiDX12] Reinitialized DX12 backend on device {_device.NativePointer:X8}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[ImGuiDX12] Failed to reinitialize DX12 backend on new device: {ex.Message}");
                        return;
                    }
                }
            }
        }

        if (DearImGuiInjectionPlus.Initialized && _device != null)
        {
            RenderImGui(swapChain, syncInterval, flags, presentParameters);
        }
    }

    private static void TryInitImGuiDX12()
    {
        if (DearImGuiInjectionPlus.Initialized)
            return;

        lock (_initLock)
        {
            if (DearImGuiInjectionPlus.Initialized || _currentSwapChain == null || _commandQueue == null)
                return;

            var windowHandle = _currentSwapChain.Description.OutputHandle;
            _windowHandle = windowHandle;
            _currentSwapChainPtr = _currentSwapChain.NativePointer;
            Log.Info($"[ImGuiDX12] Starting DX12 initialization with window {windowHandle:X8} and CommandQueue {_commandQueue.NativePointer:X8}");

            // Step 1 & 2: Create ImGui Context and configure IO
            DearImGuiInjectionPlus.InitImGui();
            Log.Info("[ImGuiDX12] Step 1 & 2 complete: Context and IO initialized.");

            // Step 3: Initialize Graphics backend (ImGuiDX12Impl)
            InitImGuiDX12Internal(_currentSwapChain);
            Log.Info("[ImGuiDX12] Step 3 complete: DX12 Graphics backend initialized.");

            // Step 4: Initialize Win32 backend via InputManager.Subclass (calls ImGuiWin32Impl.Init)
            if (windowHandle != IntPtr.Zero)
            {
                InputManager.Subclass(windowHandle);
                DearImGuiInjectionPlus.UpdateCursorVisibility();
                Log.Info($"[ImGuiDX12] Step 4 complete: Win32 backend subclassed on window {windowHandle:X8}.");
            }
            else
            {
                Log.Warning("[ImGuiDX12] Step 4 warning: windowHandle was zero, skipping Subclass.");
            }

            // Step 5: Mark backend as initialized
            DearImGuiInjectionPlus.Initialized = true;
            Log.Info("[ImGuiDX12] Step 5 complete: ImGui DX12 bootstrap finished successfully.");
        }
    }

    private static unsafe void InitImGuiDX12Internal(SwapChain swapChain)
    {
        _device = swapChain.GetDevice<Device>();
        if (_device.DeviceRemovedReason.Failure)
        {
            throw new Exception($"Device is in removed state: {_device.DeviceRemovedReason}");
        }

        var swapChainDescription = swapChain.Description;
        int bufferCount = Math.Max(2, swapChainDescription.BufferCount);

        var backBufferFormat = Format.Unknown;
        try
        {
            using var backBuffer = swapChain.GetBackBuffer<Resource>(0);
            if (backBuffer != null)
            {
                backBufferFormat = backBuffer.Description.Format;
            }
        }
        catch { }

        if (backBufferFormat == Format.Unknown)
        {
            try
            {
                if (swapChain is SwapChain1 swapChain1)
                {
                    backBufferFormat = swapChain1.Description1.Format;
                }
            }
            catch { }
        }

        if (backBufferFormat == Format.Unknown)
        {
            try
            {
                backBufferFormat = swapChainDescription.ModeDescription.Format;
            }
            catch { }
        }

        if (backBufferFormat == Format.Unknown)
        {
            backBufferFormat = Format.R8G8B8A8_UNorm;
        }

        DearImGuiTheme.ApplyGammaCorrection(IsHdrFormat(backBufferFormat));

        _fence = _device.CreateFence(0, FenceFlags.None);
        _fenceValue = 0;
        _fenceEvent = new System.Threading.AutoResetEvent(false);

        _shaderDescriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = bufferCount,
            Flags = DescriptorHeapFlags.ShaderVisible
        });
        _shaderDescriptorHeaps[0] = _shaderDescriptorHeap;

        _rtvDescriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
            NodeMask = 1
        });

        _frameContexts = new FrameContext[bufferCount];
        for (int i = 0; i < bufferCount; i++)
        {
            _frameContexts[i] = new FrameContext
            {
                CommandAllocator = _device.CreateCommandAllocator(CommandListType.Direct)
            };
        }

        _commandList = _device.CreateCommandList(CommandListType.Direct, _frameContexts[0].CommandAllocator, null);
        _commandList.Close();

        ImGuiDX12Impl.Init((void*)_device.NativePointer, bufferCount,
            backBufferFormat,
            (void*)_shaderDescriptorHeap.NativePointer,
            _shaderDescriptorHeap.CPUDescriptorHandleForHeapStart,
            _shaderDescriptorHeap.GPUDescriptorHandleForHeapStart);

        Log.Info($"[ImGuiDX12] InitImGuiDX12Internal completed with format {backBufferFormat}.");
    }

    private static bool IsHdrFormat(Format format) =>
        format == Format.R10G10B10A2_UNorm ||
        format == Format.R10G10B10A2_Typeless ||
        format == Format.R16G16B16A16_Float ||
        format == Format.R16G16B16A16_Typeless;

    private static unsafe void RenderImGui(SwapChain3 swapChain, uint syncInterval, uint flags, IntPtr presentParameters)
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
            return;

        // LC-04: Circuit breaker - pause overlay rendering for 5 seconds if consecutively failing
        if (_consecutiveRenderErrors >= MaxConsecutiveErrors)
        {
            if (Stopwatch.GetTimestamp() < _circuitBreakerTripUntil)
            {
                return;
            }
        }

        if (_commandQueue == null || _frameContexts == null || _device == null || _rtvDescriptorHeap == null)
            return;

        var windowHandle = swapChain.Description.OutputHandle;
        if (!IsTargetWindowHandle(windowHandle))
            return;

        if (_currentSwapChainPtr != IntPtr.Zero && swapChain.NativePointer != _currentSwapChainPtr)
        {
            bool sameDevice = false;
            try
            {
                using (var newDevice = swapChain.GetDevice<Device>())
                {
                    if (_device != null && !_device.IsDisposed && newDevice != null && newDevice.NativePointer == _device.NativePointer)
                    {
                        sameDevice = true;
                    }
                }
            }
            catch
            {
                sameDevice = false;
            }

            if (sameDevice)
            {
                _currentSwapChainPtr = swapChain.NativePointer;
                _currentSwapChain = swapChain;
            }
            else
            {
                InvalidateDevice();
                return;
            }
        }

        // Device loss check before rendering
        if (_device.IsDisposed || _device.DeviceRemovedReason.Failure)
        {
            Log.Warning("[ImGuiDX12] Device loss detected before render. Invalidating device objects.");
            InvalidateDevice();
            return;
        }

        int frame = System.Threading.Interlocked.Increment(ref _renderFrameCount);
        bool verboseFrame = frame <= 5;

        try
        {
            int backBufferIndex = swapChain.CurrentBackBufferIndex;
            if (backBufferIndex >= _frameContexts.Length)
            {
                var oldContexts = _frameContexts;
                _frameContexts = new FrameContext[backBufferIndex + 1];
                Array.Copy(oldContexts, _frameContexts, oldContexts.Length);
                for (int i = oldContexts.Length; i < _frameContexts.Length; i++)
                {
                    _frameContexts[i] = new FrameContext
                    {
                        CommandAllocator = _device.CreateCommandAllocator(CommandListType.Direct)
                    };
                }
            }

            var currentFrameContext = _frameContexts[backBufferIndex];

            if (verboseFrame)
            {
                Log.Info($"[ImGuiDX12] Frame {frame}: Starting render on backbuffer {backBufferIndex}");
            }

            if (currentFrameContext.FenceValue != 0 && _fence != null && _fenceEvent != null)
            {
                if (_fence.CompletedValue < currentFrameContext.FenceValue)
                {
                    _fence.SetEventOnCompletion(currentFrameContext.FenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                    if (!_fenceEvent.WaitOne(2000))
                    {
                        Log.Error($"[ImGuiDX12] GPU timeout/hang waiting for fence completion (Fence: {currentFrameContext.FenceValue}, Completed: {_fence.CompletedValue}) on frame {frame}. Skipping frame rendering.");
                        try
                        {
                            if (_device != null && (_device.IsDisposed || _device.DeviceRemovedReason.Failure))
                            {
                                Log.Warning("[ImGuiDX12] Device loss detected following fence wait timeout. Invalidating device objects.");
                                InvalidateDevice();
                            }
                        }
                        catch { }
                        return;
                    }
                }
            }

            currentFrameContext.CommandAllocator.Reset();
            _commandList.Reset(currentFrameContext.CommandAllocator, null);
            if (verboseFrame)
            {
                Log.Info($"[ImGuiDX12] Frame {frame}: CommandAllocator reset & CommandList reset.");
            }

            bool listClosed = false;
            try
            {
                ImGuiDX12Impl.NewFrame();
                DearImGuiInjectionPlus.NewFrame();
                if (verboseFrame)
                {
                    Log.Info($"[ImGuiDX12] Frame {frame}: DearImGuiInjectionPlus.NewFrame() finished.");
                }

                using (var backBufferResource = swapChain.GetBackBuffer<Resource>(backBufferIndex))
                {
                    if (backBufferResource == null)
                    {
                        if (_device != null && (_device.IsDisposed || _device.DeviceRemovedReason.Failure))
                        {
                            Log.Warning("[ImGuiDX12] Device loss detected on GetBackBuffer. Invalidating device objects.");
                            InvalidateDevice();
                        }
                        return;
                    }

                    var backBufferFormat = backBufferResource.Description.Format;
                    DearImGuiTheme.ApplyGammaCorrection(IsHdrFormat(backBufferFormat));

                    try
                    {
                        var desc = swapChain.Description;
                        var mode = desc.ModeDescription;
                        int w = (int)backBufferResource.Description.Width;
                        int h = backBufferResource.Description.Height;
                        if (w <= 0) w = mode.Width;
                        if (h <= 0) h = mode.Height;
                        string colorSpace = IsHdrFormat(backBufferFormat)
                            ? (backBufferFormat == Format.R16G16B16A16_Float ? "scRGB (R16G16B16A16_Float)" : "HDR10 (R10G10B10A2_UNorm)")
                            : (backBufferFormat == Format.R8G8B8A8_UNorm_SRgb || backBufferFormat == Format.B8G8R8A8_UNorm_SRgb ? "sRGB" : "SDR Linear");

                        GraphicsTelemetry.Update(
                            "Direct3D 12",
                            windowHandle,
                            swapChain.NativePointer,
                            backBufferFormat.ToString(),
                            w,
                            h,
                            mode.RefreshRate.Numerator,
                            mode.RefreshRate.Denominator,
                            IsHdrFormat(backBufferFormat),
                            colorSpace);
                    }
                    catch { }

                    if (backBufferFormat != Format.Unknown && backBufferFormat != ImGuiDX12Impl.RTVFormat)
                    {
                        Log.Info($"[ImGuiDX12] Backbuffer format changed from {ImGuiDX12Impl.RTVFormat} to {backBufferFormat}. Recreating PSO.");
                        ImGuiDX12Impl.SetRtvFormat(backBufferFormat);
                        ImGuiDX12Impl.CreateDeviceObjects();
                    }

                    _device.CreateRenderTargetView(backBufferResource, null, _rtvDescriptorHeap.CPUDescriptorHandleForHeapStart);

                    // Pre-render: Transition backbuffer to RenderTarget
                    _commandList.ResourceBarrierTransition(backBufferResource, ResourceStates.Present, ResourceStates.RenderTarget);

                    _commandList.SetRenderTargets(_rtvDescriptorHeap.CPUDescriptorHandleForHeapStart, null);
                    _commandList.SetDescriptorHeaps(_shaderDescriptorHeaps);

                    var drawData = ImGui.GetDrawData();
                    ImGuiDX12Impl.RenderDrawData(drawData, _commandList, currentFrameContext);
                    if (verboseFrame)
                    {
                        Log.Info($"[ImGuiDX12] Frame {frame}: ImGuiDX12Impl.RenderDrawData() completed.");
                    }

                    // Post-render: Transition backbuffer back to Present
                    _commandList.ResourceBarrierTransition(backBufferResource, ResourceStates.RenderTarget, ResourceStates.Present);
                }

                _commandList.Close();
                listClosed = true;
                if (verboseFrame)
                {
                    Log.Info($"[ImGuiDX12] Frame {frame}: CommandList closed.");
                }

                RendererFinder.Renderers.DX12Renderer.IsInsideExecuteCommandLists = true;
                try
                {
                    _commandQueue.ExecuteCommandList(_commandList);
                }
                finally
                {
                    RendererFinder.Renderers.DX12Renderer.IsInsideExecuteCommandLists = false;
                }

                if (_fence != null)
                {
                    _fenceValue++;
                    _commandQueue.Signal(_fence, _fenceValue);
                    currentFrameContext.FenceValue = _fenceValue;
                }

                if (verboseFrame)
                {
                    Log.Info($"[ImGuiDX12] Frame {frame}: CommandList executed on CommandQueue (Fence: {_fenceValue}).");
                }

                if (_consecutiveRenderErrors > 0)
                {
                    _consecutiveRenderErrors = 0;
                    Log.Info("[ImGuiDX12] Overlay rendering recovered successfully.");
                }
            }
            finally
            {
                if (!listClosed)
                {
                    try { _commandList.Close(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _consecutiveRenderErrors++;
            if (_consecutiveRenderErrors <= MaxConsecutiveErrors)
            {
                Log.Error($"[ImGuiDX12] Render error on frame {frame}: {ex}");
                if (_consecutiveRenderErrors == MaxConsecutiveErrors)
                {
                    _circuitBreakerTripUntil = Stopwatch.GetTimestamp() + CircuitBreakerCooldownTicks;
                    Log.Warning($"[ImGuiDX12] Circuit breaker tripped after {MaxConsecutiveErrors} consecutive render errors. Overlay rendering paused for 5 seconds to prevent log flood.");
                }
            }
            else
            {
                _circuitBreakerTripUntil = Stopwatch.GetTimestamp() + CircuitBreakerCooldownTicks;
            }

            bool isLost = false;
            try
            {
                if (_device != null && (_device.IsDisposed || _device.DeviceRemovedReason.Failure))
                    isLost = true;
            }
            catch
            {
                isLost = true;
            }

            if (isLost)
            {
                Log.Warning("[ImGuiDX12] Device loss confirmed via DeviceRemovedReason during render. Invalidating device objects.");
                InvalidateDevice();
            }
        }
    }

    private static bool IsTargetWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
        {
            if (_windowHandle == IntPtr.Zero)
            {
                _windowHandle = windowHandle;
            }
            return windowHandle == _windowHandle;
        }

        return false;
    }
}
