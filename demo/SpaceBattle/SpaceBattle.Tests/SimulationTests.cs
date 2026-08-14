using NUnit.Framework;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class SimulationTests
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
    public void Run_ExecutesTheConfiguredFixedLogicFrames()
    {
        var definition = CreateDefinition(shipCount: 8, maximumCompletedTicks: 2);
        var sink = new RecordingSink();

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, sink);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Items.OfType<SimulationTickCompleted>().Count(), Is.EqualTo(2));
            Assert.That(sink.Items.OfType<SimulationCompleted>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void Movement_ChangesComponentsAndPublishesEntityKeySnapshots()
    {
        var definition = CreateDefinition(shipCount: 8, maximumCompletedTicks: 2);
        var initialRoot = Path.Combine(_root, "initial");
        SpaceBattleHost.BootstrapOnly(definition, initialRoot, CancellationToken.None, new RecordingSink());
        var initial = SpaceBattleHost.ReadSnapshot(definition, initialRoot);

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());
        var final = SpaceBattleHost.ReadSnapshot(definition, _root);

        Assert.That(final.Ships, Has.Count.EqualTo(8));
        Assert.That(result.PublishedSnapshot.Ships, Has.Count.EqualTo(8));
        Assert.That(final.Ships.Select(static ship => ship.EntityKey), Is.EqualTo(Enumerable.Range(1, 8).Select(static value => (long)value)));
        Assert.That(final.Ships.Any(ship => ship.Motion.Speed > 0f), Is.True);
        Assert.That(final.Ships.Any(ship =>
            MathF.Abs(initial.Ships.Single(old => old.EntityKey == ship.EntityKey).Hull.Bounds.MinX - ship.Hull.Bounds.MinX) > 0.001f ||
            MathF.Abs(initial.Ships.Single(old => old.EntityKey == ship.EntityKey).Hull.Bounds.MinY - ship.Hull.Bounds.MinY) > 0.001f), Is.True);
        Assert.That(result.PublishedSnapshot.Ships.Select(static ship => ship.EntityKey), Is.EqualTo(final.Ships.Select(static ship => ship.EntityKey)));
    }

    [Test]
    public void SpatialQuery_FindsShipsAtTheirMovedBounds()
    {
        var definition = CreateDefinition(shipCount: 8, maximumCompletedTicks: 70, worldWidth: 200f, worldHeight: 200f, spatialCellSize: 100f);
        SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());
        var snapshot = SpaceBattleHost.ReadSnapshot(definition, _root);

        foreach (var ship in snapshot.Ships)
        {
            var bounds = ship.Hull.Bounds;
            var found = SpaceBattleHost.QueryShipKeysInAabb(definition, _root, bounds);
            Assert.That(found, Does.Contain(ship.EntityKey), $"空间查询未找到 EntityKey={ship.EntityKey}。");
        }
    }

    [Test]
    public void Checkpoint_ReopenReadsTheActualComponentValues()
    {
        var definition = CreateDefinition(shipCount: 8, maximumCompletedTicks: 3);
        SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());
        var beforeCheckpoint = SpaceBattleHost.ReadSnapshot(definition, _root);

        SpaceBattleHost.ForceCheckpoint(definition, _root);
        var afterCheckpoint = SpaceBattleHost.ReadSnapshot(definition, _root);

        Assert.That(afterCheckpoint.Ships, Is.EqualTo(beforeCheckpoint.Ships).AsCollection);
    }

    private static SimulationDefinition CreateDefinition(
        int shipCount,
        ulong maximumCompletedTicks,
        float worldWidth = 1_000f,
        float worldHeight = 1_000f,
        float spatialCellSize = 100f) =>
        new(
            shipCount: shipCount,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: worldWidth,
            worldHeight: worldHeight,
            worldDepth: 400f,
            maximumHealth: 1_000,
            maximumCompletedTicks: maximumCompletedTicks,
            spatialCellSize: spatialCellSize);

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation)
        {
            Items.Add(observation);
        }
    }
}
