namespace SpaceBattle;

public enum WanderingDecision : byte
{
    ContinueWandering = 1,
    Track = 2,
    Combat = 3,
}

public static class BehaviorRules
{
    public const ushort WanderingDecisionIntervalTicks = 250;
    public const ushort TrackingDurationTicks = 250;
    public const float TrackingSpeed = SimulationDefinition.BaseMaximumSpeed;

    private const ulong WanderingDecisionPurpose = 7;
    private const ulong TrackingTargetPurpose = 8;
    private const ulong WanderingSpeedPurpose = 4;
    private const ulong WanderingAzimuthPurpose = 5;
    private const ulong WanderingElevationPurpose = 6;

    public static WanderingDecision DecideWandering(
        ulong seed,
        ulong shipId,
        ulong decisionOrdinal) => (WanderingDecision)(DeterministicRandom.UniformIndex(
            seed,
            shipId,
            decisionOrdinal,
            WanderingDecisionPurpose,
            exclusiveUpperBound: 3) + 1);

    public static MotionSnapshot CreateWanderingMotion(
        ulong seed,
        ulong shipId,
        ulong decisionOrdinal)
    {
        var direction = DeterministicRandom.UnitDirection(
            seed,
            shipId,
            decisionOrdinal,
            WanderingAzimuthPurpose,
            WanderingElevationPurpose);
        var speed = DeterministicRandom.UnitInterval(
            seed,
            shipId,
            decisionOrdinal,
            WanderingSpeedPurpose) * SimulationDefinition.MaximumWanderingSpeed;
        return direction with { Speed = speed };
    }

    public static int SelectTrackingTargetIndex(
        ulong seed,
        ulong shipId,
        ulong decisionOrdinal,
        int rosterCount,
        int sourceIndex)
    {
        if (rosterCount < 2)
        {
            return -1;
        }

        if (sourceIndex < 0 || sourceIndex >= rosterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }

        var selected = DeterministicRandom.UniformIndex(
            seed,
            shipId,
            decisionOrdinal,
            TrackingTargetPurpose,
            rosterCount - 1);
        return selected < sourceIndex ? selected : selected + 1;
    }

    public static MotionSnapshot CreateTrackingMotion(
        PositionSnapshot source,
        PositionSnapshot target,
        MotionSnapshot priorMotion)
    {
        var directionX = target.X - source.X;
        var directionY = target.Y - source.Y;
        var directionZ = target.Z - source.Z;
        var lengthSquared =
            (directionX * directionX) +
            (directionY * directionY) +
            (directionZ * directionZ);
        if (lengthSquared <= 0f)
        {
            return priorMotion with { Speed = TrackingSpeed };
        }

        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        return new MotionSnapshot(
            directionX * inverseLength,
            directionY * inverseLength,
            directionZ * inverseLength,
            TrackingSpeed);
    }
}
