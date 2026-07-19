using System.Diagnostics;
using Typhon.Engine;

namespace SpaceBattle;

public sealed class SpaceBattleSimulation : IDisposable
{
    private readonly DatabaseEngine _engine;
    private readonly TyphonRuntime _runtime;
    private readonly SimulationRuntimeState _state;
    private bool _disposed;

    internal SpaceBattleSimulation(
        DatabaseEngine engine,
        TyphonRuntime runtime,
        SimulationRuntimeState state,
        SpaceBattleRunResult startupResult)
    {
        _engine = engine;
        _runtime = runtime;
        _state = state;
        StartupResult = startupResult;
    }

    public SpaceBattleRunResult StartupResult { get; }

    public int TickRate => _runtime.Options.BaseTickRate;

    public float SimulationDeltaSeconds => SimulationDefinition.FixedSimulationDeltaSeconds;

    public SpaceBattleRuntimeConfiguration RuntimeConfiguration => new(
        SpaceBattleProductionSettings.ResourceEnvelope.PageCacheSizeBytes,
        SpaceBattleProductionSettings.ResourceEnvelope.MemoryBudgetBytes,
        _runtime.Options.WorkerCount,
        _runtime.Scheduler.WorkerCount,
        _runtime.Options.Overload.MinTickRateHz,
        _runtime.Options.Overload.QueueGrowthTicks,
        _runtime.CurrentOverloadLevel,
        _runtime.UserSystems.Select(static system => new SpaceBattleSystemConfiguration(
            system.Name,
            system.Priority,
            system.TickDivisor,
            system.ThrottledTickDivisor,
            system.CanShed)).ToArray());

    public IReadOnlyList<string> SystemNames => _runtime.UserSystems.Select(static system => system.Name).ToArray();

    public IReadOnlyList<string> SystemPhases => _runtime.UserSystems.Select(static system => system.Phase.Name).ToArray();

    public bool WaitForCompletedTicks(ulong completedTicks, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startedAt = Stopwatch.GetTimestamp();
        if (!_state.WaitForCompletedTicks(completedTicks, timeout))
        {
            return false;
        }

        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        var remaining = timeout - TimeSpan.FromSeconds(elapsed / (double)Stopwatch.Frequency);
        return remaining > TimeSpan.Zero && SpinWait.SpinUntil(
            () => _runtime.CurrentTickNumber >= _state.RuntimeTicksRequired(completedTicks),
            remaining);
    }

    public InitialWorldSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SpaceBattleHost.ReadSnapshot(_engine);
    }

    internal static SpaceBattleSimulation Create(
        DatabaseEngine engine,
        SimulationDefinition definition,
        EntityId runEntityId,
        ulong completedTicks,
        SpaceBattleRunResult startupResult)
    {
        var state = new SimulationRuntimeState(definition, runEntityId, completedTicks);
        TyphonRuntime runtime = null!;

        try
        {
            runtime = TyphonRuntime.Create(
                engine,
                schedule => ConfigureRuntime(schedule, state),
                new RuntimeOptions
                {
                    BaseTickRate = SimulationDefinition.FixedTickRate,
                    WorkerCount = SpaceBattleProductionSettings.AutomaticWorkerCount,
                    Overload = new OverloadOptions
                    {
                        MinTickRateHz = SimulationDefinition.FixedTickRate,
                        QueueGrowthTicks = SpaceBattleProductionSettings.DisabledQueueGrowthEscalationTicks,
                    },
                });
            runtime.Start();
            return new SpaceBattleSimulation(engine, runtime, state, startupResult);
        }
        catch
        {
            runtime?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Shutdown();
        _runtime.Dispose();
        _engine.Dispose();
    }

    private static void ConfigureRuntime(RuntimeSchedule schedule, SimulationRuntimeState state)
    {
        var dag = schedule.PublicTrack.DeclareDag("SpaceBattle")
            .Phases(
                SpaceBattlePhases.State,
                SpaceBattlePhases.Steering,
                SpaceBattlePhases.Movement,
                SpaceBattlePhases.Targeting,
                SpaceBattlePhases.Combat,
                SpaceBattlePhases.Resolution,
                SpaceBattlePhases.Output);
        dag.Add(new StateSystem());
        dag.Add(new SteeringSystem(state));
        dag.Add(new MovementSystem(state));
        dag.Add(new TargetingSystem());
        dag.Add(new CombatSystem());
        dag.Add(new ResolutionSystem(state));
        dag.Add(new OutputSystem(state));
    }
}

