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
        Accessor = new PointInTimeAccessor();
    }

    public DatabaseEngine Engine { get; }

    public PointInTimeAccessor Accessor { get; }

    public int WorkerCount { get; }

    public int ChunkCount { get; }

    public int ShipCount => _definition.ShipCount;

    public uint MaximumHealth => _definition.MaximumHealth;

    public float FixedDeltaSeconds => _definition.FixedDeltaSeconds;

    public float WorldWidth => _definition.WorldWidth;
    public long CompletedTicks => Interlocked.Read(ref _completedTicks);
    public float WorldHeight => _definition.WorldHeight;

    public long TickStartedAt => Volatile.Read(ref _tickStartedAt);


    public int CurrentGeneration => Volatile.Read(ref _generation);

    public int PublishedShipCount => Volatile.Read(ref _publishedShipCount);
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
        Accessor.Dispose();
    }

}

internal static class SpaceBattleMath
{
    public static float HeadingX(long entityKey)
    {
        var angle = (Mix(entityKey) % 6283UL) * 0.001f;
        return MathF.Cos(angle);
    }

    public static float HeadingY(long entityKey)
    {
        var angle = (Mix(entityKey) % 6283UL) * 0.001f;
        return MathF.Sin(angle);
    }

    public static long NextTargetRaw(ushort archetypeId, long entityKey, int shipCount)
    {
        if (shipCount < 2)
        {
            return 0;
        }

        var targetKey = entityKey == shipCount ? 1 : entityKey + 1;
        return (targetKey << 16) | archetypeId;
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
        var x = current.MinX + headingX * speed * deltaSeconds;
        var y = current.MinY + headingY * speed * deltaSeconds;
        resultingHeadingX = headingX;
        resultingHeadingY = headingY;

        if (x < 0f)
        {
            x = 0f;
            resultingHeadingX = MathF.Abs(resultingHeadingX);
        }
        else if (x >= worldWidth)
        {
            x = MathF.BitDecrement(worldWidth);
            resultingHeadingX = -MathF.Abs(resultingHeadingX);
        }

        if (y < 0f)
        {
            y = 0f;
            resultingHeadingY = MathF.Abs(resultingHeadingY);
        }
        else if (y >= worldHeight)
        {
            y = MathF.BitDecrement(worldHeight);
            resultingHeadingY = -MathF.Abs(resultingHeadingY);
        }

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

    private static ulong Mix(long value)
    {
        var x = unchecked((ulong)value + 0x9E37_79B9_7F4A_7C15UL);
        x = unchecked((x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL);
        x = unchecked((x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL);
        return x ^ (x >> 31);
    }
}
