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
    private static IntPtr _currentSwapChainPtr;
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

        WaitForGpuIdle();

        if (DearImGuiInjectionPlus.Initialized)
        {
            Log.Info("[ImGuiDX11] Shutting down DX11 ImGui backend...");
            try
            {
                ImGuiDX11Impl.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX11] Error during ImGuiDX11Impl.Shutdown: {ex.Message}");
            }

            try
            {
                _deviceContext?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX11] Error disposing DeviceContext: {ex.Message}");
            }
            _deviceContext = null;

            try
            {
                _device?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX11] Error disposing Device: {ex.Message}");
            }
            _device = null;
        }

        _currentSwapChainPtr = IntPtr.Zero;
        _windowHandle = IntPtr.Zero;
    }

    private static void WaitForGpuIdle()
    {
        try
        {
            if (_device != null && !_device.IsDisposed && _device.DeviceRemovedReason.Success)
            {
                _deviceContext?.Flush();
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[ImGuiDX11] Error flushing DX11 device context: {ex.Message}");
        }
    }

    private static void InvalidateDevice()
    {
        try
        {
            ImGuiDX11Impl.InvalidateDeviceObjects();
        }
        catch (Exception ex)
        {
            Log.Warning($"[ImGuiDX11] Error invalidating device objects: {ex.Message}");
        }

        try
        {
            _deviceContext?.Dispose();
        }
        catch { }
        _deviceContext = null;

        try
        {
            _device?.Dispose();
        }
        catch { }
        _device = null;

        _currentSwapChainPtr = IntPtr.Zero;
    }

    private static unsafe void InitImGui(SwapChain swapChain, uint syncInterval, uint flags)
    {
        var windowHandle = swapChain.Description.OutputHandle;

        if (!DearImGuiInjectionPlus.Initialized)
        {
            _windowHandle = windowHandle;
            _currentSwapChainPtr = swapChain.NativePointer;
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
        if (_device.DeviceRemovedReason.Failure)
        {
            throw new Exception($"Device is in removed state: {_device.DeviceRemovedReason}");
        }

        _deviceContext = _device.ImmediateContext;

        var backBufferFormat = Format.Unknown;
        try
        {
            backBufferFormat = swapChain.Description.ModeDescription.Format;
        }
        catch { }

        if (backBufferFormat == Format.Unknown && swapChain is SwapChain1 swapChain1)
        {
            try
            {
                backBufferFormat = swapChain1.Description1.Format;
            }
            catch { }
        }

        DearImGuiTheme.ApplyGammaCorrection(IsHdrFormat(backBufferFormat));

        ImGuiDX11Impl.Init((void*)_device.NativePointer, (void*)_deviceContext.NativePointer);
    }

    private static bool IsHdrFormat(Format format) =>
        format == Format.R10G10B10A2_UNorm ||
        format == Format.R10G10B10A2_Typeless ||
        format == Format.R16G16B16A16_Float ||
        format == Format.R16G16B16A16_Typeless;

    private static unsafe void RenderImGui(SwapChain swapChain, uint syncInterval, uint flags)
    {
        if (swapChain == null || swapChain.NativePointer == IntPtr.Zero)
        {
            return;
        }

        var windowHandle = swapChain.Description.OutputHandle;

        if (!IsTargetWindowHandle(windowHandle))
        {
            return;
        }

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
                Log.Warning("[ImGuiDX11] Device loss detected. Invalidating device objects.");
                InvalidateDevice();
            }
        }

        if (_device == null)
        {
            try
            {
                InitImGuiDX11(swapChain);
                _currentSwapChainPtr = swapChain.NativePointer;
                Log.Info($"[ImGuiDX11] Successfully reinitialized DX11 backend on device {_device.NativePointer:X8}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImGuiDX11] Failed to reinitialize DX11 backend on new device: {ex.Message}");
                return;
            }
        }

        if (_deviceContext == null || _device == null)
        {
            return;
        }

        try
        {
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
                        if (backBuffer == null)
                            return;

                        var bbFormat = backBuffer.Description.Format;
                        DearImGuiTheme.ApplyGammaCorrection(IsHdrFormat(bbFormat));

                        if (bbFormat == Format.R8G8B8A8_UNorm_SRgb)
                        {
                            try
                            {
                                var rtvDesc = new RenderTargetViewDescription
                                {
                                    Format = Format.R8G8B8A8_UNorm,
                                    Dimension = RenderTargetViewDimension.Texture2D
                                };
                                rtv = new RenderTargetView(_device, backBuffer, rtvDesc);
                            }
                            catch
                            {
                                rtv = new RenderTargetView(_device, backBuffer);
                            }
                        }
                        else if (bbFormat == Format.B8G8R8A8_UNorm_SRgb)
                        {
                            try
                            {
                                var rtvDesc = new RenderTargetViewDescription
                                {
                                    Format = Format.B8G8R8A8_UNorm,
                                    Dimension = RenderTargetViewDimension.Texture2D
                                };
                                rtv = new RenderTargetView(_device, backBuffer, rtvDesc);
                            }
                            catch
                            {
                                rtv = new RenderTargetView(_device, backBuffer);
                            }
                        }
                        else
                        {
                            rtv = new RenderTargetView(_device, backBuffer);
                        }

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
        catch (SharpDX.SharpDXException sEx)
        {
            Log.Warning($"[ImGuiDX11] DirectX exception during render: {sEx.Message}");
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
                Log.Warning("[ImGuiDX11] Device loss confirmed via DeviceRemovedReason during render. Invalidating device objects.");
                InvalidateDevice();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ImGuiDX11] Error during render: {ex}");
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