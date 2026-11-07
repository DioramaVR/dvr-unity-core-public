using System.Diagnostics;

namespace Dvr;

// Accumulates how long a repeated operation takes (average and worst) between
// PerfLog reports. Written from the main or render thread; good enough for
// diagnostics without locking.
internal sealed class FrameTimer
{
    long totalTicks, worstTicks;
    int count;

    public void Add(long startTimestamp)
    {
        long ticks = Stopwatch.GetTimestamp() - startTimestamp;
        totalTicks += ticks;
        if (ticks > worstTicks) worstTicks = ticks;
        count++;
    }

    // "avg/worst ms", then reset.
    public string TakeSummary()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        string s = count == 0 ? "-" : $"{totalTicks * ms / count:F1}/{worstTicks * ms:F0} ms";
        totalTicks = worstTicks = 0;
        count = 0;
        return s;
    }
}
