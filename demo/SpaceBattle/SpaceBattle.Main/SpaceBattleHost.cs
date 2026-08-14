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

        var databaseDirectory = SpaceBattlePaths.DatabaseDirectory(databaseRoot);
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CreateCancelledResult(databaseDirectory, definition);
            if (runSimulation)
            {
                PublishSimulationCompleted(observationSink, cancelled);
            }

            return cancelled;
        }

        SpaceBattlePaths.ReplaceDatabaseDirectory(databaseRoot);
        using var engine = SpaceBattleDatabase.Open(definition, databaseDirectory);
        var timing = new TickTiming();
        var bootstrapStartedAt = Stopwatch.GetTimestamp();

        try
        {
            Bootstrap(engine, definition, cancellationToken, observationSink);
        }
        catch (OperationCanceledException)
        {
            var bootstrapDuration = Stopwatch.GetElapsedTime(bootstrapStartedAt);
            timing.RecordBootstrap(bootstrapDuration);
            var cancelled = new SpaceBattleRunResult(
                databaseDirectory,
                definition.ShipCount,
                bootstrapDuration,
                timing.Snapshot())
            {
                TerminationReason = SpaceBattleTerminationReason.Cancelled,
                RemainingShips = ReadShipCountFromOpenEngine(engine),
            };
            if (runSimulation)
            {
                PublishSimulationCompleted(observationSink, cancelled);
            }

            return cancelled;
        }

        var bootstrapDurationCompleted = Stopwatch.GetElapsedTime(bootstrapStartedAt);
        timing.RecordBootstrap(bootstrapDurationCompleted);
        observationSink.Publish(new InitializationCompleted(definition.ShipCount, bootstrapDurationCompleted));

        if (!runSimulation)
        {
            return new SpaceBattleRunResult(
                databaseDirectory,
                definition.ShipCount,
                bootstrapDurationCompleted,
                timing.Snapshot())
            {
                TerminationReason = SpaceBattleTerminationReason.BootstrapOnly,
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

        TickOutcome? fatalOutcome = null;
        var fatalGate = new object();
        var runtimeStopped = 0;
        long cancellationRequestedAtCompletedTicks = -1;
        long cancellationRequestedAtRuntimeTick = -1;
        TickOutcome? ReadFatalOutcome()
        {
            lock (fatalGate)
            {
                return fatalOutcome;
            }
        }
        using (var runtime = TyphonRuntime.Create(
                   engine,
                   schedule => BuildSchedule(schedule, state, timing),
                   runtimeOptions))
        {
            runtime.OnTickAborted += (abortedRuntime, outcome) =>
            {
                if (Interlocked.Exchange(ref runtimeStopped, 1) == 0)
                {
                    try
                    {
                        abortedRuntime.FatalStop();
                    }
                    catch (Exception)
                    {
                        // fatal stop 本身失败时仍保留原始 system exception 供宿主返回。
                    }
                }

                lock (fatalGate)
                {
                    fatalOutcome = outcome;
                }
            };
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                Interlocked.CompareExchange(
                    ref cancellationRequestedAtCompletedTicks,
                    state.CompletedTicks,
                    -1);
                Interlocked.CompareExchange(
                    ref cancellationRequestedAtRuntimeTick,
                    runtime.CurrentTickNumber,
                    -1);
            });


            runtime.Start();
            SpaceBattleTerminationReason requestedTermination;
            try
            {
                requestedTermination = WaitForCompletion(
                    state,
                    runtime,
                    definition.MaximumCompletedTicks,
                    cancellationToken,
                    ReadFatalOutcome,
                    () => Volatile.Read(ref cancellationRequestedAtCompletedTicks),
                    () => Volatile.Read(ref cancellationRequestedAtRuntimeTick));
            }
            finally
            {
                var observedFatal = ReadFatalOutcome();
                if (!observedFatal.HasValue)
                {
                    WaitForInFlightTick(
                        state,
                        runtime,
                        state.CompletedTicks,
                        runtime.CurrentTickNumber,
                        ReadFatalOutcome);
                    observedFatal = ReadFatalOutcome();
                }

                if (observedFatal.HasValue)
                {
                    if (Interlocked.Exchange(ref runtimeStopped, 1) == 0)
                    {
                        runtime.FatalStop();
                    }
                }
                else if (Interlocked.Exchange(ref runtimeStopped, 1) == 0)
                {
                    runtime.Shutdown();
                }
            }

            TickOutcome? finalFatalOutcome;
            lock (fatalGate)
            {
                finalFatalOutcome = fatalOutcome;
            }

            var remainingShips = ReadShipCountFromOpenEngine(engine);
            state.ReleaseAllAcquisitionTransactions();
            var termination = ResolveTerminationReason(
                requestedTermination,
                state.CompletedTicks,
                remainingShips,
                definition.MaximumCompletedTicks,
                definition.ShipCount,
                finalFatalOutcome);
            var result = new SpaceBattleRunResult(
                databaseDirectory,
                definition.ShipCount,
                bootstrapDurationCompleted,
                timing.Snapshot())
            {
                CompletedTicks = state.CompletedTicks,
                RemainingShips = remainingShips,
                PublishedSnapshot = state.BuildPublishedSnapshot(),
                TerminationReason = termination,
                FatalOutcome = finalFatalOutcome,
                FailedSystemName = finalFatalOutcome?.FailedSystemName,
                FatalException = finalFatalOutcome?.FailedSystemException,
            };
            PublishSimulationCompleted(observationSink, result);
            return result;
        }
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

    private static SpaceBattleTerminationReason WaitForCompletion(
        SpaceBattleSimulationState state,
        TyphonRuntime runtime,
        ulong maximumTicks,
        CancellationToken cancellationToken,
        Func<TickOutcome?> readFatalOutcome,
        Func<long> readCancellationCompletedTicks,
        Func<long> readCancellationRuntimeTick)
    {
        var observedCompletedTicks = state.CompletedTicks;
        while (true)
        {
            if (readFatalOutcome().HasValue)
            {
                return SpaceBattleTerminationReason.Fatal;
            }

            var completedTicks = state.CompletedTicks;
            if (completedTicks != observedCompletedTicks)
            {
                observedCompletedTicks = completedTicks;
                var boundaryTermination = ResolveBoundaryTermination(
                    completedTicks,
                    state.LastCompletedRemainingShips,
                    state.ShipCount,
                    maximumTicks);
                if (boundaryTermination.HasValue)
                {
                    return boundaryTermination.Value;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                var completedTicksAtRequest = readCancellationCompletedTicks();
                var runtimeTickAtRequest = readCancellationRuntimeTick();
                WaitForInFlightTick(
                    state,
                    runtime,
                    completedTicksAtRequest >= 0 ? completedTicksAtRequest : completedTicks,
                    runtimeTickAtRequest >= 0 ? runtimeTickAtRequest : runtime.CurrentTickNumber,
                    readFatalOutcome);
                return readFatalOutcome().HasValue
                    ? SpaceBattleTerminationReason.Fatal
                    : SpaceBattleTerminationReason.Cancelled;
            }
            Thread.Sleep(1);
        }
    }

    private static void WaitForInFlightTick(
        SpaceBattleSimulationState state,
        TyphonRuntime runtime,
        long completedTicksAtRequest,
        long runtimeTickAtRequest,
        Func<TickOutcome?> readFatalOutcome)
    {
        var targetCompletedTicks = completedTicksAtRequest;
        if (state.IsTickInFlight && runtimeTickAtRequest >= completedTicksAtRequest)
        {
            targetCompletedTicks = runtimeTickAtRequest + 1;
        }

        while (true)
        {
            if (readFatalOutcome().HasValue)
            {
                return;
            }

            var stateCompletedTicks = state.CompletedTicks;
            var targetOutcomeTick = targetCompletedTicks - 1;
            if (stateCompletedTicks >= targetCompletedTicks &&
                (targetOutcomeTick < 0 || runtime.LastTickOutcome.TickNumber >= targetOutcomeTick))
            {
                return;
            }

            if (targetCompletedTicks == 0)
            {
                return;
            }

            Thread.Sleep(1);
        }
    }

    private static SpaceBattleTerminationReason ResolveTerminationReason(
        SpaceBattleTerminationReason requestedTermination,
        long completedTicks,
        int remainingShips,
        ulong maximumTicks,
        int initialShipCount,
        TickOutcome? fatalOutcome)
    {
        if (fatalOutcome.HasValue)
        {
            return SpaceBattleTerminationReason.Fatal;
        }

        var battleTermination = ResolveBoundaryTermination(
            completedTicks,
            remainingShips,
            initialShipCount,
            maximumTicks);
        if (battleTermination.HasValue)
        {
            return battleTermination.Value;
        }

        return requestedTermination;

    }
    private static SpaceBattleTerminationReason? ResolveBoundaryTermination(
        long completedTicks,
        int remainingShips,
        int initialShipCount,
        ulong maximumTicks)
    {
        if (completedTicks <= 0)
        {
            return null;
        }

        if (remainingShips == 0)
        {
            return SpaceBattleTerminationReason.Draw;
        }

        if (remainingShips == 1 && initialShipCount > 1)
        {
            return SpaceBattleTerminationReason.Winner;
        }

        return (ulong)completedTicks >= maximumTicks
            ? SpaceBattleTerminationReason.TickLimit
            : null;
    }

    private static SpaceBattleRunResult CreateCancelledResult(
        string databaseDirectory,
        SimulationDefinition definition) =>
        new(
            databaseDirectory,
            definition.ShipCount,
            TimeSpan.Zero,
            new TickPerformanceSnapshot(0, 0, 0, 0, 0))
        {
            TerminationReason = SpaceBattleTerminationReason.Cancelled,
            PublishedSnapshot = new SpaceBattleSnapshot([]),
        };

    private static void PublishSimulationCompleted(
        ISpaceBattleObservationSink observationSink,
        SpaceBattleRunResult result)
    {
        observationSink.Publish(new SimulationCompleted(
            result.CompletedTicks,
            result.RemainingShips,
            result.TickPerformance,
            result.PublishedSnapshot)
        {
            TerminationReason = result.TerminationReason,
            FailedSystemName = result.FailedSystemName,
            FatalException = result.FatalException,
            FatalOutcome = result.FatalOutcome,
        });
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
