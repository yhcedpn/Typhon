using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal sealed class SpaceBattleSimulationState : IDisposable
{
    private readonly object _tickGate = new();
    private readonly int[] _frameGenerations;
    private readonly int[] _modifiedGenerations;
    private readonly int[] _reapGenerations;
    private readonly EntityId[] _entityIds;
    private readonly ShipSnapshot[] _telemetryFrames;
    private readonly ShipSnapshot[] _frames;
    private readonly uint[][] _incomingDamageLanes;
    private readonly long[][] _incomingDamageTouchedKeys;
    private readonly int[][] _incomingDamageTouchedGenerations;
    private readonly int[] _incomingDamageTouchedCounts;
    private readonly EntityId[][] _reapBuffers;
    private readonly int[] _reapCounts;
    private readonly long[] _directTargetingQueries;
    private readonly long[] _batchedTargetingQueries;
    private readonly long[] _gatherTargetingCandidates;
    private readonly long[] _exactTargetingDistanceTests;
    private readonly SpaceBattleSystemMetricAccumulator[] _systemMetrics;
    private readonly object _telemetryGate = new();
    private TyphonRuntime _runtime;
    private SpaceBattleTelemetrySnapshot _lastTelemetry;
    private long _dirtyMarkTicks;
    private long _dirtyMarkCount;
    private int _dirtyMarkWorkerMask;
    private long _weaponUses;
    private long _inRangeAttacks;
    private long _damageApplied;
    private long _deaths;
    private long _lastCapturedRuntimeTick = -1;
    private readonly SimulationDefinition _definition;
    private long _preparedTick = -1;
    private readonly ISpaceBattleObservationSink _observationSink;
    private int _generation;
    private int _publishedShipCount;
    private int _publishedAliveShipCount;
    private long _tickStartedAt;
    private long _lastCompletedTickStartedAt;
    private bool _disposed;
    private readonly AcquisitionTransactionSlot[] _acquisitionTransactions;
    private long _acquisitionTransactionsCreated;
    private long _acquisitionTransactionsDisposed;
    private int _lastCompletedRemainingShips;
    private long _completedTicks;
    public SpaceBattleSimulationState(
        DatabaseEngine engine,
        SimulationDefinition definition,
        ISpaceBattleObservationSink observationSink,
        int workerCount)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _observationSink = observationSink ?? throw new ArgumentNullException(nameof(observationSink));
        WorkerCount = workerCount;
        ChunkCount = Math.Max(1, workerCount * 2);
        var capacity = checked(definition.ShipCount + 1);
        _frameGenerations = new int[capacity];
        _modifiedGenerations = new int[capacity];
        _reapGenerations = new int[capacity];
        _entityIds = new EntityId[capacity];
        _frames = new ShipSnapshot[capacity];
        _telemetryFrames = new ShipSnapshot[capacity];
        _incomingDamageLanes = new uint[workerCount][];
        _incomingDamageTouchedKeys = new long[workerCount][];
        _incomingDamageTouchedGenerations = new int[workerCount][];
        _incomingDamageTouchedCounts = new int[workerCount];
        _reapBuffers = new EntityId[workerCount][];
        _reapCounts = new int[workerCount];
        _directTargetingQueries = new long[workerCount];
        _batchedTargetingQueries = new long[workerCount];
        _gatherTargetingCandidates = new long[workerCount];
        _exactTargetingDistanceTests = new long[workerCount];
        _systemMetrics = new SpaceBattleSystemMetricAccumulator[SpaceBattleSystemMetricCatalog.Names.Length];
        for (var metricId = 0; metricId < _systemMetrics.Length; metricId++)
        {
            _systemMetrics[metricId] = new SpaceBattleSystemMetricAccumulator();
        }
        _acquisitionTransactions = new AcquisitionTransactionSlot[workerCount];
        for (var workerId = 0; workerId < workerCount; workerId++)
        {
            _incomingDamageLanes[workerId] = new uint[capacity];
            _incomingDamageTouchedKeys[workerId] = new long[capacity];
            _incomingDamageTouchedGenerations[workerId] = new int[capacity];
            _reapBuffers[workerId] = new EntityId[capacity];
        }
        for (var workerId = 0; workerId < workerCount; workerId++)
        {
            _acquisitionTransactions[workerId] = new AcquisitionTransactionSlot();
        }
        Accessor = new PointInTimeAccessor();
    }

    public DatabaseEngine Engine { get; }

    public PointInTimeAccessor Accessor { get; }

    public int WorkerCount { get; }

    public int ChunkCount { get; }

    public int ShipCount => _definition.ShipCount;

    public ulong Seed => _definition.Seed;

    public uint MaximumHealth => _definition.MaximumHealth;

    public float FixedDeltaSeconds => _definition.FixedDeltaSeconds;

    public float WorldWidth => _definition.WorldWidth;
    public float WorldHeight => _definition.WorldHeight;
    public float WorldDepth => _definition.WorldDepth;
    public ulong MaximumCompletedTicks => _definition.MaximumCompletedTicks;

    public long CompletedTicks => Interlocked.Read(ref _completedTicks);
    public long TickStartedAt => Volatile.Read(ref _tickStartedAt);
    public bool IsTickInFlight => Volatile.Read(ref _tickStartedAt) != Volatile.Read(ref _lastCompletedTickStartedAt);
    public bool IsAtCompletionBoundary(long zeroBasedTickNumber) =>
        zeroBasedTickNumber >= 0 &&
        CompletedTicks >= zeroBasedTickNumber + 1 &&
        !IsTickInFlight;


    public int CurrentGeneration => Volatile.Read(ref _generation);

    public int PublishedShipCount => Volatile.Read(ref _publishedShipCount);
    public int PublishedAliveShipCount => Volatile.Read(ref _publishedAliveShipCount);

    public int LastCompletedRemainingShips => Volatile.Read(ref _lastCompletedRemainingShips);
    public long AcquisitionTransactionsCreated => Interlocked.Read(ref _acquisitionTransactionsCreated);
    public long AcquisitionTransactionsDisposed => Interlocked.Read(ref _acquisitionTransactionsDisposed);

    public long DirectTargetingQueryCount => Sum(_directTargetingQueries);

    public long BatchedTargetingQueryCount => Sum(_batchedTargetingQueries);

    public long GatherTargetingCandidateCount => Sum(_gatherTargetingCandidates);

    public long ExactTargetingDistanceTestCount => Sum(_exactTargetingDistanceTests);
    public long WeaponUseCount => Interlocked.Read(ref _weaponUses);

    public long InRangeAttackCount => Interlocked.Read(ref _inRangeAttacks);

    public long DamageApplied => Interlocked.Read(ref _damageApplied);

    public long DeathCount => Interlocked.Read(ref _deaths);

    public SpaceBattleTelemetrySnapshot LastTelemetry
    {
        get
        {
            lock (_telemetryGate)
            {
                return _lastTelemetry;
            }
        }
    }

    public int ActiveAcquisitionTransactions
    {
        get
        {
            var active = 0;
            foreach (var slot in _acquisitionTransactions)
            {
                if (Volatile.Read(ref slot.Transaction) is not null)
                {
                    active++;
                }
            }

            return active;
        }
    }

    public Transaction GetAcquisitionTransaction(int workerId, long tickNumber)
    {
        if ((uint)workerId >= (uint)_acquisitionTransactions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var slot = _acquisitionTransactions[workerId];
        var threadId = Environment.CurrentManagedThreadId;
        var transaction = Volatile.Read(ref slot.Transaction);
        if (transaction is not null)
        {
            if (slot.OwnerThreadId != threadId)
            {
                throw new InvalidOperationException("SpaceBattle acquisition transaction 必须在创建它的 worker 线程上复用。");
            }

            if (tickNumber - slot.CreatedTick <= 1)
            {
                slot.LastUsedTick = tickNumber;
                return transaction;
            }

            ReleaseAcquisitionTransaction(workerId);
        }

        transaction = Engine.CreateReadOnlyTransaction();
        slot.OwnerThreadId = threadId;
        slot.CreatedTick = tickNumber;
        slot.LastUsedTick = tickNumber;
        Volatile.Write(ref slot.Transaction, transaction);
        Interlocked.Increment(ref _acquisitionTransactionsCreated);
        return transaction;
    }

    public void ReleaseAcquisitionTransaction(int workerId)
    {
        if ((uint)workerId >= (uint)_acquisitionTransactions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var slot = _acquisitionTransactions[workerId];
        var transaction = Volatile.Read(ref slot.Transaction);
        if (transaction is null)
        {
            return;
        }

        if (slot.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("SpaceBattle acquisition transaction 只能在原 worker 线程上释放。");
        }

        Volatile.Write(ref slot.Transaction, null);
        slot.OwnerThreadId = 0;
        slot.CreatedTick = -1;
        slot.LastUsedTick = -1;
        transaction.Dispose();
        Interlocked.Increment(ref _acquisitionTransactionsDisposed);
    }

    public void ReleaseAcquisitionTransactionIfOwnedByCurrentThread(int workerId)
    {
        if ((uint)workerId >= (uint)_acquisitionTransactions.Length)
        {
            return;
        }

        var slot = _acquisitionTransactions[workerId];
        if (Volatile.Read(ref slot.Transaction) is not null && slot.OwnerThreadId == Environment.CurrentManagedThreadId)
        {
            ReleaseAcquisitionTransaction(workerId);
        }
    }
    public int ReleaseAllAcquisitionTransactions()
    {
        var released = 0;
        foreach (var slot in _acquisitionTransactions)
        {
            var transaction = Interlocked.Exchange(ref slot.Transaction, null);
            if (transaction is null)
            {
                continue;
            }

            slot.OwnerThreadId = 0;
            slot.CreatedTick = -1;
            slot.LastUsedTick = -1;
            try
            {
                transaction.Dispose();
            }
            catch (Exception)
            {
                // runtime 已停止，单个事务清理失败不能阻止其他 worker-owned 事务释放。
            }

            Interlocked.Increment(ref _acquisitionTransactionsDisposed);
            released++;
        }

        return released;
    }


    private sealed class AcquisitionTransactionSlot
    {
        public Transaction Transaction;
        public long CreatedTick = -1;
        public long LastUsedTick = -1;
        public int OwnerThreadId;
    }
    public void RecordTargetingMetrics(int workerId, in TargetingQueryMetrics metrics)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        _directTargetingQueries[workerId] += metrics.DirectQueryCount;
        _batchedTargetingQueries[workerId] += metrics.BatchedQueryCount;
        _gatherTargetingCandidates[workerId] += metrics.GatherCandidateCount;
        _exactTargetingDistanceTests[workerId] += metrics.ExactDistanceTestCount;
    }
    public void RecordSystemMetric(
        SpaceBattleSystemMetricId metricId,
        long startedAt,
        int entities,
        int workerId)
    {
        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        _systemMetrics[(int)metricId].Record(elapsed, entities, workerId);
    }

    private void RecordSystemMetricAggregate(
        SpaceBattleSystemMetricId metricId,
        long elapsed,
        int entities,
        int workerCount)
    {
        _systemMetrics[(int)metricId].RecordAggregate(elapsed, entities, workerCount);
    }

    public void RecordWeaponUse() => Interlocked.Increment(ref _weaponUses);

    public void RecordInRangeAttack() => Interlocked.Increment(ref _inRangeAttacks);

    public void RecordDamage(uint damage) => Interlocked.Add(ref _damageApplied, damage);

    public void RecordDeath() => Interlocked.Increment(ref _deaths);

    public void AttachRuntime(TyphonRuntime runtime)
    {
        lock (_telemetryGate)
        {
            _runtime = runtime;
        }
    }

    /// <summary>
    /// 在 runtime 的围栏完成后读取一条完整 telemetry。Host 不调用时，Observe 仍提供应用侧稳定统计。
    /// </summary>
    public void CaptureRuntimeTelemetry(TyphonRuntime runtime = null)
    {
        runtime ??= _runtime;
        if (runtime is null)
        {
            return;
        }

        var ring = runtime.Telemetry;
        var tickNumber = ring.NewestTick;
        if (tickNumber < 0)
        {
            return;
        }

        SpaceBattleTelemetrySnapshot baseSnapshot;
        lock (_telemetryGate)
        {
            if (_lastCapturedRuntimeTick >= tickNumber || _lastTelemetry is null || _lastTelemetry.TickNumber != tickNumber)
            {
                return;
            }

            baseSnapshot = _lastTelemetry;
        }

        try
        {
            ref readonly var tick = ref ring.GetTick(tickNumber);
            var runtimeSystems = BuildRuntimeSystemMetrics(runtime, tickNumber);
            var performance = baseSnapshot.TickPerformance with
            {
                ActualHz = baseSnapshot.TickPerformance.ActualHz,
                Overload = tick.CurrentLevel.ToString().ToLowerInvariant(),
                TickMultiplier = Math.Max(1, tick.TickMultiplier),
                WorkerCount = tick.ActiveWorkerCount > 0 ? tick.ActiveWorkerCount : WorkerCount,
                SystemCount = runtime.Systems.Length,
                SystemMetrics = runtimeSystems,
            };
            var enriched = baseSnapshot with
            {
                TickPerformance = performance,
                Systems = runtimeSystems,
                HasRuntimeTelemetry = true,
            };
            lock (_telemetryGate)
            {
                _lastCapturedRuntimeTick = tickNumber;
                _lastTelemetry = enriched;
            }

            _observationSink.Publish(new SimulationTelemetrySample(enriched));
        }
        catch (ArgumentOutOfRangeException)
        {
            // ring 在最新 tick 读取与访问器之间可能轮转；下一个边界可重试。
        }
    }

    private static IReadOnlyList<SpaceBattleSystemTelemetrySnapshot> BuildRuntimeSystemMetrics(
        TyphonRuntime runtime,
        long tickNumber)
    {
        var definitions = runtime.Systems;
        var metrics = runtime.Telemetry.GetSystemMetrics(tickNumber);
        var snapshots = new SpaceBattleSystemTelemetrySnapshot[definitions.Length];
        for (var index = 0; index < definitions.Length; index++)
        {
            var system = metrics[index];
            var duration = Math.Max(0d, system.DurationUs);
            snapshots[index] = new SpaceBattleSystemTelemetrySnapshot(
                definitions[index].Name ?? $"system_{index.ToString(CultureInfo.InvariantCulture)}",
                duration,
                duration,
                duration,
                system.EntitiesProcessed,
                system.WorkersTouched,
                1);
        }

        return snapshots;
    }

    private SpaceBattleTelemetrySnapshot BuildTelemetry(long tickNumber, TickTiming timing)
    {
        FlushDirtyMarkMetric();
        var alive = 0;
        var wandering = 0;
        var tracking = 0;
        var approaching = 0;
        var attacking = 0;
        var turning = 0;
        var validLocks = 0;
        var generation = CurrentGeneration;
        for (var index = 1; index < _frames.Length; index++)
        {
            if (Volatile.Read(ref _frameGenerations[index]) != generation)
            {
                continue;
            }

            ref readonly var ship = ref _telemetryFrames[index];
            if (ship.Vitals.CurrentHealth == 0)
            {
                continue;
            }

            alive++;

            switch ((BehaviorMode)ship.Behavior.Mode)
            {
                case BehaviorMode.Wandering:
                    wandering++;
                    break;
                case BehaviorMode.Tracking:
                    tracking++;
                    break;
                case BehaviorMode.Approaching:
                    approaching++;
                    break;
                case BehaviorMode.Attacking:
                    attacking++;
                    break;
                case BehaviorMode.Turning:
                    turning++;
                    break;
                default:
                    wandering++;
                    break;
            }

            if (ship.Targeting.TargetEntityId != 0 && IsTelemetryLockValid(ship))
            {
                validLocks++;
            }
        }

        var systems = new SpaceBattleSystemTelemetrySnapshot[_systemMetrics.Length];
        for (var metricId = 0; metricId < _systemMetrics.Length; metricId++)
        {
            var snapshot = _systemMetrics[metricId].Snapshot(SpaceBattleSystemMetricCatalog.Names[metricId]);
            if (snapshot.SampleCount == 0 && metricId >= (int)SpaceBattleSystemMetricId.DirtyMarking)
            {
                snapshot = snapshot with { Workers = WorkerCount };
            }

            systems[metricId] = snapshot;
        }

        var performance = timing?.Snapshot(
            workerCount: WorkerCount,
            systemCount: SpaceBattleSystemMetricCatalog.Names.Length)
            ?? new TickPerformanceSnapshot(
                0,
                0,
                0,
                0,
                0)
            {
                WorkerCount = WorkerCount,
                SystemCount = SpaceBattleSystemMetricCatalog.Names.Length,
            };
        performance = performance with { SystemMetrics = systems };
        var telemetry = new SpaceBattleTelemetrySnapshot(
            tickNumber,
            alive,
            wandering,
            tracking,
            approaching,
            attacking,
            turning,
            validLocks,
            performance,
            new SpaceBattleQueryMetricsSnapshot(
                DirectTargetingQueryCount,
                BatchedTargetingQueryCount,
                GatherTargetingCandidateCount,
                ExactTargetingDistanceTestCount),
            new SpaceBattleCombatMetricsSnapshot(
                WeaponUseCount,
                InRangeAttackCount,
                DamageApplied,
                DeathCount),
            systems);

        lock (_telemetryGate)
        {
            _lastTelemetry = telemetry;
        }

        return telemetry;
    }

    private bool IsTelemetryLockValid(in ShipSnapshot source)
    {
        var targetKey = SpaceBattleTargeting.EntityKeyFromRaw(source.Targeting.TargetEntityId);
        if (targetKey == 0 || targetKey == source.EntityKey || !TryGetFrameIndex(targetKey, out var targetIndex))
        {
            return false;
        }

        ref readonly var target = ref _telemetryFrames[targetIndex];
        return target.Vitals.CurrentHealth > 0 &&
               SpaceBattleTargeting.DistanceSquared(source, target) <=
               SpaceBattleTargeting.LockRange * SpaceBattleTargeting.LockRange;
    }

    private void FlushDirtyMarkMetric()
    {
        var elapsed = Interlocked.Exchange(ref _dirtyMarkTicks, 0);
        var count = Interlocked.Exchange(ref _dirtyMarkCount, 0);
        var workers = Interlocked.Exchange(ref _dirtyMarkWorkerMask, 0);
        if (count == 0 && elapsed == 0)
        {
            return;
        }

        RecordSystemMetricAggregate(
            SpaceBattleSystemMetricId.DirtyMarking,
            elapsed,
            checked((int)Math.Min(count, int.MaxValue)),
            BitOperations.PopCount((uint)workers));
    }

    private static long Sum(long[] values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    public void PrepareTick(long tickNumber)
    {
        lock (_tickGate)
        {
            if (_preparedTick == tickNumber)
            {
                return;
            }

            if (_preparedTick > tickNumber)
            {
                throw new InvalidOperationException("SpaceBattle 逻辑帧编号不能倒退。");
            }

            _preparedTick = tickNumber;
            _generation++;
            _publishedShipCount = 0;
            _publishedAliveShipCount = 0;
            ClearIncomingDamage();
            Array.Clear(_reapCounts);
            _tickStartedAt = Stopwatch.GetTimestamp();
            Accessor.Attach(Engine, WorkerCount);
        }
    }


    public EntityAccessor GetWorkerAccessor(int workerId) => Accessor.GetWorkerAccessor(workerId);

    public (int Start, int End) GetClusterRange(EntityAccessor accessor, int chunkIndex, int chunkCount)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if ((uint)chunkIndex >= (uint)chunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        using var archetype = accessor.For<Ship>();
        var clusterCount = archetype.ClusterCount;
        var start = (int)((long)chunkIndex * clusterCount / chunkCount);
        var end = (int)((long)(chunkIndex + 1) * clusterCount / chunkCount);
        return (start, end);
    }
    public void PublishFrame(EntityId entityId, in ShipSnapshot frame)
    {
        var key = frame.EntityKey;
        if (key <= 0 || (ulong)key >= (ulong)_frames.Length)
        {
            throw new InvalidOperationException($"SpaceBattle EntityKey {key} 超出快照容量。");
        }
        var index = (int)key;

        var wasPublished = Volatile.Read(ref _frameGenerations[index]) == CurrentGeneration;
        var previousHealth = _frames[index].Vitals.CurrentHealth;
        _entityIds[index] = entityId;
        _frames[index] = frame;
        _telemetryFrames[index] = frame;
        Volatile.Write(ref _frameGenerations[index], CurrentGeneration);
        if (!wasPublished)
        {
            Interlocked.Increment(ref _publishedShipCount);
            if (frame.Vitals.CurrentHealth > 0)
            {
                Interlocked.Increment(ref _publishedAliveShipCount);
            }
        }
        else if (previousHealth > 0 && frame.Vitals.CurrentHealth == 0)
        {
            Interlocked.Decrement(ref _publishedAliveShipCount);
        }
        else if (previousHealth == 0 && frame.Vitals.CurrentHealth > 0)
        {
            Interlocked.Increment(ref _publishedAliveShipCount);
        }
    }
    public bool TryGetFrameIndex(long entityKey, out int index)
    {
        if ((ulong)entityKey < (ulong)_frames.Length)
        {
            index = (int)entityKey;
            return Volatile.Read(ref _frameGenerations[index]) == CurrentGeneration;
        }

        index = -1;
        return false;
    }

    public ref readonly ShipSnapshot GetFrame(int index) => ref _frames[index];

    public void MarkModified(long entityKey) => MarkModified(0, entityKey);

    public void MarkModified(int workerId, long entityKey)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var startedAt = Stopwatch.GetTimestamp();
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        Volatile.Write(ref _modifiedGenerations[index], CurrentGeneration);
        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        Interlocked.Add(ref _dirtyMarkTicks, Math.Max(0, elapsed));
        Interlocked.Increment(ref _dirtyMarkCount);
        Interlocked.Or(ref _dirtyMarkWorkerMask, 1 << workerId);
    }

    public bool WasModified(int index) => Volatile.Read(ref _modifiedGenerations[index]) == CurrentGeneration;

    public void RecordIncomingDamage(int workerId, long targetEntityKey, uint damage)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        if (damage == 0 || !TryGetEntityIndex(targetEntityKey, out var index))
        {
            return;
        }

        var touchedGenerations = _incomingDamageTouchedGenerations[workerId];
        if (touchedGenerations[index] != CurrentGeneration)
        {
            touchedGenerations[index] = CurrentGeneration;
            var touchedCount = _incomingDamageTouchedCounts[workerId];
            var touchedKeys = _incomingDamageTouchedKeys[workerId];
            if ((uint)touchedCount >= (uint)touchedKeys.Length)
            {
                throw new InvalidOperationException("SpaceBattle incoming damage touched-key 缓冲区不足。");
            }

            touchedKeys[touchedCount] = targetEntityKey;
            _incomingDamageTouchedCounts[workerId] = touchedCount + 1;
        }

        var lane = _incomingDamageLanes[workerId];
        lane[index] = unchecked(lane[index] + damage);
    }

    public uint ReadIncomingDamage(int workerId, long targetEntityKey)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        return TryGetEntityIndex(targetEntityKey, out var index)
            ? _incomingDamageLanes[workerId][index]
            : 0u;
    }

    public int IncomingDamageTouchedCount(int workerId)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        return _incomingDamageTouchedCounts[workerId];
    }

    public ReadOnlySpan<long> IncomingDamageTouchedKeys(int workerId)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        return _incomingDamageTouchedKeys[workerId].AsSpan(0, _incomingDamageTouchedCounts[workerId]);
    }

    public uint ReduceIncomingDamage(long targetEntityKey)
    {
        if (!TryGetEntityIndex(targetEntityKey, out var index))
        {
            return 0u;
        }

        var total = 0u;
        for (var workerId = 0; workerId < WorkerCount; workerId++)
        {
            total = unchecked(total + _incomingDamageLanes[workerId][index]);
        }

        return total;
    }

    public void ClearIncomingDamage()
    {
        for (var workerId = 0; workerId < WorkerCount; workerId++)
        {
            var lane = _incomingDamageLanes[workerId];
            var touchedKeys = _incomingDamageTouchedKeys[workerId];
            var touchedGenerations = _incomingDamageTouchedGenerations[workerId];
            var touchedCount = _incomingDamageTouchedCounts[workerId];
            for (var index = 0; index < touchedCount; index++)
            {
                if (TryGetEntityIndex(touchedKeys[index], out var entityIndex))
                {
                    lane[entityIndex] = 0;
                    touchedGenerations[entityIndex] = 0;
                }
            }

            _incomingDamageTouchedCounts[workerId] = 0;
        }
    }

    public void UpdateFrameHealth(long entityKey, uint health)
    {
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        var previousHealth = _frames[index].Vitals.CurrentHealth;
        _frames[index] = _frames[index] with
        {
            Vitals = new Vitals { CurrentHealth = health },
        };
        _telemetryFrames[index] = _telemetryFrames[index] with
        {
            Vitals = new Vitals { CurrentHealth = health },
        };
        if (previousHealth > 0 && health == 0)
        {
            Interlocked.Decrement(ref _publishedAliveShipCount);
        }
        else if (previousHealth == 0 && health > 0)
        {
            Interlocked.Increment(ref _publishedAliveShipCount);
        }
    }
    public void UpdateFrameBehavior(
        long entityKey,
        in Motion motion,
        in Targeting targeting,
        in Behavior behavior)
    {
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        _telemetryFrames[index] = _telemetryFrames[index] with
        {
            Motion = motion,
            Targeting = targeting,
            Behavior = behavior,
        };
    }

    public void UpdateFrameMovement(long entityKey, in Hull hull, in Motion motion)
    {
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        _telemetryFrames[index] = _telemetryFrames[index] with
        {
            Hull = hull,
            Motion = motion,
        };
    }

    public void MarkForReap(int workerId, long entityKey)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        if (!TryGetFrameIndex(entityKey, out var index) ||
            _reapGenerations[index] == CurrentGeneration)
        {
            return;
        }

        _reapGenerations[index] = CurrentGeneration;
        var reapCount = _reapCounts[workerId];
        var reapBuffer = _reapBuffers[workerId];
        if ((uint)reapCount >= (uint)reapBuffer.Length)
        {
            throw new InvalidOperationException("SpaceBattle per-worker 死亡缓冲区不足。");
        }

        reapBuffer[reapCount] = _entityIds[index];
        _reapCounts[workerId] = reapCount + 1;
    }

    public bool IsPendingReap(long entityKey) =>
        TryGetFrameIndex(entityKey, out var index) && _reapGenerations[index] == CurrentGeneration;

    public int PendingReapCount
    {
        get
        {
            var count = 0;
            for (var workerId = 0; workerId < WorkerCount; workerId++)
            {
                count += _reapCounts[workerId];
            }

            return count;
        }
    }

    public int CopyPendingReaps(Span<EntityId> destination)
    {
        var total = PendingReapCount;
        if (destination.Length < total)
        {
            throw new ArgumentException("目标缓冲区不足以容纳待回收实体。", nameof(destination));
        }

        var offset = 0;
        for (var workerId = 0; workerId < WorkerCount; workerId++)
        {
            var count = _reapCounts[workerId];
            _reapBuffers[workerId].AsSpan(0, count).CopyTo(destination[offset..]);
            offset += count;
        }

        return total;
    }

    public void CompleteReaps() => Array.Clear(_reapCounts);

    private bool TryGetEntityIndex(long entityKey, out int index)
    {
        if ((ulong)entityKey < (ulong)_frames.Length)
        {
            index = (int)entityKey;
            return index > 0;
        }

        index = -1;
        return false;
    }

    public SpaceBattleSnapshot BuildPublishedSnapshot()
    {
        var ships = new List<ShipSnapshot>(PublishedShipCount);
        var generation = CurrentGeneration;
        for (var index = 1; index < _frames.Length; index++)
        {
            if (Volatile.Read(ref _frameGenerations[index]) == generation &&
                _frames[index].Vitals.CurrentHealth > 0)
            {
                ships.Add(_frames[index]);
            }
        }

        return new SpaceBattleSnapshot(ships);
    }

    public void CompleteTick(long tickNumber, TickTiming timing = null)
    {
        var duration = Stopwatch.GetElapsedTime(_tickStartedAt);
        timing?.RecordTick(duration, _tickStartedAt);
        var remainingShips = PublishedAliveShipCount;
        Volatile.Write(ref _lastCompletedRemainingShips, remainingShips);
        SpaceBattleTelemetrySnapshot telemetry = null;
        if (SpaceBattleTelemetrySampling.IsSampleTick(tickNumber))
        {
            telemetry = BuildTelemetry(tickNumber, timing);
            telemetry = telemetry with
            {
                TickPerformance = telemetry.TickPerformance with
                {
                    Overload = duration.TotalSeconds > FixedDeltaSeconds ? "overload" : "normal",
                },
            };
            lock (_telemetryGate)
            {
                _lastTelemetry = telemetry;
            }
        }

        _observationSink.Publish(new SimulationTickCompleted(
            tickNumber,
            PublishedShipCount,
            duration,
            tickNumber + 1 == (long)_definition.MaximumCompletedTicks ? BuildPublishedSnapshot() : null)
        {
            Telemetry = telemetry,
        });
        Volatile.Write(ref _lastCompletedTickStartedAt, _tickStartedAt);
        Interlocked.Increment(ref _completedTicks);
    }

    public TickPerformanceSnapshot GetTimingSnapshot(TickTiming timing) => timing.Snapshot();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAllAcquisitionTransactions();

        Accessor.Dispose();
    }

}

