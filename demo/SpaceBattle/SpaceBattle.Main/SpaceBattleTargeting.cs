using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

internal static class SpaceBattleTargeting
{
    public const float LockRange = 200f;
    public const float WeaponRange = 100f;
    public const float ApproachSpeed = SpaceBattleMath.MaximumWanderSpeed;

    private const double LockRangeSquared = LockRange * LockRange;

    public static EntityId FindNearest(
        Transaction acquisitionTransaction,
        SpaceBattleSimulationState state,
        in ShipSnapshot source,
        out double distanceSquared)
    {
        ArgumentNullException.ThrowIfNull(acquisitionTransaction);
        ArgumentNullException.ThrowIfNull(state);

        var sourcePosition = PositionOf(source);
        var candidateIds = acquisitionTransaction.QueryExact<Ship>()
            .WhereNearby<Hull>(sourcePosition.X, sourcePosition.Y, sourcePosition.Z, LockRange)
            .Execute();

        var nearest = EntityId.Null;
        var nearestDistanceSquared = double.PositiveInfinity;
        foreach (var candidateId in candidateIds)
        {
            var candidateKey = candidateId.EntityKey;
            if (candidateKey == source.EntityKey || !state.TryGetFrameIndex(candidateKey, out var frameIndex))
            {
                continue;
            }

            ref readonly var candidate = ref state.GetFrame(frameIndex);
            if (candidate.Vitals.CurrentHealth == 0)
            {
                continue;
            }

            var candidateDistanceSquared = DistanceSquared(source, candidate);
            if (candidateDistanceSquared > LockRangeSquared ||
                (candidateDistanceSquared > nearestDistanceSquared) ||
                (candidateDistanceSquared == nearestDistanceSquared && candidateKey >= nearest.EntityKey))
            {
                continue;
            }

            nearest = candidateId;
            nearestDistanceSquared = candidateDistanceSquared;
        }

        distanceSquared = nearest.IsNull ? double.PositiveInfinity : nearestDistanceSquared;
        return nearest;
    }

    public static long FindNearestBruteForce(
        IReadOnlyList<ShipSnapshot> frames,
        in ShipSnapshot source,
        out double distanceSquared)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var nearestKey = 0L;
        var nearestDistanceSquared = double.PositiveInfinity;
        foreach (var candidate in frames)
        {
            if (candidate.EntityKey == source.EntityKey || candidate.Vitals.CurrentHealth == 0)
            {
                continue;
            }

            var candidateDistanceSquared = DistanceSquared(source, candidate);
            if (candidateDistanceSquared > LockRangeSquared ||
                (candidateDistanceSquared > nearestDistanceSquared) ||
                (candidateDistanceSquared == nearestDistanceSquared && candidate.EntityKey >= nearestKey))
            {
                continue;
            }

            nearestKey = candidate.EntityKey;
            nearestDistanceSquared = candidateDistanceSquared;
        }

        distanceSquared = nearestKey == 0 ? double.PositiveInfinity : nearestDistanceSquared;
        return nearestKey;
    }

    public static bool TryReadTarget(
        SpaceBattleSimulationState state,
        in ShipSnapshot source,
        out ShipSnapshot target,
        out double distanceSquared)
    {
        var targetKey = EntityKeyFromRaw(source.Targeting.TargetEntityId);
        if (targetKey == 0 || targetKey == source.EntityKey || !state.TryGetFrameIndex(targetKey, out var targetIndex))
        {
            target = default;
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        target = state.GetFrame(targetIndex);
        if (target.Vitals.CurrentHealth == 0)
        {
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        distanceSquared = DistanceSquared(source, target);
        if (distanceSquared > LockRangeSquared)
        {
            return false;
        }

        return true;
    }

    public static long PackRaw(EntityId entityId) =>
        unchecked((long)(((ulong)entityId.EntityKey << 16) | entityId.ArchetypeId));

    public static long EntityKeyFromRaw(long rawEntityId) =>
        unchecked((long)((ulong)rawEntityId >> 16));

    public static double DistanceSquared(in ShipSnapshot source, in ShipSnapshot target)
    {
        var sourcePosition = PositionOf(source);
        var targetPosition = PositionOf(target);
        var dx = (double)targetPosition.X - sourcePosition.X;
        var dy = (double)targetPosition.Y - sourcePosition.Y;
        var dz = (double)targetPosition.Z - sourcePosition.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    public static Vector3 PositionOf(in ShipSnapshot ship) => new(
        ship.Hull.Bounds.MinX,
        ship.Hull.Bounds.MinY,
        ship.Hull.Bounds.MinZ);
}
