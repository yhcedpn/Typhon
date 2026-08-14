namespace SpaceBattle;

public interface ISpaceBattleObservationSink
{
    void Publish(SpaceBattleObservation observation);
}

public abstract record SpaceBattleObservation;

public sealed record InitializationProgress(
    int CreatedShips,
    int TotalShips) : SpaceBattleObservation;

public sealed record InitializationCompleted(
    int ShipCount,
    TimeSpan BootstrapDuration) : SpaceBattleObservation
{
    public TimeSpan Duration => BootstrapDuration;
}

public sealed record SimulationTickCompleted(
    long TickNumber,
    int ShipCount,
    TimeSpan Duration,
    SpaceBattleSnapshot PublishedSnapshot) : SpaceBattleObservation;

public sealed record SimulationCompleted(
    long CompletedTicks,
    int RemainingShips,
    TickPerformanceSnapshot TickPerformance,
    SpaceBattleSnapshot PublishedSnapshot) : SpaceBattleObservation;

public sealed record TickPerformanceSnapshot(
    int SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds);

public sealed record SpaceBattleRunResult(
    string DatabaseDirectory,
    int ShipCount,
    TimeSpan BootstrapDuration,
    TickPerformanceSnapshot TickPerformance)
{
    public long CompletedTicks { get; init; }

    public int RemainingShips { get; init; }

    public SpaceBattleSnapshot PublishedSnapshot { get; init; }
}

public readonly record struct ShipSnapshot(
    long EntityKey,
    Hull Hull,
    Motion Motion,
    Vitals Vitals,
    Targeting Targeting,
    Behavior Behavior);

public sealed record SpaceBattleSnapshot(IReadOnlyList<ShipSnapshot> Ships);
