using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CppInterop;
using Reloaded.Hooks;
using RendererFinder.Windows;
using SharpDX.Direct3D12;
using SharpDX.DXGI;

namespace RendererFinder.Renderers;

public class DX12Renderer : IRenderer
{
    private static readonly List<object> _cache = new();
    private static SwapChain3 _cachedSwapChain;
    private static bool _loggedPresent = false;
    private static readonly Guid D3D12DeviceGuid = new("1898197d-37b4-45ae-80f7-745f44453b36");

    public static volatile bool _captureQueue = true;

    public static bool CaptureQueue
    {
        get => _captureQueue;
        set => _captureQueue = value;
    }

    private static IntPtr _capturedCommandQueue = IntPtr.Zero;

    public static IntPtr CapturedCommandQueue
    {
        get => _capturedCommandQueue;
        set
        {
            _capturedCommandQueue = value;
            if (value != IntPtr.Zero)
            {
                _captureQueue = false;
            }
        }
    }

    public static IntPtr ExpectedDevicePointer { get; set; } = IntPtr.Zero;

    [ThreadStatic]
    private static IntPtr _lastDirectQueueOnCurrentThread;

    private static IntPtr _candidateCommandQueue = IntPtr.Zero;
    private static uint _candidateThreadId = 0;

    [ThreadStatic]
    private static bool _isInsideExecuteCommandLists;

    public static bool IsInsideExecuteCommandLists
    {
        get => _isInsideExecuteCommandLists;
        set => _isInsideExecuteCommandLists = value;
    }

    public static event Action<SwapChain3, uint, uint, IntPtr> OnPresent
    {
        add => _onPresentAction += value;
        remove => _onPresentAction -= value;
    }
    private static Action<SwapChain3, uint, uint, IntPtr> _onPresentAction;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    public delegate void CommandQueueExecuteCommandListDelegate(IntPtr self, int numCommandLists, IntPtr ppCommandLists);

    private static readonly CommandQueueExecuteCommandListDelegate _commandQueueExecuteCommandListHookDelegate = CommandQueueExecuteCommandListHook;
    private static Hook<CommandQueueExecuteCommandListDelegate> _commandQueueExecuteCommandListHook;

    public class CommandQueueHookWrapper
    {
        private readonly Hook<CommandQueueExecuteCommandListDelegate> _hook;
        public CommandQueueHookWrapper(Hook<CommandQueueExecuteCommandListDelegate> hook) => _hook = hook;

        public bool IsHooked => _hook != null && _hook.IsHookActivated;
        public bool IsHookActivated => _hook != null && _hook.IsHookActivated;
        public bool IsHookEnabled => _hook != null && _hook.IsHookEnabled;

        public void Disable()
        {
            _captureQueue = false;
            _hook?.Disable();
        }
        public void Activate() => _hook?.Activate();
        public void Enable() => _hook?.Enable();
        public CommandQueueExecuteCommandListDelegate OriginalFunction => _hook?.OriginalFunction;
    }

    public static CommandQueueHookWrapper CommandQueueHook => _commandQueueExecuteCommandListHook != null ? new CommandQueueHookWrapper(_commandQueueExecuteCommandListHook) : null;

    public static event Func<CommandQueue, uint, IntPtr, bool> OnExecuteCommandList
    {
        add
        {
            lock (_onExecuteCommandListActionLock)
            {
                OnExecuteCommandListAction += value;
            }
        }
        remove
        {
            lock (_onExecuteCommandListActionLock)
            {
                OnExecuteCommandListAction -= value;
            }
        }
    }
    private static readonly object _onExecuteCommandListActionLock = new();
    private static event Func<CommandQueue, uint, IntPtr, bool> OnExecuteCommandListAction;

