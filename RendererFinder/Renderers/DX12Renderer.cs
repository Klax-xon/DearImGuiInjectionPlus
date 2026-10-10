using System;
using System.Collections.Generic;
using CppInterop;
using Reloaded.Hooks;
using SharpDX.Direct3D12;
using SharpDX.DXGI;

namespace RendererFinder.Renderers;

public class DX12Renderer : IRenderer
{
    private static readonly List<object> _cache = new();
    private static SwapChain3 _cachedSwapChain;
    private static bool _loggedPresent = false;

    public static IntPtr CapturedCommandQueue { get; set; } = IntPtr.Zero;

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

        public void Disable() => _hook?.Disable();
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
        _onPresentAction = null;
        _cachedSwapChain = null;
        CapturedCommandQueue = IntPtr.Zero;

        try
        {
            _commandQueueExecuteCommandListHook?.Disable();
        }
        catch (Exception ex)
        {
            Log.Warning($"[DX12Renderer] Error disabling ExecuteCommandLists hook: {ex.Message}");
        }
    }

    public static void NotifyPresentFromDXGISwapChain(IntPtr self, uint syncInterval, uint flags)
    {
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
        if (CapturedCommandQueue != IntPtr.Zero)
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
                System.Runtime.InteropServices.Marshal.AddRef(self);
                using var checkQueue = new CommandQueue(self);
                var queueType = checkQueue.Description.Type;
                if (queueType == CommandListType.Direct)
                {
                    CapturedCommandQueue = self;
                    Log.Info($"[DX12Renderer] Successfully captured Direct ID3D12CommandQueue: {self:X8}");
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
                    if (!action.Invoke(commandQueue, (uint)numCommandLists, ppCommandLists))
                    {
                        commandQueue.Dispose();
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    commandQueue.Dispose();
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