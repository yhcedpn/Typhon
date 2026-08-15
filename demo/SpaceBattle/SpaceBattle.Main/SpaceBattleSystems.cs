using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

internal static class SpaceBattleCombat
{
    public const float WeaponRange = SpaceBattleTargeting.WeaponRange;
    public const uint WeaponDamage = 250u;
    public const ushort WeaponPeriodTicks = 15;
    public const float WeaponSpeed = SpaceBattleTargeting.ApproachSpeed;

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
        var startedAt = Stopwatch.GetTimestamp();
        _state.PrepareTick(ctx.TickNumber);
        _state.RecordSystemMetric(
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
        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        foreach (var cluster in clusters)
        {
            var hulls = cluster.GetReadOnlySpan(Ship.Hull);
            var motions = cluster.GetReadOnlySpan(Ship.Motion);
            var vitalsSpan = cluster.GetReadOnlySpan(Ship.Vitals);
            var targetings = cluster.GetReadOnlySpan(Ship.Targeting);
            var behaviors = cluster.GetReadOnlySpan(Ship.Behavior);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityId = cluster.GetEntityId(slot);
                var entityKey = entityId.EntityKey;
                var vitals = vitalsSpan[slot];
                if (vitals.CurrentHealth == 0)
                {
                    // bootstrap 的批量 spawn 在首个 cluster fence 可能暂读零填充；本逻辑帧尚未进入伤害阶段，按存活事实发布。
                    vitals = new Vitals { CurrentHealth = State.MaximumHealth };
                }

                State.PublishFrame(entityId, new ShipSnapshot(
                    entityKey,
                    hulls[slot],
                    motions[slot],
                    vitals,
                    targetings[slot],
                    behaviors[slot]));
            }
        }

