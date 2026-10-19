using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using BlendState = SharpDX.Direct3D11.BlendState;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using SamplerStateDescription = SharpDX.Direct3D11.SamplerStateDescription;
using ShaderResourceViewDescription = SharpDX.Direct3D11.ShaderResourceViewDescription;
using ShaderResourceViewDimension = SharpDX.Direct3D.ShaderResourceViewDimension;

namespace DearImGuiInjectionPlus.Backends;

public static class ImGuiDX11Impl
{
    private static IntPtr _renderNamePtr;
    private static Device _device;
    private static DeviceContext _deviceContext;
    private static ShaderResourceView _fontResourceView;
    private static SamplerState _fontSampler;
    private static VertexShader _vertexShader;
    private static PixelShader _pixelShader;
    private static InputLayout _inputLayout;
    private static Buffer _vertexConstantBuffer;
    private static BlendState _blendState;
    private static RasterizerState _rasterizerState;
    private static DepthStencilState _depthStencilState;
    private static Buffer _vertexBuffer;
    private static Buffer _indexBuffer;
    private static int _vertexBufferSize;
    private static int _indexBufferSize;
    private static VertexBufferBinding _vertexBinding;
    // so we don't make a temporary object every frame
    private static RawColor4 _blendColor = new RawColor4(0, 0, 0, 0);
    private static bool _deviceObjectsCreationFailed;

    public unsafe struct VERTEX_CONSTANT_BUFFER_DX11
    {
        public fixed float mvp[4 * 4];
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void PSSetShaderResourcesDelegate(
        IntPtr thisPtr,
        uint startSlot,
        uint numViews,
        IntPtr* ppShaderResourceViews);

    private static PSSetShaderResourcesDelegate _psSetShaderResources;

    private static readonly Dictionary<IntPtr, ImDrawUserCallBack> _userCallbackCache = new();

    private delegate void GetRenderTargetsInternalDelegate(
        OutputMergerStage stage,
        int numViews,
        RenderTargetView[] renderTargetViewsOut,
        out DepthStencilView depthStencilViewOut);

    private delegate void GetConstantBuffersInternalDelegate(
        CommonShaderStage stage,
        int startSlot,
        int numBuffers,
        Buffer[] constantBuffersRef);

    private delegate void GetSamplersInternalDelegate(
        CommonShaderStage stage,
        int startSlot,
        int numSamplers,
        SamplerState[] samplersRef);

    private delegate void GetShaderResourcesInternalDelegate(
        CommonShaderStage stage,
        int startSlot,
        int numViews,
        ShaderResourceView[] shaderResourceViewsRef);

    private static readonly GetRenderTargetsInternalDelegate _getRenderTargetsInternal;
    private static readonly GetConstantBuffersInternalDelegate _getConstantBuffersInternal;
    private static readonly GetSamplersInternalDelegate _getSamplersInternal;
    private static readonly GetShaderResourcesInternalDelegate _getShaderResourcesInternal;

    private static readonly StateBackup _stateBackup = new();
    private static readonly Rectangle[][] _scissorRectPool = CreateScissorRectPool();

    private static Rectangle[][] CreateScissorRectPool()
    {
        var pool = new Rectangle[17][];
        for (int i = 0; i <= 16; i++)
        {
            pool[i] = new Rectangle[i];
        }
        return pool;
    }

    static ImGuiDX11Impl()
    {
        try
        {
            var omMethod = typeof(OutputMergerStage).GetMethod(
                "GetRenderTargets",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(RenderTargetView[]), typeof(DepthStencilView).MakeByRefType() },
                null);

            if (omMethod != null)
            {
                _getRenderTargetsInternal = (GetRenderTargetsInternalDelegate)Delegate.CreateDelegate(
                    typeof(GetRenderTargetsInternalDelegate),
                    omMethod);
            }
        }
        catch
        {
            _getRenderTargetsInternal = null;
        }

