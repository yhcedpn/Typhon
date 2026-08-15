using Typhon.Engine;

namespace SpaceBattle;

public interface ISpaceBattleObservationSink
{
    void Publish(SpaceBattleObservation observation);
}

public enum SpaceBattleTerminationReason : byte
{
    Draw = 0,
    Winner = 1,
    TickLimit = 2,
    Cancelled = 3,
    Fatal = 4,
    BootstrapOnly = 5,
}

public abstract record SpaceBattleObservation;

public sealed record InitializationProgress(
    int CreatedShips,
    int TotalShips) : SpaceBattleObservation;

public sealed record InitializationCompleted(
    int ShipCount,
    TimeSpan BootstrapDuration) : SpaceBattleObservation;

public sealed record SimulationTickCompleted(
    long TickNumber,
    int ShipCount,
    TimeSpan Duration,
    SpaceBattleSnapshot PublishedSnapshot) : SpaceBattleObservation
{
    public SpaceBattleTelemetrySnapshot Telemetry { get; init; }
}


public sealed record SimulationCompleted(
    long CompletedTicks,
    int RemainingShips,
    TickPerformanceSnapshot TickPerformance,
    SpaceBattleSnapshot PublishedSnapshot) : SpaceBattleObservation
{
    public SpaceBattleTerminationReason TerminationReason { get; init; }
    public string FailedSystemName { get; init; }

    public Exception FatalException { get; init; }

    public TickOutcome? FatalOutcome { get; init; }
}

public sealed record TickPerformanceSnapshot(
    int SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds)
{
    public long Over40Milliseconds { get; init; }

    public double ActualHz { get; init; }

    public string Overload { get; init; } = "none";

    public int TickMultiplier { get; init; } = 1;

    public int WorkerCount { get; init; }

    public int SystemCount { get; init; }

}

public sealed record SpaceBattleRunResult(
    string DatabaseDirectory,
    int ShipCount,
    TimeSpan BootstrapDuration,
    TickPerformanceSnapshot TickPerformance)
{
    public long CompletedTicks { get; init; }

    public int RemainingShips { get; init; }

    public SpaceBattleSnapshot PublishedSnapshot { get; init; } = new([]);

    public SpaceBattleTerminationReason TerminationReason { get; init; }

    public string FailedSystemName { get; init; }

    public Exception FatalException { get; init; }

    public TickOutcome? FatalOutcome { get; init; }

    public bool IsCancelled => TerminationReason == SpaceBattleTerminationReason.Cancelled;

    public bool IsFatal => TerminationReason == SpaceBattleTerminationReason.Fatal;

    public int ExitCode => IsFatal ? 1 : 0;

    public string FatalExceptionText => FatalException?.ToString();
}

public readonly record struct ShipSnapshot(
    long EntityKey,
    Hull Hull,
    Motion Motion,
    Vitals Vitals,
    Targeting Targeting,
    Behavior Behavior);

public sealed record SpaceBattleSnapshot(IReadOnlyList<ShipSnapshot> Ships);
