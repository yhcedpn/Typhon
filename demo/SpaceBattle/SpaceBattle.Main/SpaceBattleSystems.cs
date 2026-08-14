using System.Diagnostics;
using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

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
        _state.PrepareTick(ctx.TickNumber);
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
                    // #41 尚未引入伤害结算；cluster spawn 的零填充槽按 bootstrap 的存活事实发布。
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
    }
}

internal sealed class BehaviorSystem : ShipChunkSystem
{
    public BehaviorSystem(SpaceBattleSimulationState state)
        : base(state)
    {
    }

    protected override void Configure(SystemBuilder b) => ConfigureChunked(b, "Behavior", "Publish")
        .Phase(SpaceBattlePhases.Behavior)
        .Writes<Motion, Targeting, Behavior>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var motions = cluster.GetSpan(Ship.Motion);
            var targetings = cluster.GetSpan(Ship.Targeting);
            var behaviors = cluster.GetSpan(Ship.Behavior);
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

                var nextMotion = frame.Motion;
                var nextTargeting = frame.Targeting;
                var nextBehavior = frame.Behavior;
                switch ((BehaviorMode)frame.Behavior.Mode)
                {
                    case BehaviorMode.Wandering:
                        AdvanceWandering(
                            ctx.TickNumber,
                            entityKey,
                            ref nextMotion,
                            ref nextBehavior,
                            (BehaviorPhase)frame.Behavior.Phase);
                        break;
                    case BehaviorMode.Tracking:
                        AcquireTarget(
                            ctx,
                            frame,
                            ref nextMotion,
                            ref nextTargeting,
                            ref nextBehavior);
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

                if (!MotionEquals(frame.Motion, nextMotion) ||
                    !TargetingEquals(frame.Targeting, nextTargeting) ||
                    !BehaviorEquals(frame.Behavior, nextBehavior))
                {
                    motions[slot] = nextMotion;
                    targetings[slot] = nextTargeting;
                    behaviors[slot] = nextBehavior;
                    State.MarkModified(entityKey);
                    clusterDirty = true;
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
    }

    private void AcquireTarget(
        TickContext ctx,
        in ShipSnapshot source,
        ref Motion motion,
        ref Targeting targeting,
        ref Behavior behavior)
    {
        var targetId = SpaceBattleTargeting.FindNearest(
            State.GetAcquisitionTransaction(ctx.WorkerId, ctx.TickNumber),
            State,
            source,
            out var distanceSquared);
        if (targetId.IsNull)
        {
            // 没有候选时保留原速度和航向，下一 tick 继续重试。
            targeting.TargetEntityId = 0;
            return;
        }

        targeting.TargetEntityId = SpaceBattleTargeting.PackRaw(targetId);
        behavior.Mode = (byte)(distanceSquared <= SpaceBattleTargeting.WeaponRange * SpaceBattleTargeting.WeaponRange
            ? BehaviorMode.Attacking
            : BehaviorMode.Approaching);
        behavior.Phase = (byte)BehaviorPhase.Ready;
        behavior.TicksRemaining = 0;
        behavior.ModeStartedTick = unchecked((uint)(ctx.TickNumber + 1));
        motion.Speed = SpaceBattleTargeting.ApproachSpeed;
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
            // 目标失效的这一帧仍由 Movement 使用旧快照完成一次移动，然后回到锁定。
            targeting.TargetEntityId = 0;
            behavior.Mode = (byte)BehaviorMode.Tracking;
            behavior.Phase = (byte)BehaviorPhase.Ready;
            behavior.TicksRemaining = 0;
            behavior.ModeStartedTick = unchecked((uint)(tickNumber + 1));
            return;
        }

        motion.Speed = SpaceBattleTargeting.ApproachSpeed;
        var currentMode = (BehaviorMode)source.Behavior.Mode;
        if (currentMode == BehaviorMode.Approaching &&
            distanceSquared <= SpaceBattleTargeting.WeaponRange * SpaceBattleTargeting.WeaponRange)
        {
            behavior.Mode = (byte)BehaviorMode.Attacking;
            behavior.ModeStartedTick = unchecked((uint)(tickNumber + 1));
        }
        else if (currentMode == BehaviorMode.Attacking &&
                 distanceSquared > SpaceBattleTargeting.WeaponRange * SpaceBattleTargeting.WeaponRange)
        {
            behavior.Mode = (byte)BehaviorMode.Approaching;
            behavior.ModeStartedTick = unchecked((uint)(tickNumber + 1));
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
                    behavior.ModeStartedTick = unchecked((uint)(tickNumber + 1));
                }
                else
                {
                    behavior.TicksRemaining--;
                }

                break;
        }
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
        .Phase(SpaceBattlePhases.Damage);

    protected override void Execute(TickContext ctx)
    {
        // #41 只建立进入攻击模式的入口，伤害和销毁留给 #42。
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
                if (frame.Vitals.CurrentHealth == 0)
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
                    State.MarkModified(entityKey);
                    clusterDirty = true;
                    continue;
                }

                var isTargeted = mode is BehaviorMode.Approaching or BehaviorMode.Attacking;
                var shouldMove = mode == BehaviorMode.Tracking ||
                                 isTargeted ||
                                 (mode == BehaviorMode.Wandering &&
                                  phase == BehaviorPhase.Flying &&
                                  frame.Behavior.TicksRemaining > 0);
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
                State.MarkModified(entityKey);
                clusterDirty = true;
            }

            if (clusterDirty)
            {
                clusters.MarkCurrentDirty();
            }
        }
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
        var count = _state.CopyPendingReaps(_reapBuffer);
        if (count == 0)
        {
            return;
        }

        using var transaction = _state.Engine.CreateQuickTransaction();
        transaction.DestroyBatch(_reapBuffer.AsSpan(0, count));
        if (!transaction.Commit())
        {
            throw new InvalidOperationException("SpaceBattle 回收事务提交失败。");
        }

        _state.CompleteReaps();
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
        if (ctx.TickNumber + 1 == (long)_state.MaximumCompletedTicks)
        {
            _state.ReleaseAcquisitionTransactionIfOwnedByCurrentThread(ctx.WorkerId);
        }
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
        _timing.RecordTick(Stopwatch.GetElapsedTime(_state.TickStartedAt));
        _state.CompleteTick(ctx.TickNumber);
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
