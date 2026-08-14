using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
[NonParallelizable]
public sealed class DeterminismTests
{
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

    [Test]
    public void FixedOneWorker_RepeatedHostRunsHaveTheSameDiagnosticChecksums()
    {
        var definition = CreateDefinition(workerCount: 1);
        var first = RunDiagnostic(definition, "one-first");
        var second = RunDiagnostic(definition, "one-second");
        var comparison = SpaceBattleDeterminism.Compare(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Scope, Is.EqualTo(SpaceBattleDeterminismScope.FixedWorkerTopology));
            Assert.That(comparison.IsExplicitWorkerCountComparison, Is.False);
            Assert.That(comparison.IsMatch, Is.True);
            Assert.That(first.Tick, Is.EqualTo(first.Run.CompletedTicks));
            Assert.That(second.Tick, Is.EqualTo(second.Run.CompletedTicks));
            Assert.That(first.AliveShips, Is.EqualTo(first.Run.RemainingShips));
            Assert.That(first.Tick, Is.EqualTo(second.Tick));
            Assert.That(first.AliveShips, Is.EqualTo(second.AliveShips));
            Assert.That(first.HealthChecksum, Is.EqualTo(second.HealthChecksum));
            Assert.That(first.TargetChecksum, Is.EqualTo(second.TargetChecksum));
            Assert.That(first.ModeChecksum, Is.EqualTo(second.ModeChecksum));
            Assert.That(second.AliveShips, Is.EqualTo(second.Run.RemainingShips));
        });
    }

    [Test]
    public void FixedFourWorkers_RepeatedHostRunsHaveTheSameDiagnosticChecksums()
    {
        var definition = CreateDefinition(workerCount: 4);
        var first = RunDiagnostic(definition, "four-first");
        var second = RunDiagnostic(definition, "four-second");
        var comparison = SpaceBattleDeterminism.Compare(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Scope, Is.EqualTo(SpaceBattleDeterminismScope.FixedWorkerTopology));
            Assert.That(comparison.IsExplicitWorkerCountComparison, Is.False);
            Assert.That(comparison.IsMatch, Is.True);
            Assert.That(first.Tick, Is.EqualTo(first.Run.CompletedTicks));
            Assert.That(second.Tick, Is.EqualTo(second.Run.CompletedTicks));
            Assert.That(first.Tick, Is.EqualTo(second.Tick));
            Assert.That(first.AliveShips, Is.EqualTo(second.AliveShips));
            Assert.That(first.AliveShips, Is.EqualTo(first.Run.RemainingShips));
            Assert.That(first.HealthChecksum, Is.EqualTo(second.HealthChecksum));
            Assert.That(first.TargetChecksum, Is.EqualTo(second.TargetChecksum));
            Assert.That(first.ModeChecksum, Is.EqualTo(second.ModeChecksum));
            Assert.That(second.AliveShips, Is.EqualTo(second.Run.RemainingShips));
        });
    }

    [Test]
    public void HostDiagnostic_ObservesPublishedStatisticsAndDatabaseState()
    {
        var definition = CreateDefinition(workerCount: 1);
        var sink = new RecordingSink();
        var observedRoot = Path.Combine(_root, "observed");
        var diagnostic = SpaceBattleHost.RunDeterminismDiagnostic(
            definition,
            observedRoot,
            CancellationToken.None,
            sink);
        var databaseSnapshot = SpaceBattleHost.ReadSnapshot(definition, observedRoot);
        var finalTick = sink.Items
            .OfType<SimulationTickCompleted>()
            .Single(static tick => tick.PublishedSnapshot is not null);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Run.IsFatal, Is.False);
            Assert.That(diagnostic.Run.IsCancelled, Is.False);
            Assert.That(sink.Items.OfType<InitializationCompleted>().Count(), Is.EqualTo(1));
            Assert.That(sink.Items.OfType<SimulationTickCompleted>().Count(), Is.EqualTo(diagnostic.Tick));
            Assert.That(sink.Items.OfType<SimulationCompleted>().Count(), Is.EqualTo(1));
            Assert.That(diagnostic.Run.TickPerformance.SampleCount, Is.EqualTo(diagnostic.Tick));
            Assert.That(SpaceBattleHost.ReadShipCount(definition, observedRoot), Is.EqualTo(diagnostic.AliveShips));
            Assert.That(databaseSnapshot.Ships, Is.EqualTo(diagnostic.FinalSnapshot.Ships).AsCollection);
            Assert.That(finalTick.PublishedSnapshot.Ships, Is.EqualTo(diagnostic.FinalSnapshot.Ships).AsCollection);
            Assert.That(diagnostic.Run.PublishedSnapshot.Ships, Is.EqualTo(diagnostic.FinalSnapshot.Ships).AsCollection);
        });
    }

    [Test]
    public void Diagnostic_RequiresAnExplicitWorkerCount()
    {
        var definition = CreateDefinition(SimulationDefinition.AutomaticWorkerCount);
        Assert.That(
            () => SpaceBattleHost.RunDeterminismDiagnostic(
                definition,
                Path.Combine(_root, "automatic"),
                CancellationToken.None,
                new RecordingSink()),
            Throws.ArgumentException);
    }

    [Test]
    [Explicit("跨 worker 数只作为显式复现诊断；尚未宣称 worker-count independent determinism。")]
    public void DifferentWorkerCounts_AreMarkedAsAnExplicitDiagnostic()
    {
        var oneWorker = RunDiagnostic(CreateDefinition(workerCount: 1), "explicit-one");
        var fourWorkers = RunDiagnostic(CreateDefinition(workerCount: 4), "explicit-four");
        var comparison = SpaceBattleDeterminism.Compare(oneWorker, fourWorkers);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Scope, Is.EqualTo(SpaceBattleDeterminismScope.ExplicitWorkerCountComparison));
            Assert.That(comparison.IsExplicitWorkerCountComparison, Is.True);
        });
    }

    private SpaceBattleDeterminismDiagnostic RunDiagnostic(SimulationDefinition definition, string name)
    {
        var runRoot = Path.Combine(_root, name);
        return SpaceBattleHost.RunDeterminismDiagnostic(
            definition,
            runRoot,
            CancellationToken.None,
            new RecordingSink());
    }

    private static SimulationDefinition CreateDefinition(int workerCount) =>
        new(
            shipCount: 65,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: 20f,
            worldHeight: 20f,
            worldDepth: 20f,
            maximumHealth: 250,
            tickRate: 100,
            fixedDeltaSeconds: SimulationDefinition.FixedSimulationDeltaSeconds,
            maximumCompletedTicks: 8,
            spatialCellSize: 10f,
            runName: "determinism",
            workerCount: workerCount);

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation)
        {
            Items.Add(observation);
        }
    }
}
