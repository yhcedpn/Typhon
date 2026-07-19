using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class RuntimeProgressionTests
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
    public void Start_UsesOnePublicDagWithTheFixedSimulationCadence()
    {
        var definition = CreateDefinition(stagingTicks: 2);
        var databaseLocation = Path.Combine(_temporaryDirectory, "cadence.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.Multiple(() =>
        {
            Assert.That(simulation.TickRate, Is.EqualTo(25));
            Assert.That(simulation.SimulationDeltaSeconds, Is.EqualTo(0.04f));
            Assert.That(simulation.SystemNames, Is.EqualTo(new[]
            {
                "State",
                "Steering",
                "Movement",
                "Targeting",
                "Combat",
                "Resolution",
                "Output",
            }));
            Assert.That(simulation.SystemPhases, Is.EqualTo(new[]
            {
                "State",
                "Steering",
                "Movement",
                "Targeting",
                "Combat",
                "Resolution",
                "Output",
            }));
            Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);
        });
    }

    [Test]
    public void Start_KeepsShipsInStagingForTheFullDurationThenAdvancesExactlyOneFixedStep()
    {
        var definition = CreateDefinition(stagingTicks: 2);
        var databaseLocation = Path.Combine(_temporaryDirectory, "staging.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        var initial = simulation.GetSnapshot();

        Assert.That(simulation.WaitForCompletedTicks(2, TimeSpan.FromSeconds(5)), Is.True);
        var afterStaging = simulation.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(afterStaging.Run.CompletedTicks, Is.EqualTo(2));
            Assert.That(afterStaging.Ships.Select(static ship => ship.Position),
                Is.EqualTo(initial.Ships.Select(static ship => ship.Position)));
            Assert.That(afterStaging.Ships, Has.All.Matches<ShipSnapshot>(
                ship => ship.Mode == BehaviorMode.Staging && ship.ModeTicksRemaining == 0));
        });

        Assert.That(simulation.WaitForCompletedTicks(3, TimeSpan.FromSeconds(5)), Is.True);
        var afterMovement = simulation.GetSnapshot();
        var initialShip = afterStaging.Ships[0];
        var movedShip = afterMovement.Ships[0];
        var expectedStep = MovementRules.Advance(
            initialShip.Position,
            movedShip.Motion,
            SimulationDefinition.FixedSimulationDeltaSeconds,
            definition.WorldSize);
        var directionLengthSquared =
            (movedShip.Motion.DirectionX * movedShip.Motion.DirectionX) +
            (movedShip.Motion.DirectionY * movedShip.Motion.DirectionY) +
            (movedShip.Motion.DirectionZ * movedShip.Motion.DirectionZ);

        Assert.Multiple(() =>
        {
            Assert.That(afterMovement.Run.CompletedTicks, Is.EqualTo(3));
            Assert.That(movedShip.Mode, Is.EqualTo(BehaviorMode.Wandering));
            Assert.That(movedShip.Position, Is.EqualTo(expectedStep.Position));
            Assert.That(directionLengthSquared, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(movedShip.Motion.Speed, Is.InRange(0f, SimulationDefinition.MaximumWanderingSpeed));
            Assert.That(movedShip.Bounds, Is.EqualTo(new SpatialBoundsSnapshot(
                movedShip.Position.X,
                movedShip.Position.Y,
                movedShip.Position.Z,
                movedShip.Position.X,
                movedShip.Position.Y,
                movedShip.Position.Z)));
        });
    }

    private static SimulationDefinition CreateDefinition(ushort stagingTicks) => new(
        runName: "runtime-test",
        shipCount: 4,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: stagingTicks,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
