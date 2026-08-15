using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Typhon.Engine;
using static SpaceBattle.PercentileMath;

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
        "Observe",
        "dirty_marking",
        "AABB_refresh",
        "migrate_fence",
        "FenceFinalize",
    ];

}

public static class SpaceBattleTelemetrySampling
{
    public const long SamplePeriodTicks = 125;

    public static bool IsSampleTick(long zeroBasedTickNumber) =>
        zeroBasedTickNumber >= 0 &&
        (zeroBasedTickNumber == 0 || zeroBasedTickNumber % SamplePeriodTicks == 0);
}
internal readonly record struct DurationStatistics(
    int SampleCount,
    double Mean,
    double P50,
    double P95,
    double P99,
    double Maximum);

/// <summary>保存最近一段固定容量的耗时样本，并以有界成本计算统计值。</summary>
internal sealed class BoundedDurationWindow
{
    public const int Capacity = 4_096;

    private readonly double[] _samples = new double[Capacity];
    private int _next;
    private int _count;
    private double _sum;

    public int Count => _count;

    public void Add(double sample)
    {
        if (_count == Capacity)
        {
            _sum -= _samples[_next];
        }
        else
        {
            _count++;
        }

        _samples[_next] = sample;
        _sum += sample;
        _next = (_next + 1) % Capacity;
    }

