using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace DearImGuiInjectionPlus.BepInEx;

/// <summary>
/// High-performance, exception-resilient dispatcher ensuring mods and ImGui callbacks
/// can safely interact with UnityEngine.Object on the Unity main thread without engine crashes.
/// </summary>
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _executionQueue = new();
    private static UnityMainThreadDispatcher _instance;
    private static int _mainThreadId = -1;

    public const int MaxBatchPerFrame = 500;

    /// <summary>
    /// Returns true if the current calling thread is the Unity main scripting thread.
    /// </summary>
    public static bool IsMainThread => _mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    private void Awake()
    {
        _instance = this;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        DontDestroyOnLoad(gameObject);
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
    /// Synchronously executes a function on the Unity main thread and returns the result.
    /// If called directly from the main thread, executes immediately without context switching.
    /// If called from a background/render thread, blocks until completion or timeout with deadlock protection.
    /// </summary>
    public static T InvokeOnMainThread<T>(Func<T> func, int timeoutMs = 2000)
    {
        if (func == null)
            throw new ArgumentNullException(nameof(func));

        if (IsMainThread)
        {
            return func();
        }

        using var completionEvent = new ManualResetEventSlim(false);
        T result = default;
        Exception caughtException = null;

        Enqueue(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
            finally
            {
                completionEvent.Set();
            }
        });

        if (!completionEvent.Wait(timeoutMs))
        {
            throw new TimeoutException($"InvokeOnMainThread timed out after {timeoutMs}ms waiting for Unity main thread.");
        }

        if (caughtException != null)
        {
            throw new AggregateException("Exception occurred during main thread execution", caughtException);
        }

        return result;
    }

    /// <summary>
    /// Synchronously executes an action on the Unity main thread with deadlock timeout protection.
    /// </summary>
    public static void InvokeOnMainThread(Action action, int timeoutMs = 2000)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        if (IsMainThread)
        {
            action();
            return;
        }

        using var completionEvent = new ManualResetEventSlim(false);
        Exception caughtException = null;

        Enqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
            finally
            {
                completionEvent.Set();
            }
        });

        if (!completionEvent.Wait(timeoutMs))
        {
            throw new TimeoutException($"InvokeOnMainThread timed out after {timeoutMs}ms waiting for Unity main thread.");
        }

        if (caughtException != null)
        {
            throw new AggregateException("Exception occurred during main thread execution", caughtException);
        }
    }
}