internal enum SpaceBattleRandomPurpose : ulong
{
    InitialWanderHeading = 0x11A7_6C4D_2F90_B381UL,
    WanderHeading = 0x8C31_5A72_D4E6_109FUL,
    WanderSpeed = 0xE27B_4390_6D1F_A508UL,
    WeaponPhase = 0xC49E_2D17_6A83_F051UL,
    TurnHeading = 0x3B7E_91D4_0A62_F5C8UL,
    TurnAngle = 0xF1C3_58A7_2D49_806EUL,
}

internal static class SpaceBattleMath
{
    public const float MaximumWanderSpeed = 200f;
    public const float MaximumTurnRadiansPerSecond = 1f;
    public const ushort WanderFlightTicks = 50;
    public const float EvasiveSpeed = 100f;
    public const ushort EvasiveFlightTicks = 25;
    public const float MinimumTurnDegrees = 50f;
    public const float MaximumTurnDegrees = 300f;
    public const float MinimumTurnRadians = MinimumTurnDegrees * MathF.PI / 180f;
    public const float MaximumTurnRadians = MaximumTurnDegrees * MathF.PI / 180f;

    private const float TwoPi = 2f * MathF.PI;
    // 24 位分位数缩放到 [0,1] 闭区间：速度可达 [0,200] 上界，转向量程可达 300°。
    private const float UnitFloatScale = 1f / 16_777_215f;
    private const float VectorEpsilonSquared = 1e-12f;

