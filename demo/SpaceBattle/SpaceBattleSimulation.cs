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
                new RuntimeOptions { BaseTickRate = SimulationDefinition.FixedTickRate });
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
        dag.Add(new ResolutionSystem());
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
    protected override void Configure(SystemBuilder builder) => builder
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
    private const ulong WanderingSpeedPurpose = 4;
    private const ulong WanderingAzimuthPurpose = 5;
    private const ulong WanderingElevationPurpose = 6;

    protected override void Configure(SystemBuilder builder) => builder
        .Name("Steering")
        .After("State")
        .Phase(SpaceBattlePhases.Steering)
        .ReadsFresh<BehaviorComponent>()
        .Writes<BehaviorComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext context)
    {
        foreach (var shipId in context.Transaction.Query<Ship>().Execute())
        {
            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Wandering || behavior.DecisionOrdinal != 1)
            {
                continue;
            }

            var packedShipId = ((ulong)shipId.EntityKey << 12) | shipId.ArchetypeId;
            var direction = DeterministicRandom.UnitDirection(
                state.Definition.Seed,
                packedShipId,
                behavior.DecisionOrdinal,
                WanderingAzimuthPurpose,
                WanderingElevationPurpose);
            ref var motion = ref ship.Write(Ship.Motion);
            motion.DirectionX = direction.DirectionX;
            motion.DirectionY = direction.DirectionY;
            motion.DirectionZ = direction.DirectionZ;
            motion.Speed = DeterministicRandom.UnitInterval(
                state.Definition.Seed,
                packedShipId,
                behavior.DecisionOrdinal,
                WanderingSpeedPurpose) * SimulationDefinition.MaximumWanderingSpeed;
            behavior.DecisionOrdinal++;
        }
    }
}

internal sealed class MovementSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => builder
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
    protected override void Configure(SystemBuilder builder) => builder
        .Name("Targeting")
        .After("Movement")
        .Phase(SpaceBattlePhases.Targeting);

    protected override void Execute(TickContext context)
    {
    }
}

internal sealed class CombatSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => builder
        .Name("Combat")
        .After("Targeting")
        .Phase(SpaceBattlePhases.Combat);

    protected override void Execute(TickContext context)
    {
    }
}

internal sealed class ResolutionSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => builder
        .Name("Resolution")
        .After("Combat")
        .Phase(SpaceBattlePhases.Resolution);

    protected override void Execute(TickContext context)
    {
    }
}

internal sealed class OutputSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => builder
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
