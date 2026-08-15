using System.Diagnostics;
using System.Globalization;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class TelemetryTests
{
    [Test]
    public void SampleCadenceStartsAtTickOneThenEvery125Ticks()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(0), Is.True);
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(125), Is.True);
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(250), Is.True);
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(1), Is.False);
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(124), Is.False);
            Assert.That(SpaceBattleTelemetrySampling.IsSampleTick(-1), Is.False);
        });
    }

    [Test]
    public void TickTimingUsesInvariantPercentilesAndCountsOnlyDurationsOver40Milliseconds()
    {
        var timing = new TickTiming();
        var start = Stopwatch.Frequency * 10L;
        timing.RecordTick(TimeSpan.FromMilliseconds(40), start);
        timing.RecordTick(TimeSpan.FromMilliseconds(41), start + Stopwatch.Frequency);
        timing.RecordTick(TimeSpan.FromMilliseconds(80), start + (2 * Stopwatch.Frequency));

        var snapshot = timing.Snapshot(workerCount: 4, systemCount: 9);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SampleCount, Is.EqualTo(3));
            Assert.That(snapshot.P50Milliseconds, Is.EqualTo(41d));
            Assert.That(snapshot.P95Milliseconds, Is.GreaterThan(41d));
            Assert.That(snapshot.P99Milliseconds, Is.LessThanOrEqualTo(80d));
            Assert.That(snapshot.MaximumMilliseconds, Is.EqualTo(80d));
            Assert.That(snapshot.Over40Milliseconds, Is.EqualTo(2));
            Assert.That(snapshot.ActualHz, Is.EqualTo(1d).Within(0.0001d));
            Assert.That(snapshot.WorkerCount, Is.EqualTo(4));
            Assert.That(snapshot.SystemCount, Is.EqualTo(9));
        });
    }

    [Test]
    public void DurationStatisticsKeepOnlyTheLatestBoundedWindow()
    {
        const int windowSize = 4_096;
        var timing = new TickTiming();
        var system = new SpaceBattleSystemMetricAccumulator();

        for (var index = 0; index <= windowSize; index++)
        {
            timing.RecordTick(TimeSpan.FromMilliseconds(index));
            system.Record(index * Stopwatch.Frequency, entities: 1, workerId: 0);
        }

        var tickSnapshot = timing.Snapshot();
        var systemSnapshot = system.Snapshot("test");
        Assert.Multiple(() =>
        {
            Assert.That(tickSnapshot.SampleCount, Is.EqualTo(windowSize));
            Assert.That(tickSnapshot.P50Milliseconds, Is.GreaterThan(2_048d));
            Assert.That(tickSnapshot.MaximumMilliseconds, Is.EqualTo(4_096d));
            Assert.That(systemSnapshot.SampleCount, Is.EqualTo(windowSize));
            Assert.That(systemSnapshot.MeanMicroseconds, Is.GreaterThan(2_048_000_000d));
            Assert.That(systemSnapshot.MaximumMicroseconds, Is.EqualTo(4_096_000_000d));
        });
    }

    [Test]
    public void TelemetryRejectsUnknownBehaviorModeInsteadOfCountingItAsWandering()
    {
        var frames = new SpaceBattleFrameStore(shipCount: 1);
        frames.BeginTick();
        frames.Publish(default, new ShipSnapshot(
            EntityKey: 1,
            Hull: default,
            Motion: default,
            Vitals: new Vitals { CurrentHealth = 1 },
            Targeting: default,
            Behavior: new Behavior { Mode = byte.MaxValue }));
        var telemetry = new SpaceBattleTelemetryState(workerCount: 1);

        Assert.That(
            () => telemetry.BuildSnapshot(tickNumber: 0, timing: null, frames),
            Throws.InvalidOperationException.With.Message.Contains("未知行为模式"));
    }

    [Test]
    public void FormatterKeepsStableKeysWithoutStragglerGap()
    {
        var snapshot = new SpaceBattleTelemetrySnapshot(
            TickNumber: 0,
            AliveShips: 10,
            WanderingNextTick: 1,
            TrackingNextTick: 2,
            ApproachingNextTick: 3,
            AttackingNextTick: 2,
            TurningNextTick: 2,
            ValidLocksAfterMovement: 4,
            TickPerformance: new TickPerformanceSnapshot(1, 1, 2, 3, 4)
            {
                Over40Milliseconds = 1,
                ActualHz = 25,
                Overload = "normal",
                TickMultiplier = 1,
                WorkerCount = 4,
                SystemCount = 9,
            },
            Queries: new SpaceBattleQueryMetricsSnapshot(1, 2, 3, 4),
            Combat: new SpaceBattleCombatMetricsSnapshot(5, 6, 7, 8),
            Systems:
            [
                new SpaceBattleSystemTelemetrySnapshot("Movement", 1, 2, 3, 10, 4, 1),
            ]);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var text = SpaceBattleTelemetryFormatter.Format(snapshot);
            var core = SpaceBattleTelemetryFormatter.FormatCore(snapshot);
            var fields = core.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(static field => field[..field.IndexOf('=')])
                .ToHashSet(StringComparer.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(fields, Does.Contain("tick"));
                Assert.That(fields, Does.Contain("alive"));
                Assert.That(fields, Does.Contain("next_wandering"));
                Assert.That(fields, Does.Contain("next_tracking"));
                Assert.That(fields, Does.Contain("next_approaching"));
                Assert.That(fields, Does.Contain("next_attacking"));
                Assert.That(fields, Does.Contain("next_turning"));
                Assert.That(fields, Does.Contain("valid_locks"));
                Assert.That(fields, Does.Contain("tick_p50_ms"));
                Assert.That(fields, Does.Contain("tick_p95_ms"));
                Assert.That(fields, Does.Contain("tick_p99_ms"));
                Assert.That(fields, Does.Contain("tick_max_ms"));
                Assert.That(fields, Does.Contain("tick_over_40ms"));
                Assert.That(fields, Does.Contain("actual_hz"));
                Assert.That(fields, Does.Contain("overload"));
                Assert.That(fields, Does.Contain("tick_multiplier"));
                Assert.That(fields, Does.Contain("workers"));
                Assert.That(fields, Does.Contain("systems"));
                Assert.That(fields, Does.Contain("query_direct"));
                Assert.That(fields, Does.Contain("query_batched"));
                Assert.That(fields, Does.Contain("gather_candidates"));
                Assert.That(fields, Does.Contain("exact_distance_tests"));
                Assert.That(fields, Does.Contain("weapon_uses"));
                Assert.That(fields, Does.Contain("in_range_attacks"));
                Assert.That(fields, Does.Contain("damage"));
                Assert.That(fields, Does.Contain("deaths"));
                Assert.That(text, Does.Contain("actual_hz=25.000"));
                Assert.That(text, Does.Contain("system=Movement mean_us=1.000 p95_us=2.000 max_us=3.000 entities=10 workers=4"));
                Assert.That(text, Does.Not.Contain("StragglerGapUs"));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public void HostPublishesSampleWithModeSumAndStableSystemMetrics()
    {
        var root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(root);
        try
        {
            var sink = new RecordingSink();
            var definition = new SimulationDefinition(
                shipCount: 10,
                seed: SimulationDefinition.DefaultSeed,
                maximumCompletedTicks: 1);
            var result = SpaceBattleHost.Run(definition, root, CancellationToken.None, sink);
            var tick = sink.Items.OfType<SimulationTickCompleted>().Single();
            var telemetry = tick.Telemetry;

            Assert.That(telemetry, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(telemetry.AliveShips, Is.EqualTo(10));
                Assert.That(telemetry.NextTickModeTotal, Is.EqualTo(telemetry.AliveShips));
                Assert.That(telemetry.ValidLocksAfterMovement, Is.InRange(0, telemetry.AliveShips));
                Assert.That(telemetry.TickPerformance.SampleCount, Is.EqualTo(1));
                Assert.That(telemetry.TickPerformance.WorkerCount, Is.GreaterThanOrEqualTo(1));
                Assert.That(telemetry.Systems.Select(static system => system.Name), Does.Contain("dirty_marking"));
                Assert.That(telemetry.Systems.Select(static system => system.Name), Does.Contain("AABB_refresh"));
                Assert.That(telemetry.Systems.Select(static system => system.Name), Does.Contain("migrate_fence"));
                Assert.That(telemetry.Queries.DirectQueries, Is.GreaterThanOrEqualTo(0));
                Assert.That(telemetry.Combat.WeaponUses, Is.GreaterThanOrEqualTo(0));
                Assert.That(result.TickPerformance.SampleCount, Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation) => Items.Add(observation);
    }
}