    public static ulong DeriveUInt64(
        ulong seed,
        long entityKey,
        long modeStartedTick,
        SpaceBattleRandomPurpose purpose)
    {
        var value = Mix(seed ^ 0xD1B5_4A32_9C87_E601UL);
        value = Mix(value ^ unchecked((ulong)entityKey + 0x9E37_79B9_7F4A_7C15UL));
        value = Mix(value ^ unchecked((ulong)modeStartedTick));
        return Mix(value ^ (ulong)purpose);
    }

    public static float DeriveUnitFloat(
        ulong seed,
        long entityKey,
        long modeStartedTick,
        SpaceBattleRandomPurpose purpose)
        => (DeriveUInt64(seed, entityKey, modeStartedTick, purpose) >> 40) * UnitFloatScale;

    public static Vector3 RandomDirection(
        ulong seed,
        long entityKey,
        long modeStartedTick,
        SpaceBattleRandomPurpose purpose)
    {
        var azimuthUnit = DeriveUnitFloat(
            seed,
            entityKey,
            modeStartedTick,
            purpose ^ (SpaceBattleRandomPurpose)0xA5A5_A5A5_A5A5_A5A5UL);
        var zUnit = DeriveUnitFloat(
            seed,
            entityKey,
            modeStartedTick,
            purpose ^ (SpaceBattleRandomPurpose)0x5A5A_5A5A_5A5A_5A5AUL);
        var z = (zUnit * 2f) - 1f;
        var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
        var azimuth = azimuthUnit * TwoPi;
        return new Vector3(
            radius * MathF.Cos(azimuth),
            radius * MathF.Sin(azimuth),
            z);
    }

