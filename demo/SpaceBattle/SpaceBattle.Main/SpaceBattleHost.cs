using System.Diagnostics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal static class SpaceBattleHost
{
    private const int ProgressBatchSize = 2_048;

    public static SpaceBattleRunResult Run(
        SimulationDefinition definition,
        string databaseRoot,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        ArgumentNullException.ThrowIfNull(observationSink);
        definition.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        SpaceBattlePaths.ReplaceDatabaseDirectory(databaseRoot);
        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var timing = new TickTiming();
        var bootstrapStartedAt = Stopwatch.GetTimestamp();

        Bootstrap(engine, definition, cancellationToken, observationSink);

        var bootstrapDuration = Stopwatch.GetElapsedTime(bootstrapStartedAt);
        timing.RecordBootstrap(bootstrapDuration);
        var result = new SpaceBattleRunResult(
            databaseDirectory,
            definition.ShipCount,
            bootstrapDuration,
            timing.Snapshot());
        observationSink.Publish(new InitializationCompleted(definition.ShipCount, bootstrapDuration));
        return result;
    }

    public static SpaceBattleSnapshot ReadSnapshot(
        SimulationDefinition definition,
        string databaseRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        definition.Validate();

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        using var transaction = engine.CreateReadOnlyTransaction();
        var ids = transaction.QueryExact<Ship>().Execute().ToList();
        ids.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));

        var ships = new List<ShipSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            var entity = transaction.Open(id);
            ships.Add(new ShipSnapshot(
                id.EntityKey,
                entity.Read(Ship.Hull),
                entity.Read(Ship.Motion),
                entity.Read(Ship.Vitals),
                entity.Read(Ship.Targeting),
                entity.Read(Ship.Behavior)));
        }

        return new SpaceBattleSnapshot(ships);
    }

    public static int ReadShipCount(
        SimulationDefinition definition,
        string databaseRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        definition.Validate();

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        using var transaction = engine.CreateReadOnlyTransaction();
        return transaction.QueryExact<Ship>().Count();
    }

    private static void Bootstrap(
        DatabaseEngine engine,
        SimulationDefinition definition,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        var shipCount = definition.ShipCount;
        var ids = new EntityId[shipCount];
        var hulls = new Hull[shipCount];
        var motions = new Motion[shipCount];
        var vitals = new Vitals[shipCount];
        var targetings = new Targeting[shipCount];
        var behaviors = new Behavior[shipCount];
        var random = new SplitMix64(definition.Seed);

        observationSink.Publish(new InitializationProgress(0, shipCount));
        for (var index = 0; index < shipCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var x = random.NextCoordinate(definition.WorldWidth);
            var y = random.NextCoordinate(definition.WorldHeight);
            var z = random.NextCoordinate(definition.WorldDepth);
            hulls[index] = new Hull
            {
                Bounds = new AABB3F
                {
                    MinX = x,
                    MinY = y,
                    MinZ = z,
                    MaxX = x,
                    MaxY = y,
                    MaxZ = z,
                },
            };
            motions[index] = default;
            vitals[index] = new Vitals { CurrentHealth = definition.MaximumHealth };
            targetings[index] = default;
            behaviors[index] = new Behavior
            {
                Mode = (byte)BehaviorMode.Wandering,
                Phase = (byte)BehaviorPhase.Ready,
            };

            if ((index + 1) % ProgressBatchSize == 0)
            {
                observationSink.Publish(new InitializationProgress(index + 1, shipCount));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        transaction.SpawnBatchAllocate<Ship>(shipCount, ids);
        transaction.SpawnBatchWriteAll(0, shipCount, Ship.Hull, hulls);
        transaction.SpawnBatchWriteAll(0, shipCount, Ship.Motion, motions);
        transaction.SpawnBatchWriteAll(0, shipCount, Ship.Vitals, vitals);
        transaction.SpawnBatchWriteAll(0, shipCount, Ship.Targeting, targetings);
        transaction.SpawnBatchWriteAll(0, shipCount, Ship.Behavior, behaviors);
        if (!transaction.Commit())
        {
            throw new InvalidOperationException("SpaceBattle bootstrap 事务提交失败。");
        }

        observationSink.Publish(new InitializationProgress(shipCount, shipCount));
    }
}
