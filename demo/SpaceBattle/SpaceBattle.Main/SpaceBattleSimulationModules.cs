using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

/// <summary>管理本逻辑帧发布的只读快照和阶段内观察镜像。</summary>
internal sealed class SpaceBattleFrameStore
{
    private readonly int[] _publishedGenerations;
    private readonly int[] _modifiedGenerations;
    private readonly EntityId[] _entityIds;
    private readonly ShipSnapshot[] _publishedFrames;
    private readonly ShipSnapshot[] _observedFrames;
    private int _generation;
    private int _publishedShipCount;
    private int _publishedAliveShipCount;

    public SpaceBattleFrameStore(int shipCount)
    {
        var capacity = checked(shipCount + 1);
        _publishedGenerations = new int[capacity];
        _modifiedGenerations = new int[capacity];
        _entityIds = new EntityId[capacity];
        _publishedFrames = new ShipSnapshot[capacity];
        _observedFrames = new ShipSnapshot[capacity];
    }

    public int Capacity => _publishedFrames.Length;

    public int CurrentGeneration => Volatile.Read(ref _generation);

    public int PublishedShipCount => Volatile.Read(ref _publishedShipCount);

    public int PublishedAliveShipCount => Volatile.Read(ref _publishedAliveShipCount);

    public void BeginTick()
    {
        Volatile.Write(ref _generation, unchecked(CurrentGeneration + 1));
        Volatile.Write(ref _publishedShipCount, 0);
        Volatile.Write(ref _publishedAliveShipCount, 0);
    }