    public static float RandomTurnRadians(ulong seed, long entityKey, long modeStartedTick) =>
        MinimumTurnRadians
        + (DeriveUnitFloat(seed, entityKey, modeStartedTick, SpaceBattleRandomPurpose.TurnAngle)
           * (MaximumTurnRadians - MinimumTurnRadians));

    public static Vector3 RandomTurnTarget(
        ulong seed,
        long entityKey,
        long modeStartedTick,
        Vector3 current,
        out float turnRadians)
    {
        var from = NormalizeOrFallback(current, Vector3.UnitX);
        turnRadians = RandomTurnRadians(seed, entityKey, modeStartedTick);
        var basis = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var firstPerpendicular = NormalizeOrFallback(Vector3.Cross(from, basis), Vector3.UnitZ);
        var secondPerpendicular = NormalizeOrFallback(Vector3.Cross(from, firstPerpendicular), Vector3.UnitZ);
        var azimuth = DeriveUnitFloat(seed, entityKey, modeStartedTick, SpaceBattleRandomPurpose.TurnHeading) * TwoPi;
        var perpendicular = NormalizeOrFallback(
            (firstPerpendicular * MathF.Cos(azimuth)) + (secondPerpendicular * MathF.Sin(azimuth)),
            firstPerpendicular);
        return NormalizeOrFallback(
            (from * MathF.Cos(turnRadians)) + (perpendicular * MathF.Sin(turnRadians)),
            from);
    }

