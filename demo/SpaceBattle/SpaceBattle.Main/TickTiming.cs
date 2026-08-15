using System.Diagnostics;
using static SpaceBattle.PercentileMath;

namespace SpaceBattle;

internal sealed class TickTiming
{
    private readonly BoundedDurationWindow _samples = new();
    private readonly long[] _tickStarts = new long[BoundedDurationWindow.Capacity];
    private int _nextTickStart;
    private int _tickStartCount;

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

        _samples.Add(duration.TotalMilliseconds);
        if (tickStartTimestamp == 0)
        {
            tickStartTimestamp = Stopwatch.GetTimestamp();
        }

        _tickStarts[_nextTickStart] = tickStartTimestamp;
        _nextTickStart = (_nextTickStart + 1) % _tickStarts.Length;
        if (_tickStartCount < _tickStarts.Length)
        {
            _tickStartCount++;
        }
    }

    public TickPerformanceSnapshot Snapshot(
        string overload = "none",
        int tickMultiplier = 1,
        int workerCount = 0,
        int systemCount = 0)
    {
        var statistics = _samples.Snapshot();
        if (statistics.SampleCount == 0)
        {
            return new TickPerformanceSnapshot(0, 0, 0, 0, 0)
            {
                Overload = overload,
                TickMultiplier = tickMultiplier,
                WorkerCount = workerCount,
                SystemCount = systemCount,
            };
        }

        var actualHz = 0d;
        if (_tickStartCount > 1)
        {
            var firstIndex = _tickStartCount == _tickStarts.Length ? _nextTickStart : 0;
            var lastIndex = (_nextTickStart + _tickStarts.Length - 1) % _tickStarts.Length;
            var elapsed = _tickStarts[lastIndex] - _tickStarts[firstIndex];
            if (elapsed > 0)
            {
                actualHz = (_tickStartCount - 1d) * Stopwatch.Frequency / elapsed;
            }
        }

        return new TickPerformanceSnapshot(
            statistics.SampleCount,
            statistics.P50,
            statistics.P95,
            statistics.P99,
            statistics.Maximum)
        {
            Over40Milliseconds = _samples.CountGreaterThan(40d),
            ActualHz = actualHz,
            Overload = overload,
            TickMultiplier = tickMultiplier,
            WorkerCount = workerCount,
            SystemCount = systemCount,
        };
    }
}
