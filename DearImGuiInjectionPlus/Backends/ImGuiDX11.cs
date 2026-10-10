using System;
using DearImGuiInjectionPlus.Windows;
using ImGuiNET;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjectionPlus.Backends;

internal static class ImGuiDX11
{
    private static IntPtr _windowHandle;
    private static Device _device;
    private static DeviceContext _deviceContext;

    internal static void Init()
    {
        RendererFinder.Renderers.DX11Renderer.OnPresent += InitImGui;
        RendererFinder.Renderers.DX11Renderer.OnPresent += RenderImGui;
    }

    internal static void Dispose()
    {
        RendererFinder.Renderers.DX11Renderer.OnPresent -= RenderImGui;
        RendererFinder.Renderers.DX11Renderer.OnPresent -= InitImGui;

        InputManager.Unsubclass();

        if (DearImGuiInjectionPlus.Initialized && !RendererFinder.RendererFinder.IsShuttingDown)
        {
            Log.Info("[ImGuiDX11] Shutting down DX11 ImGui backend...");
            ImGuiDX11Impl.Shutdown();

            _deviceContext?.Dispose();
            _deviceContext = null;

            _device?.Dispose();
            _device = null;
        }

        _windowHandle = IntPtr.Zero;
    }

    private static unsafe void InitImGui(SwapChain swapChain, uint syncInterval, uint flags)
    {
        var windowHandle = swapChain.Description.OutputHandle;

        if (!DearImGuiInjectionPlus.Initialized)
        {
            _windowHandle = windowHandle;
            Log.Info($"[ImGuiDX11] Starting DX11 initialization with window handle {windowHandle:X8}");

            // Step 1 & 2: Create ImGui Context and configure IO
            DearImGuiInjectionPlus.InitImGui();
            Log.Info("[ImGuiDX11] Step 1 & 2 complete: Context and IO initialized.");

            // Step 3: Initialize Graphics backend
            InitImGuiDX11(swapChain);
            Log.Info("[ImGuiDX11] Step 3 complete: DX11 Graphics backend initialized.");

            // Step 4: Initialize Win32 backend via InputManager.Subclass (calls ImGuiWin32Impl.Init)
            if (windowHandle != IntPtr.Zero)
            {
                InputManager.Subclass(windowHandle);
                DearImGuiInjectionPlus.UpdateCursorVisibility();
                Log.Info($"[ImGuiDX11] Step 4 complete: Win32 backend subclassed on window {windowHandle:X8}.");
            }
            else
            {
                Log.Warning("[ImGuiDX11] Step 4 warning: windowHandle was zero, skipping Subclass.");
            }

            // Step 5: Mark backend as initialized
            DearImGuiInjectionPlus.Initialized = true;
            Log.Info("[ImGuiDX11] Step 5 complete: ImGui DX11 bootstrap finished successfully.");
        }

        RendererFinder.Renderers.DX11Renderer.OnPresent -= InitImGui;
    }

    private static unsafe void InitImGuiDX11(SwapChain swapChain)
    {
        _device = swapChain.GetDevice<Device>();
        _deviceContext = _device.ImmediateContext;

        ImGuiDX11Impl.Init((void*)_device.NativePointer, (void*)_deviceContext.NativePointer);
    }

    private static unsafe void RenderImGui(SwapChain swapChain, uint syncInterval, uint flags)
    {
        var windowHandle = swapChain.Description.OutputHandle;

        if (!IsTargetWindowHandle(windowHandle))
        {
            return;
        }

        if (_deviceContext == null || _device == null)
        {
            return;
        }

        ImGuiDX11Impl.NewFrame();
        DearImGuiInjectionPlus.NewFrame();

        var drawData = ImGui.GetDrawData();
        if (drawData.NativePtr != null)
        {
            using (var old = ImGuiDX11Impl.BackupRenderState(_deviceContext))
            {
                Texture2D backBuffer = null;
                RenderTargetView rtv = null;
                try
                {
                    backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
                    rtv = new RenderTargetView(_device, backBuffer);

                    _deviceContext.OutputMerger.SetRenderTargets(rtv);

                    ImGuiDX11Impl.RenderDrawData(drawData.NativePtr);
                }
                finally
                {
                    try
                    {
                        ImGuiDX11Impl.RestoreRenderState(_deviceContext, old);
                    }
                    finally
                    {
                        rtv?.Dispose();
                        backBuffer?.Dispose();
                    }
                }
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
            return true;
        }

        return false;
    }
}