        State.RecordSystemMetric(SpaceBattleSystemMetricId.Publish, startedAt, State.PublishedShipCount, ctx.WorkerId);
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
            var bits = cluster.OccupancyBits;
            var clusterDirty = false;

            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.TryGetFrameIndex(entityKey, out var frameIndex))
                {
                    continue;
                }

                ref readonly var frame = ref State.GetFrame(frameIndex);
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
                switch (mode)
                {
                    case BehaviorMode.Wandering:
                        AdvanceWandering(
                            ctx.TickNumber,
                            entityKey,
                            ref nextMotion,
                            ref nextBehavior,
                            (BehaviorPhase)frame.Behavior.Phase);
                        break;
                    case BehaviorMode.Turning:
                        AdvanceTurning(
                            ctx.TickNumber,
                            entityKey,
                            ref nextMotion,
                            ref nextBehavior,
                            (BehaviorPhase)frame.Behavior.Phase);
                        break;
                    case BehaviorMode.Approaching:
                    case BehaviorMode.Attacking:
                        AdvanceTargetedMode(
                            ctx.TickNumber,
                            frame,
                            ref nextMotion,
                            ref nextTargeting,
                            ref nextBehavior);
                        break;
                }
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
                    State.GetAcquisitionTransaction(ctx.WorkerId, ctx.TickNumber),
                    State,
                    trackingSources[..trackingCount],
                    trackingResults[..trackingCount],
                    out var targetingMetrics);
                State.RecordTargetingMetrics(ctx.WorkerId, targetingMetrics);

                for (var index = 0; index < trackingCount; index++)
                {
                    var slot = trackingSlots[index];
                    var source = trackingSources[index];
                    var nextMotion = source.Motion;
                    var nextTargeting = source.Targeting;
                    var nextBehavior = source.Behavior;
                    ApplyAcquiredTarget(
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
            State.ReleaseAcquisitionTransactionIfOwnedByCurrentThread(ctx.WorkerId);
        }

        State.RecordSystemMetric(SpaceBattleSystemMetricId.Behavior, startedAt, State.PublishedShipCount, ctx.WorkerId);
    }

    private void EmitWeaponUse(TickContext ctx, in ShipSnapshot source)
    {
        if (!SpaceBattleCombat.IsWeaponUseTick(
                source.EntityKey,
                ctx.TickNumber - source.Behavior.ModeStartedTick))
        {
            return;
        }

        State.RecordWeaponUse();
        if (!SpaceBattleTargeting.TryReadTarget(State, source, out var target, out var distanceSquared))
        {
            return;
        }

        var damage = SpaceBattleCombat.DamageForDistance(distanceSquared);
        if (damage != 0)
        {
            State.RecordInRangeAttack();
            State.RecordIncomingDamage(ctx.WorkerId, target.EntityKey, damage);
        }
    }

    private void ApplyAcquiredTarget(
        long tickNumber,
        in TargetingResult result,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        if (result.EntityId.IsNull)
        {
            // 没有候选时保留原速度和航向，下一 tick 继续重试。
            targeting.TargetEntityId = 0;
            return;
        }

        targeting.TargetEntityId = SpaceBattleTargeting.PackRaw(result.EntityId);
        behavior.Mode = (byte)(result.DistanceSquared <= SpaceBattleCombat.WeaponRange * SpaceBattleCombat.WeaponRange
            ? BehaviorMode.Attacking
            : BehaviorMode.Approaching);
        behavior.Phase = (byte)BehaviorPhase.Ready;
        behavior.TicksRemaining = 0;
        behavior.ModeStartedTick = tickNumber + 1;
        motion.Speed = SpaceBattleCombat.WeaponSpeed;
    }

    private void AdvanceTargetedMode(
        long tickNumber,
        in ShipSnapshot source,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        if (!SpaceBattleTargeting.TryReadTarget(State, source, out _, out var distanceSquared))
        {
            // 目标失效的这一帧清除锁定，Movement 仍用失效前的速度完成一次移动。
            targeting.TargetEntityId = 0;
            behavior.Mode = (byte)BehaviorMode.Turning;
            behavior.Phase = (byte)BehaviorPhase.Ready;
            behavior.TicksRemaining = 0;
            behavior.ModeStartedTick = tickNumber + 1;
            motion.RemainingTurnRadians = 0f;
            return;
        }

        motion.Speed = SpaceBattleCombat.WeaponSpeed;
        if ((BehaviorMode)source.Behavior.Mode == BehaviorMode.Approaching &&
            distanceSquared <= SpaceBattleCombat.WeaponRange * SpaceBattleCombat.WeaponRange)
        {
            behavior.Mode = (byte)BehaviorMode.Attacking;
            behavior.ModeStartedTick = tickNumber + 1;
        }

        behavior.Phase = (byte)BehaviorPhase.Ready;
        behavior.TicksRemaining = 0;
    }

    private void AdvanceWandering(
        long tickNumber,
        long entityKey,
        ref Motion motion,
        ref Behavior behavior,
        BehaviorPhase phase)
    {
        var turnStep = SpaceBattleMath.MaximumTurnRadiansPerSecond * State.FixedDeltaSeconds;
        switch (phase)
        {
            case BehaviorPhase.Ready:
            {
                var hasCurrentHeading = motion.CurrentHeadingX != 0f ||
                                        motion.CurrentHeadingY != 0f ||
                                        motion.CurrentHeadingZ != 0f;
                var purpose = hasCurrentHeading
                    ? SpaceBattleRandomPurpose.WanderHeading
                    : SpaceBattleRandomPurpose.InitialWanderHeading;
                var target = SpaceBattleMath.RandomDirection(State.Seed, entityKey, behavior.ModeStartedTick, purpose);
                var speed = SpaceBattleMath.RandomWanderSpeed(State.Seed, entityKey, behavior.ModeStartedTick);
                motion.TargetHeadingX = target.X;
                motion.TargetHeadingY = target.Y;
                motion.TargetHeadingZ = target.Z;
                motion.Speed = speed;

                if (!hasCurrentHeading)
                {
                    motion.CurrentHeadingX = target.X;
                    motion.CurrentHeadingY = target.Y;
                    motion.CurrentHeadingZ = target.Z;
                    motion.RemainingTurnRadians = 0f;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                    break;
                }

                var current = new Vector3(
                    motion.CurrentHeadingX,
                    motion.CurrentHeadingY,
                    motion.CurrentHeadingZ);
                var angle = SpaceBattleMath.AngleBetween(current, target);
                motion.RemainingTurnRadians = angle;
                if (angle <= turnStep)
                {
                    motion.CurrentHeadingX = target.X;
                    motion.CurrentHeadingY = target.Y;
                    motion.CurrentHeadingZ = target.Z;
                    motion.RemainingTurnRadians = 0f;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                }
                else
                {
                    behavior.Phase = (byte)BehaviorPhase.Aligning;
                    behavior.TicksRemaining = 0;
                }

                break;
            }
            case BehaviorPhase.Aligning:
            {
                var current = new Vector3(
                    motion.CurrentHeadingX,
                    motion.CurrentHeadingY,
                    motion.CurrentHeadingZ);
                var target = new Vector3(
                    motion.TargetHeadingX,
                    motion.TargetHeadingY,
                    motion.TargetHeadingZ);
                var angle = SpaceBattleMath.AngleBetween(current, target);
                motion.RemainingTurnRadians = angle;
                if (angle <= turnStep)
                {
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.WanderFlightTicks;
                }

                break;
            }
            case BehaviorPhase.Flying:
                if (behavior.TicksRemaining == 0)
                {
                    behavior.Mode = (byte)BehaviorMode.Tracking;
                    behavior.Phase = (byte)BehaviorPhase.Ready;
                    behavior.ModeStartedTick = tickNumber + 1;
                }
                else
                {
                    behavior.TicksRemaining--;
                }

                break;
        }
    }

    private void AdvanceTurning(
        long tickNumber,
        long entityKey,
        ref Motion motion,
        ref Behavior behavior,
        BehaviorPhase phase)
    {
        var turnStep = SpaceBattleMath.MaximumTurnRadiansPerSecond * State.FixedDeltaSeconds;
        switch (phase)
        {
            case BehaviorPhase.Ready:
            {
                var current = new Vector3(
                    motion.CurrentHeadingX,
                    motion.CurrentHeadingY,
                    motion.CurrentHeadingZ);
                var target = SpaceBattleMath.RandomTurnTarget(
                    State.Seed,
                    entityKey,
                    behavior.ModeStartedTick,
                    current,
                    out var turnRadians);
                motion.TargetHeadingX = target.X;
                motion.TargetHeadingY = target.Y;
                motion.TargetHeadingZ = target.Z;
                motion.RemainingTurnRadians = turnRadians;
                motion.Speed = 0f;
                behavior.Phase = (byte)BehaviorPhase.Aligning;
                behavior.TicksRemaining = 0;
                break;
            }
            case BehaviorPhase.Aligning:
                if (!float.IsFinite(motion.RemainingTurnRadians) || motion.RemainingTurnRadians <= turnStep)
                {
                    motion.RemainingTurnRadians = MathF.Max(0f, motion.RemainingTurnRadians);
                    motion.Speed = SpaceBattleMath.EvasiveSpeed;
                    behavior.Phase = (byte)BehaviorPhase.Flying;
                    behavior.TicksRemaining = SpaceBattleMath.EvasiveFlightTicks;
                }

                break;
            case BehaviorPhase.Flying:
                if (behavior.TicksRemaining == 0)
                {
                    behavior.Mode = (byte)BehaviorMode.Wandering;
                    behavior.Phase = (byte)BehaviorPhase.Ready;
                    behavior.ModeStartedTick = tickNumber + 1;
                }
                else
                {
                    behavior.TicksRemaining--;
                }

                break;
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
        State.UpdateFrameBehavior(entityKey, nextMotion, nextTargeting, nextBehavior);
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
        left.TargetEntityId == right.TargetEntityId;

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
        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var vitals = cluster.GetSpan(Ship.Vitals);
            var bits = cluster.OccupancyBits;
            var clusterDirty = false;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.TryGetFrameIndex(entityKey, out _))
                {
                    continue;
                }

                var incomingDamage = State.ReduceIncomingDamage(entityKey);
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
                State.UpdateFrameHealth(entityKey, nextHealth);
                State.RecordDamage(incomingDamage);
                State.MarkModified(ctx.WorkerId, entityKey);
                clusterDirty = true;
                if (nextHealth == 0)
                {
                    State.RecordDeath();
                    // 本 tick 后续 Movement 通过已更新的 frame health 跳过该飞船，Reap 再统一销毁。
                    State.MarkForReap(ctx.WorkerId, entityKey);
                }
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }
        State.RecordSystemMetric(SpaceBattleSystemMetricId.Damage, startedAt, State.PublishedShipCount, ctx.WorkerId);
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
        var startedAt = Stopwatch.GetTimestamp();
        _state.ClearIncomingDamage();
        _state.RecordSystemMetric(SpaceBattleSystemMetricId.DamageCleanup, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class MovementSystem : ShipChunkSystem
{
    public MovementSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }

    internal static bool CanMove(in ShipSnapshot frame, bool pendingReap) =>
        frame.Vitals.CurrentHealth != 0 && !pendingReap;

    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Movement", "Damage")
        .Phase(SpaceBattlePhases.Movement)
        .Writes<Hull, Motion>();

    protected override void Execute(TickContext ctx)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var hulls = cluster.GetSpan(Ship.Hull);
            var motions = cluster.GetSpan(Ship.Motion);
            var bits = cluster.OccupancyBits;
            var clusterDirty = false;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityKey = cluster.GetEntityId(slot).EntityKey;
                if (!State.TryGetFrameIndex(entityKey, out var frameIndex))
                {
                    continue;
                }

                ref readonly var frame = ref State.GetFrame(frameIndex);
                if (!CanMove(frame, State.IsPendingReap(entityKey)))
                {
                    continue;
                }

                var mode = (BehaviorMode)frame.Behavior.Mode;
                var phase = (BehaviorPhase)frame.Behavior.Phase;
                if (mode == BehaviorMode.Wandering && phase == BehaviorPhase.Aligning)
                {
                    var current = new Vector3(
                        frame.Motion.CurrentHeadingX,
                        frame.Motion.CurrentHeadingY,
                        frame.Motion.CurrentHeadingZ);
                    var target = new Vector3(
                        frame.Motion.TargetHeadingX,
                        frame.Motion.TargetHeadingY,
                        frame.Motion.TargetHeadingZ);
                    var turned = SpaceBattleMath.TurnTowards(
                        current,
                        target,
                        SpaceBattleMath.MaximumTurnRadiansPerSecond * State.FixedDeltaSeconds,
                        out var remainingRadians);
                    var nextMotion = frame.Motion;
                    nextMotion.CurrentHeadingX = turned.X;
                    nextMotion.CurrentHeadingY = turned.Y;
                    nextMotion.CurrentHeadingZ = turned.Z;
                    nextMotion.RemainingTurnRadians = remainingRadians;
                    motions[slot] = nextMotion;
                    State.UpdateFrameMovement(entityKey, frame.Hull, nextMotion);
                    State.MarkModified(ctx.WorkerId, entityKey);
                    clusterDirty = true;
                    continue;
                }

                if (mode == BehaviorMode.Turning && phase == BehaviorPhase.Aligning)
                {
                    var current = new Vector3(
                        frame.Motion.CurrentHeadingX,
                        frame.Motion.CurrentHeadingY,
                        frame.Motion.CurrentHeadingZ);
                    var target = new Vector3(
                        frame.Motion.TargetHeadingX,
                        frame.Motion.TargetHeadingY,
                        frame.Motion.TargetHeadingZ);
                    var turned = SpaceBattleMath.TurnAlongGreatCircle(
                        current,
                        target,
                        frame.Motion.RemainingTurnRadians,
                        SpaceBattleMath.MaximumTurnRadiansPerSecond * State.FixedDeltaSeconds,
                        out var remainingRadians);
                    var nextMotion = motions[slot];
                    nextMotion.CurrentHeadingX = turned.X;
                    nextMotion.CurrentHeadingY = turned.Y;
                    nextMotion.CurrentHeadingZ = turned.Z;
                    nextMotion.RemainingTurnRadians = remainingRadians;
                    motions[slot] = nextMotion;
                    State.UpdateFrameMovement(entityKey, frame.Hull, nextMotion);
                    State.MarkModified(ctx.WorkerId, entityKey);
                    clusterDirty = true;
                    continue;
                }

                var isTargeted = mode is BehaviorMode.Approaching or BehaviorMode.Attacking;
                var shouldMove = mode == BehaviorMode.Tracking ||
                                 isTargeted ||
                                 (mode == BehaviorMode.Wandering &&
                                  phase == BehaviorPhase.Flying &&
                                  frame.Behavior.TicksRemaining > 0) ||
                                 (mode == BehaviorMode.Turning
                                  && phase == BehaviorPhase.Flying
                                  && frame.Behavior.TicksRemaining > 0);
                if (!shouldMove)
                {
                    continue;
                }

                var currentMotion = motions[slot];
                var heading = new Vector3(
                    currentMotion.CurrentHeadingX,
                    currentMotion.CurrentHeadingY,
                    currentMotion.CurrentHeadingZ);
                var nextMotionForTurn = currentMotion;
                if (isTargeted && SpaceBattleTargeting.TryReadTarget(State, frame, out var targetFrame, out _))
                {
                    var direction = SpaceBattleTargeting.PositionOf(targetFrame) - SpaceBattleTargeting.PositionOf(frame);
                    if (direction.LengthSquared() > 1e-12f)
                    {
                        direction = Vector3.Normalize(direction);
                    }

                    var turned = SpaceBattleMath.TurnTowards(
                        heading,
                        direction,
                        SpaceBattleMath.MaximumTurnRadiansPerSecond * State.FixedDeltaSeconds,
                        out var remainingRadians);
                    heading = turned;
                    nextMotionForTurn.CurrentHeadingX = turned.X;
                    nextMotionForTurn.CurrentHeadingY = turned.Y;
                    nextMotionForTurn.CurrentHeadingZ = turned.Z;
                    nextMotionForTurn.RemainingTurnRadians = remainingRadians;
                }

                var movementSpeed = mode == BehaviorMode.Tracking
                    ? frame.Motion.Speed
                    : currentMotion.Speed;
                var bounds = SpaceBattleMath.MoveBounds(
                    frame.Hull.Bounds,
                    heading,
                    movementSpeed,
                    State.FixedDeltaSeconds,
                    State.WorldWidth,
                    State.WorldHeight,
                    State.WorldDepth,
                    out var resultingHeading);
                hulls[slot] = new Hull { Bounds = bounds };
                nextMotionForTurn.CurrentHeadingX = resultingHeading.X;
                nextMotionForTurn.CurrentHeadingY = resultingHeading.Y;
                nextMotionForTurn.CurrentHeadingZ = resultingHeading.Z;
                nextMotionForTurn.TargetHeadingX = resultingHeading.X;
                nextMotionForTurn.TargetHeadingY = resultingHeading.Y;
                nextMotionForTurn.TargetHeadingZ = resultingHeading.Z;
                nextMotionForTurn.RemainingTurnRadians = isTargeted
                    ? nextMotionForTurn.RemainingTurnRadians
                    : 0f;
                motions[slot] = nextMotionForTurn;
                State.UpdateFrameMovement(entityKey, hulls[slot], nextMotionForTurn);
                State.MarkModified(ctx.WorkerId, entityKey);
                clusterDirty = true;
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }
        State.RecordSystemMetric(SpaceBattleSystemMetricId.Movement, startedAt, State.PublishedShipCount, ctx.WorkerId);
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
        var startedAt = Stopwatch.GetTimestamp();
        var count = _state.CopyPendingReaps(_reapBuffer);
        if (count == 0)
        {
            _state.RecordSystemMetric(SpaceBattleSystemMetricId.Reap, startedAt, _state.PublishedShipCount, ctx.WorkerId);
            return;
        }

        using var transaction = _state.Engine.CreateQuickTransaction();
        transaction.DestroyBatch(_reapBuffer.AsSpan(0, count));
        if (!transaction.Commit())
        {
            throw new InvalidOperationException("SpaceBattle 回收事务提交失败。");
        }

        _state.CompleteReaps();
        _state.RecordSystemMetric(SpaceBattleSystemMetricId.Reap, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}

internal sealed class AcquisitionCleanupSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;

    public AcquisitionCleanupSystem(SpaceBattleSimulationState state)
    {
        _state = state;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("AcquisitionCleanup")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Observe)
        .After("Reap")
        .ChunkedParallel(_state.ChunkCount);

    protected override void Execute(TickContext ctx)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (ctx.TickNumber + 1 == (long)_state.MaximumCompletedTicks)
        {
            _state.ReleaseAcquisitionTransactionIfOwnedByCurrentThread(ctx.WorkerId);
        }

        _state.RecordSystemMetric(SpaceBattleSystemMetricId.AcquisitionCleanup, startedAt, _state.PublishedShipCount, ctx.WorkerId);
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
        .After("AcquisitionCleanup")
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _state.CompleteTick(ctx.TickNumber, _timing);
        _state.RecordSystemMetric(SpaceBattleSystemMetricId.Observe, startedAt, _state.PublishedShipCount, ctx.WorkerId);
    }
}


internal abstract class ShipChunkSystem : ChunkedCallbackSystem
{
    protected ShipChunkSystem(SpaceBattleSimulationState state)
    {
        State = state;
    }

    protected SpaceBattleSimulationState State { get; }

    protected int ShipCount => State.ShipCount;

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
