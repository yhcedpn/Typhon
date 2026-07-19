using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

public static class SpaceBattleHost
{
    private const ulong InitialPositionX = 1;
    private const ulong InitialPositionY = 2;
    private const ulong InitialPositionZ = 3;

    public static SpaceBattleRunResult Run(
        SimulationDefinition definition,
        string databaseLocation,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        ArgumentNullException.ThrowIfNull(observationSink);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        using var engine = OpenEngine(definition, databaseLocation);
        using var bulkLoad = engine.BeginBulkLoad(new BulkLoadOptions
        {
            ProgressBatchSize = Math.Max(1, Math.Min(10_000, definition.ShipCount)),
            ProgressReporter = progress => observationSink.Publish(new InitializationProgress(
                checked((int)Math.Max(0, progress.EntitiesSpawned - 1)),
                definition.ShipCount)),
        });

        var run = new SimulationRunComponent
        {
            Seed = definition.Seed,
            CompletedTicks = 0,
            RulesetVersion = definition.RulesetVersion,
            InitialShipCount = checked((uint)definition.ShipCount),
            AliveShipCount = checked((uint)definition.ShipCount),
            ProcessSegment = 1,
            Status = (byte)SimulationRunStatus.Running,
        };
        bulkLoad.Spawn<SimulationRunEntity>(SimulationRunEntity.Run.Set(in run));

        var motion = new MotionComponent { DirectionX = 1f, DirectionY = 0f, DirectionZ = 0f, Speed = 0f };
        var health = new HealthComponent { Current = definition.MaximumHealth };
        var behavior = new BehaviorComponent
        {
            DecisionOrdinal = 0,
            ModeTicksRemaining = definition.StagingTicks,
            Mode = (byte)BehaviorMode.Staging,
        };
        var tracking = new TrackingComponent
        {
            Target = EntityLink<Ship>.Null,
            TrackingTicksRemaining = 0,
        };

        for (var index = 0; index < definition.ShipCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var position = default(PositionComponent);
            var bounds = default(SpatialBoundsComponent);

            var entityId = bulkLoad.Spawn<Ship>(
                Ship.Position.Set(in position),
                Ship.SpatialBounds.Set(in bounds),
                Ship.Motion.Set(in motion),
                Ship.Health.Set(in health),
                Ship.Behavior.Set(in behavior),
                Ship.Tracking.Set(in tracking));

            var packedEntityId = ((ulong)entityId.EntityKey << 12) | entityId.ArchetypeId;
            position = CreateInitialPosition(definition, packedEntityId);
            bounds = CreatePointBounds(position);
            bulkLoad.Update(entityId, in position);
            bulkLoad.Update(entityId, in bounds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bulkLoad.CompleteBulkLoad();
        engine.WriteTickFence(0);
        stopwatch.Stop();

        var observation = new InitializationCompleted(definition.ShipCount, stopwatch.Elapsed);
        observationSink.Publish(observation);
        return new SpaceBattleRunResult(definition.ShipCount, stopwatch.Elapsed);
    }

    public static InitialWorldSnapshot ReadSnapshot(
        SimulationDefinition definition,
        string databaseLocation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);

        using var engine = OpenEngine(definition, databaseLocation);
        using var transaction = engine.CreateReadOnlyTransaction();

        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count != 1)
        {
            throw new InvalidOperationException($"SimulationRun 实体数量必须为 1，实际为 {runEntities.Count}。");
        }

        var runEntity = transaction.Open(runEntities.Single());
        ref readonly var run = ref runEntity.Read(SimulationRunEntity.Run);
        var runSnapshot = new SimulationRunSnapshot(
            run.Seed,
            run.CompletedTicks,
            run.RulesetVersion,
            run.InitialShipCount,
            run.AliveShipCount,
            run.ProcessSegment,
            (SimulationRunStatus)run.Status);

        var shipEntities = transaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey);
        var ships = new List<ShipSnapshot>();
        foreach (var entityId in shipEntities)
        {
            var entity = transaction.Open(entityId);
            ref readonly var position = ref entity.Read(Ship.Position);
            ref readonly var bounds = ref entity.Read(Ship.SpatialBounds);
            ref readonly var motion = ref entity.Read(Ship.Motion);
            ref readonly var health = ref entity.Read(Ship.Health);
            ref readonly var behavior = ref entity.Read(Ship.Behavior);
            ref readonly var tracking = ref entity.Read(Ship.Tracking);

            ships.Add(new ShipSnapshot(
                entityId.EntityKey,
                new PositionSnapshot(position.X, position.Y, position.Z),
                new SpatialBoundsSnapshot(
                    bounds.Bounds.MinX,
                    bounds.Bounds.MinY,
                    bounds.Bounds.MinZ,
                    bounds.Bounds.MaxX,
                    bounds.Bounds.MaxY,
                    bounds.Bounds.MaxZ),
                new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed),
                health.Current,
                (BehaviorMode)behavior.Mode,
                behavior.ModeTicksRemaining,
                tracking.Target.IsNull,
                entity.IsEnabled(Ship.Weapon),
                entity.IsEnabled(Ship.Afterburner)));
        }

        return new InitialWorldSnapshot(runEntities.Count, runSnapshot, ships);
    }

    private static DatabaseEngine OpenEngine(SimulationDefinition definition, string databaseLocation) =>
        DatabaseEngine.Open(databaseLocation, options => options
            .Register<SimulationRunComponent>()
            .Register<PositionComponent>()
            .Register<SpatialBoundsComponent>()
            .Register<MotionComponent>()
            .Register<HealthComponent>()
            .Register<BehaviorComponent>()
            .Register<TrackingComponent>()
            .Register<WeaponComponent>()
            .Register<AfterburnerComponent>()
            .RegisterArchetype<SimulationRunEntity>()
            .RegisterArchetype<Ship>()
            .ConfigureSpatialGrid(new SpatialGridConfig(
                Vector2.Zero,
                new Vector2(definition.WorldSize, definition.WorldSize),
                definition.SpatialCellSize)));

    private static PositionComponent CreateInitialPosition(
        SimulationDefinition definition,
        ulong packedEntityId) => new()
        {
            X = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionX,
                definition.WorldSize),
            Y = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionY,
                definition.WorldSize),
            Z = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionZ,
                definition.WorldSize),
        };

    private static SpatialBoundsComponent CreatePointBounds(PositionComponent position) => new()
    {
        Bounds = new AABB3F
        {
            MinX = position.X,
            MinY = position.Y,
            MinZ = position.Z,
            MaxX = position.X,
            MaxY = position.Y,
            MaxZ = position.Z,
        },
    };
}