        try
        {
            var cbMethod = typeof(CommonShaderStage).GetMethod(
                "GetConstantBuffers",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(int), typeof(Buffer[]) },
                null);

            if (cbMethod != null)
            {
                _getConstantBuffersInternal = (GetConstantBuffersInternalDelegate)Delegate.CreateDelegate(
                    typeof(GetConstantBuffersInternalDelegate),
                    cbMethod);
            }
        }
        catch
        {
            _getConstantBuffersInternal = null;
        }

        try
        {
            var smMethod = typeof(CommonShaderStage).GetMethod(
                "GetSamplers",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(int), typeof(SamplerState[]) },
                null);

            if (smMethod != null)
            {
                _getSamplersInternal = (GetSamplersInternalDelegate)Delegate.CreateDelegate(
                    typeof(GetSamplersInternalDelegate),
                    smMethod);
            }
        }
        catch
        {
            _getSamplersInternal = null;
        }

        try
        {
            var srMethod = typeof(CommonShaderStage).GetMethod(
                "GetShaderResources",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(int), typeof(ShaderResourceView[]) },
                null);

            if (srMethod != null)
            {
                _getShaderResourcesInternal = (GetShaderResourcesInternalDelegate)Delegate.CreateDelegate(
                    typeof(GetShaderResourcesInternalDelegate),
                    srMethod);
            }
        }
        catch
        {
            _getShaderResourcesInternal = null;
        }
    }

    // Functions
    static unsafe void ImGui_ImplDX11_SetupRenderState(ImDrawData* draw_data, DeviceContext deviceContext)
    {
        // Setup viewport
        deviceContext.Rasterizer.SetViewport(0, 0, draw_data->DisplaySize.X, draw_data->DisplaySize.Y);


        // Setup shader and vertex buffers
        deviceContext.InputAssembler.InputLayout = _inputLayout;

        deviceContext.InputAssembler.SetVertexBuffers(0, _vertexBinding);
        deviceContext.InputAssembler.SetIndexBuffer(_indexBuffer, SharpDX.DXGI.Format.R16_UInt, 0);
        deviceContext.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
        deviceContext.VertexShader.SetShader(_vertexShader, null, 0);
        deviceContext.VertexShader.SetConstantBuffer(0, _vertexConstantBuffer);
        deviceContext.PixelShader.SetShader(_pixelShader, null, 0);
        deviceContext.PixelShader.SetSampler(0, _fontSampler);
        deviceContext.GeometryShader.SetShader(null, null, 0);
        deviceContext.HullShader.SetShader(null, null, 0);
        deviceContext.DomainShader.SetShader(null, null, 0);
        deviceContext.ComputeShader.SetShader(null, null, 0);

        // Setup blend state
        RawColor4 blendFactor = new(0.0f, 0.0f, 0.0f, 0.0f);
        deviceContext.OutputMerger.SetBlendState(_blendState, blendFactor, 0xffffffff);
        deviceContext.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
        deviceContext.Rasterizer.State = _rasterizerState;
    }

    private unsafe delegate void ImDrawUserCallBack(ImDrawList* a, ImDrawCmd* b);

    // Render function
    public static unsafe void RenderDrawData(ImDrawData* draw_data)
    {
        // Avoid rendering when minimized
        if (draw_data->DisplaySize.X <= 0.0f || draw_data->DisplaySize.Y <= 0.0f)
            return;

        if (_deviceContext == null || _fontSampler == null)
            return;

        DeviceContext ctx = _deviceContext;

        if (_psSetShaderResources == null)
        {
            IntPtr vtable = *(IntPtr*)ctx.NativePointer;
            IntPtr methodPtr = *(IntPtr*)(vtable + 8 * IntPtr.Size);
            _psSetShaderResources = Marshal.GetDelegateForFunctionPointer<PSSetShaderResourcesDelegate>(methodPtr);
        }

        // Create and grow vertex/index buffers if needed
        if (_vertexBuffer == null || _vertexBufferSize < draw_data->TotalVtxCount)
        {
            _vertexBuffer?.Dispose();

            _vertexBufferSize = draw_data->TotalVtxCount + 5000;

            _vertexBuffer = new Buffer(_device, new BufferDescription
            {
                Usage = ResourceUsage.Dynamic,
                SizeInBytes = _vertexBufferSize * Unsafe.SizeOf<ImDrawVert>(),
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });

            // (Re)make this here rather than every frame
            _vertexBinding = new VertexBufferBinding
            {
                Buffer = _vertexBuffer,
                Stride = Unsafe.SizeOf<ImDrawVert>(),
                Offset = 0
            };
        }

        if (_indexBuffer == null || _indexBufferSize < draw_data->TotalIdxCount)
        {
            _indexBuffer?.Dispose();

            _indexBufferSize = draw_data->TotalIdxCount + 10000;

            _indexBuffer = new Buffer(_device, new BufferDescription
            {
                Usage = ResourceUsage.Dynamic,
                SizeInBytes = _indexBufferSize * sizeof(ushort),
                BindFlags = BindFlags.IndexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
            });
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        ctx.MapSubresource(_vertexBuffer, MapMode.WriteDiscard, MapFlags.None, out var vtx_resource);
        ctx.MapSubresource(_indexBuffer, MapMode.WriteDiscard, MapFlags.None, out var idx_resource);

        ImDrawVert* vtx_dst = (ImDrawVert*)vtx_resource.DataPointer;
        ushort* idx_dst = (ushort*)idx_resource.DataPointer;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* cmd_list = ((ImDrawList**)draw_data->CmdLists.Data)[n];

            var len = cmd_list->VtxBuffer.Size * sizeof(ImDrawVert);
            System.Buffer.MemoryCopy((void*)cmd_list->VtxBuffer.Data, vtx_dst, len, len);

            len = cmd_list->IdxBuffer.Size * sizeof(ushort);
            System.Buffer.MemoryCopy((void*)cmd_list->IdxBuffer.Data, idx_dst, len, len);

            vtx_dst += cmd_list->VtxBuffer.Size;
            idx_dst += cmd_list->IdxBuffer.Size;
        }

        ctx.UnmapSubresource(_vertexBuffer, 0);
        ctx.UnmapSubresource(_indexBuffer, 0);

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        ctx.MapSubresource(_vertexConstantBuffer, MapMode.WriteDiscard, MapFlags.None, out var mapped_resource);

        VERTEX_CONSTANT_BUFFER_DX11* constant_buffer = (VERTEX_CONSTANT_BUFFER_DX11*)mapped_resource.DataPointer;
        float L = draw_data->DisplayPos.X;
        float R = draw_data->DisplayPos.X + draw_data->DisplaySize.X;
        float T = draw_data->DisplayPos.Y;
        float B = draw_data->DisplayPos.Y + draw_data->DisplaySize.Y;

        constant_buffer->mvp[0] = 2.0f / (R - L);
        constant_buffer->mvp[1] = 0.0f;
        constant_buffer->mvp[2] = 0.0f;
        constant_buffer->mvp[3] = 0.0f;

        constant_buffer->mvp[4] = 0.0f;
        constant_buffer->mvp[5] = 2.0f / (T - B);
        constant_buffer->mvp[6] = 0.0f;
        constant_buffer->mvp[7] = 0.0f;

        constant_buffer->mvp[8] = 0.0f;
        constant_buffer->mvp[9] = 0.0f;
        constant_buffer->mvp[10] = 0.5f;
        constant_buffer->mvp[11] = 0.0f;

        constant_buffer->mvp[12] = (R + L) / (L - R);
        constant_buffer->mvp[13] = (T + B) / (B - T);
        constant_buffer->mvp[14] = 0.5f;
        constant_buffer->mvp[15] = 1.0f;

        ctx.UnmapSubresource(_vertexConstantBuffer, 0);

        ImGui_ImplDX11_SetupRenderState(draw_data, ctx);

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        int global_idx_offset = 0;
        int global_vtx_offset = 0;
        Vector2 clip_off = draw_data->DisplayPos;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* cmd_list = ((ImDrawList**)draw_data->CmdLists.Data)[n];
            for (int cmd_i = 0; cmd_i < cmd_list->CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmd_list->CmdBuffer.Ref<ImDrawCmd>(cmd_i);
                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    // User callback, registered via ImDrawList::AddCallback()
                    // (ImDrawCallback_ResetRenderState is a special callback value used by the user to request the renderer to reset render state.)
                    if (pcmd.UserCallback == new IntPtr(-1))
                    {
                        ImGui_ImplDX11_SetupRenderState(draw_data, ctx);
                    }
                    else
                    {
                        if (!_userCallbackCache.TryGetValue(pcmd.UserCallback, out var userCallback))
                        {
                            userCallback = Marshal.GetDelegateForFunctionPointer<ImDrawUserCallBack>(pcmd.UserCallback);
                            _userCallbackCache[pcmd.UserCallback] = userCallback;
                        }
                        userCallback(cmd_list, &pcmd);
                    }
                }
                else
                {
                    // Project scissor/clipping rectangles into framebuffer space
                    Vector2 clip_min = new(pcmd.ClipRect.X - clip_off.X, pcmd.ClipRect.Y - clip_off.Y);
                    Vector2 clip_max = new(pcmd.ClipRect.Z - clip_off.X, pcmd.ClipRect.W - clip_off.Y);
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                        continue;

                    // Apply scissor/clipping rectangle
                    ctx.Rasterizer.SetScissorRectangle((int)clip_min.X, (int)clip_min.Y, (int)clip_max.X, (int)clip_max.Y);

                    // MEM-04: Bind texture via native ID3D11DeviceContext::PSSetShaderResources vtable call (slot 8)
                    // Avoids allocating transient SharpDX ShaderResourceView wrappers and subsequent finalizer thread over-releases
                    IntPtr textureId = pcmd.TextureId;
                    _psSetShaderResources(ctx.NativePointer, 0, 1, &textureId);
                    ctx.DrawIndexed((int)pcmd.ElemCount, (int)(pcmd.IdxOffset + global_idx_offset), (int)(pcmd.VtxOffset + global_vtx_offset));
                }
            }
            global_idx_offset += cmd_list->IdxBuffer.Size;
            global_vtx_offset += cmd_list->VtxBuffer.Size;
        }
    }

    internal static StateBackup BackupRenderState(DeviceContext ctx)
    {
        const int D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE = 16;

        var backup = _stateBackup;
        backup.Dispose();

        if (backup.RenderTargetViews == null || backup.RenderTargetViews.Length < OutputMergerStage.SimultaneousRenderTargetCount)
            backup.RenderTargetViews = new RenderTargetView[OutputMergerStage.SimultaneousRenderTargetCount];
        if (backup.VSConstantBuffers == null || backup.VSConstantBuffers.Length < 1)
            backup.VSConstantBuffers = new Buffer[1];
        if (backup.PSSamplers == null || backup.PSSamplers.Length < 1)
            backup.PSSamplers = new SamplerState[1];
        if (backup.PSResourceViews == null || backup.PSResourceViews.Length < 1)
            backup.PSResourceViews = new ShaderResourceView[1];

        // IA
        backup.InputLayout = ctx.InputAssembler.InputLayout;
        backup.PrimitiveTopology = ctx.InputAssembler.PrimitiveTopology;
        ctx.InputAssembler.GetIndexBuffer(out backup.IndexBuffer, out backup.IndexBufferFormat, out backup.IndexBufferOffset);
        ctx.InputAssembler.GetVertexBuffers(0, 1, backup.VertexBuffers, backup.VertexBufferStrides, backup.VertexBufferOffsets);

        // RS
        backup.RS = ctx.Rasterizer.State;
        ctx.Rasterizer.GetScissorRectangles<Rectangle>(backup.ScissorRects);
        ctx.Rasterizer.GetViewports<RawViewportF>(backup.Viewports);

        int scissorCount = 0;
        while (scissorCount < D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE &&
               (backup.ScissorRects[scissorCount].Width > 0 || backup.ScissorRects[scissorCount].Height > 0))
        {
            scissorCount++;
        }
        backup.ScissorRectsCount = scissorCount;

        int viewportCount = 0;
        while (viewportCount < D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE &&
               backup.Viewports[viewportCount].Width > 0.0f && backup.Viewports[viewportCount].Height > 0.0f)
        {
            viewportCount++;
        }
        backup.ViewportsCount = viewportCount;

        // OM
        backup.BlendState = ctx.OutputMerger.GetBlendState(out backup.BlendFactor, out backup.SampleMask);
        backup.DepthStencilState = ctx.OutputMerger.GetDepthStencilState(out backup.DepthStencilRef);
        if (_getRenderTargetsInternal != null)
        {
            _getRenderTargetsInternal(ctx.OutputMerger, OutputMergerStage.SimultaneousRenderTargetCount, backup.RenderTargetViews, out backup.DepthStencilView);
        }
        else
        {
            backup.RenderTargetViews = ctx.OutputMerger.GetRenderTargets(OutputMergerStage.SimultaneousRenderTargetCount, out backup.DepthStencilView);
        }

        // VS (slot 0 constant buffer only)
        backup.VS = ctx.VertexShader.Get();
        if (_getConstantBuffersInternal != null)
        {
            _getConstantBuffersInternal(ctx.VertexShader, 0, 1, backup.VSConstantBuffers);
        }
        else
        {
            backup.VSConstantBuffers = ctx.VertexShader.GetConstantBuffers(0, 1);
        }

        // GS, HS, DS, CS (shader instances only)
        backup.GS = ctx.GeometryShader.Get();
        backup.HS = ctx.HullShader.Get();
        backup.DS = ctx.DomainShader.Get();
        backup.CS = ctx.ComputeShader.Get();

        // PS (slot 0 sampler and slot 0 SRV only)
        backup.PS = ctx.PixelShader.Get();
        if (_getSamplersInternal != null)
        {
            _getSamplersInternal(ctx.PixelShader, 0, 1, backup.PSSamplers);
        }
        else
        {
            backup.PSSamplers = ctx.PixelShader.GetSamplers(0, 1);
        }

        if (_getShaderResourcesInternal != null)
        {
            _getShaderResourcesInternal(ctx.PixelShader, 0, 1, backup.PSResourceViews);
        }
        else
        {
            backup.PSResourceViews = ctx.PixelShader.GetShaderResources(0, 1);
        }

        return backup;
    }

    internal static void RestoreRenderState(DeviceContext ctx, StateBackup old)
    {
        // IA
        ctx.InputAssembler.InputLayout = old.InputLayout;
        ctx.InputAssembler.SetIndexBuffer(old.IndexBuffer, old.IndexBufferFormat, old.IndexBufferOffset);
        ctx.InputAssembler.PrimitiveTopology = old.PrimitiveTopology;
        if (old.VertexBuffers != null)
        {
            ctx.InputAssembler.SetVertexBuffers(0, old.VertexBuffers, old.VertexBufferStrides, old.VertexBufferOffsets);
        }

        // RS
        ctx.Rasterizer.State = old.RS;
        if (old.ScissorRectsCount > 0 && old.ScissorRectsCount <= 16)
        {
            var rects = _scissorRectPool[old.ScissorRectsCount];
            Array.Copy(old.ScissorRects, rects, old.ScissorRectsCount);
            ctx.Rasterizer.SetScissorRectangles(rects);
        }
        else
        {
            ctx.Rasterizer.SetScissorRectangles((Rectangle[])null);
        }

        if (old.ViewportsCount > 0)
        {
            ctx.Rasterizer.SetViewports(old.Viewports, old.ViewportsCount);
        }
        else if (old.Viewports != null && old.Viewports.Length > 0)
        {
            ctx.Rasterizer.SetViewports(old.Viewports, old.Viewports.Length);
        }

        // OM
        ctx.OutputMerger.SetBlendState(old.BlendState, old.BlendFactor, old.SampleMask);
        ctx.OutputMerger.SetDepthStencilState(old.DepthStencilState, old.DepthStencilRef);
        ctx.OutputMerger.SetRenderTargets(old.DepthStencilView, old.RenderTargetViews);

        // VS
        ctx.VertexShader.Set(old.VS);
        if (old.VSConstantBuffers != null)
        {
            ctx.VertexShader.SetConstantBuffers(0, old.VSConstantBuffers);
        }

        // GS, HS, DS, CS
        ctx.GeometryShader.Set(old.GS);
        ctx.HullShader.Set(old.HS);
        ctx.DomainShader.Set(old.DS);
        ctx.ComputeShader.Set(old.CS);

        // PS
        ctx.PixelShader.Set(old.PS);
        if (old.PSSamplers != null)
        {
            ctx.PixelShader.SetSamplers(0, old.PSSamplers);
        }
        if (old.PSResourceViews != null)
        {
            ctx.PixelShader.SetShaderResources(0, old.PSResourceViews);
        }
    }

    internal sealed class StateBackup : IDisposable
    {
        public const int MaxViewportsAndScissorRects = 16;

        // IA
        public InputLayout InputLayout;
        public PrimitiveTopology PrimitiveTopology;
        public Buffer IndexBuffer;
        public SharpDX.DXGI.Format IndexBufferFormat;
        public int IndexBufferOffset;
        public readonly Buffer[] VertexBuffers = new Buffer[1];
        public readonly int[] VertexBufferStrides = new int[1];
        public readonly int[] VertexBufferOffsets = new int[1];

        // RS
        public RasterizerState RS;
        public readonly Rectangle[] ScissorRects = new Rectangle[MaxViewportsAndScissorRects];
        public int ScissorRectsCount;
        public readonly RawViewportF[] Viewports = new RawViewportF[MaxViewportsAndScissorRects];
        public int ViewportsCount;

        // OM
        public BlendState BlendState;
        public RawColor4 BlendFactor;
        public int SampleMask;
        public DepthStencilState DepthStencilState;
        public int DepthStencilRef;
        public DepthStencilView DepthStencilView;
        public RenderTargetView[] RenderTargetViews = new RenderTargetView[OutputMergerStage.SimultaneousRenderTargetCount];

        // Shaders & Resources
        public VertexShader VS;
        public Buffer[] VSConstantBuffers = new Buffer[1];

        public GeometryShader GS;
        public HullShader HS;
        public DomainShader DS;
        public ComputeShader CS;

        public PixelShader PS;
        public SamplerState[] PSSamplers = new SamplerState[1];
        public ShaderResourceView[] PSResourceViews = new ShaderResourceView[1];

        public void Dispose()
        {
            InputLayout?.Dispose();
            InputLayout = null;

            IndexBuffer?.Dispose();
            IndexBuffer = null;

            DisposeAll(VertexBuffers);

            RS?.Dispose();
            RS = null;

            BlendState?.Dispose();
            BlendState = null;

            DepthStencilState?.Dispose();
            DepthStencilState = null;

            DepthStencilView?.Dispose();
            DepthStencilView = null;

            DisposeAll(RenderTargetViews);

            VS?.Dispose();
            VS = null;

            DisposeAll(VSConstantBuffers);

            GS?.Dispose();
            GS = null;

            HS?.Dispose();
            HS = null;

            DS?.Dispose();
            DS = null;

            CS?.Dispose();
            CS = null;

            PS?.Dispose();
            PS = null;

            DisposeAll(PSSamplers);

            DisposeAll(PSResourceViews);

            Array.Clear(ScissorRects, 0, ScissorRectsCount);
            ScissorRectsCount = 0;

            Array.Clear(Viewports, 0, ViewportsCount);
            ViewportsCount = 0;
        }

        private static void DisposeAll<T>(T[] a) where T : class, IDisposable
        {
            if (a == null) return;
            for (int i = 0; i < a.Length; i++)
            {
                try
                {
                    a[i]?.Dispose();
                }
                catch
                {
                    // Suppress exceptions during dispose to ensure remaining items are disposed
                }
                a[i] = null;
            }
        }
    }

    public static unsafe void CreateFontsTexture()
    {
        var io = ImGui.GetIO();

        // Build texture atlas
        io.Fonts.GetTexDataAsRGBA32(out IntPtr fontPixels, out int fontWidth, out int fontHeight, out int fontBytesPerPixel);

        // Upload texture to graphics system
        var texDesc = new Texture2DDescription
        {
            Width = fontWidth,
            Height = fontHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        using (var fontTexture = new Texture2D(_device, texDesc, new DataRectangle(fontPixels, fontWidth * fontBytesPerPixel)))
        {
            // Create texture view
            _fontResourceView = new ShaderResourceView(_device, fontTexture, new ShaderResourceViewDescription
            {
                Format = texDesc.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = { MipLevels = texDesc.MipLevels }
            });
        }

        // Store our identifier
        io.Fonts.SetTexID(_fontResourceView.NativePointer);

        // Create texture sampler
        _fontSampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MipLodBias = 0,
            ComparisonFunction = Comparison.Always,
            MinimumLod = 0,
            MaximumLod = 0
        });
    }

    public static unsafe bool CreateDeviceObjects()
    {
        if (_device == null)
        {
            return false;
        }

        if (_fontSampler != null)
        {
            InvalidateDeviceObjects();
        }

        try
        {
            var shadersFolder = Path.Combine(DearImGuiInjectionPlus.AssetsFolderPath, "Shaders");

            var vertexShaderPath = Path.Combine(shadersFolder, "imgui-vertex.hlsl.bytes");
            byte[] vertexShaderBytes = File.ReadAllBytes(vertexShaderPath);
            _vertexShader = new VertexShader(_device, vertexShaderBytes);

            // Create the input layout
            _inputLayout = new InputLayout(_device, vertexShaderBytes, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32_Float, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 0),
                new InputElement("COLOR", 0, Format.R8G8B8A8_UNorm, 0)
            });

            // Create the constant buffer
            _vertexConstantBuffer = new Buffer(_device, new BufferDescription
            {
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 16 * sizeof(float)
            });

            var pixelShaderPath = Path.Combine(shadersFolder, "imgui-frag.hlsl.bytes");
            byte[] pixelShaderBytes = File.ReadAllBytes(pixelShaderPath);
            _pixelShader = new PixelShader(_device, pixelShaderBytes);

            // Create the blending setup
            // ...of course this was setup in a way that can't be done inline
            var blendStateDesc = new BlendStateDescription
            {
                AlphaToCoverageEnable = false
            };
            blendStateDesc.RenderTarget[0].IsBlendEnabled = true;
            blendStateDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            blendStateDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            blendStateDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
            blendStateDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.InverseSourceAlpha;
            blendStateDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.Zero;
            blendStateDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            blendStateDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
            _blendState = new BlendState(_device, blendStateDesc);

            // Create the rasterizer state
            _rasterizerState = new RasterizerState(_device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                IsScissorEnabled = true,
                IsDepthClipEnabled = true
            });

            // Create the depth-stencil State
            _depthStencilState = new DepthStencilState(_device, new DepthStencilStateDescription
            {
                IsDepthEnabled = false,
                DepthWriteMask = DepthWriteMask.All,
                DepthComparison = Comparison.Always,
                IsStencilEnabled = false,
                FrontFace =
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Keep,
                    Comparison = Comparison.Always
                },
                BackFace =
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Keep,
                    Comparison = Comparison.Always
                }
            });

            CreateFontsTexture();

            if (_fontSampler == null)
            {
                Log.Error("[ImGuiDX11Impl] Failed to create device objects: font sampler is null.");
                InvalidateDeviceObjects();
                _deviceObjectsCreationFailed = true;
                return false;
            }

            _deviceObjectsCreationFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[ImGuiDX11Impl] Failed to create device objects: {ex}");
            InvalidateDeviceObjects();
            _deviceObjectsCreationFailed = true;
            return false;
        }
    }

    public static void InvalidateDeviceObjects()
    {
        _deviceObjectsCreationFailed = false;

        if (_device == null)
        {
            return;
        }

        _fontSampler?.Dispose();
        _fontSampler = null;

        _fontResourceView?.Dispose();
        _fontResourceView = null;
        ImGui.GetIO().Fonts.SetTexID(IntPtr.Zero);

        _indexBuffer?.Dispose();
        _indexBuffer = null;

        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _vertexBinding = default;

        _blendState?.Dispose();
        _blendState = null;

        _depthStencilState?.Dispose();
        _depthStencilState = null;

        _rasterizerState?.Dispose();
        _rasterizerState = null;

        _pixelShader?.Dispose();
        _pixelShader = null;

        _vertexConstantBuffer?.Dispose();
        _vertexConstantBuffer = null;

        _inputLayout?.Dispose();
        _inputLayout = null;

        _vertexShader?.Dispose();
        _vertexShader = null;

        _stateBackup.Dispose();
        _userCallbackCache.Clear();
    }

    public static unsafe void Shutdown()
    {
        InvalidateDeviceObjects();

        // we don't own these, so no Dispose()
        _device = null;
        _deviceContext = null;
        _psSetShaderResources = null;

        if (ImGui.GetCurrentContext() != IntPtr.Zero)
        {
            var io = ImGui.GetIO();
            if (io.NativePtr != null)
            {
                io.NativePtr->BackendRendererName = null;
                io.NativePtr->BackendRendererUserData = null;
                io.BackendFlags &= ~ImGuiBackendFlags.RendererHasVtxOffset;
            }
        }

        if (_renderNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_renderNamePtr);
            _renderNamePtr = IntPtr.Zero;
        }
    }

    public static void NewFrame()
    {
        if (_deviceObjectsCreationFailed)
        {
            return;
        }

        if (_fontSampler == null)
        {
            CreateDeviceObjects();
        }
    }

    internal static unsafe void Init(void* device, void* deviceContext)
    {
        _deviceObjectsCreationFailed = false;
        ImGui.GetIO().BackendFlags = ImGui.GetIO().BackendFlags | ImGuiBackendFlags.RendererHasVtxOffset;

        if (_renderNamePtr == IntPtr.Zero)
        {
            _renderNamePtr = Marshal.StringToHGlobalAnsi("imgui_impl_dx11_c#");
        }
        ImGui.GetIO().NativePtr->BackendRendererName = (byte*)_renderNamePtr.ToPointer();

        _device = new((IntPtr)device);
        GC.SuppressFinalize(_device);
        _deviceContext = new((IntPtr)deviceContext);
        GC.SuppressFinalize(_deviceContext);

        IntPtr vtable = *(IntPtr*)_deviceContext.NativePointer;
        IntPtr psSetShaderResourcesPtr = *(IntPtr*)(vtable + 8 * IntPtr.Size);
        _psSetShaderResources = Marshal.GetDelegateForFunctionPointer<PSSetShaderResourcesDelegate>(psSetShaderResourcesPtr);
    }
}
