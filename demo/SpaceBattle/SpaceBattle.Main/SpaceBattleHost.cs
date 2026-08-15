using System.Diagnostics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal static class SpaceBattleHost
{
    private const int ProgressBatchSize = 2_048;
    private const int TickOutcomeLivenessWaitMilliseconds = 10;

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

    public static SpaceBattleDeterminismDiagnostic RunDeterminismDiagnostic(
        SimulationDefinition definition,
        string databaseRoot,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.WorkerCount <= 0)
        {
            throw new ArgumentException("复现诊断必须显式指定正数 worker 数。", nameof(definition));
        }

        var result = Run(definition, databaseRoot, cancellationToken, observationSink);
        var snapshot = ReadSnapshot(definition, databaseRoot);
        return SpaceBattleDeterminismDiagnostic.Capture(definition, result, snapshot);
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
        return ReadSnapshotFromOpenEngine(engine);

    }

    private static SpaceBattleSnapshot ReadSnapshotFromOpenEngine(DatabaseEngine engine)
    {
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

        var workerCount = ResolveWorkerCount(definition);
        using var completionSignal = new AutoResetEvent(false);
        var finalTickObservationSink = new FinalTickObservationSink(
            observationSink,
            definition.MaximumCompletedTicks);
        using var state = new SpaceBattleSimulationState(
            engine,
            definition,
            finalTickObservationSink,
            workerCount,
            tickCompleted: () => completionSignal.Set(),
            enforceMaximumCompletedTicks: definition.WorkerCount > 0);
        var runtimeOptions = new RuntimeOptions
        {
            BaseTickRate = definition.TickRate,
            WorkerCount = workerCount,
            EnableParallelFence = true,
            AdaptiveFenceCost = false,
            SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
            Overload = new OverloadOptions
            {
                MinTickRateHz = definition.TickRate,
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
                   schedule => BuildSchedule(schedule, state, timing, definition.WorkerCount > 0),
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
                completionSignal.Set();
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
                completionSignal.Set();
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
                    () => Volatile.Read(ref cancellationRequestedAtRuntimeTick),
                    completionSignal);
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
                        ReadFatalOutcome,
                        completionSignal);
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

            ReapDeadShips(
                engine,
                state.CompletedTicks,
                definition.ShipCount - state.LastCompletedRemainingShips);
            var committedSnapshot = ReadSnapshotFromOpenEngine(engine);
            var finalSnapshot = new SpaceBattleSnapshot(
                committedSnapshot.Ships
                    .Where(static ship => ship.Vitals.CurrentHealth > 0)
                    .ToArray());
            // Observe 在 tick 事务 flush 前执行；最终结果必须读取 flush 后的数据库组件，而不是旧镜像。
            finalTickObservationSink.PublishCommittedSnapshot(finalSnapshot);
            // 持久化只读口径不等价于内存最终状态（FenceWal 恢复存在 #569 已知缺口）：terminate 后读盘可能得到
            // 过时/缺失的存活集（曾观测 remaining=0 而最后 tick telemetry 有 32,029 存活）。战果判定必须与
            // WaitForCompletion 同源——使用内存最终存活数；finalSnapshot 仅供观察者展示。
            var remainingShips = state.LastCompletedRemainingShips;
            state.AcquisitionTransactions.ReleaseAllAfterRuntimeStop();
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
                PublishedSnapshot = finalSnapshot,
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
        TickTiming timing,
        bool resetAcquisitionTransactions)
    {
        var dag = schedule.PublicTrack.DeclareDag("SpaceBattle")
            .Phases(
                SpaceBattlePhases.Publish,
                SpaceBattlePhases.Behavior,
                SpaceBattlePhases.Damage,
                SpaceBattlePhases.Movement,
                SpaceBattlePhases.Reap,
                SpaceBattlePhases.Observe);
        if (resetAcquisitionTransactions)
        {
            dag.Add(new DeterminismAcquisitionResetSystem(state));
        }

        dag.Add(new FramePrepareSystem(state));
        dag.Add(new PublishSystem(state));
        dag.Add(new BehaviorSystem(state));
        dag.Add(new DamageSystem(state));
        dag.Add(new DamageCleanupSystem(state));
        dag.Add(new MovementSystem(state));
        dag.Add(new ReapSystem(state));
        dag.Add(new ObserveSystem(state, timing));
    }

    private static SpaceBattleTerminationReason WaitForCompletion(
        SpaceBattleSimulationState state,
        TyphonRuntime runtime,
        ulong maximumTicks,
        CancellationToken cancellationToken,
        Func<TickOutcome?> readFatalOutcome,
        Func<long> readCancellationCompletedTicks,
        Func<long> readCancellationRuntimeTick,
        AutoResetEvent completionSignal)
    {
        while (true)
        {
            if (readFatalOutcome().HasValue)
            {
                return SpaceBattleTerminationReason.Fatal;
            }

            var completedTicks = state.CompletedTicks;
            var boundaryTermination = ResolveBoundaryTermination(
                completedTicks,
                state.LastCompletedRemainingShips,
                state.ShipCount,
                maximumTicks);
            if (boundaryTermination.HasValue)
            {
                return boundaryTermination.Value;
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
                    readFatalOutcome,
                    completionSignal);
                return readFatalOutcome().HasValue
                    ? SpaceBattleTerminationReason.Fatal
                    : SpaceBattleTerminationReason.Cancelled;
            }
            completionSignal.WaitOne();
        }
    }

    private static void WaitForInFlightTick(
        SpaceBattleSimulationState state,
        TyphonRuntime runtime,
        long completedTicksAtRequest,
        long runtimeTickAtRequest,
        Func<TickOutcome?> readFatalOutcome,
        AutoResetEvent completionSignal)
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

            // 正常路径由逻辑帧完成信号唤醒；超时只防止 LastTickOutcome 在 Observe 后发布时丢失唤醒。
            completionSignal.WaitOne(TickOutcomeLivenessWaitMilliseconds);
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

    private static int ResolveWorkerCount(SimulationDefinition definition) =>
        definition.WorkerCount == SimulationDefinition.AutomaticWorkerCount
            ? Math.Max(1, Math.Min(SimulationDefinition.MaximumWorkerCount, Environment.ProcessorCount - 4))
            : definition.WorkerCount;

    private static int ReadShipCountFromOpenEngine(DatabaseEngine engine)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        return transaction.QueryExact<Ship>().Count();
    }

    // runtime 可能在 Observe 后停止，补做一次幂等回收，避免零血实体留在最终数据库快照中。
    // expectedDeaths 来自内存口径（ShipCount - 最后 tick 存活数）；只读事务读到的"零血"候选若远超
    // 预期（持久化只读口径落后于内存，见 #569 缺口），放弃清理——销毁会误杀活船。
    private static void ReapDeadShips(DatabaseEngine engine, long tickNumber, int expectedDeaths)
    {
        using var readTransaction = engine.CreateReadOnlyTransaction();
        var deadIds = new List<EntityId>();
        foreach (var entityId in readTransaction.QueryExact<Ship>().Execute())
        {
            if (readTransaction.Open(entityId).Read(Ship.Vitals).CurrentHealth == 0)
            {
                deadIds.Add(entityId);
            }
        }

        if (deadIds.Count == 0)
        {
            return;
        }

        if (deadIds.Count > expectedDeaths)
        {
            Console.Error.WriteLine(
                $"warning=reap_skipped dead_candidates={deadIds.Count} expected_deaths={expectedDeaths} " +
                "readonly_snapshot_lags_memory; 跳过终态回收以避免误杀活船。");
            return;
        }

        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        transaction.DestroyBatch(deadIds.ToArray().AsSpan());
        if (!transaction.Commit())
        {
            throw new InvalidOperationException("SpaceBattle 终态死船回收事务提交失败。");
        }

        engine.WriteTickFence(tickNumber);
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
            vitals[index] = new Vitals { CurrentHealth = definition.MaximumHealth };
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
