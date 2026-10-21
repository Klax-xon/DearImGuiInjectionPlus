using System;
using System.Collections.Concurrent;
using System.Threading;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

/// <summary>
/// High-performance, exception-resilient dispatcher ensuring mods and background tasks
/// can safely dispatch work to the Unity main scripting thread.
/// <para>
/// <b>CRITICAL CONCURRENCY RULE:</b>
/// Synchronous blocking dispatch via <see cref="InvokeOnMainThread{T}"/> or <see cref="InvokeOnMainThread(Action, int)"/>
/// must <b>NEVER</b> be called from within the ImGui render loop (<c>IImGuiWindow.OnRender</c> or DirectX Present callbacks).
/// In Unity's multithreaded rendering pipeline, the main scripting thread frequently blocks waiting for the render thread
/// to present before starting the next frame. A blocking call from the render thread creates an engine pipeline deadlock
/// that stalls the game until timeout.
/// For all render-thread to main-thread communication, always enforce asynchronous non-blocking dispatch via
/// <see cref="Enqueue(Action)"/> or lock-free state snapshot pipelines (<see cref="StateSnapshotPipeline{T}"/>).
/// </para>
/// </summary>
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _executionQueue = new();
    private static UnityMainThreadDispatcher _instance;
    private static int _mainThreadId = -1;
    private static bool _registered;

    public const int MaxBatchPerFrame = 500;

    public UnityMainThreadDispatcher(IntPtr handle) : base(handle) { }

    /// <summary>
    /// Registers the UnityMainThreadDispatcher type with the IL2CPP domain and captures the calling main thread ID.
    /// </summary>
    public static void Register()
    {
        if (_mainThreadId <= 0)
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        if (!_registered)
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<UnityMainThreadDispatcher>())
            {
                ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>();
            }
            _registered = true;
        }
    }

    /// <summary>
    /// Returns true if the current calling thread is the Unity main scripting thread.
    /// </summary>
    public static bool IsMainThread => _mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    private void Awake()
    {
        _instance = this;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public void Update()
    {
        DrainQueue();
    }

    private static void DrainQueue()
    {
        int processed = 0;
        while (processed < MaxBatchPerFrame && _executionQueue.TryDequeue(out var action))
        {
            processed++;
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error($"[UnityMainThreadDispatcher] Exception in dispatched action: {ex}");
            }
        }
    }

    /// <summary>
    /// Enqueues an action to be executed on the Unity main thread during the next Update frame.
    /// Lock-free, zero coroutine overhead, resilient to unhandled exceptions.
    /// </summary>
    /// <param name="action">Action to execute on the main thread.</param>
    public static void Enqueue(Action action)
    {
        if (action == null)
            return;

        _executionQueue.Enqueue(action);
    }

    /// <summary>
    /// Thread-safe reference-counted synchronization handle for synchronous main thread dispatch.
    /// Manages the lifetime of <see cref="ManualResetEventSlim"/> across the calling thread and the
    /// Unity main thread, preventing <see cref="ObjectDisposedException"/> and post-timeout object corruption.
    /// </summary>
    private sealed class DispatchCompletionHandle : IDisposable
    {
        private readonly ManualResetEventSlim _event = new(false);
        private int _refCount = 2; // Reference 1: caller thread, Reference 2: dispatched queue action
        private int _state = StatePending;

        public const int StatePending = 0;
        public const int StateRunning = 1;
        public const int StateTimedOut = 2;

        public ManualResetEventSlim Event => _event;

        /// <summary>
        /// Attempts to transition the handle from Pending to Running.
        /// Returns true if execution can proceed, or false if the caller has already timed out.
        /// </summary>
        public bool TryBeginExecution()
        {
            return Interlocked.CompareExchange(ref _state, StateRunning, StatePending) == StatePending;
        }

        /// <summary>
        /// Attempts to transition the handle from Pending to TimedOut.
        /// Returns true if timeout was flagged before execution began on the main thread.
        /// </summary>
        public bool TryMarkTimedOut()
        {
            return Interlocked.CompareExchange(ref _state, StateTimedOut, StatePending) == StatePending;
        }

        /// <summary>
        /// Releases one reference. When the reference count reaches zero, the underlying event is disposed.
        /// </summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                _event.Dispose();
            }
        }

        /// <summary>
        /// Aborts the queued action's reference if enqueueing failed before the action was placed in the queue.
        /// </summary>
        public void AbortNotEnqueued()
        {
            Release();
        }

        public void Dispose()
        {
            Release();
        }
    }

    /// <summary>
    /// Synchronously executes a function on the Unity main thread and returns the result.
    /// If called directly from the main thread, executes immediately without context switching.
    /// <para>
    /// <b>DEADLOCK WARNING:</b> This blocking method must <b>NEVER</b> be called from within the ImGui render loop
    /// (<c>IImGuiWindow.OnRender</c> or the DirectX Present thread). In Unity's multithreaded rendering pipeline,
    /// the main scripting thread frequently blocks at the end of Update waiting for the graphics render thread
    /// to finish presenting the current frame before starting the next frame. Calling this blocking method from
    /// the render thread creates an engine pipeline deadlock that stalls the entire game until <paramref name="timeoutMs"/> expires.
    /// </para>
    /// <para>
    /// For all render-thread to main-thread communication, enforce asynchronous non-blocking dispatch via
    /// <see cref="Enqueue(Action)"/> or lock-free state snapshot pipelines (<see cref="StateSnapshotPipeline{T}"/>).
    /// </para>
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="func">Function to execute on the main thread.</param>
    /// <param name="timeoutMs">Timeout in milliseconds before aborting wait to prevent permanent deadlocks (default 2000ms).</param>
    /// <returns>The result of the function execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="func"/> is null.</exception>
    /// <exception cref="TimeoutException">Thrown if the main thread does not complete execution within <paramref name="timeoutMs"/>.</exception>
    /// <exception cref="AggregateException">Thrown if an unhandled exception occurred during execution on the main thread.</exception>
    public static T InvokeOnMainThread<T>(Func<T> func, int timeoutMs = 2000)
    {
        if (func == null)
            throw new ArgumentNullException(nameof(func));

        if (IsMainThread)
        {
            return func();
        }

        using var handle = new DispatchCompletionHandle();
        bool enqueued = false;
        T result = default;
        Exception caughtException = null;

        try
        {
            Enqueue(() =>
            {
                try
                {
                    if (!handle.TryBeginExecution())
                    {
                        // Caller already timed out before this action reached the front of the queue.
                        // Skip execution to prevent post-timeout object corruption.
                        return;
                    }

                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                    }
                }
                finally
                {
                    try
                    {
                        handle.Event.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Defensive guard in case event was unexpectedly disposed
                    }
                    finally
                    {
                        handle.Release();
                    }
                }
            });

            enqueued = true;

            if (!handle.Event.Wait(timeoutMs))
            {
                handle.TryMarkTimedOut();
                throw new TimeoutException(
                    $"InvokeOnMainThread timed out after {timeoutMs}ms waiting for Unity main thread. " +
                    "Deadlock note: InvokeOnMainThread must never be called from the ImGui render loop or render thread. " +
                    "Use UnityMainThreadDispatcher.Enqueue() or StateSnapshotPipeline<T> instead.");
            }

            if (caughtException != null)
            {
                throw new AggregateException("Exception occurred during main thread execution", caughtException);
            }

            return result;
        }
        finally
        {
            if (!enqueued)
            {
                handle.AbortNotEnqueued();
            }
        }
    }

    /// <summary>
    /// Synchronously executes an action on the Unity main thread with deadlock timeout protection.
    /// If called directly from the main thread, executes immediately without context switching.
    /// <para>
    /// <b>DEADLOCK WARNING:</b> This blocking method must <b>NEVER</b> be called from within the ImGui render loop
    /// (<c>IImGuiWindow.OnRender</c> or the DirectX Present thread). In Unity's multithreaded rendering pipeline,
    /// the main scripting thread frequently blocks at the end of Update waiting for the graphics render thread
    /// to finish presenting the current frame before starting the next frame. Calling this blocking method from
    /// the render thread creates an engine pipeline deadlock that stalls the entire game until <paramref name="timeoutMs"/> expires.
    /// </para>
    /// <para>
    /// For all render-thread to main-thread communication, enforce asynchronous non-blocking dispatch via
    /// <see cref="Enqueue(Action)"/> or lock-free state snapshot pipelines (<see cref="StateSnapshotPipeline{T}"/>).
    /// </para>
    /// </summary>
    /// <param name="action">Action to execute on the main thread.</param>
    /// <param name="timeoutMs">Timeout in milliseconds before aborting wait to prevent permanent deadlocks (default 2000ms).</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="action"/> is null.</exception>
    /// <exception cref="TimeoutException">Thrown if the main thread does not complete execution within <paramref name="timeoutMs"/>.</exception>
    /// <exception cref="AggregateException">Thrown if an unhandled exception occurred during execution on the main thread.</exception>
    public static void InvokeOnMainThread(Action action, int timeoutMs = 2000)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        if (IsMainThread)
        {
            action();
            return;
        }

        using var handle = new DispatchCompletionHandle();
        bool enqueued = false;
        Exception caughtException = null;

        try
        {
            Enqueue(() =>
            {
                try
                {
                    if (!handle.TryBeginExecution())
                    {
                        // Caller already timed out before this action reached the front of the queue.
                        // Skip execution to prevent post-timeout object corruption.
                        return;
                    }

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                    }
                }
                finally
                {
                    try
                    {
                        handle.Event.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Defensive guard in case event was unexpectedly disposed
                    }
                    finally
                    {
                        handle.Release();
                    }
                }
            });

            enqueued = true;

            if (!handle.Event.Wait(timeoutMs))
            {
                handle.TryMarkTimedOut();
                throw new TimeoutException(
                    $"InvokeOnMainThread timed out after {timeoutMs}ms waiting for Unity main thread. " +
                    "Deadlock note: InvokeOnMainThread must never be called from the ImGui render loop or render thread. " +
                    "Use UnityMainThreadDispatcher.Enqueue() or StateSnapshotPipeline<T> instead.");
            }

            if (caughtException != null)
            {
                throw new AggregateException("Exception occurred during main thread execution", caughtException);
            }
        }
        finally
        {
            if (!enqueued)
            {
                handle.AbortNotEnqueued();
            }
        }
    }
}
