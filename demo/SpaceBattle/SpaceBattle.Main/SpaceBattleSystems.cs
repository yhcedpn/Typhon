using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

internal static class SpaceBattleCombat
{
    public const float WeaponRange = SpaceBattleTargeting.WeaponRange;
    public const uint WeaponDamage = 250u;
    public const ushort WeaponPeriodTicks = 15;
    public const float AttackSpeed = 200f;

    public static int FirstWeaponPhase(long entityKey)
    {
        if (entityKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityKey));
        }

        return (int)(SpaceBattleMath.DeriveUInt64(
            seed: 0,
            entityKey,
            modeStartedTick: 0,
            SpaceBattleRandomPurpose.WeaponPhase) % WeaponPeriodTicks);
    }

    public static bool IsWeaponUseTick(long entityKey, long tickNumber)
    {
        var phase = FirstWeaponPhase(entityKey);
        var tickPhase = tickNumber % WeaponPeriodTicks;
        if (tickPhase < 0)
        {
            tickPhase += WeaponPeriodTicks;
        }

        return tickPhase == phase;
    }

    public static uint DamageForDistance(double distanceSquared) =>
        distanceSquared >= 0d && distanceSquared <= WeaponRange * WeaponRange
            ? WeaponDamage
            : 0u;
}

internal static class SpaceBattlePhases
{
    public static readonly Phase Publish = new("Publish");
    public static readonly Phase Behavior = new("Behavior");
    public static readonly Phase Damage = new("Damage");
    public static readonly Phase Movement = new("Movement");
    public static readonly Phase Reap = new("Reap");
    public static readonly Phase Observe = new("Observe");
}

internal sealed class FramePrepareSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;

    public FramePrepareSystem(SpaceBattleSimulationState state)
    {
        _state = state;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("FramePrepare")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Publish)
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        if (!_state.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        _state.PrepareTick(ctx.TickNumber);
        _state.Telemetry.RecordSystem(
            SpaceBattleSystemMetricId.FramePrepare,
            startedAt,
            _state.PublishedShipCount,
            ctx.WorkerId);
    }
}

