namespace SpaceBattle;

internal sealed class TickTiming
{
    private readonly List<double> _samples = [];

    public TimeSpan BootstrapDuration { get; private set; }

    public int SampleCount => _samples.Count;

    public void RecordBootstrap(TimeSpan duration)
    {
        BootstrapDuration = duration;
    }

    public void RecordTick(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _samples.Add(duration.TotalMilliseconds);
    }

    public TickPerformanceSnapshot Snapshot()
    {
        if (_samples.Count == 0)
        {
            return new TickPerformanceSnapshot(0, 0, 0, 0, 0);
        }

        var ordered = _samples.ToArray();
        Array.Sort(ordered);
        return new TickPerformanceSnapshot(
            ordered.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1]);
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
