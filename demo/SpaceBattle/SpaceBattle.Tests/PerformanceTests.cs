using System.Globalization;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class PerformanceTests
{
    private const int ShipCount = 50_000;
    private const int TickRate = 25;
    private const int TotalTicks = 500;
    private const int WarmupTicks = 125;
    private const int MeasuredTicks = TotalTicks - WarmupTicks;

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // 仅 Release 的墙钟负载场景；共享 CI 机器无法提供稳定的延迟预算。
    [Test]
    [Explicit("Release-only 50,000-ship performance scenario; run manually on reference hardware.")]
    [Category("Performance")]
    [Category("Manual")]
    public void FiftyThousandShips_25Hz_500Ticks_ReportsMeasuredBreakdown()
    {
#if DEBUG
        Assert.Ignore("50,000 ship performance scenario is Release-only.");
#endif

        var definition = SimulationDefinition.Default with
        {
            ShipCount = ShipCount,
            TickRate = TickRate,
            FixedDeltaSeconds = SimulationDefinition.FixedSimulationDeltaSeconds,
            MaximumCompletedTicks = TotalTicks,
        };
        var sink = new PerformanceSink();

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, sink);
        var ticks = sink.TickCompletions;
        Assert.That(result.CompletedTicks, Is.GreaterThanOrEqualTo(TotalTicks), "性能场景必须至少完成配置的 500 tick；runtime 可在停止边界完成一个 in-flight tick。");
        Assert.That(ticks, Has.Count.GreaterThanOrEqualTo(TotalTicks), "性能场景必须提供至少 500 个 tick observation。");
        var measuredDurations = ticks
            .Where(static tick => tick.TickNumber >= WarmupTicks && tick.TickNumber < TotalTicks)
            .Select(static tick => tick.Duration.TotalMilliseconds)
            .ToArray();
        var measuredPerformance = Measure(measuredDurations);
        var latestTelemetry = sink.TelemetrySamples
            .Where(static sample => sample.TickNumber >= WarmupTicks)
            .OrderBy(static sample => sample.TickNumber)
            .LastOrDefault();
        Assert.That(latestTelemetry, Is.Not.Null, "500 tick 场景应在 warmup 后发布 telemetry sample。");

        TestContext.Progress.WriteLine(
            $"performance=spacebattle_50k_release breakdown=tick_wallclock " +
            $"ships={ShipCount.ToString(CultureInfo.InvariantCulture)} " +
            $"tick_rate_hz={TickRate.ToString(CultureInfo.InvariantCulture)} " +
            $"total_ticks={TotalTicks.ToString(CultureInfo.InvariantCulture)} " +
            $"warmup_discarded={WarmupTicks.ToString(CultureInfo.InvariantCulture)} " +
            $"measured_ticks={MeasuredTicks.ToString(CultureInfo.InvariantCulture)} " +
            $"bootstrap_ms={Fixed(result.BootstrapDuration.TotalMilliseconds)} " +
            $"p50_ms={Fixed(measuredPerformance.P50Milliseconds)} " +
            $"p95_ms={Fixed(measuredPerformance.P95Milliseconds)} " +
            $"p99_ms={Fixed(measuredPerformance.P99Milliseconds)} " +
            $"max_ms={Fixed(measuredPerformance.MaximumMilliseconds)} " +
            $"over_40ms={measuredPerformance.Over40Milliseconds.ToString(CultureInfo.InvariantCulture)} " +
            $"completed_ticks={result.CompletedTicks.ToString(CultureInfo.InvariantCulture)} " +
            $"remaining_ships={result.RemainingShips.ToString(CultureInfo.InvariantCulture)}");
        TestContext.Progress.WriteLine(
            $"warning={(measuredPerformance.P95Milliseconds > 40d ? "p95_over_40ms" : "none")} " +
            "threshold=warning_only_no_ci_failure");
        TestContext.Progress.WriteLine(SpaceBattleTelemetryFormatter.Format(latestTelemetry!));
        TestContext.Progress.WriteLine(
            "system_breakdown_scope=rolling_4096_duration_samples " +
            "warmup_inclusion=window_dependent fence_metrics=unexposed_zero_not_cost");
    }

    private static TickPerformanceSnapshot Measure(double[] durations)
    {
        var timing = new TickTiming();
        foreach (var duration in durations)
        {
            timing.RecordTick(TimeSpan.FromMilliseconds(duration));
        }

        return timing.Snapshot();
    }

    private static string Fixed(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class PerformanceSink : ISpaceBattleObservationSink
    {
        private readonly object _gate = new();
        private readonly List<SimulationTickCompleted> _ticks = [];
        private readonly List<SpaceBattleTelemetrySnapshot> _telemetrySamples = [];

        public IReadOnlyList<SimulationTickCompleted> TickCompletions
        {
            get
            {
                lock (_gate)
                {
                    return _ticks.ToArray();
                }
            }
        }

        public IReadOnlyList<SpaceBattleTelemetrySnapshot> TelemetrySamples
        {
            get
            {
                lock (_gate)
                {
                    return _telemetrySamples.ToArray();
                }
            }
        }

        public void Publish(SpaceBattleObservation observation)
        {
            lock (_gate)
            {
                if (observation is SimulationTickCompleted tick)
                {
                    _ticks.Add(tick);
                    if (tick.Telemetry is not null)
                    {
                        _telemetrySamples.Add(tick.Telemetry);
                    }
                }
            }
        }
    }
}