    public void Publish(EntityId entityId, in ShipSnapshot frame)
    {
        var key = frame.EntityKey;
        if (key <= 0 || (ulong)key >= (ulong)_publishedFrames.Length)
        {
            throw new InvalidOperationException($"SpaceBattle EntityKey {key} 超出快照容量。");
        }

        var index = (int)key;
        var generation = CurrentGeneration;
        var wasPublished = Volatile.Read(ref _publishedGenerations[index]) == generation;
        var previousHealth = _publishedFrames[index].Vitals.CurrentHealth;
        _entityIds[index] = entityId;
        _publishedFrames[index] = frame;
        _observedFrames[index] = frame;
        Volatile.Write(ref _publishedGenerations[index], generation);
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

    public bool TryGetIndex(long entityKey, out int index)
    {
        if ((ulong)entityKey < (ulong)_publishedFrames.Length)
        {
            index = (int)entityKey;
            return Volatile.Read(ref _publishedGenerations[index]) == CurrentGeneration;
        }

        index = -1;
        return false;
    }

    public bool TryGetSlotIndex(long entityKey, out int index)
    {
        if ((ulong)entityKey < (ulong)_publishedFrames.Length)
        {
            index = (int)entityKey;
            return index > 0;
        }

        index = -1;
        return false;
    }

    public bool IsPublishedIndex(int index) =>
        (uint)index < (uint)_publishedFrames.Length &&
        Volatile.Read(ref _publishedGenerations[index]) == CurrentGeneration;

    public ref readonly ShipSnapshot GetPublished(int index) => ref _publishedFrames[index];

    public ref readonly ShipSnapshot GetObserved(int index) => ref _observedFrames[index];

    public EntityId GetEntityId(int index) => _entityIds[index];

    public bool MarkModified(long entityKey)
    {
        if (!TryGetIndex(entityKey, out var index))
        {
            return false;
        }

        Volatile.Write(ref _modifiedGenerations[index], CurrentGeneration);
        return true;
    }

    public bool WasModified(int index) =>
        Volatile.Read(ref _modifiedGenerations[index]) == CurrentGeneration;

    public void UpdateHealth(long entityKey, uint health)
    {
        if (!TryGetIndex(entityKey, out var index))
        {
            return;
        }

        var previousHealth = _publishedFrames[index].Vitals.CurrentHealth;
        _publishedFrames[index] = _publishedFrames[index] with
        {
            Vitals = new Vitals { CurrentHealth = health },
        };
        _observedFrames[index] = _observedFrames[index] with
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

    public void UpdateBehavior(
        long entityKey,
        in Motion motion,
        in Targeting targeting,
        in Behavior behavior)
    {
        if (!TryGetIndex(entityKey, out var index))
        {
            return;
        }

        _observedFrames[index] = _observedFrames[index] with
        {
            Motion = motion,
            Targeting = targeting,
            Behavior = behavior,
        };
    }

    public void UpdateMovement(long entityKey, in Hull hull, in Motion motion)
    {
        if (!TryGetIndex(entityKey, out var index))
        {
            return;
        }

        _observedFrames[index] = _observedFrames[index] with
        {
            Hull = hull,
            Motion = motion,
        };
    }

    public SpaceBattleSnapshot BuildPublishedSnapshot()
    {
        var ships = new List<ShipSnapshot>(PublishedShipCount);
        for (var index = 1; index < _publishedFrames.Length; index++)
        {
            if (IsPublishedIndex(index) && _publishedFrames[index].Vitals.CurrentHealth > 0)
            {
                ships.Add(_publishedFrames[index]);
            }
        }

        return new SpaceBattleSnapshot(ships);
    }
}

/// <summary>管理同时战斗结算的 worker-private 伤害和待回收缓冲。</summary>
internal sealed class SpaceBattleCombatSettlement
{
    private readonly SpaceBattleFrameStore _frames;
    private readonly uint[][] _incomingDamageLanes;
    private readonly long[][] _incomingDamageTouchedKeys;
    private readonly int[][] _incomingDamageTouchedGenerations;
    private readonly int[] _incomingDamageTouchedCounts;
    private readonly int[] _reapGenerations;
    private readonly EntityId[][] _reapBuffers;
    private readonly int[] _reapCounts;

    public SpaceBattleCombatSettlement(SpaceBattleFrameStore frames, int workerCount)
    {
        _frames = frames;
        WorkerCount = workerCount;
        _incomingDamageLanes = new uint[workerCount][];
        _incomingDamageTouchedKeys = new long[workerCount][];
        _incomingDamageTouchedGenerations = new int[workerCount][];
        _incomingDamageTouchedCounts = new int[workerCount];
        _reapGenerations = new int[frames.Capacity];
        _reapBuffers = new EntityId[workerCount][];
        _reapCounts = new int[workerCount];
        for (var workerId = 0; workerId < workerCount; workerId++)
        {
            _incomingDamageLanes[workerId] = new uint[frames.Capacity];
            _incomingDamageTouchedKeys[workerId] = new long[frames.Capacity];
            _incomingDamageTouchedGenerations[workerId] = new int[frames.Capacity];
            _reapBuffers[workerId] = new EntityId[frames.Capacity];
        }
    }

    public int WorkerCount { get; }

    public void RecordIncomingDamage(int workerId, long targetEntityKey, uint damage)
    {
        ValidateWorker(workerId);
        if (damage == 0 || !_frames.TryGetSlotIndex(targetEntityKey, out var index))
        {
            return;
        }

        var touchedGenerations = _incomingDamageTouchedGenerations[workerId];
        if (touchedGenerations[index] != _frames.CurrentGeneration)
        {
            touchedGenerations[index] = _frames.CurrentGeneration;
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
        ValidateWorker(workerId);
        return _frames.TryGetSlotIndex(targetEntityKey, out var index)
            ? _incomingDamageLanes[workerId][index]
            : 0u;
    }

    public int IncomingDamageTouchedCount(int workerId)
    {
        ValidateWorker(workerId);
        return _incomingDamageTouchedCounts[workerId];
    }

    public ReadOnlySpan<long> IncomingDamageTouchedKeys(int workerId)
    {
        ValidateWorker(workerId);
        return _incomingDamageTouchedKeys[workerId].AsSpan(0, _incomingDamageTouchedCounts[workerId]);
    }

    public uint ReduceIncomingDamage(long targetEntityKey)
    {
        if (!_frames.TryGetSlotIndex(targetEntityKey, out var index))
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
                if (_frames.TryGetSlotIndex(touchedKeys[index], out var entityIndex))
                {
                    lane[entityIndex] = 0;
                    touchedGenerations[entityIndex] = 0;
                }
            }

            _incomingDamageTouchedCounts[workerId] = 0;
        }
    }

    public void MarkForReap(int workerId, long entityKey)
    {
        ValidateWorker(workerId);
        if (!_frames.TryGetIndex(entityKey, out var index) ||
            _reapGenerations[index] == _frames.CurrentGeneration)
        {
            return;
        }

        _reapGenerations[index] = _frames.CurrentGeneration;
        var reapCount = _reapCounts[workerId];
        var reapBuffer = _reapBuffers[workerId];
        if ((uint)reapCount >= (uint)reapBuffer.Length)
        {
            throw new InvalidOperationException("SpaceBattle per-worker 死亡缓冲区不足。");
        }

        reapBuffer[reapCount] = _frames.GetEntityId(index);
        _reapCounts[workerId] = reapCount + 1;
    }

    public bool IsPendingReap(long entityKey) =>
        _frames.TryGetIndex(entityKey, out var index) &&
        _reapGenerations[index] == _frames.CurrentGeneration;

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

    private void ValidateWorker(int workerId)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }
}

