using System.Diagnostics;
namespace SpaceBattle;

internal sealed class TickTiming
{
    private readonly List<double> _samples = [];
    private long _over40Milliseconds;
    private long _firstTickStart;
    private long _lastTickStart;

    public TimeSpan BootstrapDuration { get; private set; }

    public int SampleCount => _samples.Count;

    public void RecordBootstrap(TimeSpan duration)
    {
        BootstrapDuration = duration;
    }

    public void RecordTick(TimeSpan duration, long tickStartTimestamp = 0)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var milliseconds = duration.TotalMilliseconds;
        _samples.Add(milliseconds);
        if (milliseconds > 40d)
        {
            _over40Milliseconds++;
        }

        if (tickStartTimestamp == 0)
        {
            tickStartTimestamp = Stopwatch.GetTimestamp();
        }

        if (_samples.Count == 1)
        {
            _firstTickStart = tickStartTimestamp;
        }

        _lastTickStart = tickStartTimestamp;
    }

    public TickPerformanceSnapshot Snapshot(
        string overload = "none",
        int tickMultiplier = 1,
        int workerCount = 0,
        int systemCount = 0)
    {
        if (_samples.Count == 0)
        {
            return new TickPerformanceSnapshot(0, 0, 0, 0, 0)
            {
                Overload = overload,
                TickMultiplier = tickMultiplier,
                WorkerCount = workerCount,
                SystemCount = systemCount,
            };
        }

        var ordered = _samples.ToArray();
        Array.Sort(ordered);
        var actualHz = 0d;
        if (ordered.Length > 1 && _lastTickStart > _firstTickStart)
        {
            actualHz = (ordered.Length - 1d) * Stopwatch.Frequency / (_lastTickStart - _firstTickStart);
        }

        return new TickPerformanceSnapshot(
            ordered.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1])
        {
            Over40Milliseconds = _over40Milliseconds,
            ActualHz = actualHz,
            Overload = overload,
            TickMultiplier = tickMultiplier,
            WorkerCount = workerCount,
            SystemCount = systemCount,
        };
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)position;
        var upper = Math.Min(lower + 1, ordered.Length - 1);
        var fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }
}
