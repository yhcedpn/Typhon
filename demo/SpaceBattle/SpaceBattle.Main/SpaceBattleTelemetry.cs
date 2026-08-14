using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Typhon.Engine;

namespace SpaceBattle;

/// <summary>应用系统和围栏阶段的稳定名称。</summary>
internal enum SpaceBattleSystemMetricId : byte
{
    FramePrepare,
    Publish,
    Behavior,
    Damage,
    DamageCleanup,
    Movement,
    Reap,
    AcquisitionCleanup,
    Observe,
    DirtyMarking,
    AabbRefresh,
    MigrateFence,
    FenceFinalize,
}

internal static class SpaceBattleSystemMetricCatalog
{
    public static readonly string[] Names =
    [
        "FramePrepare",
        "Publish",
        "Behavior",
        "Damage",
        "DamageCleanup",
        "Movement",
        "Reap",
        "AcquisitionCleanup",
        "Observe",
        "dirty_marking",
        "AABB_refresh",
        "migrate_fence",
        "FenceFinalize",
    ];

    public static string Name(SpaceBattleSystemMetricId id) => Names[(int)id];
}

public static class SpaceBattleTelemetrySampling
{
    public static bool IsSampleTick(long zeroBasedTickNumber) =>
        zeroBasedTickNumber >= 0 &&
        (zeroBasedTickNumber == 0 || zeroBasedTickNumber % 125 == 0);
}

/// <summary>一个系统在本次运行内的持续统计。</summary>
internal sealed class SpaceBattleSystemMetricAccumulator
{
    private readonly object _gate = new();
    private readonly List<double> _durationsMicroseconds = [];
    private long _entities;
    private int _workerMask;

    public void Record(long elapsedStopwatchTicks, int entities, int workerId)
    {
        var microseconds = elapsedStopwatchTicks <= 0
            ? 0d
            : elapsedStopwatchTicks * 1_000_000d / Stopwatch.Frequency;
        lock (_gate)
        {
            _durationsMicroseconds.Add(microseconds);
            _entities = entities;
            if ((uint)workerId < 31u)
            {
                _workerMask |= 1 << workerId;
            }
        }
    }
    public void RecordAggregate(long elapsedStopwatchTicks, int entities, int workerCount)
    {
        var microseconds = elapsedStopwatchTicks <= 0
            ? 0d
            : elapsedStopwatchTicks * 1_000_000d / Stopwatch.Frequency;
        lock (_gate)
        {
            _durationsMicroseconds.Add(microseconds);
            _entities = entities;
            _workerMask = workerCount >= 31
                ? int.MaxValue
                : (1 << Math.Max(0, workerCount)) - 1;
        }
    }

