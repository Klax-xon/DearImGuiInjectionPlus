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
/// Thread-safe triple-buffered snapshot pipeline for value types (structs).
/// Eliminates GC pressure and torn reads by exchanging between three pre-allocated slots.
/// </summary>
/// <typeparam name="T">Struct snapshot data type.</typeparam>
public class StructSnapshotPipeline<T> where T : struct
{
    private T _slot0;
    private T _slot1;
    private T _slot2;
    private int _sharedIndex;
    private int _writeIndex;
    private int _readIndex;
    private readonly object _writeLock = new();

    private const int NewDataFlag = 0x4;
    private const int IndexMask = 0x3;

    public StructSnapshotPipeline(T initial = default)
    {
        _slot0 = initial;
        _slot1 = initial;
        _slot2 = initial;
        _readIndex = 0;
        _sharedIndex = 1;
        _writeIndex = 2;
    }

    /// <summary>
    /// Publishes a new struct snapshot from the Unity main thread.
    /// </summary>
    public void Publish(in T snapshot)
    {
        lock (_writeLock)
        {
            if (_writeIndex == 0)
                _slot0 = snapshot;
            else if (_writeIndex == 1)
                _slot1 = snapshot;
            else
                _slot2 = snapshot;

            int oldShared = Interlocked.Exchange(ref _sharedIndex, _writeIndex | NewDataFlag);
            _writeIndex = oldShared & IndexMask;
        }
    }

    /// <summary>
    /// Reads the most recent struct snapshot from the render thread. Lock-free and zero-allocation.
    /// </summary>
    public T GetLatest()
    {
        int shared = Volatile.Read(ref _sharedIndex);
        if ((shared & NewDataFlag) != 0)
        {
            while ((shared & NewDataFlag) != 0)
            {
                int prev = Interlocked.CompareExchange(ref _sharedIndex, _readIndex, shared);
                if (prev == shared)
                {
                    _readIndex = shared & IndexMask;
                    break;
                }
                shared = prev;
            }
        }

        return _readIndex switch
        {
            0 => _slot0,
            1 => _slot1,
            _ => _slot2
        };
    }
}