internal static class SpaceBattlePhases
{
    public static readonly Phase State = new("State");
    public static readonly Phase Steering = new("Steering");
    public static readonly Phase Movement = new("Movement");
    public static readonly Phase Targeting = new("Targeting");
    public static readonly Phase Combat = new("Combat");
    public static readonly Phase Resolution = new("Resolution");
    public static readonly Phase Output = new("Output");
}

internal static class SpaceBattleSystemPolicies
{
    public static SystemBuilder Apply(SystemBuilder builder) => builder
        .Priority(SystemPriority.Critical)
        .TickDivisor(1)
        .ThrottledTickDivisor(1)
        .CanShed(false);
}

internal sealed class SimulationRuntimeState
{
    private readonly object _sync = new();
    private ulong _completedTicks;

    public SimulationRuntimeState(
        SimulationDefinition definition,
        EntityId runEntityId,
        ulong completedTicks)
    {
        Definition = definition;
        RunEntityId = runEntityId;
        _completedTicks = completedTicks;
        StartingCompletedTicks = completedTicks;
    }

    public SimulationDefinition Definition { get; }

    public EntityId RunEntityId { get; }

    public ulong StartingCompletedTicks { get; }

    public ulong CompletedTicks
    {
        get
        {
            lock (_sync)
            {
                return _completedTicks;
            }
        }
    }

    public ulong CompletedTicksForRuntimeTick(long runtimeTickNumber) => checked(
        StartingCompletedTicks + (ulong)runtimeTickNumber + 1);

    public long RuntimeTicksRequired(ulong completedTicks) => completedTicks <= StartingCompletedTicks
        ? 0
        : checked((long)(completedTicks - StartingCompletedTicks));

    public void MarkCompletedTicks(ulong completedTicks)
    {
        lock (_sync)
        {
            _completedTicks = completedTicks;
            Monitor.PulseAll(_sync);
        }
    }

    public bool WaitForCompletedTicks(ulong completedTicks, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        lock (_sync)
        {
            while (_completedTicks < completedTicks)
            {
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return false;
                }

                Monitor.Wait(_sync, TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            }

            return true;
        }
    }
}

internal sealed class StateSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("State")
        .Phase(SpaceBattlePhases.State)
        .Writes<BehaviorComponent>();

    protected override void Execute(TickContext context)
    {
        foreach (var shipId in context.Transaction.Query<Ship>().Execute())
        {
            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Staging)
            {
                continue;
            }

            if (behavior.ModeTicksRemaining > 0)
            {
                behavior.ModeTicksRemaining--;
                continue;
            }

            behavior.Mode = (byte)BehaviorMode.Wandering;
            behavior.DecisionOrdinal++;
        }
    }
}