    public SpaceBattleSystemTelemetrySnapshot Snapshot(string name)
    {
        lock (_gate)
        {
            if (_durationsMicroseconds.Count == 0)
            {
                return new SpaceBattleSystemTelemetrySnapshot(name, 0d, 0d, 0d, 0, 0, 0);
            }

            var ordered = _durationsMicroseconds.ToArray();
            Array.Sort(ordered);
            var sum = 0d;
            foreach (var value in ordered)
            {
                sum += value;
            }

            return new SpaceBattleSystemTelemetrySnapshot(
                name,
                sum / ordered.Length,
                Percentile(ordered, 0.95),
                ordered[^1],
                checked((int)Math.Min(_entities, int.MaxValue)),
                BitOperations.PopCount((uint)_workerMask),
                ordered.Length);
        }
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

/// <summary>稳定的目标获取计数口径。</summary>
public sealed record SpaceBattleQueryMetricsSnapshot(
    long DirectQueries,
    long BatchedQueries,
    long GatherCandidates,
    long ExactDistanceTests)
{
    public long DirectQueryCount => DirectQueries;
    public long BatchedQueryCount => BatchedQueries;
    public long GatherCandidateCount => GatherCandidates;
    public long ExactTargetingDistanceTestCount => ExactDistanceTests;
}

/// <summary>稳定的战斗计数口径。</summary>
public sealed record SpaceBattleCombatMetricsSnapshot(
    long WeaponUses,
    long InRangeAttacks,
    long Damage,
    long Deaths)
{
    public long WeaponUseCount => WeaponUses;
    public long InRangeAttackCount => InRangeAttacks;
    public long DamageCount => Damage;
    public long DeathCount => Deaths;
}

/// <summary>一个应用系统或围栏阶段的耗时摘要。</summary>
public sealed record SpaceBattleSystemTelemetrySnapshot(
    string Name,
    double MeanMicroseconds,
    double P95Microseconds,
    double MaximumMicroseconds,
    int Entities,
    int Workers,
    int SampleCount)
{
    public double MeanUs => MeanMicroseconds;
    public double P95Us => P95Microseconds;
    public double MaxUs => MaximumMicroseconds;
    public int EntityCount => Entities;
    public int WorkerCount => Workers;
}

/// <summary>Observe 阶段发布的稳定战况和成本统计。</summary>
public sealed record SpaceBattleTelemetrySnapshot(
    long TickNumber,
    int AliveShips,
    int WanderingNextTick,
    int TrackingNextTick,
    int ApproachingNextTick,
    int AttackingNextTick,
    int TurningNextTick,
    int ValidLocksAfterMovement,
    TickPerformanceSnapshot TickPerformance,
    SpaceBattleQueryMetricsSnapshot Queries,
    SpaceBattleCombatMetricsSnapshot Combat,
    IReadOnlyList<SpaceBattleSystemTelemetrySnapshot> Systems)
{
    public int AliveCount => AliveShips;
    public int NextTickWandering => WanderingNextTick;
    public int NextTickTracking => TrackingNextTick;
    public int NextTickApproaching => ApproachingNextTick;
    public int NextTickAttacking => AttackingNextTick;
    public int NextTickTurning => TurningNextTick;
    public int ValidLockCount => ValidLocksAfterMovement;
    public bool HasRuntimeTelemetry { get; init; }

    public int NextTickModeTotal =>
        WanderingNextTick + TrackingNextTick + ApproachingNextTick + AttackingNextTick + TurningNextTick;
}

/// <summary>将稳定字段写成可供脚本消费的 key=value 文本。</summary>
public static class SpaceBattleTelemetryFormatter
{
    public static string Format(SpaceBattleTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder(768);
        AppendCore(builder, snapshot);
        foreach (var system in snapshot.Systems)
        {
            builder.AppendLine();
            builder.Append("system=").Append(system.Name)
                .Append(" mean_us=").Append(Fixed(system.MeanMicroseconds))
                .Append(" p95_us=").Append(Fixed(system.P95Microseconds))
                .Append(" max_us=").Append(Fixed(system.MaximumMicroseconds))
                .Append(" entities=").Append(Int(system.Entities))
                .Append(" workers=").Append(Int(system.Workers));
        }

        return builder.ToString();
    }

    public static string FormatCore(SpaceBattleTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder(512);
        AppendCore(builder, snapshot);
        return builder.ToString();
    }

    private static void AppendCore(StringBuilder builder, SpaceBattleTelemetrySnapshot snapshot)
    {
        var tick = snapshot.TickPerformance;
        builder.Append("tick=").Append(Int(snapshot.TickNumber + 1))
            .Append(" alive=").Append(Int(snapshot.AliveShips))
            .Append(" next_wandering=").Append(Int(snapshot.WanderingNextTick))
            .Append(" next_tracking=").Append(Int(snapshot.TrackingNextTick))
            .Append(" next_approaching=").Append(Int(snapshot.ApproachingNextTick))
            .Append(" next_attacking=").Append(Int(snapshot.AttackingNextTick))
            .Append(" next_turning=").Append(Int(snapshot.TurningNextTick))
            .Append(" valid_locks=").Append(Int(snapshot.ValidLocksAfterMovement))
            .Append(" tick_p50_ms=").Append(Fixed(tick.P50Milliseconds))
            .Append(" tick_p95_ms=").Append(Fixed(tick.P95Milliseconds))
            .Append(" tick_p99_ms=").Append(Fixed(tick.P99Milliseconds))
            .Append(" tick_max_ms=").Append(Fixed(tick.MaximumMilliseconds))
            .Append(" tick_over_40ms=").Append(Int(tick.Over40Milliseconds))
            .Append(" actual_hz=").Append(Fixed(tick.ActualHz))
            .Append(" overload=").Append(tick.Overload ?? "none")
            .Append(" tick_multiplier=").Append(Int(tick.TickMultiplier))
            .Append(" workers=").Append(Int(tick.WorkerCount))
            .Append(" systems=").Append(Int(tick.SystemCount))
            .Append(" query_direct=").Append(Int(snapshot.Queries.DirectQueries))
            .Append(" query_batched=").Append(Int(snapshot.Queries.BatchedQueries))
            .Append(" gather_candidates=").Append(Int(snapshot.Queries.GatherCandidates))
            .Append(" exact_distance_tests=").Append(Int(snapshot.Queries.ExactDistanceTests))
            .Append(" weapon_uses=").Append(Int(snapshot.Combat.WeaponUses))
            .Append(" in_range_attacks=").Append(Int(snapshot.Combat.InRangeAttacks))
            .Append(" damage=").Append(Int(snapshot.Combat.Damage))
            .Append(" deaths=").Append(Int(snapshot.Combat.Deaths));
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Fixed(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
