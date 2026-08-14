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
        .Writes<Targeting, Behavior>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = OpenClusters(ctx);
        foreach (var cluster in clusters)
        {
            var targetings = cluster.GetSpan(Ship.Targeting);
            var behaviors = cluster.GetSpan(Ship.Behavior);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var entityId = cluster.GetEntityId(slot);
                var entityKey = entityId.EntityKey;
                if (!State.TryGetFrameIndex(entityKey, out var frameIndex))
                {
                    continue;
                }

                ref readonly var frame = ref State.GetFrame(frameIndex);
                targetings[slot] = new Targeting
                {
                    TargetEntityId = SpaceBattleMath.NextTargetRaw(entityId.ArchetypeId, frame.EntityKey, State.ShipCount),
                };
                behaviors[slot] = new Behavior
                {
                    Mode = (byte)BehaviorMode.Tracking,
                    Phase = (byte)BehaviorPhase.Flying,
                    TicksRemaining = 1,
                    ModeStartedTick = unchecked((uint)ctx.TickNumber),
                };
                State.MarkModified(frame.EntityKey);
            }
        }
    }
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

                var headingX = frame.Motion.CurrentHeadingX;
                var headingY = frame.Motion.CurrentHeadingY;
                var headingLength = MathF.Sqrt((headingX * headingX) + (headingY * headingY));
                if (headingLength < 0.001f)
                {
                    headingX = SpaceBattleMath.HeadingX(frame.EntityKey);
                    headingY = SpaceBattleMath.HeadingY(frame.EntityKey);
                }
                else
                {
                    headingX /= headingLength;
                    headingY /= headingLength;
                }

                const float speed = 75f;
                var bounds = SpaceBattleMath.MoveBounds(
                    frame.Hull.Bounds,
                    headingX,
                    headingY,
                    speed,
                    State.FixedDeltaSeconds,
                    State.WorldWidth,
                    State.WorldHeight,
                    out var resultingHeadingX,
                    out var resultingHeadingY);
                hulls[slot] = new Hull { Bounds = bounds };
                motions[slot] = new Motion
                {
                    CurrentHeadingX = resultingHeadingX,
                    CurrentHeadingY = resultingHeadingY,
                    CurrentHeadingZ = 0f,
                    TargetHeadingX = resultingHeadingX,
                    TargetHeadingY = resultingHeadingY,
                    TargetHeadingZ = 0f,
                    Speed = speed,
                    RemainingTurnRadians = 0f,
                };
                State.MarkModified(frame.EntityKey);
            }

            clusters.MarkCurrentDirty();
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
