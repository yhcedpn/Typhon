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
        dag.Add(new TargetingSystem(state));
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

internal static class ShipRoster
{
    public static EntityId[] Ordered(IEnumerable<EntityId> shipIds) => shipIds
        .OrderBy(static id => id.EntityKey)
        .ToArray();

    public static Dictionary<long, int> IndexByEntityKey(IReadOnlyList<EntityId> roster)
    {
        var indexes = new Dictionary<long, int>(roster.Count);
        for (var index = 0; index < roster.Count; index++)
        {
            indexes.Add(roster[index].EntityKey, index);
        }

        return indexes;
    }
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
            var shipIdForRandom = BehaviorRules.PackShipId(shipId);

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
                behavior.ModeTicksRemaining = BehaviorRules.CombatAcquisitionDurationTicks + 1;
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
        var roster = ShipRoster.Ordered(shipIds);
        var rosterIndexes = ShipRoster.IndexByEntityKey(roster);

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

internal sealed class TargetingSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Targeting")
        .After("Movement")
        .Phase(SpaceBattlePhases.Targeting)
        .Reads<PositionComponent>()
        .ReadsFresh<BehaviorComponent>()
        .Reads<TargetLockComponent>()
        .Writes<BehaviorComponent>()
        .Writes<TargetLockComponent>();

    protected override void Execute(TickContext context)
    {
        var activeLockCounts = AdvanceExistingLocks(context);
        var roster = ShipRoster.Ordered(context.Transaction.Query<Ship>().Execute());
        var rosterIndexes = ShipRoster.IndexByEntityKey(roster);

        foreach (var shipId in roster)
        {
            if (activeLockCounts.GetValueOrDefault(shipId.EntityKey) >=
                BehaviorRules.MaximumTargetLocksPerShip)
            {
                continue;
            }

            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Combat)
            {
                continue;
            }

            if (behavior.ModeTicksRemaining > 0 && --behavior.ModeTicksRemaining == 0)
            {
                ref var tracking = ref ship.Write(Ship.Tracking);
                ref var motion = ref ship.Write(Ship.Motion);
                SteeringSystem.StartWandering(
                    state.Definition,
                    BehaviorRules.PackShipId(shipId),
                    ref behavior,
                    ref tracking,
                    ref motion);
                continue;
            }

            var decisionOrdinal = behavior.DecisionOrdinal++;
            var targetId = FindInRangeCandidate(
                context,
                roster,
                rosterIndexes[shipId.EntityKey],
                shipId,
                state.Definition.Seed,
                decisionOrdinal);
            if (targetId.IsNull)
            {
                continue;
            }

            var targetLock = new TargetLockComponent
            {
                Owner = shipId,
                Target = targetId,
                TicksRemaining = BehaviorRules.LockAcquisitionDurationTicks,
                Status = (byte)TargetLockStatus.Acquiring,
            };
            context.Transaction.Spawn<TargetLock>(TargetLock.Data.Set(in targetLock));
            activeLockCounts[shipId.EntityKey] =
                activeLockCounts.GetValueOrDefault(shipId.EntityKey) + 1;
        }
    }

    private static Dictionary<long, int> AdvanceExistingLocks(TickContext context)
    {
        var activeLockCounts = new Dictionary<long, int>();
        foreach (var targetLockId in context.Transaction.Query<TargetLock>().Execute())
        {
            var targetLockEntity = context.Transaction.OpenMut(targetLockId);
            ref var targetLock = ref targetLockEntity.Write(TargetLock.Data);
            var ownerId = (EntityId)targetLock.Owner;
            var targetId = (EntityId)targetLock.Target;
            if (!context.Transaction.TryOpen(ownerId, out var owner) ||
                !context.Transaction.TryOpen(targetId, out var target))
            {
                context.Transaction.Destroy(targetLockId);
                continue;
            }

            if (!IsWithinLockRange(owner, target))
            {
                if ((TargetLockStatus)targetLock.Status == TargetLockStatus.Locked)
                {
                    ref var ownerBehavior = ref context.Transaction.OpenMut(ownerId).Write(Ship.Behavior);
                    if ((BehaviorMode)ownerBehavior.Mode == BehaviorMode.Combat)
                    {
                        ownerBehavior.ModeTicksRemaining = BehaviorRules.CombatAcquisitionDurationTicks + 1;
                    }
                }

                context.Transaction.Destroy(targetLockId);
                continue;
            }

            if ((TargetLockStatus)targetLock.Status == TargetLockStatus.Acquiring)
            {
                if (targetLock.TicksRemaining == 0)
                {
                    targetLock.Status = (byte)TargetLockStatus.Locked;
                }
                else
                {
                    targetLock.TicksRemaining--;
                }
            }

            activeLockCounts[ownerId.EntityKey] = activeLockCounts.GetValueOrDefault(ownerId.EntityKey) + 1;
        }

        return activeLockCounts;
    }

    private static EntityId FindInRangeCandidate(
        TickContext context,
        IReadOnlyList<EntityId> roster,
        int sourceIndex,
        EntityId shipId,
        ulong seed,
        ulong decisionOrdinal)
    {
        var source = context.Transaction.Open(shipId);
        for (var candidateOrdinal = 0;
             candidateOrdinal < BehaviorRules.MaximumLockCandidatesPerAttempt;
             candidateOrdinal++)
        {
            var candidateIndex = BehaviorRules.SelectLockTargetCandidateIndex(
                seed,
                BehaviorRules.PackShipId(shipId),
                decisionOrdinal,
                roster.Count,
                sourceIndex,
                candidateOrdinal);
            var candidate = context.Transaction.Open(roster[candidateIndex]);
            if (IsWithinLockRange(source, candidate))
            {
                return roster[candidateIndex];
            }
        }

        return EntityId.Null;
    }

    private static bool IsWithinLockRange(EntityRef source, EntityRef target)
    {
        ref readonly var sourcePosition = ref source.Read(Ship.Position);
        ref readonly var targetPosition = ref target.Read(Ship.Position);
        var deltaX = targetPosition.X - sourcePosition.X;
        var deltaY = targetPosition.Y - sourcePosition.Y;
        var deltaZ = targetPosition.Z - sourcePosition.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= BehaviorRules.LockRange * BehaviorRules.LockRange;
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
                BehaviorRules.PackShipId(shipId),
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
