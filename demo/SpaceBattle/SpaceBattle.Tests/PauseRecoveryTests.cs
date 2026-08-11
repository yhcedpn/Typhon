using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class PauseRecoveryTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "SpaceBattle.Tests",
            TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public void CancellationDuringRun_PausesAfterFenceAndResumesFromPersistedTick()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "pause.typhon");
        using var cancellation = new CancellationTokenSource();
        InitialWorldSnapshot paused;

        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   cancellation.Token,
                   new RecordingObservationSink()))
        {
            Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);

            cancellation.Cancel();

            Assert.Multiple(() =>
            {
                Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(simulation.WaitForTerminal(TimeSpan.FromMilliseconds(100)), Is.False);
            });

            paused = simulation.GetSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(paused.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
                Assert.That(paused.Run.CompletedTicks, Is.GreaterThanOrEqualTo(1));
                Assert.That(simulation.WaitForCompletedTicks(
                    checked(paused.Run.CompletedTicks + 1),
                    TimeSpan.FromMilliseconds(200)), Is.False);
            });
        }

        var persisted = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
            Assert.That(persisted.Run.CompletedTicks, Is.EqualTo(paused.Run.CompletedTicks));
            Assert.That(persisted.Ships, Is.EqualTo(paused.Ships));
            Assert.That(persisted.TargetLocks, Is.EqualTo(paused.TargetLocks));
        });

        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(resumed.StartupResult.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));

        var resumedAtPause = resumed.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(resumedAtPause.Run.CompletedTicks, Is.EqualTo(paused.Run.CompletedTicks));
            Assert.That(resumedAtPause.Ships, Is.EqualTo(paused.Ships));
            Assert.That(resumedAtPause.TargetLocks, Is.EqualTo(paused.TargetLocks));
        });

        var nextTick = checked(paused.Run.CompletedTicks + 1);
        Assert.That(resumed.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
        var afterResume = resumed.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(afterResume.Run.ProcessSegment, Is.EqualTo(2));
            Assert.That(afterResume.Run.CompletedTicks, Is.GreaterThanOrEqualTo(nextTick));
        });

        resumed.RequestPause();
        Assert.That(resumed.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void RuntimeDiagnostics_RebuildAcrossPauseAndResumeWithoutChangingMembership()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "runtime-state.typhon");
        using var cancellation = new CancellationTokenSource();
        SpaceBattleRuntimeDiagnosticsSnapshot pausedDiagnostics;

        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   cancellation.Token,
                   new RecordingObservationSink()))
        {
            Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);
            cancellation.Cancel();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
            pausedDiagnostics = simulation.GetRuntimeDiagnostics();

            Assert.Multiple(() =>
            {
                Assert.That(pausedDiagnostics.ViewMembershipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.CombatViewMembershipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.ShipRosterCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.TickWorksetCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.DerivedAliveShipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.DerivedActiveLockCount, Is.EqualTo(0));
                Assert.That(pausedDiagnostics.ConsumerProcessingCounts["State"], Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.ConsumerProcessingCounts["Combat"], Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.RuntimeShipViewRefreshCount, Is.GreaterThan(0));
                Assert.That(
                    pausedDiagnostics.RuntimeShipViewRefreshCount,
                    Is.EqualTo(checked((long)pausedDiagnostics.CompletedTicks)));
                Assert.That(
                    pausedDiagnostics.CombatShipViewRefreshCount,
                    Is.EqualTo(pausedDiagnostics.RuntimeShipViewRefreshCount));
                Assert.That(pausedDiagnostics.RuntimeShipViewAddedCount, Is.Zero);
                Assert.That(pausedDiagnostics.CombatShipViewAddedCount, Is.Zero);
            });
        }

        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        var resumedDiagnostics = resumed.GetRuntimeDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(resumedDiagnostics.ViewMembershipCount, Is.EqualTo(pausedDiagnostics.ViewMembershipCount));
            Assert.That(resumedDiagnostics.CombatViewMembershipCount, Is.EqualTo(pausedDiagnostics.CombatViewMembershipCount));
            Assert.That(resumedDiagnostics.ShipRosterCount, Is.EqualTo(pausedDiagnostics.ShipRosterCount));
            Assert.That(resumedDiagnostics.TickWorksetCount, Is.EqualTo(pausedDiagnostics.TickWorksetCount));
            Assert.That(resumedDiagnostics.DerivedAliveShipCount, Is.EqualTo(pausedDiagnostics.DerivedAliveShipCount));
            Assert.That(resumedDiagnostics.DerivedActiveLockCount, Is.EqualTo(pausedDiagnostics.DerivedActiveLockCount));
        });

        var nextTick = checked(pausedDiagnostics.CompletedTicks + 1);
        Assert.That(resumed.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
        resumed.RequestPause();
        Assert.That(resumed.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
        var afterResumeDiagnostics = resumed.GetRuntimeDiagnostics();
        Assert.Multiple(() =>
        {
            Assert.That(afterResumeDiagnostics.ViewMembershipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.CombatViewMembershipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ShipRosterCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.DerivedAliveShipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ConsumerProcessingCounts["State"], Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ConsumerProcessingCounts["Combat"], Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.RuntimeShipViewAddedCount, Is.Zero);
            Assert.That(afterResumeDiagnostics.CombatShipViewAddedCount, Is.Zero);
            Assert.That(
                afterResumeDiagnostics.RuntimeShipViewRefreshCount,
                Is.EqualTo(checked((long)(afterResumeDiagnostics.CompletedTicks - pausedDiagnostics.CompletedTicks))));
            Assert.That(
                afterResumeDiagnostics.CombatShipViewRefreshCount,
                Is.EqualTo(afterResumeDiagnostics.RuntimeShipViewRefreshCount));
        });
    }

    [Test]
    public void RuntimeDiagnostics_ExposeStableRosterAndCurrentTickWorksetKeys()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "roster-workset.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);

        var expectedKeys = simulation.GetSnapshot().Ships
            .Select(static ship => ship.EntityKey)
            .Order()
            .ToArray();
        var diagnostics = simulation.GetRuntimeDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.ShipRosterEntityKeys, Is.EqualTo(expectedKeys));
            Assert.That(diagnostics.TickWorksetEntityKeys, Is.EqualTo(expectedKeys));
        });
    }

    [Test]
    public void RuntimeDiagnostics_IndexesTargetLocksByOwnerAndTargetAtPauseBoundary()
    {
        var definition = new SimulationDefinition(
            runName: "runtime-lock-diagnostics-test",
            shipCount: 64,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 100f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "runtime-lock-diagnostics.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        InitialWorldSnapshot snapshot = simulation.WaitForSnapshot(252, TimeSpan.FromSeconds(20));
        simulation.RequestPause();
        Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);

        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics = simulation.GetRuntimeDiagnostics();
        IReadOnlyDictionary<long, int> ownerCounts = snapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.OwnerEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());
        IReadOnlyDictionary<long, int> targetCounts = snapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.TargetEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.OwnerLockIndex, Is.EqualTo(ownerCounts));
            Assert.That(diagnostics.TargetLockIndex, Is.EqualTo(targetCounts));
            Assert.That(diagnostics.DerivedActiveLockCount, Is.EqualTo(snapshot.TargetLocks.Count));
            Assert.That(diagnostics.DerivedAliveShipCount, Is.EqualTo(snapshot.Ships.Count));
        });
    }

    private static SimulationDefinition CreateDefinition() => new(
        runName: "pause-recovery-test",
        shipCount: 4,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 100f,
        maximumHealth: 1_000,
        stagingTicks: 0,
        spatialCellSize: 100f,
        spatialMargin: 20f,
        maximumCompletedTicks: 100);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