/// <summary>管理 thread-affine 目标获取事务的创建、复用、失效和停止后清理。</summary>
internal sealed class SpaceBattleAcquisitionTransactions
{
    private readonly DatabaseEngine _engine;
    private readonly AcquisitionTransactionSlot[] _slots;
    private int _resetVersion;
    private long _created;
    private long _disposed;

    public SpaceBattleAcquisitionTransactions(DatabaseEngine engine, int workerCount)
    {
        _engine = engine;
        _slots = new AcquisitionTransactionSlot[workerCount];
        for (var workerId = 0; workerId < workerCount; workerId++)
        {
            _slots[workerId] = new AcquisitionTransactionSlot();
        }
    }

    public long Created => Interlocked.Read(ref _created);

    public long Disposed => Interlocked.Read(ref _disposed);

    public int Active
    {
        get
        {
            var active = 0;
            foreach (var slot in _slots)
            {
                if (Volatile.Read(ref slot.Transaction) is not null)
                {
                    active++;
                }
            }

            return active;
        }
    }

    public Transaction Get(int workerId, long tickNumber)
    {
        ValidateWorker(workerId);
        var slot = _slots[workerId];
        var threadId = Environment.CurrentManagedThreadId;
        var transaction = Volatile.Read(ref slot.Transaction);
        var resetVersion = Volatile.Read(ref _resetVersion);
        if (transaction is not null)
        {
            if (slot.OwnerThreadId != threadId)
            {
                throw new InvalidOperationException("SpaceBattle acquisition transaction 必须在创建它的 worker 线程上复用。");
            }

            if (slot.ResetVersion == resetVersion && tickNumber - slot.CreatedTick <= 1)
            {
                return transaction;
            }

            Release(workerId);
        }

        transaction = _engine.CreateReadOnlyTransaction();
        slot.OwnerThreadId = threadId;
        slot.CreatedTick = tickNumber;
        slot.ResetVersion = resetVersion;
        Volatile.Write(ref slot.Transaction, transaction);
        Interlocked.Increment(ref _created);
        return transaction;
    }

    public void InvalidateForNextUse() => Interlocked.Increment(ref _resetVersion);

    public void Release(int workerId)
    {
        ValidateWorker(workerId);
        var slot = _slots[workerId];
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
        ResetSlot(slot);
        transaction.Dispose();
        Interlocked.Increment(ref _disposed);
    }

    public void ReleaseIfOwnedByCurrentThread(int workerId)
    {
        if ((uint)workerId >= (uint)_slots.Length)
        {
            return;
        }

        var slot = _slots[workerId];
        if (Volatile.Read(ref slot.Transaction) is not null &&
            slot.OwnerThreadId == Environment.CurrentManagedThreadId)
        {
            Release(workerId);
        }
    }

    public int ReleaseAllAfterRuntimeStop()
    {
        var released = 0;
        foreach (var slot in _slots)
        {
            var transaction = Interlocked.Exchange(ref slot.Transaction, null);
            if (transaction is null)
            {
                continue;
            }

            ResetSlot(slot);
            try
            {
                transaction.Dispose();
            }
            catch (Exception)
            {
                // runtime 已停止，单个事务清理失败不能阻止其他 worker-owned 事务释放。
            }

            Interlocked.Increment(ref _disposed);
            released++;
        }

        return released;
    }

    private static void ResetSlot(AcquisitionTransactionSlot slot)
    {
        slot.OwnerThreadId = 0;
        slot.CreatedTick = -1;
        slot.ResetVersion = -1;
    }

    private void ValidateWorker(int workerId)
    {
        if ((uint)workerId >= (uint)_slots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }

    private sealed class AcquisitionTransactionSlot : IDisposable
    {
        public Transaction Transaction;
        public long CreatedTick = -1;
        public int OwnerThreadId;
        public int ResetVersion = -1;

        public void Dispose()
        {
            Transaction?.Dispose();
            Transaction = null;
        }
    }
}

/// <summary>聚合战斗、锁定和系统耗时统计，并生成稳定 telemetry。</summary>
internal sealed class SpaceBattleTelemetryState
{
    private readonly int _workerCount;
    private readonly long[] _directTargetingQueries;
    private readonly long[] _batchedTargetingQueries;
    private readonly long[] _gatherTargetingCandidates;
    private readonly long[] _exactTargetingDistanceTests;
    private readonly SpaceBattleSystemMetricAccumulator[] _systemMetrics;
    private long _dirtyMarkTicks;
    private long _dirtyMarkCount;
    private int _dirtyMarkWorkerMask;
    private long _weaponUses;
    private long _inRangeAttacks;
    private long _damageApplied;
    private long _deaths;

