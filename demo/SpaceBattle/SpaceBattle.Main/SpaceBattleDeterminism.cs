using Typhon.Engine;

namespace SpaceBattle;

public enum SpaceBattleDeterminismScope : byte
{
    FixedWorkerTopology = 0,
    ExplicitWorkerCountComparison = 1,
}

public sealed record SpaceBattleDeterminismDiagnostic(
    int WorkerCount,
    long Tick,
    int AliveShips,
    ulong HealthChecksum,
    ulong TargetChecksum,
    ulong ModeChecksum,
    SpaceBattleRunResult Run,
    SpaceBattleSnapshot FinalSnapshot)
{
    internal static SpaceBattleDeterminismDiagnostic Capture(
        SimulationDefinition definition,
        SpaceBattleRunResult run,
        SpaceBattleSnapshot finalSnapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(finalSnapshot);
        if (definition.WorkerCount <= 0)
        {
            throw new ArgumentException("复现诊断必须使用固定 worker 拓扑。", nameof(definition));
        }

        var ships = finalSnapshot.Ships
            .Where(static ship => ship.Vitals.CurrentHealth > 0)
            .OrderBy(static ship => ship.EntityKey)
            .ToArray();
        var aliveShips = ships.Length;
        var healthChecksum = ChecksumOffset;
        var targetChecksum = ChecksumOffset;
        var modeChecksum = ChecksumOffset;
        foreach (var ship in ships)
        {
            healthChecksum = Append(healthChecksum, unchecked((ulong)ship.EntityKey));
            healthChecksum = Append(healthChecksum, ship.Vitals.CurrentHealth);

            targetChecksum = Append(targetChecksum, unchecked((ulong)ship.EntityKey));
            targetChecksum = Append(targetChecksum, unchecked((ulong)ship.Targeting.TargetRawEntityId));

            var behavior = ship.Behavior;
            var modeWord = (ulong)behavior.Mode |
                           ((ulong)behavior.Phase << 8) |
                           ((ulong)behavior.TicksRemaining << 16) |
                           ((ulong)behavior.ModeStartedTick << 32);
            modeChecksum = Append(modeChecksum, unchecked((ulong)ship.EntityKey));
            modeChecksum = Append(modeChecksum, modeWord);
        }

        return new SpaceBattleDeterminismDiagnostic(
            definition.WorkerCount,
            run.CompletedTicks,
            aliveShips,
            healthChecksum,
            targetChecksum,
            modeChecksum,
            run,
            new SpaceBattleSnapshot(ships));
    }

    private const ulong ChecksumOffset = 14_695_981_039_346_656_037UL;
    private const ulong ChecksumPrime = 1_099_511_628_211UL;

    private static ulong Append(ulong checksum, ulong value) =>
        unchecked((checksum ^ value) * ChecksumPrime);
}

internal sealed class DeterminismAcquisitionResetSystem : ChunkedCallbackSystem
{
    private readonly SpaceBattleSimulationState _state;

    public DeterminismAcquisitionResetSystem(SpaceBattleSimulationState state)
    {
        _state = state;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("DeterminismAcquisitionReset")
        .Priority(SystemPriority.Critical)
        .CanShed(false)
        .Phase(SpaceBattlePhases.Publish)
        .Before("FramePrepare")
        .ChunkedParallel(1);

    protected override void Execute(TickContext ctx)
    {
        if (!_state.ShouldExecuteTick(ctx.TickNumber))
        {
            return;
        }

        _state.AcquisitionTransactions.InvalidateForNextUse();
    }
}

internal sealed class FinalTickObservationSink : ISpaceBattleObservationSink
{
    private readonly ISpaceBattleObservationSink _inner;
    private readonly long _lastExpectedTick;
    private SimulationTickCompleted _pendingFinalTick;

    public FinalTickObservationSink(ISpaceBattleObservationSink inner, ulong maximumCompletedTicks)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _lastExpectedTick = maximumCompletedTicks >= (ulong)long.MaxValue
            ? long.MaxValue
            : (long)maximumCompletedTicks - 1;
    }

    public void Publish(SpaceBattleObservation observation)
    {
        if (observation is SimulationTickCompleted tick && tick.TickNumber >= _lastExpectedTick)
        {
            _pendingFinalTick = tick;
            return;
        }

        _inner.Publish(observation);
    }

    public void PublishCommittedSnapshot(SpaceBattleSnapshot snapshot)
    {
        if (_pendingFinalTick is not null)
        {
            _inner.Publish(_pendingFinalTick with { PublishedSnapshot = snapshot });
            _pendingFinalTick = null;
        }
    }
}

public sealed record SpaceBattleDeterminismComparison(
    SpaceBattleDeterminismDiagnostic Left,
    SpaceBattleDeterminismDiagnostic Right,
    SpaceBattleDeterminismScope Scope)
{
    public bool IsExplicitWorkerCountComparison =>
        Scope == SpaceBattleDeterminismScope.ExplicitWorkerCountComparison;

    public bool IsMatch =>
        Left is not null &&
        Right is not null &&
        Left.Tick == Right.Tick &&
        Left.AliveShips == Right.AliveShips &&
        Left.HealthChecksum == Right.HealthChecksum &&
        Left.TargetChecksum == Right.TargetChecksum &&
        Left.ModeChecksum == Right.ModeChecksum;
}

public static class SpaceBattleDeterminism
{
    public static SpaceBattleDeterminismComparison Compare(
        SpaceBattleDeterminismDiagnostic left,
        SpaceBattleDeterminismDiagnostic right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var scope = left.WorkerCount == right.WorkerCount
            ? SpaceBattleDeterminismScope.FixedWorkerTopology
            : SpaceBattleDeterminismScope.ExplicitWorkerCountComparison;
        return new SpaceBattleDeterminismComparison(left, right, scope);
    }
}
