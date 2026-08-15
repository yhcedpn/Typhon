using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal sealed class SpaceBattleSimulationState : IDisposable
{
    private readonly object _tickGate = new();
    private readonly SimulationDefinition _definition;
    private readonly ISpaceBattleObservationSink _observationSink;
    private readonly Action _tickCompleted;
    private readonly bool _enforceMaximumCompletedTicks;
    private long _preparedTick = -1;
    private long _tickStartedAt;
    private long _lastCompletedTickStartedAt;
    private int _lastCompletedRemainingShips;
    private long _completedTicks;
    private bool _disposed;

    public SpaceBattleSimulationState(
        DatabaseEngine engine,
        SimulationDefinition definition,
        ISpaceBattleObservationSink observationSink,
        int workerCount,
        Action tickCompleted = null,
        bool enforceMaximumCompletedTicks = false)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _observationSink = observationSink ?? throw new ArgumentNullException(nameof(observationSink));
        _tickCompleted = tickCompleted;
        _enforceMaximumCompletedTicks = enforceMaximumCompletedTicks;
        WorkerCount = workerCount;
        ChunkCount = Math.Max(1, workerCount * 2);
        Frames = new SpaceBattleFrameStore(definition.ShipCount);
        Settlement = new SpaceBattleCombatSettlement(Frames, workerCount);
        AcquisitionTransactions = new SpaceBattleAcquisitionTransactions(engine, workerCount);
        BehaviorModes = new SpaceBattleBehaviorModes(this);
        Telemetry = new SpaceBattleTelemetryState(workerCount);
        Accessor = new PointInTimeAccessor();
    }

    public DatabaseEngine Engine { get; }

    public PointInTimeAccessor Accessor { get; }

    public SpaceBattleFrameStore Frames { get; }

    public SpaceBattleCombatSettlement Settlement { get; }

    public SpaceBattleAcquisitionTransactions AcquisitionTransactions { get; }
    public SpaceBattleBehaviorModes BehaviorModes { get; }

    public SpaceBattleTelemetryState Telemetry { get; }

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

    public bool IsTickInFlight =>
        Volatile.Read(ref _tickStartedAt) != Volatile.Read(ref _lastCompletedTickStartedAt);

    public int PublishedShipCount => Frames.PublishedShipCount;

    public bool ShouldExecuteTick(long tickNumber) =>
        !_enforceMaximumCompletedTicks || (tickNumber >= 0 && (ulong)tickNumber < MaximumCompletedTicks);

    public int PublishedAliveShipCount => Frames.PublishedAliveShipCount;

    public int LastCompletedRemainingShips => Volatile.Read(ref _lastCompletedRemainingShips);

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
            Frames.BeginTick();
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

    public void MarkModified(int workerId, long entityKey)
    {
        if ((uint)workerId >= (uint)WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var startedAt = Stopwatch.GetTimestamp();
        if (Frames.MarkModified(entityKey))
        {
            Telemetry.RecordDirtyMark(Stopwatch.GetTimestamp() - startedAt, workerId);
        }
    }

    public void CompleteTick(long tickNumber, TickTiming timing = null)
    {
        var duration = Stopwatch.GetElapsedTime(_tickStartedAt);
        timing?.RecordTick(duration, _tickStartedAt);
        Volatile.Write(ref _lastCompletedRemainingShips, PublishedAliveShipCount);
        SpaceBattleTelemetrySnapshot telemetry = null;
        if (SpaceBattleTelemetrySampling.IsSampleTick(tickNumber))
        {
            telemetry = Telemetry.BuildSnapshot(tickNumber, timing, Frames);
            telemetry = telemetry with
            {
                TickPerformance = telemetry.TickPerformance with
                {
                    Overload = duration.TotalSeconds > FixedDeltaSeconds ? "overload" : "normal",
                },
            };
        }

        _observationSink.Publish(new SimulationTickCompleted(
            tickNumber,
            PublishedShipCount,
            duration,
            tickNumber + 1 == (long)_definition.MaximumCompletedTicks
                ? Frames.BuildPublishedSnapshot()
                : null)
        {
            Telemetry = telemetry,
        });
        Volatile.Write(ref _lastCompletedTickStartedAt, _tickStartedAt);
        Interlocked.Increment(ref _completedTicks);
        _tickCompleted?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AcquisitionTransactions.ReleaseAllAfterRuntimeStop();
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
        var value = SplitMix64.Mix(seed ^ 0xD1B5_4A32_9C87_E601UL);
        value = SplitMix64.Mix(value ^ unchecked((ulong)entityKey + 0x9E37_79B9_7F4A_7C15UL));
        value = SplitMix64.Mix(value ^ unchecked((ulong)modeStartedTick));
        return SplitMix64.Mix(value ^ (ulong)purpose);
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
        Vector3 heading,
        float speed,
        float deltaSeconds,
        float worldWidth,
        float worldHeight,
        float worldDepth,
        out Vector3 resultingHeading)
    {
        var x = ReflectCoordinate(
            current.MinX,
            (double)heading.X * speed * deltaSeconds,
            worldWidth,
            out var xDirection);
        var y = ReflectCoordinate(
            current.MinY,
            (double)heading.Y * speed * deltaSeconds,
            worldHeight,
            out var yDirection);
        var z = ReflectCoordinate(
            current.MinZ,
            (double)heading.Z * speed * deltaSeconds,
            worldDepth,
            out var zDirection);
        resultingHeading = new Vector3(
            heading.X * xDirection,
            heading.Y * yDirection,
            heading.Z * zDirection);
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

}