    /// <summary>
    /// Initializes DX12 command queue hooks. ONLY called once D3D12 has been actively detected on the game swapchain.
    /// Never called in DX11 mode.
    /// </summary>
    public unsafe bool Init()
    {
        try
        {
            _captureQueue = true;
            var d3d12Module = Windows.User32.GetModuleHandle("d3d12.dll");
            if (d3d12Module == IntPtr.Zero)
            {
                d3d12Module = Windows.User32.LoadLibrary("d3d12.dll");
                if (d3d12Module == IntPtr.Zero)
                {
                    Log.Warning("[DX12Renderer] d3d12.dll is not available. Skipping DX12 command queue hook.");
                    return false;
                }
            }

            using var device = new SharpDX.Direct3D12.Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);
            using var commandQueue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));

            const int ExecuteCommandListTableIndex = 10;
            const int CommandListFunctionCount = ExecuteCommandListTableIndex + 1;
            var commandQueueVTable = VirtualFunctionTable.FromObject((nuint)(nint)commandQueue.NativePointer, CommandListFunctionCount);
            var executePtr = commandQueueVTable.TableEntries[ExecuteCommandListTableIndex].FunctionPointer;

            _cache.Add(_commandQueueExecuteCommandListHookDelegate);
            _commandQueueExecuteCommandListHook = new(_commandQueueExecuteCommandListHookDelegate, executePtr);
            _commandQueueExecuteCommandListHook.Activate();

            Log.Info("[DX12Renderer] Successfully hooked ID3D12CommandQueue.ExecuteCommandLists (Slot 10).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[DX12Renderer] Exception during DX12Renderer.Init(): {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _captureQueue = false;
        _onPresentAction = null;
        _cachedSwapChain = null;
        CapturedCommandQueue = IntPtr.Zero;
        _candidateCommandQueue = IntPtr.Zero;
        _candidateThreadId = 0;
        _lastDirectQueueOnCurrentThread = IntPtr.Zero;

        if (ExpectedDevicePointer != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(ExpectedDevicePointer);
            }
            catch { }
            ExpectedDevicePointer = IntPtr.Zero;
        }

        try
        {
            _commandQueueExecuteCommandListHook?.Disable();
        }
        catch (Exception ex)
        {
            Log.Warning($"[DX12Renderer] Error disabling ExecuteCommandLists hook: {ex.Message}");
        }
    }

    public static void EnsureFallbackHookEnabled(IntPtr swapChainPtr)
    {
        if (ExpectedDevicePointer == IntPtr.Zero && swapChainPtr != IntPtr.Zero)
        {
            ExpectedDevicePointer = RendererFinder.QueryDeviceFromSwapChain(swapChainPtr);
            if (ExpectedDevicePointer != IntPtr.Zero)
            {
                Log.Info($"[DX12Renderer] SwapChain device pointer registered for CommandQueue affinity: {ExpectedDevicePointer:X8}");
            }
        }

        RendererFinder.EnableDX12CommandQueueHook();
    }

    public static IntPtr GetOrBindQueueForPresent(IntPtr swapChainPtr)
    {
        if (CapturedCommandQueue != IntPtr.Zero)
            return CapturedCommandQueue;

        uint currentThreadId = User32.GetCurrentThreadId();

        // 1. Thread-local candidate direct queue executed on this Present thread
        if (_lastDirectQueueOnCurrentThread != IntPtr.Zero)
        {
            CapturedCommandQueue = _lastDirectQueueOnCurrentThread;
            Log.Info($"[DX12Renderer] Bound Direct ID3D12CommandQueue from Present thread ({currentThreadId}): {CapturedCommandQueue:X8}");
            return CapturedCommandQueue;
        }

        // 2. Global candidate direct queue matching this Present thread
        if (_candidateCommandQueue != IntPtr.Zero && _candidateThreadId == currentThreadId)
        {
            CapturedCommandQueue = _candidateCommandQueue;
            Log.Info($"[DX12Renderer] Bound candidate Direct ID3D12CommandQueue matching Present thread affinity ({currentThreadId}): {CapturedCommandQueue:X8}");
            return CapturedCommandQueue;
        }

        return IntPtr.Zero;
    }

    public static void NotifyPresentFromDXGISwapChain(IntPtr self, uint syncInterval, uint flags)
    {
        const uint DXGI_PRESENT_TEST = 0x00000001;
        if ((flags & DXGI_PRESENT_TEST) != 0)
            return;

        if (!_loggedPresent)
        {
            _loggedPresent = true;
            Log.Info($"[Hook] SwapChainPresentHook invoked! SwapChain: {self:X8}");
        }

        if (RendererFinder.IsShuttingDown)
            return;

        if (_onPresentAction != null)
        {
            var swapChain = (_cachedSwapChain != null && _cachedSwapChain.NativePointer == self)
                ? _cachedSwapChain
                : (_cachedSwapChain = new SwapChain3(self));

            try
            {
                _onPresentAction.Invoke(swapChain, syncInterval, flags, IntPtr.Zero);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }
    }

    private static void CommandQueueExecuteCommandListHook(IntPtr self, int numCommandLists, IntPtr ppCommandLists)
    {
        if (!_captureQueue || CapturedCommandQueue != IntPtr.Zero)
        {
            _commandQueueExecuteCommandListHook.OriginalFunction(self, numCommandLists, ppCommandLists);
            return;
        }

        if (_isInsideExecuteCommandLists || RendererFinder.IsShuttingDown)
        {
            _commandQueueExecuteCommandListHook.OriginalFunction(self, numCommandLists, ppCommandLists);
            return;
        }

        _isInsideExecuteCommandLists = true;
        try
        {
            try
            {
                var checkQueue = new CommandQueue(self);
                var queueType = checkQueue.Description.Type;
                if (queueType == CommandListType.Direct)
                {
                    bool deviceMatches = true;
                    if (ExpectedDevicePointer != IntPtr.Zero)
                    {
                        deviceMatches = false;
                        try
                        {
                            checkQueue.GetDevice(D3D12DeviceGuid, out IntPtr queueDevicePtr);
                            if (queueDevicePtr != IntPtr.Zero)
                            {
                                try
                                {
                                    if (queueDevicePtr == ExpectedDevicePointer)
                                    {
                                        deviceMatches = true;
                                    }
                                }
                                finally
                                {
                                    Marshal.Release(queueDevicePtr);
                                }
                            }
                        }
                        catch (Exception devEx)
                        {
                            Log.Warning($"[DX12Renderer] Failed to query device from command queue: {devEx.Message}");
                        }
                    }

                    if (deviceMatches)
                    {
                        // Defer queue binding until Present is invoked; record candidate queue and thread affinity
                        _lastDirectQueueOnCurrentThread = self;
                        _candidateCommandQueue = self;
                        _candidateThreadId = User32.GetCurrentThreadId();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DX12Renderer] Failed to query command queue type: {ex.Message}");
            }

            Func<CommandQueue, uint, IntPtr, bool> action;
            lock (_onExecuteCommandListActionLock)
            {
                action = OnExecuteCommandListAction;
            }

            if (action != null && CapturedCommandQueue == self)
            {
                var commandQueue = new CommandQueue(self);
                try
                {
                    action.Invoke(commandQueue, (uint)numCommandLists, ppCommandLists);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }

            _commandQueueExecuteCommandListHook.OriginalFunction(self, numCommandLists, ppCommandLists);
        }
        finally
        {
            _isInsideExecuteCommandLists = false;
        }
    }
}