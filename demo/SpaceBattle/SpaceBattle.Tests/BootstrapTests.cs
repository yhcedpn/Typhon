using System.Reflection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class BootstrapTests
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
    public void DefaultDefinition_RequestsExactlyFiftyThousandShips()
    {
        Assert.That(SimulationDefinition.Default.ShipCount, Is.EqualTo(50_000));
    }


    [Test]
    public void Ship_ContainsExactlyFiveSingleVersionComponents()
    {
        var componentFields = typeof(Ship)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static field => field.FieldType.IsGenericType &&
                                   field.FieldType.GetGenericTypeDefinition() == typeof(Typhon.Engine.Comp<>))
            .ToArray();

        Assert.That(componentFields.Select(static field => field.Name),
            Is.EqualTo(new[] { "Hull", "Motion", "Vitals", "Targeting", "Behavior" }));
        Assert.Multiple(() =>
        {
            foreach (var field in componentFields)
            {
                var componentType = field.FieldType.GetGenericArguments()[0];
                var attribute = componentType.GetCustomAttribute<ComponentAttribute>();
                Assert.That(attribute, Is.Not.Null, componentType.Name);
                Assert.That(attribute!.StorageMode, Is.EqualTo(StorageMode.SingleVersion), componentType.Name);
            }
        });
    }

    [Test]
    public void Bootstrap_CreatesTheConfiguredShipsInTheExpectedInitialState()
    {
        var definition = CreateDefinition(shipCount: 32);
        var result = SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var snapshot = SpaceBattleHost.ReadSnapshot(definition, _root);

        Assert.Multiple(() =>
        {
            Assert.That(result.ShipCount, Is.EqualTo(32));
            Assert.That(snapshot.Ships, Has.Count.EqualTo(32));
            Assert.That(result.BootstrapDuration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(result.TickPerformance.SampleCount, Is.Zero);
        });

        foreach (var ship in snapshot.Ships)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ship.Hull.Bounds.MinX, Is.InRange(0f, definition.WorldWidth));
                Assert.That(ship.Hull.Bounds.MinX, Is.LessThan(definition.WorldWidth));
                Assert.That(ship.Hull.Bounds.MinY, Is.InRange(0f, definition.WorldHeight));
                Assert.That(ship.Hull.Bounds.MinY, Is.LessThan(definition.WorldHeight));
                Assert.That(ship.Hull.Bounds.MinZ, Is.InRange(0f, definition.WorldDepth));
                Assert.That(ship.Hull.Bounds.MinZ, Is.LessThan(definition.WorldDepth));
                Assert.That(ship.Hull.Bounds.MaxX, Is.EqualTo(ship.Hull.Bounds.MinX));
                Assert.That(ship.Hull.Bounds.MaxY, Is.EqualTo(ship.Hull.Bounds.MinY));
                Assert.That(ship.Hull.Bounds.MaxZ, Is.EqualTo(ship.Hull.Bounds.MinZ));
                Assert.That(ship.Motion.Speed, Is.Zero);
                Assert.That(ship.Motion.RemainingTurnRadians, Is.Zero);
                Assert.That(ship.Vitals.CurrentHealth, Is.EqualTo(definition.MaximumHealth));
                Assert.That(ship.Targeting.TargetRawEntityId, Is.Zero);
                Assert.That(ship.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
                Assert.That(ship.Behavior.TicksRemaining, Is.Zero);
            });
        }
    }

    [Test]
    public void Bootstrap_WithTheSameSeed_ReproducesTheShipSnapshot()
    {
        var definition = CreateDefinition(shipCount: 48, seed: 0x1234_5678_9ABC_DEF0UL);
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");

        SpaceBattleHost.BootstrapOnly(definition, firstRoot, CancellationToken.None, new RecordingSink());
        SpaceBattleHost.BootstrapOnly(definition, secondRoot, CancellationToken.None, new RecordingSink());

        var first = SpaceBattleHost.ReadSnapshot(definition, firstRoot);
        var second = SpaceBattleHost.ReadSnapshot(definition, secondRoot);

        Assert.That(second.Ships, Is.EqualTo(first.Ships).AsCollection);
    }

    [Test]
    public void Bootstrap_ReplacesOnlyItsOwnDatabaseDirectory_AndStopKeepsTheNewDatabase()
    {
        var definition = CreateDefinition(shipCount: 12, seed: 1);
        var adjacentDirectory = Path.Combine(_root, "adjacent-data");
        Directory.CreateDirectory(adjacentDirectory);
        var adjacentFile = Path.Combine(adjacentDirectory, "keep.txt");
        File.WriteAllText(adjacentFile, "keep");

        var first = SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var marker = Path.Combine(first.DatabaseDirectory, "must-be-replaced");
        File.WriteAllText(marker, "old");

        var secondDefinition = definition with { Seed = 2 };
        var second = SpaceBattleHost.BootstrapOnly(secondDefinition, _root, CancellationToken.None, new RecordingSink());

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(adjacentFile), Is.True);
            Assert.That(File.Exists(marker), Is.False);
            Assert.That(Directory.Exists(second.DatabaseDirectory), Is.True);
            Assert.That(second.DatabaseDirectory, Is.EqualTo(first.DatabaseDirectory));
        });
    }

    [Test]
    public void TickTiming_KeepsBootstrapOutsideTickPercentiles()
    {
        var timing = new TickTiming();
        timing.RecordBootstrap(TimeSpan.FromSeconds(10));
        timing.RecordTick(TimeSpan.FromMilliseconds(2));
        timing.RecordTick(TimeSpan.FromMilliseconds(4));
        timing.RecordTick(TimeSpan.FromMilliseconds(8));

        var snapshot = timing.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(timing.BootstrapDuration, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(snapshot.SampleCount, Is.EqualTo(3));
            Assert.That(snapshot.P50Milliseconds, Is.EqualTo(4d));
            Assert.That(snapshot.P95Milliseconds, Is.LessThan(10d));
            Assert.That(snapshot.MaximumMilliseconds, Is.EqualTo(8d));
        });
    }

    private static SimulationDefinition CreateDefinition(int shipCount, ulong seed = SimulationDefinition.DefaultSeed) =>
        new(
            shipCount: shipCount,
            seed: seed,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumHealth: 1_000);

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation)
        {
            Items.Add(observation);
        }
    }
}