    public static float RandomWanderSpeed(ulong seed, long entityKey, long modeStartedTick)
        => DeriveUnitFloat(seed, entityKey, modeStartedTick, SpaceBattleRandomPurpose.WanderSpeed) * MaximumWanderSpeed;

    public static float AngleBetween(Vector3 current, Vector3 target)
    {
        var from = NormalizeOrFallback(current, Vector3.UnitX);
        var to = NormalizeOrFallback(target, from);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        return MathF.Acos(dot);
    }

    public static Vector3 TurnTowards(Vector3 current, Vector3 target, float maximumRadians, out float remainingRadians)
    {
        var from = NormalizeOrFallback(current, Vector3.UnitX);
        var to = NormalizeOrFallback(target, from);
        var angle = AngleBetween(from, to);
        if (!float.IsFinite(maximumRadians) || maximumRadians <= 0f || angle <= maximumRadians)
        {
            remainingRadians = 0f;
            return to;
        }

        remainingRadians = angle - maximumRadians;
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var sine = MathF.Sin(angle);
        if (MathF.Abs(sine) > 1e-5f)
        {
            var firstWeight = MathF.Sin(angle - maximumRadians) / sine;
            var secondWeight = MathF.Sin(maximumRadians) / sine;
            return NormalizeOrFallback((from * firstWeight) + (to * secondWeight), to);
        }

        var basis = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var perpendicular = NormalizeOrFallback(Vector3.Cross(from, basis), Vector3.UnitZ);
        return NormalizeOrFallback(
            (from * MathF.Cos(maximumRadians)) + (perpendicular * MathF.Sin(maximumRadians)),
            to);
    }

