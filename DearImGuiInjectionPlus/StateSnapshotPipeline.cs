using System;
using System.Threading;

namespace DearImGuiInjectionPlus;

/// <summary>
/// A zero-allocation, lock-free state snapshot pipeline.
/// Allows the Unity main thread (e.g. Update/LateUpdate) to publish state snapshots,
/// and the ImGui render thread (inside Present) to read the latest state safely without locks or thread contention.
/// </summary>
/// <typeparam name="T">Snapshot data type (typically an immutable class or snapshot record).</typeparam>
public class StateSnapshotPipeline<T> where T : class
{
    private T _latestSnapshot;

    public StateSnapshotPipeline(T initialSnapshot = null)
    {
        _latestSnapshot = initialSnapshot;
    }

    /// <summary>
    /// Publishes a new snapshot from the Unity main thread. Lock-free and atomic.
    /// </summary>
    public void Publish(T newSnapshot)
    {
        Interlocked.Exchange(ref _latestSnapshot, newSnapshot);
    }

    /// <summary>
    /// Reads the most recent snapshot from the render thread. Lock-free and zero-allocation.
    /// </summary>
    public T GetLatest()
    {
        return Volatile.Read(ref _latestSnapshot);
    }
}

/// <summary>
/// Thread-safe double-buffered snapshot pipeline for value types (structs).
/// Eliminates GC pressure by copying into alternating pre-allocated slots.
/// </summary>
/// <typeparam name="T">Struct snapshot data type.</typeparam>
public class StructSnapshotPipeline<T> where T : struct
{
    private T _slot0;
    private T _slot1;
    private volatile int _activeReadIndex;
    private readonly object _writeLock = new();

    public StructSnapshotPipeline(T initial = default)
    {
        _slot0 = initial;
        _slot1 = initial;
        _activeReadIndex = 0;
    }

    /// <summary>
    /// Publishes a new struct snapshot from the Unity main thread.
    /// </summary>
    public void Publish(in T snapshot)
    {
        lock (_writeLock)
        {
            int writeIndex = 1 - _activeReadIndex;
            if (writeIndex == 0)
                _slot0 = snapshot;
            else
                _slot1 = snapshot;

            _activeReadIndex = writeIndex;
        }
    }

    /// <summary>
    /// Reads the most recent struct snapshot from the render thread.
    /// </summary>
    public T GetLatest()
    {
        return _activeReadIndex == 0 ? _slot0 : _slot1;
    }
}