internal sealed class SteeringSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Steering")
        .After("State")
        .Phase(SpaceBattlePhases.Steering)
        .ReadsFresh<BehaviorComponent>()
        .Reads<PositionComponent>()
        .Writes<BehaviorComponent>()
        .Writes<TrackingComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext context)
    {
        var shipIds = context.Transaction.Query<Ship>().Execute();
        List<TrackingStart> trackingStarts = null;

        foreach (var shipId in shipIds)
        {
            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            ref var tracking = ref ship.Write(Ship.Tracking);
            var shipIdForRandom = PackShipId(shipId);

            switch ((BehaviorMode)behavior.Mode)
            {
                case BehaviorMode.Wandering:
                    ProcessWandering(
                        state.Definition,
                        shipId,
                        shipIdForRandom,
                        ref behavior,
                        ref tracking,
                        ref ship.Write(Ship.Motion),
                        ref trackingStarts);
                    break;
                case BehaviorMode.Tracking:
                    ProcessTracking(
                        context,
                        state.Definition,
                        ship,
                        shipIdForRandom,
                        ref behavior,
                        ref tracking,
                        ref ship.Write(Ship.Motion));
                    break;
            }
        }

        if (trackingStarts is not null)
        {
            StartTracking(context, state.Definition, shipIds, trackingStarts);
        }
    }

    private static void ProcessWandering(
        SimulationDefinition definition,
        EntityId shipId,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion,
        ref List<TrackingStart> trackingStarts)
    {
        if (behavior.DecisionOrdinal == 1 && behavior.ModeTicksRemaining == 0)
        {
            StartWandering(definition, shipIdForRandom, ref behavior, ref tracking, ref motion);
            return;
        }

        if (behavior.ModeTicksRemaining > 0)
        {
            behavior.ModeTicksRemaining--;
            return;
        }

        var decisionOrdinal = behavior.DecisionOrdinal;
        switch (BehaviorRules.DecideWandering(definition.Seed, shipIdForRandom, decisionOrdinal))
        {
            case WanderingDecision.ContinueWandering:
                StartWandering(definition, shipIdForRandom, ref behavior, ref tracking, ref motion);
                break;
            case WanderingDecision.Track:
                behavior.Mode = (byte)BehaviorMode.Tracking;
                behavior.DecisionOrdinal++;
                trackingStarts ??= new List<TrackingStart>();
                trackingStarts.Add(new TrackingStart(shipId, shipIdForRandom, decisionOrdinal));
                break;
            case WanderingDecision.Combat:
                behavior.Mode = (byte)BehaviorMode.Combat;
                behavior.DecisionOrdinal++;
                break;
            default:
                throw new InvalidOperationException("未知的游荡决策。");
        }
    }

    private static void ProcessTracking(
        TickContext context,
        SimulationDefinition definition,
        EntityRef ship,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion)
    {
        if (tracking.Target.IsNull ||
            !context.Transaction.TryOpen(tracking.Target, out var target) ||
            tracking.TrackingTicksRemaining == 0)
        {
            StartWandering(
                definition,
                shipIdForRandom,
                ref behavior,
                ref tracking,
                ref motion);
            return;
        }

        ref readonly var position = ref ship.Read(Ship.Position);
        ref readonly var targetPosition = ref target.Read(Ship.Position);
        var nextMotion = BehaviorRules.CreateTrackingMotion(
            new PositionSnapshot(position.X, position.Y, position.Z),
            new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z),
            new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed));
        SetMotion(ref motion, nextMotion);
        tracking.TrackingTicksRemaining--;
    }

    private static void StartTracking(
        TickContext context,
        SimulationDefinition definition,
        IEnumerable<EntityId> shipIds,
        IReadOnlyList<TrackingStart> trackingStarts)
    {
        var roster = shipIds.OrderBy(static id => id.EntityKey).ToArray();
        var rosterIndexes = new Dictionary<long, int>(roster.Length);
        for (var index = 0; index < roster.Length; index++)
        {
            rosterIndexes.Add(roster[index].EntityKey, index);
        }

        foreach (var trackingStart in trackingStarts)
        {
            var ship = context.Transaction.OpenMut(trackingStart.ShipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            ref var tracking = ref ship.Write(Ship.Tracking);
            ref var motion = ref ship.Write(Ship.Motion);
            var sourceIndex = rosterIndexes[trackingStart.ShipId.EntityKey];
            var targetIndex = BehaviorRules.SelectTrackingTargetIndex(
                definition.Seed,
                trackingStart.ShipIdForRandom,
                trackingStart.DecisionOrdinal,
                roster.Length,
                sourceIndex);
            if (targetIndex < 0)
            {
                StartWandering(
                    definition,
                    trackingStart.ShipIdForRandom,
                    ref behavior,
                    ref tracking,
                    ref motion);
                continue;
            }

            var target = context.Transaction.Open(roster[targetIndex]);
            ref readonly var position = ref ship.Read(Ship.Position);
            ref readonly var targetPosition = ref target.Read(Ship.Position);
            tracking.Target = roster[targetIndex];
            tracking.TrackingTicksRemaining = BehaviorRules.TrackingDurationTicks;
            SetMotion(
                ref motion,
                BehaviorRules.CreateTrackingMotion(
                    new PositionSnapshot(position.X, position.Y, position.Z),
                    new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z),
                    new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed)));
        }
    }

    internal static void StartWandering(
        SimulationDefinition definition,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion)
    {
        behavior.Mode = (byte)BehaviorMode.Wandering;
        behavior.ModeTicksRemaining = BehaviorRules.WanderingDecisionIntervalTicks;
        tracking.Target = EntityLink<Ship>.Null;
        tracking.TrackingTicksRemaining = 0;
        SetMotion(
            ref motion,
            BehaviorRules.CreateWanderingMotion(definition.Seed, shipIdForRandom, behavior.DecisionOrdinal));
        behavior.DecisionOrdinal++;
    }

    private static void SetMotion(ref MotionComponent motion, MotionSnapshot value)
    {
        motion.DirectionX = value.DirectionX;
        motion.DirectionY = value.DirectionY;
        motion.DirectionZ = value.DirectionZ;
        motion.Speed = value.Speed;
    }

    internal static ulong PackShipId(EntityId shipId) => ((ulong)shipId.EntityKey << 12) | shipId.ArchetypeId;

    private readonly record struct TrackingStart(EntityId ShipId, ulong ShipIdForRandom, ulong DecisionOrdinal);
}

