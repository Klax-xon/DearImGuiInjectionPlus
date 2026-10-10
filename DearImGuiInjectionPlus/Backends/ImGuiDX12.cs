using System;
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
    private static SwapChain3 _currentSwapChain;
    private static CommandQueue _commandQueue;

    internal class FrameContext
    {
        internal CommandAllocator CommandAllocator;
        internal long FenceValue;
    }
    private static FrameContext[] _frameContexts;
    private static GraphicsCommandList _commandList;
    private static DescriptorHeap _shaderDescriptorHeap;
    private static DescriptorHeap _rtvDescriptorHeap;
    private static Device _device;

    private static Fence _fence;
    private static long _fenceValue;
    private static System.Threading.AutoResetEvent _fenceEvent;
    private static int _renderFrameCount = 0;

    internal static void Init(IntPtr swapChainPtr = default)
    {
        IntPtr windowHandle = IntPtr.Zero;
        if (swapChainPtr != IntPtr.Zero)
        {
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
            if (queuePtr != IntPtr.Zero && _commandQueue == null)
            {
                _commandQueue = new CommandQueue(queuePtr);
                Log.Info($"[ImGuiDX12] Acquired CommandQueue on frame 1: {queuePtr:X8}");
            }

            try
            {
                _currentSwapChain = new SwapChain3(swapChainPtr);
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

        try { _commandList?.Dispose(); } catch { }
        try { _rtvDescriptorHeap?.Dispose(); } catch { }
        try { _shaderDescriptorHeap?.Dispose(); } catch { }
        try { _fence?.Dispose(); } catch { }
        try { _fenceEvent?.Dispose(); } catch { }
        if (_frameContexts != null)
        {
            for (int i = 0; i < _frameContexts.Length; i++)
            {
                try { _frameContexts[i]?.CommandAllocator?.Dispose(); } catch { }
            }
            _frameContexts = null;
        }

        _commandList = null;
        _rtvDescriptorHeap = null;
        _shaderDescriptorHeap = null;
        _fence = null;
        _fenceEvent = null;
        _commandQueue = null;
        _device = null;
        _currentSwapChain = null;
        _windowHandle = IntPtr.Zero;
    }

    private static void WaitForGpuIdle()
    {
        if (_commandQueue == null || _fence == null || _fenceEvent == null)
            return;

        try
        {
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
            Log.Info($"[ImGuiDX12] Successfully captured Direct CommandQueue: {commandQueue.NativePointer:X8}");
            _commandQueue = commandQueue;

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

        _currentSwapChain = swapChain;

        var windowHandle = swapChain?.Description.OutputHandle ?? IntPtr.Zero;
        if (windowHandle != IntPtr.Zero && _windowHandle == IntPtr.Zero)
        {
            _windowHandle = windowHandle;
        }

        // If command queue is not yet known, check CapturedCommandQueue or query swapchain
        if (_commandQueue == null && swapChain != null)
        {
            var queuePtr = RendererFinder.Renderers.DX12Renderer.CapturedCommandQueue;
            if (queuePtr == IntPtr.Zero)
            {
                queuePtr = RendererFinder.RendererFinder.QueryCommandQueueFromSwapChain(swapChain.NativePointer);
            }
            if (queuePtr != IntPtr.Zero)
            {
                _commandQueue = new CommandQueue(queuePtr);
                Log.Info($"[ImGuiDX12] Acquired CommandQueue on present: {queuePtr:X8}");
            }
        }

        // Trigger initialization if command queue is known
        if (!DearImGuiInjectionPlus.Initialized)
        {
            TryInitImGuiDX12();
        }

        if (DearImGuiInjectionPlus.Initialized)
        {
            RenderImGui(swapChain, syncInterval, flags, presentParameters);
        }
    }

    private static void TryInitImGuiDX12()
    {
        if (DearImGuiInjectionPlus.Initialized)
            return;

        if (_currentSwapChain == null || _commandQueue == null)
            return;

        var windowHandle = _currentSwapChain.Description.OutputHandle;
        _windowHandle = windowHandle;
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

    private static unsafe void InitImGuiDX12Internal(SwapChain swapChain)
    {
        _device = swapChain.GetDevice<Device>();
        var swapChainDescription = swapChain.Description;
        int bufferCount = Math.Max(2, swapChainDescription.BufferCount);

        _fence = _device.CreateFence(0, FenceFlags.None);
        _fenceValue = 0;
        _fenceEvent = new System.Threading.AutoResetEvent(false);

        _shaderDescriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = bufferCount,
            Flags = DescriptorHeapFlags.ShaderVisible
        });

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
            Format.R8G8B8A8_UNorm,
            (void*)_shaderDescriptorHeap.NativePointer,
            _shaderDescriptorHeap.CPUDescriptorHandleForHeapStart,
            _shaderDescriptorHeap.GPUDescriptorHandleForHeapStart);

        Log.Info("[ImGuiDX12] InitImGuiDX12Internal completed.");
    }

    private static unsafe void RenderImGui(SwapChain3 swapChain, uint syncInterval, uint flags, IntPtr presentParameters)
    {
        if (RendererFinder.RendererFinder.IsShuttingDown)
            return;

        if (_commandQueue == null || _frameContexts == null || _device == null || _rtvDescriptorHeap == null)
            return;

        int frame = System.Threading.Interlocked.Increment(ref _renderFrameCount);
        bool verboseFrame = frame <= 5;

        try
        {
            var windowHandle = swapChain.Description.OutputHandle;
            if (!IsTargetWindowHandle(windowHandle))
                return;

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
                    _fenceEvent.WaitOne();
                }
            }

            currentFrameContext.CommandAllocator.Reset();
            _commandList.Reset(currentFrameContext.CommandAllocator, null);
            if (verboseFrame)
            {
                Log.Info($"[ImGuiDX12] Frame {frame}: CommandAllocator reset & CommandList reset.");
            }

            ImGuiDX12Impl.NewFrame();
            DearImGuiInjectionPlus.NewFrame();
            if (verboseFrame)
            {
                Log.Info($"[ImGuiDX12] Frame {frame}: DearImGuiInjectionPlus.NewFrame() finished.");
            }

            using (var backBufferResource = swapChain.GetBackBuffer<Resource>(backBufferIndex))
            {
                if (backBufferResource == null)
                    return;

                _device.CreateRenderTargetView(backBufferResource, null, _rtvDescriptorHeap.CPUDescriptorHandleForHeapStart);

                // Pre-render: Transition backbuffer to RenderTarget
                _commandList.ResourceBarrierTransition(backBufferResource, ResourceStates.Present, ResourceStates.RenderTarget);

                _commandList.SetRenderTargets(_rtvDescriptorHeap.CPUDescriptorHandleForHeapStart, null);
                _commandList.SetDescriptorHeaps(_shaderDescriptorHeap);

                var drawData = ImGui.GetDrawData();
                ImGuiDX12Impl.RenderDrawData(drawData, _commandList);
                if (verboseFrame)
                {
                    Log.Info($"[ImGuiDX12] Frame {frame}: ImGuiDX12Impl.RenderDrawData() completed.");
                }

                // Post-render: Transition backbuffer back to Present
                _commandList.ResourceBarrierTransition(backBufferResource, ResourceStates.RenderTarget, ResourceStates.Present);
            }

            _commandList.Close();
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
        }
        catch (Exception ex)
        {
            Log.Error($"[ImGuiDX12] Render error on frame {frame}: {ex}");
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
            return true;
        }

        return false;
    }
}