internal sealed class PublishSystem : ShipChunkSystem
{
    public PublishSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }

    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Publish", "FramePrepare")
        .Phase(SpaceBattlePhases.Publish)
        .Reads<Hull, Motion, Vitals, Targeting, Behavior>();

    protected override void Execute(TickContext ctx)
    {
        if (!State.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        foreach (var cluster in clusters)
        {
            var hulls = cluster.GetReadOnlySpan(Ship.Hull);
            var motions = cluster.GetReadOnlySpan(Ship.Motion);
            var vitalsSpan = cluster.GetReadOnlySpan(Ship.Vitals);
            var targetings = cluster.GetReadOnlySpan(Ship.Targeting);
            var behaviors = cluster.GetReadOnlySpan(Ship.Behavior);
            foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
            {
                var entityId = cluster.GetEntityId(slot);
                var entityKey = entityId.EntityKey;
                if (entityKey == 0)
                {
                    // 并行 fence 的跨 cell 迁移会在本 tick 清空源槽：occupancy 快照之后 EntityKey 已被清零。
                    // 引擎自己的迁移路径以 entityPK==0 作为 destroyed-in-flight 判别（DatabaseEngine.ClusterMigration），
                    // 这里采用同一范式跳过；该实体会在目标 cluster 被发布，本逻辑帧不产生该舰船帧。
                    continue;
                }
                var vitals = vitalsSpan[slot];
                if (vitals.CurrentHealth == 0)
                {
                    // bootstrap 的批量 spawn 在首个 cluster fence 可能暂读零填充；本逻辑帧尚未进入伤害阶段，按存活事实发布。
                    vitals = new Vitals { CurrentHealth = State.MaximumHealth };
                }

                State.Frames.Publish(entityId, new ShipSnapshot(
                    entityKey,
                    hulls[slot],
                    motions[slot],
                    vitals,
                    targetings[slot],
                    behaviors[slot]));
            }
        }

        State.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Publish, startedAt, State.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class BehaviorSystem : ShipChunkSystem
{
    private const int MaximumClusterSize = 64;

    public BehaviorSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }

    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Behavior", "Publish")
        .Phase(SpaceBattlePhases.Behavior)
        .Writes<Motion, Targeting, Behavior>();

    protected override void Execute(TickContext ctx)
    {
        if (!State.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        Span<ShipSnapshot> trackingSources = stackalloc ShipSnapshot[MaximumClusterSize];
        Span<int> trackingSlots = stackalloc int[MaximumClusterSize];
        Span<TargetingResult> trackingResults = stackalloc TargetingResult[MaximumClusterSize];
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var motions = cluster.GetSpan(Ship.Motion);
            var targetings = cluster.GetSpan(Ship.Targeting);
            var behaviors = cluster.GetSpan(Ship.Behavior);
            if (motions.Length > MaximumClusterSize)
            {
                throw new InvalidOperationException("SpaceBattle cluster 超出锁定临时缓冲容量。");
            }

            var trackingCount = 0;
            var clusterDirty = false;

            foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
            {
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.Frames.TryGetIndex(entityKey, out var frameIndex))
                {
                    continue;
                }

                ref readonly var frame = ref State.Frames.GetPublished(frameIndex);
                if (frame.Vitals.CurrentHealth == 0)
                {
                    continue;
                }

                var mode = (BehaviorMode)frame.Behavior.Mode;
                if (mode == BehaviorMode.Tracking)
                {
                    trackingSources[trackingCount] = frame;
                    trackingSlots[trackingCount] = slot;
                    trackingCount++;
                    continue;
                }

                if (mode == BehaviorMode.Attacking)
                {
                    EmitWeaponUse(ctx, frame);
                }

                var nextMotion = frame.Motion;
                var nextTargeting = frame.Targeting;
                var nextBehavior = frame.Behavior;
                State.BehaviorModes.Advance(
                    ctx.TickNumber,
                    frame,
                    ref nextMotion,
                    ref nextTargeting,
                    ref nextBehavior);
                if (WriteBehaviorChanges(
                        ctx.WorkerId,
                        entityKey,
                        slot,
                        frame,
                        nextMotion,
                        nextTargeting,
                        nextBehavior,
                        motions,
                        targetings,
                        behaviors))
                {
                    clusterDirty = true;
                }
            }

            if (trackingCount > 0)
            {
                SpaceBattleTargeting.FindNearestBatch(
                    State.AcquisitionTransactions.Get(ctx.WorkerId, ctx.TickNumber),
                    State,
                    trackingSources[..trackingCount],
                    trackingResults[..trackingCount],
                    out var targetingMetrics);
                State.Telemetry.RecordTargeting(ctx.WorkerId, targetingMetrics);

                for (var index = 0; index < trackingCount; index++)
                {
                    var slot = trackingSlots[index];
                    var source = trackingSources[index];
                    var nextMotion = source.Motion;
                    var nextTargeting = source.Targeting;
                    var nextBehavior = source.Behavior;
                    SpaceBattleBehaviorModes.ApplyAcquiredTarget(
                        ctx.TickNumber,
                        trackingResults[index],
                        ref nextMotion,
                        ref nextTargeting,
                        ref nextBehavior);
                    if (WriteBehaviorChanges(
                            ctx.WorkerId,
                            source.EntityKey,
                            slot,
                            source,
                            nextMotion,
                            nextTargeting,
                            nextBehavior,
                            motions,
                            targetings,
                            behaviors))
                    {
                        clusterDirty = true;
                    }
                }
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }

        if (ctx.TickNumber + 1 == (long)State.MaximumCompletedTicks)
        {
            State.AcquisitionTransactions.ReleaseIfOwnedByCurrentThread(ctx.WorkerId);
        }

        State.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Behavior, startedAt, State.PublishedShipCount, ctx.WorkerId);
    }

    private void EmitWeaponUse(TickContext ctx, in ShipSnapshot source)
    {
        if (!SpaceBattleCombat.IsWeaponUseTick(
                source.EntityKey,
                ctx.TickNumber - source.Behavior.ModeStartedTick))
        {
            return;
        }

        State.Telemetry.RecordWeaponUse();
        if (!SpaceBattleTargeting.TryReadTarget(State, source, out var target, out var distanceSquared))
        {
            return;
        }

        var damage = SpaceBattleCombat.DamageForDistance(distanceSquared);
        if (damage != 0)
        {
            State.Telemetry.RecordInRangeAttack();
            State.Settlement.RecordIncomingDamage(ctx.WorkerId, target.EntityKey, damage);
        }
    }

    private bool WriteBehaviorChanges(
        int workerId,
        long entityKey,
        int slot,
        in ShipSnapshot frame,
        in Motion nextMotion,
        in Targeting nextTargeting,
        in Behavior nextBehavior,
        Span<Motion> motions,
        Span<Targeting> targetings,
        Span<Behavior> behaviors)
    {
        if (MotionEquals(frame.Motion, nextMotion) &&
            TargetingEquals(frame.Targeting, nextTargeting) &&
            BehaviorEquals(frame.Behavior, nextBehavior))
        {
            return false;
        }

        motions[slot] = nextMotion;
        targetings[slot] = nextTargeting;
        behaviors[slot] = nextBehavior;
        State.Frames.UpdateBehavior(entityKey, nextMotion, nextTargeting, nextBehavior);
        State.MarkModified(workerId, entityKey);
        return true;
    }

    private static bool MotionEquals(in Motion left, in Motion right) =>
        left.CurrentHeadingX == right.CurrentHeadingX &&
        left.CurrentHeadingY == right.CurrentHeadingY &&
        left.CurrentHeadingZ == right.CurrentHeadingZ &&
        left.TargetHeadingX == right.TargetHeadingX &&
        left.TargetHeadingY == right.TargetHeadingY &&
        left.TargetHeadingZ == right.TargetHeadingZ &&
        left.Speed == right.Speed &&
        left.RemainingTurnRadians == right.RemainingTurnRadians;

    private static bool TargetingEquals(in Targeting left, in Targeting right) =>
        left.TargetRawEntityId == right.TargetRawEntityId;

    private static bool BehaviorEquals(in Behavior left, in Behavior right) =>
        left.Mode == right.Mode &&
        left.Phase == right.Phase &&
        left.TicksRemaining == right.TicksRemaining &&
        left.ModeStartedTick == right.ModeStartedTick;
}

