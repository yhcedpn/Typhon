using System.Diagnostics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal static class SpaceBattleHost
{
    private const int ProgressBatchSize = 2_048;
    private const int MaximumWorkerCount = 8;

    public static SpaceBattleRunResult Run(
        SimulationDefinition definition,
        string databaseRoot,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
        => RunCore(definition, databaseRoot, cancellationToken, observationSink, runSimulation: true);

    public static SpaceBattleRunResult BootstrapOnly(
        SimulationDefinition definition,
        string databaseRoot,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
        => RunCore(definition, databaseRoot, cancellationToken, observationSink, runSimulation: false);

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

    public static IReadOnlyList<long> QueryShipKeysInAabb(
        SimulationDefinition definition,
        string databaseRoot,
        AABB3F bounds)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        definition.Validate();

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        using var transaction = engine.CreateReadOnlyTransaction();
        var ids = transaction.QueryExact<Ship>()
            .WhereInAABB<Hull>(
                bounds.MinX,
                bounds.MinY,
                bounds.MinZ,
                bounds.MaxX,
                bounds.MaxY,
                bounds.MaxZ)
            .Execute()
            .Select(static id => id.EntityKey)
            .ToArray();
        Array.Sort(ids);
        return ids;
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

    public static void ForceCheckpoint(
        SimulationDefinition definition,
        string databaseRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        definition.Validate();

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        engine.ForceCheckpoint();
    }

    private static SpaceBattleRunResult RunCore(
        SimulationDefinition definition,
        string databaseRoot,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink,
        bool runSimulation)
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
        observationSink.Publish(new InitializationCompleted(definition.ShipCount, bootstrapDuration));

        if (!runSimulation)
        {
            return new SpaceBattleRunResult(
                databaseDirectory,
                definition.ShipCount,
                bootstrapDuration,
                timing.Snapshot())
            {
                RemainingShips = definition.ShipCount,
            };
        }

        var workerCount = ResolveWorkerCount();
        using var state = new SpaceBattleSimulationState(engine, definition, observationSink, workerCount);
        var runtimeOptions = new RuntimeOptions
        {
            BaseTickRate = SimulationDefinition.FixedTickRate,
            WorkerCount = workerCount,
            EnableParallelFence = true,
            AdaptiveFenceCost = false,
            SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
            Overload = new OverloadOptions
            {
                MinTickRateHz = SimulationDefinition.FixedTickRate,
            },
        };

        using (var runtime = TyphonRuntime.Create(
                   engine,
                   schedule => BuildSchedule(schedule, state, timing),
                   runtimeOptions))
        {
            runtime.Start();
            using var runtimeAborted = new CancellationTokenSource();
            runtime.OnTickAborted += (_, outcome) =>
            {
                runtimeAborted.Cancel();
            };
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeAborted.Token);
            try
            {
                WaitForCompletion(state, definition.MaximumCompletedTicks, linkedCancellation.Token);
            }
            finally
            {
                runtime.Shutdown();
            }
        }

        var publishedSnapshot = state.BuildPublishedSnapshot();
        var tickPerformance = timing.Snapshot();
        var remainingShips = ReadShipCountFromOpenEngine(engine);
        var result = new SpaceBattleRunResult(
            databaseDirectory,
            definition.ShipCount,
            bootstrapDuration,
            tickPerformance)
        {
            CompletedTicks = state.CompletedTicks,
            RemainingShips = remainingShips,
            PublishedSnapshot = publishedSnapshot,
        };
        observationSink.Publish(new SimulationCompleted(
            result.CompletedTicks,
            remainingShips,
            tickPerformance,
            publishedSnapshot));
        return result;
    }

    private static void BuildSchedule(
        RuntimeSchedule schedule,
        SpaceBattleSimulationState state,
        TickTiming timing)
    {
        var dag = schedule.PublicTrack.DeclareDag("SpaceBattle")
            .Phases(
                SpaceBattlePhases.Publish,
                SpaceBattlePhases.Behavior,
                SpaceBattlePhases.Damage,
                SpaceBattlePhases.Movement,
                SpaceBattlePhases.Reap,
                SpaceBattlePhases.Observe);
        dag.Add(new FramePrepareSystem(state));
        dag.Add(new PublishSystem(state));
        dag.Add(new BehaviorSystem(state));
        dag.Add(new DamageSystem(state));
        dag.Add(new DamageCleanupSystem(state));
        dag.Add(new MovementSystem(state));
        dag.Add(new ReapSystem(state));
        dag.Add(new AcquisitionCleanupSystem(state));
        dag.Add(new ObserveSystem(state, timing));
    }

    private static void WaitForCompletion(
        SpaceBattleSimulationState state,
        ulong maximumTicks,
        CancellationToken cancellationToken)
    {
        while ((ulong)state.CompletedTicks < maximumTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(1);
        }
    }
    private static int ResolveWorkerCount() =>
        Math.Max(1, Math.Min(MaximumWorkerCount, Environment.ProcessorCount - 4));

    private static int ReadShipCountFromOpenEngine(DatabaseEngine engine)
    {
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
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
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
        }

        // 首个 Publish 直接读取 cluster 快照，先用一次 fence 将批量 spawn 的列同步到 cluster。
        engine.WriteTickFence(0);

        observationSink.Publish(new InitializationProgress(shipCount, shipCount));
    }
}