    public DurationStatistics Snapshot()
    {
        if (_count == 0)
        {
            return default;
        }

        var ordered = new double[_count];
        Array.Copy(_samples, ordered, _count);
        Array.Sort(ordered);
        return new DurationStatistics(
            _count,
            _sum / _count,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1]);
    }

    public int CountGreaterThan(double threshold)
    {
        var count = 0;
        for (var index = 0; index < _count; index++)
        {
            if (_samples[index] > threshold)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>一个系统在最近固定样本窗口内的持续统计。</summary>
internal sealed class SpaceBattleSystemMetricAccumulator
{
    private readonly object _gate = new();
    private readonly BoundedDurationWindow _durations = new();
    private long _entities;
    private int _workerMask;

    public void Record(long elapsedStopwatchTicks, int entities, int workerId)
    {
        var microseconds = elapsedStopwatchTicks <= 0
            ? 0d
            : elapsedStopwatchTicks * 1_000_000d / Stopwatch.Frequency;
        lock (_gate)
        {
            _durations.Add(microseconds);
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
            _durations.Add(microseconds);
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
            var statistics = _durations.Snapshot();
            return new SpaceBattleSystemTelemetrySnapshot(
                name,
                statistics.Mean,
                statistics.P95,
                statistics.Maximum,
                statistics.SampleCount == 0 ? 0 : checked((int)Math.Min(_entities, int.MaxValue)),
                statistics.SampleCount == 0 ? 0 : BitOperations.PopCount((uint)_workerMask),
                statistics.SampleCount);
        }
    }
}

/// <summary>稳定的目标获取计数口径。</summary>
public sealed record SpaceBattleQueryMetricsSnapshot(
    long DirectQueries,
    long BatchedQueries,
    long GatherCandidates,
    long ExactDistanceTests);

/// <summary>稳定的战斗计数口径。</summary>
public sealed record SpaceBattleCombatMetricsSnapshot(
    long WeaponUses,
    long InRangeAttacks,
    long Damage,
    long Deaths);

/// <summary>一个应用系统或围栏阶段的耗时摘要。</summary>
public sealed record SpaceBattleSystemTelemetrySnapshot(
    string Name,
    double MeanMicroseconds,
    double P95Microseconds,
    double MaximumMicroseconds,
    int Entities,
    int Workers,
    int SampleCount);

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

    /// <summary>将稳定字段格式化为带分组标题和表格对齐的可读文本。</summary>
    public static string FormatHumanReadable(SpaceBattleTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder(1024);
        AppendHumanReadableCore(builder, snapshot);
        return builder.ToString();
    }

    /// <summary>追加可读格式的核心部分。</summary>
    private static void AppendHumanReadableCore(StringBuilder builder, SpaceBattleTelemetrySnapshot snapshot)
    {
        var tick = snapshot.TickPerformance;

        // ── Tick 头部 ──
        var tickStr = Int(snapshot.TickNumber + 1);
        builder.Append('─', 3);
        builder.Append(" Tick #").Append(tickStr);
        builder.Append(' ');
        builder.Append('─', Math.Max(1, 58 - tickStr.Length));
        builder.AppendLine();

        // 存活
        builder.Append("  Alive: ").AppendLine(Int(snapshot.AliveShips));

        // 下一 tick 模式
        builder.Append("  Next tick modes:");
        AppendMode(builder, "wand", snapshot.WanderingNextTick);
        AppendMode(builder, "trk", snapshot.TrackingNextTick);
        AppendMode(builder, "app", snapshot.ApproachingNextTick);
        AppendMode(builder, "atk", snapshot.AttackingNextTick);
        AppendMode(builder, "trn", snapshot.TurningNextTick);
        builder.AppendLine();

        // 有效锁定
        builder.Append("  Valid locks: ").AppendLine(Int(snapshot.ValidLocksAfterMovement));

        // Tick 计时
        builder.Append("  Tick timing: p50=").Append(Fixed(tick.P50Milliseconds)).Append("ms")
            .Append(" p95=").Append(Fixed(tick.P95Milliseconds)).Append("ms")
            .Append(" p99=").Append(Fixed(tick.P99Milliseconds)).Append("ms")
            .Append(" max=").Append(Fixed(tick.MaximumMilliseconds)).Append("ms")
            .Append(" over_40ms=").Append(Int(tick.Over40Milliseconds)).AppendLine();

        // 实际 Hz、overload 与倍率
        builder.Append("  Actual Hz: ").Append(Fixed(tick.ActualHz))
            .Append("  Overload: ").Append(tick.Overload ?? "none")
            .Append("  Multiplier: ").Append(Int(tick.TickMultiplier)).AppendLine();

        // worker 与系统数
        builder.Append("  Workers: ").Append(Int(tick.WorkerCount))
            .Append("  Systems: ").Append(Int(tick.SystemCount)).AppendLine();

        // ── 查询 ──
        builder.AppendLine();
        builder.Append('─', 3);
        builder.Append(" Queries ");
        builder.Append('─', 47);
        builder.AppendLine();
        builder.Append("  Direct: ").Append(Int(snapshot.Queries.DirectQueries))
            .Append("  Batched: ").Append(Int(snapshot.Queries.BatchedQueries)).AppendLine();
        builder.Append("  Candidates: ").Append(Int(snapshot.Queries.GatherCandidates))
            .Append("  Distance tests: ").Append(Int(snapshot.Queries.ExactDistanceTests)).AppendLine();

        // ── 战斗 ──
        builder.AppendLine();
        builder.Append('─', 3);
        builder.Append(" Combat ");
        builder.Append('─', 48);
        builder.AppendLine();
        builder.Append("  Weapon uses: ").Append(Int(snapshot.Combat.WeaponUses))
            .Append("  In-range attacks: ").Append(Int(snapshot.Combat.InRangeAttacks)).AppendLine();
        builder.Append("  Damage: ").Append(Int(snapshot.Combat.Damage))
            .Append("  Deaths: ").Append(Int(snapshot.Combat.Deaths)).AppendLine();

        // ── 每系统计时表 ──
        builder.AppendLine();
        builder.Append('─', 3);
        builder.Append(" Per-system timing ");
        builder.Append('─', 38);
        builder.AppendLine();

        // 计算名称最大宽度
        var nameWidth = "System".Length;
        foreach (var sys in snapshot.Systems)
        {
            if (sys.Name.Length > nameWidth) nameWidth = sys.Name.Length;
        }
        if (nameWidth > 40) nameWidth = 40;

        // 表头
        builder.Append("  ");
        AppendPaddedRight(builder, "System", nameWidth);
        builder.Append("  "); AppendPaddedLeft(builder, "Mean_us", 12);
        builder.Append("  "); AppendPaddedLeft(builder, "P95_us", 12);
        builder.Append("  "); AppendPaddedLeft(builder, "Max_us", 12);
        builder.Append("  "); AppendPaddedLeft(builder, "Entities", 9);
        builder.Append("  "); AppendPaddedLeft(builder, "Workers", 7);
        builder.Append("  "); AppendPaddedLeft(builder, "Samples", 7);
        builder.AppendLine();

        // 数据行
        foreach (var sys in snapshot.Systems)
        {
            builder.Append("  ");
            AppendPaddedRight(builder, sys.Name, nameWidth);
            builder.Append("  "); AppendPaddedLeft(builder, Fixed(sys.MeanMicroseconds), 12);
            builder.Append("  "); AppendPaddedLeft(builder, Fixed(sys.P95Microseconds), 12);
            builder.Append("  "); AppendPaddedLeft(builder, Fixed(sys.MaximumMicroseconds), 12);
            builder.Append("  "); AppendPaddedLeft(builder, Int(sys.Entities), 9);
            builder.Append("  "); AppendPaddedLeft(builder, Int(sys.Workers), 7);
            builder.Append("  "); AppendPaddedLeft(builder, Int(sys.SampleCount), 7);
            builder.AppendLine();
        }
    }

    private static void AppendPaddedLeft(StringBuilder builder, string value, int totalWidth)
    {
        var padding = totalWidth - value.Length;
        if (padding > 0) builder.Append(' ', padding);
        builder.Append(value);
    }

    private static void AppendPaddedRight(StringBuilder builder, string value, int totalWidth)
    {
        builder.Append(value);
        var padding = totalWidth - value.Length;
        if (padding > 0) builder.Append(' ', padding);
    }

    private static void AppendMode(StringBuilder builder, string label, int value)
    {
        builder.Append(' ').Append(label).Append('=').Append(Int(value));
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
