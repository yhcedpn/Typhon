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

internal sealed class FramePrepareSystem : CallbackSystem
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
        .Phase(SpaceBattlePhases.Publish);

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
            var vitals = cluster.GetReadOnlySpan(Ship.Vitals);
            var targetings = cluster.GetReadOnlySpan(Ship.Targeting);
            var behaviors = cluster.GetReadOnlySpan(Ship.Behavior);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityId = cluster.GetEntityId(slot);
                var entityKey = entityId.EntityKey;
                State.PublishFrame(entityId, new ShipSnapshot(
                    entityKey,
                    hulls[slot],
                    motions[slot],
                    vitals[slot],
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
        .Writes<Motion, Behavior>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = OpenClusters(ctx);
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            var motions = cluster.GetSpan(Ship.Motion);
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
                var nextMotion = frame.Motion;
                var nextBehavior = frame.Behavior;
                var phase = (BehaviorPhase)frame.Behavior.Phase;
                if ((BehaviorMode)frame.Behavior.Mode == BehaviorMode.Wandering)
                {
                    AdvanceWandering(
                        ctx.TickNumber,
                        entityKey,
                        ref nextMotion,
                        ref nextBehavior,
                        phase);
                }

                if (!MotionEquals(frame.Motion, nextMotion) || !BehaviorEquals(frame.Behavior, nextBehavior))
                {
                    motions[slot] = nextMotion;
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
        using var clusters = OpenClusters(ctx);
        foreach (var cluster in clusters)
        {
            var vitals = cluster.GetSpan(Ship.Vitals);
            var bits = cluster.OccupancyBits;
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
                var health = frame.Vitals.CurrentHealth;
                if (frame.Targeting.TargetEntityId != 0 && ctx.TickNumber % 5 == 0 && health > 0)
                {
                    var damage = Math.Max(1u, State.MaximumHealth / 20u);
                    health = health > damage ? health - damage : 0;
                }

                if (vitals[slot].CurrentHealth != health)
                {
                    vitals[slot] = new Vitals { CurrentHealth = health };
                    State.MarkModified(frame.EntityKey);
                }

                if (health == 0)
                {
                    State.MarkForReap(frame.EntityKey);
                }
            }
        }
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

                var shouldMove = mode == BehaviorMode.Tracking ||
                                 (mode == BehaviorMode.Wandering &&
                                  phase == BehaviorPhase.Flying &&
                                  frame.Behavior.TicksRemaining > 0);
                if (!shouldMove)
                {
                    continue;
                }

                var heading = new Vector3(
                    frame.Motion.CurrentHeadingX,
                    frame.Motion.CurrentHeadingY,
                    frame.Motion.CurrentHeadingZ);
                var bounds = SpaceBattleMath.MoveBounds(
                    frame.Hull.Bounds,
                    heading,
                    frame.Motion.Speed,
                    State.FixedDeltaSeconds,
                    State.WorldWidth,
                    State.WorldHeight,
                    State.WorldDepth,
                    out var resultingHeading);
                hulls[slot] = new Hull { Bounds = bounds };
                var movedMotion = frame.Motion;
                movedMotion.CurrentHeadingX = resultingHeading.X;
                movedMotion.CurrentHeadingY = resultingHeading.Y;
                movedMotion.CurrentHeadingZ = resultingHeading.Z;
                movedMotion.TargetHeadingX = resultingHeading.X;
                movedMotion.TargetHeadingY = resultingHeading.Y;
                movedMotion.TargetHeadingZ = resultingHeading.Z;
                movedMotion.RemainingTurnRadians = 0f;
                motions[slot] = movedMotion;
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

internal sealed class ReapSystem : CallbackSystem
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
        .After("Movement");

    protected override void Execute(TickContext ctx)
    {
        var count = _state.CopyPendingReaps(_reapBuffer);
        if (count == 0)
        {
            return;
        }

        ctx.Transaction.DestroyBatch(_reapBuffer.AsSpan(0, count));
        _state.CompleteReaps();
    }
}

internal sealed class ObserveSystem : CallbackSystem
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
        .After("Reap");

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