internal sealed class DamageSystem : ShipChunkSystem
{
    public DamageSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }

    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Damage", "Behavior")
        .Phase(SpaceBattlePhases.Damage)
        .Writes<Vitals>();

    protected override void Execute(TickContext ctx)
    {
        if (!State.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var vitals = cluster.GetSpan(Ship.Vitals);
            var clusterDirty = false;
            foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
            {
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.Frames.TryGetIndex(entityKey, out _))
                {
                    continue;
                }

                var incomingDamage = State.Settlement.ReduceIncomingDamage(entityKey);
                if (incomingDamage == 0)
                {
                    continue;
                }

                var currentHealth = vitals[slot].CurrentHealth;
                var nextHealth = incomingDamage >= currentHealth
                    ? 0u
                    : currentHealth - incomingDamage;
                if (nextHealth == currentHealth)
                {
                    continue;
                }
                vitals[slot] = new Vitals { CurrentHealth = nextHealth };
                State.Frames.UpdateHealth(entityKey, nextHealth);
                State.Telemetry.RecordDamage(incomingDamage);
                State.MarkModified(ctx.WorkerId, entityKey);
                clusterDirty = true;
                if (nextHealth == 0)
                {
                    State.Telemetry.RecordDeath();
                    // 本 tick 后续 Movement 通过已更新的 frame health 跳过该飞船，Reap 再统一销毁。
                    State.Settlement.MarkForReap(ctx.WorkerId, entityKey);
                }
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }
        State.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Damage, startedAt, State.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class DamageCleanupSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;

    public DamageCleanupSystem(SpaceBattleSimulationState state)
    {
        _state = state;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("DamageCleanup")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Damage)
        .After("Damage")
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        if (!_state.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        _state.Settlement.ClearIncomingDamage();
        _state.Telemetry.RecordSystem(SpaceBattleSystemMetricId.DamageCleanup, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class MovementSystem : ShipChunkSystem
{
    public MovementSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }


    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Movement", "Damage")
        .Phase(SpaceBattlePhases.Movement)
        .Writes<Hull, Motion>();

    protected override void Execute(TickContext ctx)
    {
        if (!State.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var hulls = cluster.GetSpan(Ship.Hull);
            var motions = cluster.GetSpan(Ship.Motion);
            var clusterDirty = false;
            foreach (var slot in new SpaceBattleOccupiedSlots(cluster.OccupancyBits))
            {
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.Frames.TryGetIndex(entityKey, out var frameIndex))
                {
                    continue;
                }

                ref readonly var frame = ref State.Frames.GetPublished(frameIndex);
                if (!State.BehaviorModes.TryMove(
                        frame,
                        motions[slot],
                        State.Settlement.IsPendingReap(entityKey),
                        out var nextHull,
                        out var nextMotion))
                {
                    continue;
                }

                hulls[slot] = nextHull;
                motions[slot] = nextMotion;
                State.Frames.UpdateMovement(entityKey, nextHull, nextMotion);
                State.MarkModified(ctx.WorkerId, entityKey);
                clusterDirty = true;
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }
        State.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Movement, startedAt, State.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class ReapSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;
    private readonly EntityId[] _reapBuffer;

    public ReapSystem(SpaceBattleSimulationState state)
    {
        _state = state;
        _reapBuffer = new EntityId[state.ShipCount];
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("Reap")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Reap)
        .After("Movement")
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        if (!_state.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var count = _state.Settlement.CopyPendingReaps(_reapBuffer);
        if (count == 0)
        {
            _state.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Reap, startedAt, _state.PublishedShipCount, ctx.WorkerId);
            return;
        }

        using var transaction = _state.Engine.CreateQuickTransaction();
        transaction.DestroyBatch(_reapBuffer.AsSpan(0, count));
        if (!transaction.Commit())
        {
            throw new InvalidOperationException("SpaceBattle 回收事务提交失败。");
        }

        _state.Settlement.CompleteReaps();
        _state.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Reap, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}


internal sealed class ObserveSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;
    private readonly TickTiming _timing;

    public ObserveSystem(SpaceBattleSimulationState state, TickTiming timing)
    {
        _state = state;
        _timing = timing;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("Observe")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Observe)
        .After("Reap")
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        if (!_state.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        _state.CompleteTick(ctx.TickNumber, _timing);
        _state.Telemetry.RecordSystem(SpaceBattleSystemMetricId.Observe, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}


/// <summary>零分配枚举一个 cluster 占用位中的实体槽。</summary>
internal ref struct SpaceBattleOccupiedSlots
{
    private ulong _remaining;

    public SpaceBattleOccupiedSlots(ulong occupancyBits)
    {
        _remaining = occupancyBits;
        Current = -1;
    }

    public int Current { get; private set; }

    public readonly SpaceBattleOccupiedSlots GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_remaining == 0)
        {
            return false;
        }

        Current = BitOperations.TrailingZeroCount(_remaining);
        _remaining &= _remaining - 1;
        return true;
    }
}

internal abstract class ShipChunkSystem : ChunkedCallbackSystem
{
    protected ShipChunkSystem(SpaceBattleSimulationState state)
    {
        State = state;
    }

    protected SpaceBattleSimulationState State { get; }


    protected ClusterEnumerator<Ship> OpenClusters(TickContext ctx)
    {
        var accessor = State.GetWorkerAccessor(ctx.WorkerId);
        var range = State.GetClusterRange(accessor, ctx.ChunkIndex, ctx.ChunkCount);
        return accessor.GetClusterEnumerator<Ship>(range.Start, range.End);
    }

    protected SystemBuilder ConfigureChunked(SystemBuilder builder, string name, string after = null)
    {
        builder
            .Name(name)
            .Priority(SystemPriority.Critical)
            .CanShed(false)
            .ChunkedParallel(State.ChunkCount);
        if (after != null)
        {
            builder.After(after);
        }

        return builder;
    }
}