    public static Vector3 TurnAlongGreatCircle(
        Vector3 current,
        Vector3 target,
        float remainingRadians,
        float maximumRadians,
        out float nextRemainingRadians)
    {
        var from = NormalizeOrFallback(current, Vector3.UnitX);
        var to = NormalizeOrFallback(target, from);
        if (!float.IsFinite(remainingRadians) || remainingRadians <= 0f
            || !float.IsFinite(maximumRadians) || maximumRadians <= 0f)
        {
            nextRemainingRadians = 0f;
            return to;
        }

        if (remainingRadians <= maximumRadians)
        {
            nextRemainingRadians = 0f;
            return target;
        }

        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() <= VectorEpsilonSquared)
        {
            var basis = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            axis = Vector3.Cross(from, basis);
        }

        axis = NormalizeOrFallback(axis, Vector3.UnitZ);
        if (remainingRadians > MathF.PI)
        {
            axis = -axis;
        }

        var cosine = MathF.Cos(maximumRadians);
        var sine = MathF.Sin(maximumRadians);
        var turned = (from * cosine) + (Vector3.Cross(axis, from) * sine);
        nextRemainingRadians = remainingRadians - maximumRadians;
        return NormalizeOrFallback(turned, to);
    }

    public static AABB3F MoveBounds(
        AABB3F current,
        float headingX,
        float headingY,
        float speed,
        float deltaSeconds,
        float worldWidth,
        float worldHeight,
        out float resultingHeadingX,
        out float resultingHeadingY)
    {
        var x = ReflectCoordinate(
            current.MinX,
            (double)headingX * speed * deltaSeconds,
            worldWidth,
            out var xDirection);
        var y = ReflectCoordinate(
            current.MinY,
            (double)headingY * speed * deltaSeconds,
            worldHeight,
            out var yDirection);
        resultingHeadingX = headingX * xDirection;
        resultingHeadingY = headingY * yDirection;
        return new AABB3F
        {
            MinX = x,
            MaxX = x,
            MinY = y,
            MaxY = y,
            MinZ = current.MinZ,
            MaxZ = current.MaxZ,
        };
    }

    public static AABB3F MoveBounds(
        AABB3F current,
        float headingX,
        float headingY,
        float headingZ,
        float speed,
        float deltaSeconds,
        float worldWidth,
        float worldHeight,
        float worldDepth,
        out float resultingHeadingX,
        out float resultingHeadingY,
        out float resultingHeadingZ)
    {
        var x = ReflectCoordinate(
            current.MinX,
            (double)headingX * speed * deltaSeconds,
            worldWidth,
            out var xDirection);
        var y = ReflectCoordinate(
            current.MinY,
            (double)headingY * speed * deltaSeconds,
            worldHeight,
            out var yDirection);
        var z = ReflectCoordinate(
            current.MinZ,
            (double)headingZ * speed * deltaSeconds,
            worldDepth,
            out var zDirection);
        resultingHeadingX = headingX * xDirection;
        resultingHeadingY = headingY * yDirection;
        resultingHeadingZ = headingZ * zDirection;
        return new AABB3F
        {
            MinX = x,
            MaxX = x,
            MinY = y,
            MaxY = y,
            MinZ = z,
            MaxZ = z,
        };
    }

    public static AABB3F MoveBounds(
        AABB3F current,
        Vector3 heading,
        float speed,
        float deltaSeconds,
        float worldWidth,
        float worldHeight,
        float worldDepth,
        out Vector3 resultingHeading)
    {
        var bounds = MoveBounds(
            current,
            heading.X,
            heading.Y,
            heading.Z,
            speed,
            deltaSeconds,
            worldWidth,
            worldHeight,
            worldDepth,
            out var resultingHeadingX,
            out var resultingHeadingY,
            out var resultingHeadingZ);
        resultingHeading = new Vector3(resultingHeadingX, resultingHeadingY, resultingHeadingZ);
        return bounds;
    }

    public static float ReflectCoordinate(float position, float displacement, float upperBound, out bool reflected)
    {
        var result = ReflectCoordinate(position, (double)displacement, upperBound, out var direction);
        reflected = direction < 0;
        return result;
    }

    private static float ReflectCoordinate(
        float position,
        double displacement,
        float upperBound,
        out int direction)
    {
        if (!float.IsFinite(position) || !float.IsFinite(upperBound) || upperBound <= 0f || !double.IsFinite(displacement))
        {
            throw new ArgumentOutOfRangeException(nameof(position), "反射坐标必须使用有限值和正世界边界。");
        }

        var extent = (double)upperBound;
        var maximumInside = (double)MathF.BitDecrement(upperBound);
        var period = extent * 2d;
        var folded = ((double)position + displacement) % period;
        if (folded < 0d)
        {
            folded += period;
        }

        if (folded > extent || (folded == extent && displacement > 0d))
        {
            direction = -1;
            folded = period - folded;
        }
        else if (folded == 0d && displacement < 0d)
        {
            direction = -1;
        }
        else
        {
            direction = 1;
        }

        if (folded >= extent)
        {
            folded = maximumInside;
        }

        return (float)Math.Clamp(folded, 0d, maximumInside);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= VectorEpsilonSquared)
        {
            return fallback;
        }

        return value / MathF.Sqrt(lengthSquared);
    }

    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL);
        return value ^ (value >> 31);
    }
}
