using System.Diagnostics;
using System.Threading;

namespace RendererFinder;

public static class FrameGate
{
    private static int _state;             // 0 = open, 1 = closing
    private static int _inFlight;

    public static bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _state) != 0)
        {
            Interlocked.Decrement(ref _inFlight);
            return false;
        }
        return true;
    }

    public static void Exit() => Interlocked.Decrement(ref _inFlight);

    public static bool CloseAndDrain(int timeoutMs)
    {
        Volatile.Write(ref _state, 1);
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) != 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(1);
        }
        return true;
    }
}
