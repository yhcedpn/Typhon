using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class TargetingTests
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
    public void DirectNearestQuery_MatchesBruteForceAndAppliesTargetRules()
    {
        var definition = new SimulationDefinition(
            shipCount: 4,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);

        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var ids = ReadShipIds(engine);
        SetPositions(engine, ids, [
            new(0f, 0f, 0f),
            new(200f, 0f, 0f),
            new(200f, 0f, 0f),
            new(201f, 0f, 0f)]);

        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);
        state.PrepareTick(1);
        var frames = PublishFrames(state, engine, ids);
        var source = frames.Single(frame => frame.EntityKey == ids[0].EntityKey);
        var bruteForceKey = SpaceBattleTargeting.FindNearestBruteForce(frames, source, out var bruteForceDistance);
        var direct = SpaceBattleTargeting.FindNearest(
            state.GetAcquisitionTransaction(workerId: 0, tickNumber: 1),
            state,
            source,
            out var directDistance);

        Assert.Multiple(() =>
        {
            Assert.That(bruteForceKey, Is.EqualTo(ids[1].EntityKey));
            Assert.That(bruteForceDistance, Is.EqualTo(40_000d));
            Assert.That(direct.EntityKey, Is.EqualTo(bruteForceKey));
            Assert.That(directDistance, Is.EqualTo(bruteForceDistance));
            Assert.That(SpaceBattleTargeting.EntityKeyFromRaw(SpaceBattleTargeting.PackRaw(direct)), Is.EqualTo(direct.EntityKey));
        });
    }

    [Test]
    public void TryReadTarget_UsesDeathAndStrictLockRangeBoundary()
    {
        var definition = new SimulationDefinition(
            shipCount: 2,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        using var engine = SpaceBattleDatabase.Open(definition, SpaceBattlePaths.DatabaseDirectory(_root));
        var ids = ReadShipIds(engine);
        SetPositions(engine, ids, [
            new(0f, 0f, 0f),
            new(200f, 0f, 0f)]);

        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);
        state.PrepareTick(1);
        var frames = PublishFrames(state, engine, ids).ToArray();
        var source = frames[0] with
        {
            Targeting = new Targeting { TargetEntityId = SpaceBattleTargeting.PackRaw(ids[1]) },
        };
        state.PublishFrame(ids[0], source);

        Assert.That(SpaceBattleTargeting.TryReadTarget(state, source, out _, out var boundaryDistance), Is.True);
        Assert.That(boundaryDistance, Is.EqualTo(40_000d));

        var deadAtBoundary = frames[1] with { Vitals = new Vitals { CurrentHealth = 0 } };
        state.PublishFrame(ids[1], deadAtBoundary);
        Assert.That(SpaceBattleTargeting.TryReadTarget(state, source, out _, out _), Is.False);

        var aliveOutsideRange = frames[1] with
        {
            Hull = new Hull
            {
                Bounds = new AABB3F
                {
                    MinX = 200.001f,
                    MaxX = 200.001f,
                    MinY = 0f,
                    MaxY = 0f,
                    MinZ = 0f,
                    MaxZ = 0f,
                },
            },
        };
        state.PublishFrame(ids[1], aliveOutsideRange);
        Assert.That(SpaceBattleTargeting.TryReadTarget(state, source, out _, out _), Is.False);
    }

    [Test]
    public void DirectNearestQuery_ExcludesDeadCandidate()
    {
        var definition = new SimulationDefinition(
            shipCount: 3,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);

        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var ids = ReadShipIds(engine);
        SetPositions(engine, ids, [
            new(0f, 0f, 0f),
            new(50f, 0f, 0f),
            new(75f, 0f, 0f)]);
        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);
        state.PrepareTick(1);
        var frames = PublishFrames(state, engine, ids).ToArray();
        frames[1] = frames[1] with { Vitals = new Vitals { CurrentHealth = 0 } };
        state.PublishFrame(ids[1], frames[1]);
        var source = frames[0];

        var direct = SpaceBattleTargeting.FindNearest(
            state.GetAcquisitionTransaction(workerId: 0, tickNumber: 1),
            state,
            source,
            out _);

        Assert.That(direct.EntityKey, Is.EqualTo(ids[2].EntityKey));
    }


    [Test]
    public void TargetingBatch_UsesDirectQueriesForAtMostFourSources()
    {
        var definition = new SimulationDefinition(
            shipCount: 5,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);

        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var ids = ReadShipIds(engine);
        SetPositions(engine, ids, [
            new(0f, 0f, 0f),
            new(50f, 0f, 0f),
            new(100f, 0f, 0f),
            new(150f, 0f, 0f),
            new(500f, 0f, 0f)]);

        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);
        state.PrepareTick(1);
        var frames = PublishFrames(state, engine, ids).ToArray();
        var sources = frames.Take(4).ToArray();
        var results = new TargetingResult[sources.Length];

        SpaceBattleTargeting.FindNearestBatch(
            state.GetAcquisitionTransaction(workerId: 0, tickNumber: 1),
            state,
            sources,
            results,
            out var metrics);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.DirectQueryCount, Is.EqualTo(4));
            Assert.That(metrics.BatchedQueryCount, Is.Zero);
            Assert.That(metrics.GatherCandidateCount, Is.Zero);
            Assert.That(metrics.ExactDistanceTestCount, Is.GreaterThan(0));
            for (var index = 0; index < sources.Length; index++)
            {
                var bruteForceKey = SpaceBattleTargeting.FindNearestBruteForce(
                    frames,
                    sources[index],
                    out var bruteForceDistance);
                Assert.That(results[index].EntityId.EntityKey, Is.EqualTo(bruteForceKey));
                Assert.That(results[index].DistanceSquared, Is.EqualTo(bruteForceDistance));
            }
        });
    }

    [Test]
    public void TargetingBatch_MatchesBruteForceWithExpandedGatherBinsAndRules()
    {
        var definition = new SimulationDefinition(
            shipCount: 8,
            worldWidth: 2_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);

        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var ids = ReadShipIds(engine);
        SetPositions(engine, ids, [
            new(0f, 0f, 0f),
            new(200f, 0f, 0f),
            new(200f, 0f, 0f),
            new(201f, 10f, 10f),
            new(500f, 50f, 100f),
            new(550f, 50f, 100f),
            new(600f, 50f, 100f),
            new(900f, 50f, 100f)]);

        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);
        state.PrepareTick(1);
        var frames = PublishFrames(state, engine, ids).ToArray();
        frames[1] = frames[1] with { Vitals = new Vitals { CurrentHealth = 0 } };
        state.PublishFrame(ids[1], frames[1]);
        var sources = frames.Take(5).ToArray();
        var results = new TargetingResult[sources.Length];

        SpaceBattleTargeting.FindNearestBatch(
            state.GetAcquisitionTransaction(workerId: 0, tickNumber: 1),
            state,
            sources,
            results,
            out var metrics);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.DirectQueryCount, Is.Zero);
            Assert.That(metrics.BatchedQueryCount, Is.EqualTo(1));
            Assert.That(metrics.GatherCandidateCount, Is.GreaterThan(0));
            Assert.That(metrics.GatherCandidateCount, Is.LessThan(frames.Length));
            Assert.That(metrics.ExactDistanceTestCount, Is.GreaterThan(0));
            for (var index = 0; index < sources.Length; index++)
            {
                var bruteForceKey = SpaceBattleTargeting.FindNearestBruteForce(
                    frames,
                    sources[index],
                    out var bruteForceDistance);
                Assert.That(results[index].EntityId.EntityKey, Is.EqualTo(bruteForceKey));
                Assert.That(results[index].DistanceSquared, Is.EqualTo(bruteForceDistance));
            }

            Assert.That(results[0].EntityId.EntityKey, Is.EqualTo(ids[2].EntityKey));
            Assert.That(results[0].DistanceSquared, Is.EqualTo(40_000d));
        });
    }


    [Test]
    public void Host_EntersApproachOrAttackAfterTheFirstLockAttempt()
    {
        var definition = new SimulationDefinition(
            shipCount: 2,
            worldWidth: 100f,
            worldHeight: 100f,
            worldDepth: 100f,
            maximumCompletedTicks: 53);
        SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());
        var snapshot = SpaceBattleHost.ReadSnapshot(definition, _root);

        Assert.That(snapshot.Ships, Has.All.Matches<ShipSnapshot>(ship =>
            (ship.Behavior.Mode is (byte)BehaviorMode.Approaching or (byte)BehaviorMode.Attacking) &&
            ship.Motion.Speed == SpaceBattleTargeting.ApproachSpeed &&
            ship.Targeting.TargetEntityId != 0));

        foreach (var ship in snapshot.Ships)
        {
            var targetKey = SpaceBattleTargeting.EntityKeyFromRaw(ship.Targeting.TargetEntityId);
            var target = snapshot.Ships.Single(candidate => candidate.EntityKey == targetKey);
            Assert.That(SpaceBattleTargeting.DistanceSquared(ship, target), Is.LessThanOrEqualTo(40_000d));
        }
    }

    [Test]
    public void AcquisitionTransaction_IsReusedAcrossOneFenceAndReplacedOnTheSecond()
    {
        var definition = new SimulationDefinition(shipCount: 1, maximumCompletedTicks: 3);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(_root);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 1);

        Assert.That(state.ActiveAcquisitionTransactions, Is.Zero);
        var first = state.GetAcquisitionTransaction(workerId: 0, tickNumber: 1);
        var same = state.GetAcquisitionTransaction(workerId: 0, tickNumber: 2);
        var replacement = state.GetAcquisitionTransaction(workerId: 0, tickNumber: 3);

        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(first, same), Is.True);
            Assert.That(state.AcquisitionTransactionsCreated, Is.EqualTo(2));
            Assert.That(state.AcquisitionTransactionsDisposed, Is.EqualTo(1));
            Assert.That(state.ActiveAcquisitionTransactions, Is.EqualTo(1));
        });

        state.ReleaseAcquisitionTransaction(workerId: 0);
        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.Null);
            Assert.That(state.AcquisitionTransactionsDisposed, Is.EqualTo(2));
            Assert.That(state.ActiveAcquisitionTransactions, Is.Zero);
        });
    }

    private static EntityId[] ReadShipIds(DatabaseEngine engine)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        return transaction.QueryExact<Ship>().Execute()
            .OrderBy(static id => id.EntityKey)
            .ToArray();
    }

    private static void SetPositions(DatabaseEngine engine, IReadOnlyList<EntityId> ids, IReadOnlyList<(float X, float Y, float Z)> positions)
    {
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var index = 0; index < ids.Count; index++)
        {
            var position = positions[index];
            ref var hull = ref transaction.OpenMut(ids[index]).Write(Ship.Hull);
            hull = new Hull
            {
                Bounds = new AABB3F
                {
                    MinX = position.X,
                    MaxX = position.X,
                    MinY = position.Y,
                    MaxY = position.Y,
                    MinZ = position.Z,
                    MaxZ = position.Z,
                },
            };
        }

        Assert.That(transaction.Commit(), Is.True);
        engine.WriteTickFence(1);
    }

    private static IReadOnlyList<ShipSnapshot> PublishFrames(
        SpaceBattleSimulationState state,
        DatabaseEngine engine,
        IReadOnlyList<EntityId> ids)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        var frames = new List<ShipSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            var entity = transaction.Open(id);
            var frame = new ShipSnapshot(
                id.EntityKey,
                entity.Read(Ship.Hull),
                entity.Read(Ship.Motion),
                entity.Read(Ship.Vitals),
                entity.Read(Ship.Targeting),
                entity.Read(Ship.Behavior));
            state.PublishFrame(id, frame);
            frames.Add(frame);
        }

        return frames;
    }

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation)
        {
            Items.Add(observation);
        }
    }
}
