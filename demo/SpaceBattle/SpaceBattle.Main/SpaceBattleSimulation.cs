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
    private readonly ShipSnapshot[] _frames;
    private readonly EntityId[] _reapBuffer;
    private readonly SimulationDefinition _definition;
    private readonly ISpaceBattleObservationSink _observationSink;
    private long _preparedTick = -1;
    private int _generation;
    private int _publishedShipCount;
    private int _pendingReapCount;
    private long _tickStartedAt;
    private bool _disposed;
    private readonly AcquisitionTransactionSlot[] _acquisitionTransactions;
    private long _acquisitionTransactionsCreated;
    private long _acquisitionTransactionsDisposed;
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
        _reapBuffer = new EntityId[capacity];
        _acquisitionTransactions = new AcquisitionTransactionSlot[workerCount];
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


    public int CurrentGeneration => Volatile.Read(ref _generation);

    public int PublishedShipCount => Volatile.Read(ref _publishedShipCount);
    public long AcquisitionTransactionsCreated => Interlocked.Read(ref _acquisitionTransactionsCreated);

    public long AcquisitionTransactionsDisposed => Interlocked.Read(ref _acquisitionTransactionsDisposed);

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

    private sealed class AcquisitionTransactionSlot
    {
        public Transaction Transaction;
        public long CreatedTick = -1;
        public long LastUsedTick = -1;
        public int OwnerThreadId;
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
            _pendingReapCount = 0;
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
        _entityIds[index] = entityId;
        _frames[index] = frame;
        Volatile.Write(ref _frameGenerations[index], CurrentGeneration);
        Interlocked.Increment(ref _publishedShipCount);
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

    public void MarkModified(long entityKey)
    {
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        Volatile.Write(ref _modifiedGenerations[index], CurrentGeneration);
    }

    public bool WasModified(int index) => Volatile.Read(ref _modifiedGenerations[index]) == CurrentGeneration;

    public void MarkForReap(long entityKey)
    {
        if (!TryGetFrameIndex(entityKey, out var index))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reapGenerations[index], CurrentGeneration, CurrentGeneration) == CurrentGeneration)
        {
            return;
        }

        _reapGenerations[index] = CurrentGeneration;
        var reapIndex = Interlocked.Increment(ref _pendingReapCount) - 1;
        _reapBuffer[reapIndex] = _entityIds[index];
    }

    public int CopyPendingReaps(Span<EntityId> destination)
    {
        var count = Volatile.Read(ref _pendingReapCount);
        if (destination.Length < count)
        {
            throw new ArgumentException("目标缓冲区不足以容纳待回收实体。", nameof(destination));
        }

        _reapBuffer.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    public void CompleteReaps() => Volatile.Write(ref _pendingReapCount, 0);

    public SpaceBattleSnapshot BuildPublishedSnapshot()
    {
        var ships = new List<ShipSnapshot>(PublishedShipCount);
        var generation = CurrentGeneration;
        for (var index = 1; index < _frames.Length; index++)
        {
            if (Volatile.Read(ref _frameGenerations[index]) == generation)
            {
                ships.Add(_frames[index]);
            }
        }

        return new SpaceBattleSnapshot(ships);
    }

    public void CompleteTick(long tickNumber)
    {
        var duration = Stopwatch.GetElapsedTime(_tickStartedAt);
        Interlocked.Increment(ref _completedTicks);
        _observationSink.Publish(new SimulationTickCompleted(
            tickNumber,
            PublishedShipCount,
            duration,
            tickNumber + 1 == (long)_definition.MaximumCompletedTicks ? BuildPublishedSnapshot() : null));
    }

    public TickPerformanceSnapshot GetTimingSnapshot(TickTiming timing) => timing.Snapshot();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var workerId = 0; workerId < _acquisitionTransactions.Length; workerId++)
        {
            ReleaseAcquisitionTransactionIfOwnedByCurrentThread(workerId);
        }

        Accessor.Dispose();
    }

}

internal enum SpaceBattleRandomPurpose : ulong
{
    InitialWanderHeading = 0x11A7_6C4D_2F90_B381UL,
    WanderHeading = 0x8C31_5A72_D4E6_109FUL,
    WanderSpeed = 0xE27B_4390_6D1F_A508UL,
}

internal static class SpaceBattleMath
{
    public const float MaximumWanderSpeed = 200f;
    public const float MaximumTurnRadiansPerSecond = 1f;
    public const ushort WanderFlightTicks = 50;

    private const float TwoPi = 2f * MathF.PI;
    private const float UnitFloatScale = 1f / 16_777_216f;
    private const float VectorEpsilonSquared = 1e-12f;

    public static ulong DeriveUInt64(
        ulong seed,
        long entityKey,
        ulong modeStartedTick,
        SpaceBattleRandomPurpose purpose)
    {
        var value = Mix(seed ^ 0xD1B5_4A32_9C87_E601UL);
        value = Mix(value ^ unchecked((ulong)entityKey + 0x9E37_79B9_7F4A_7C15UL));
        value = Mix(value ^ modeStartedTick);
        return Mix(value ^ (ulong)purpose);
    }

    public static float DeriveUnitFloat(
        ulong seed,
        long entityKey,
        ulong modeStartedTick,
        SpaceBattleRandomPurpose purpose)
        => (DeriveUInt64(seed, entityKey, modeStartedTick, purpose) >> 40) * UnitFloatScale;

    public static Vector3 RandomDirection(
        ulong seed,
        long entityKey,
        ulong modeStartedTick,
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

    public static float RandomWanderSpeed(ulong seed, long entityKey, ulong modeStartedTick)
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