    public SpaceBattleTelemetryState(int workerCount)
    {
        _workerCount = workerCount;
        _directTargetingQueries = new long[workerCount];
        _batchedTargetingQueries = new long[workerCount];
        _gatherTargetingCandidates = new long[workerCount];
        _exactTargetingDistanceTests = new long[workerCount];
        _systemMetrics = new SpaceBattleSystemMetricAccumulator[SpaceBattleSystemMetricCatalog.Names.Length];
        for (var metricId = 0; metricId < _systemMetrics.Length; metricId++)
        {
            _systemMetrics[metricId] = new SpaceBattleSystemMetricAccumulator();
        }
    }

    public void RecordTargeting(int workerId, in TargetingQueryMetrics metrics)
    {
        ValidateWorker(workerId);
        _directTargetingQueries[workerId] += metrics.DirectQueryCount;
        _batchedTargetingQueries[workerId] += metrics.BatchedQueryCount;
        _gatherTargetingCandidates[workerId] += metrics.GatherCandidateCount;
        _exactTargetingDistanceTests[workerId] += metrics.ExactDistanceTestCount;
    }

    public void RecordSystem(
        SpaceBattleSystemMetricId metricId,
        long startedAt,
        int entities,
        int workerId)
    {
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - startedAt);
        _systemMetrics[(int)metricId].Record(elapsed, entities, workerId);
    }

    public void RecordDirtyMark(long elapsed, int workerId)
    {
        Interlocked.Add(ref _dirtyMarkTicks, Math.Max(0, elapsed));
        Interlocked.Increment(ref _dirtyMarkCount);
        Interlocked.Or(ref _dirtyMarkWorkerMask, 1 << workerId);
    }

    public void RecordWeaponUse() => Interlocked.Increment(ref _weaponUses);

    public void RecordInRangeAttack() => Interlocked.Increment(ref _inRangeAttacks);

    public void RecordDamage(uint damage) => Interlocked.Add(ref _damageApplied, damage);

    public void RecordDeath() => Interlocked.Increment(ref _deaths);

    public SpaceBattleTelemetrySnapshot BuildSnapshot(
        long tickNumber,
        TickTiming timing,
        SpaceBattleFrameStore frames)
    {
        FlushDirtyMarkMetric();
        var alive = 0;
        var wandering = 0;
        var tracking = 0;
        var approaching = 0;
        var attacking = 0;
        var turning = 0;
        var validLocks = 0;
        for (var index = 1; index < frames.Capacity; index++)
        {
            if (!frames.IsPublishedIndex(index))
            {
                continue;
            }

            ref readonly var ship = ref frames.GetObserved(index);
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
                    throw new InvalidOperationException(
                        $"SpaceBattle 飞船 {ship.EntityKey} 使用未知行为模式 {ship.Behavior.Mode}。");
            }

            if (ship.Targeting.TargetRawEntityId != 0 && IsLockValid(frames, ship))
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
                snapshot = snapshot with { Workers = _workerCount };
            }

            systems[metricId] = snapshot;
        }

        var performance = timing?.Snapshot(
            workerCount: _workerCount,
            systemCount: SpaceBattleSystemMetricCatalog.Names.Length)
            ?? new TickPerformanceSnapshot(0, 0, 0, 0, 0)
            {
                WorkerCount = _workerCount,
                SystemCount = SpaceBattleSystemMetricCatalog.Names.Length,
            };
        return new SpaceBattleTelemetrySnapshot(
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
                Sum(_directTargetingQueries),
                Sum(_batchedTargetingQueries),
                Sum(_gatherTargetingCandidates),
                Sum(_exactTargetingDistanceTests)),
            new SpaceBattleCombatMetricsSnapshot(
                Interlocked.Read(ref _weaponUses),
                Interlocked.Read(ref _inRangeAttacks),
                Interlocked.Read(ref _damageApplied),
                Interlocked.Read(ref _deaths)),
            systems);
    }

    private static bool IsLockValid(SpaceBattleFrameStore frames, in ShipSnapshot source)
    {
        var targetKey = SpaceBattleTargeting.EntityKeyFromRaw(source.Targeting.TargetRawEntityId);
        if (targetKey == 0 || targetKey == source.EntityKey || !frames.TryGetIndex(targetKey, out var targetIndex))
        {
            return false;
        }

        ref readonly var target = ref frames.GetObserved(targetIndex);
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

        _systemMetrics[(int)SpaceBattleSystemMetricId.DirtyMarking].RecordAggregate(
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

    private void ValidateWorker(int workerId)
    {
        if ((uint)workerId >= (uint)_workerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }
}