internal sealed class MovementSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Movement")
        .After("Steering")
        .Phase(SpaceBattlePhases.Movement)
        .ReadsFresh<BehaviorComponent>()
        .ReadsFresh<MotionComponent>()
        .Writes<PositionComponent>()
        .Writes<SpatialBoundsComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext context)
    {
        foreach (var shipId in context.Transaction.Query<Ship>().Execute())
        {
            var ship = context.Transaction.OpenMut(shipId);
            ref readonly var behavior = ref ship.Read(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode == BehaviorMode.Staging)
            {
                continue;
            }

            ref var position = ref ship.Write(Ship.Position);
            ref var motion = ref ship.Write(Ship.Motion);
            var step = MovementRules.Advance(
                new PositionSnapshot(position.X, position.Y, position.Z),
                new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed),
                SimulationDefinition.FixedSimulationDeltaSeconds,
                state.Definition.WorldSize);

            position.X = step.Position.X;
            position.Y = step.Position.Y;
            position.Z = step.Position.Z;
            motion.DirectionX = step.Motion.DirectionX;
            motion.DirectionY = step.Motion.DirectionY;
            motion.DirectionZ = step.Motion.DirectionZ;

            ref var bounds = ref ship.Write(Ship.SpatialBounds);
            bounds.Bounds.MinX = position.X;
            bounds.Bounds.MinY = position.Y;
            bounds.Bounds.MinZ = position.Z;
            bounds.Bounds.MaxX = position.X;
            bounds.Bounds.MaxY = position.Y;
            bounds.Bounds.MaxZ = position.Z;
        }
    }
}

internal sealed class TargetingSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Targeting")
        .After("Movement")
        .Phase(SpaceBattlePhases.Targeting);

    protected override void Execute(TickContext context)
    {
    }
}

internal sealed class CombatSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Combat")
        .After("Targeting")
        .Phase(SpaceBattlePhases.Combat);

    protected override void Execute(TickContext context)
    {
    }
}

internal sealed class ResolutionSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Resolution")
        .After("Combat")
        .Phase(SpaceBattlePhases.Resolution)
        .Writes<BehaviorComponent>()
        .Writes<TrackingComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext context)
    {
        foreach (var shipId in context.Transaction.Query<Ship>().Execute())
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Tracking)
            {
                continue;
            }

            ref var tracking = ref ship.Write(Ship.Tracking);
            if (!tracking.Target.IsNull && context.Transaction.IsAlive(tracking.Target))
            {
                continue;
            }

            SteeringSystem.StartWandering(
                state.Definition,
                SteeringSystem.PackShipId(shipId),
                ref behavior,
                ref tracking,
                ref ship.Write(Ship.Motion));
        }
    }
}

internal sealed class OutputSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Output")
        .After("Resolution")
        .Phase(SpaceBattlePhases.Output)
        .Writes<SimulationRunComponent>();

    protected override void Execute(TickContext context)
    {
        var completedTicks = state.CompletedTicksForRuntimeTick(context.TickNumber);
        ref var run = ref context.Transaction.OpenMut(state.RunEntityId).Write(SimulationRunEntity.Run);
        run.CompletedTicks = completedTicks;
        state.MarkCompletedTicks(completedTicks);
    }
}
