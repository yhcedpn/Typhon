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
