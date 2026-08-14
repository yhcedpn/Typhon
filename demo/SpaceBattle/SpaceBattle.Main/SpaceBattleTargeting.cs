using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal readonly struct TargetingResult
{
    public TargetingResult(EntityId entityId, double distanceSquared)
    {
        EntityId = entityId;
        DistanceSquared = distanceSquared;
    }

    public EntityId EntityId { get; }

    public double DistanceSquared { get; }
}

internal readonly struct TargetingQueryMetrics
{
    public TargetingQueryMetrics(
        int directQueryCount,
        int batchedQueryCount,
        int gatherCandidateCount,
        int exactDistanceTestCount)
    {
        DirectQueryCount = directQueryCount;
        BatchedQueryCount = batchedQueryCount;
        GatherCandidateCount = gatherCandidateCount;
        ExactDistanceTestCount = exactDistanceTestCount;
    }

    public int DirectQueryCount { get; }

    public int BatchedQueryCount { get; }

    public int GatherCandidateCount { get; }

    public int ExactDistanceTestCount { get; }
}

internal static class SpaceBattleTargeting
{
    public const float LockRange = 200f;
    public const float WeaponRange = 100f;
    public const float ApproachSpeed = SpaceBattleMath.MaximumWanderSpeed;

    private const int DirectBatchThreshold = 4;
    private const float TemporaryBinSize = 50f;
    private const double LockRangeSquared = LockRange * LockRange;

    public static EntityId FindNearest(
        Transaction acquisitionTransaction,
        SpaceBattleSimulationState state,
        in ShipSnapshot source,
        out double distanceSquared)
    {
        ArgumentNullException.ThrowIfNull(acquisitionTransaction);
        ArgumentNullException.ThrowIfNull(state);

        return FindNearestDirect(acquisitionTransaction, state, source, out distanceSquared, out _);
    }

    public static void FindNearestBatch(
        Transaction acquisitionTransaction,
        SpaceBattleSimulationState state,
        ReadOnlySpan<ShipSnapshot> sources,
        Span<TargetingResult> results,
        out TargetingQueryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(acquisitionTransaction);
        ArgumentNullException.ThrowIfNull(state);
        if (results.Length < sources.Length)
        {
            throw new ArgumentException("目标结果缓冲区不足。", nameof(results));
        }

        if (sources.Length <= DirectBatchThreshold)
        {
            var exactDistanceTestCount = 0;
            for (var index = 0; index < sources.Length; index++)
            {
                var nearest = FindNearestDirect(
                    acquisitionTransaction,
                    state,
                    sources[index],
                    out var distanceSquared,
                    out var sourceDistanceTestCount);
                results[index] = new TargetingResult(nearest, distanceSquared);
                exactDistanceTestCount += sourceDistanceTestCount;
            }

            metrics = new TargetingQueryMetrics(
                sources.Length,
                0,
                0,
                exactDistanceTestCount);
            return;
        }

        var gatherBounds = ExpandedSourceBounds(sources);
        var candidateIds = acquisitionTransaction.QueryExact<Ship>()
            .WhereInAABB<Hull>(
                gatherBounds.MinX,
                gatherBounds.MinY,
                gatherBounds.MinZ,
                gatherBounds.MaxX,
                gatherBounds.MaxY,
                gatherBounds.MaxZ)
            .Execute();

        // 分箱是本次 gather 的临时窄化结构；每次批次结束后随局部变量一起释放。
        var bins = new Dictionary<TemporaryBinKey, List<EntityId>>(candidateIds.Count);
        foreach (var candidateId in candidateIds)
        {
            if (!state.TryGetFrameIndex(candidateId.EntityKey, out var frameIndex))
            {
                continue;
            }

            var position = PositionOf(state.GetFrame(frameIndex));
            var key = new TemporaryBinKey(
                BinCoordinate(position.X),
                BinCoordinate(position.Y),
                BinCoordinate(position.Z));
            if (!bins.TryGetValue(key, out var bucket))
            {
                bucket = [];
                bins.Add(key, bucket);
            }

            bucket.Add(candidateId);
        }

        var exactDistanceTests = 0;
        for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            ref readonly var source = ref sources[sourceIndex];
            var sourcePosition = PositionOf(source);
            var minimumBinX = BinCoordinate(sourcePosition.X - LockRange);
            var maximumBinX = BinCoordinate(sourcePosition.X + LockRange);
            var minimumBinY = BinCoordinate(sourcePosition.Y - LockRange);
            var maximumBinY = BinCoordinate(sourcePosition.Y + LockRange);
            var minimumBinZ = BinCoordinate(sourcePosition.Z - LockRange);
            var maximumBinZ = BinCoordinate(sourcePosition.Z + LockRange);

            var nearest = EntityId.Null;
            var nearestDistanceSquared = double.PositiveInfinity;
            for (var binX = minimumBinX; binX <= maximumBinX; binX++)
            {
                for (var binY = minimumBinY; binY <= maximumBinY; binY++)
                {
                    for (var binZ = minimumBinZ; binZ <= maximumBinZ; binZ++)
                    {
                        if (!bins.TryGetValue(new TemporaryBinKey(binX, binY, binZ), out var bucket))
                        {
                            continue;
                        }

                        foreach (var candidateId in bucket)
                        {
                            ConsiderCandidate(
                                state,
                                source,
                                candidateId,
                                ref nearest,
                                ref nearestDistanceSquared,
                                ref exactDistanceTests);
                        }
                    }
                }
            }

            results[sourceIndex] = new TargetingResult(
                nearest,
                nearest.IsNull ? double.PositiveInfinity : nearestDistanceSquared);
        }

        metrics = new TargetingQueryMetrics(
            0,
            1,
            candidateIds.Count,
            exactDistanceTests);
    }

    private static EntityId FindNearestDirect(
        Transaction acquisitionTransaction,
        SpaceBattleSimulationState state,
        in ShipSnapshot source,
        out double distanceSquared,
        out int exactDistanceTestCount)
    {
        var sourcePosition = PositionOf(source);
        var candidateIds = acquisitionTransaction.QueryExact<Ship>()
            .WhereNearby<Hull>(sourcePosition.X, sourcePosition.Y, sourcePosition.Z, LockRange)
            .Execute();

        var nearest = EntityId.Null;
        var nearestDistanceSquared = double.PositiveInfinity;
        exactDistanceTestCount = 0;
        foreach (var candidateId in candidateIds)
        {
            ConsiderCandidate(
                state,
                source,
                candidateId,
                ref nearest,
                ref nearestDistanceSquared,
                ref exactDistanceTestCount);
        }

        distanceSquared = nearest.IsNull ? double.PositiveInfinity : nearestDistanceSquared;
        return nearest;
    }

    private static void ConsiderCandidate(
        SpaceBattleSimulationState state,
        in ShipSnapshot source,
        EntityId candidateId,
        ref EntityId nearest,
        ref double nearestDistanceSquared,
        ref int exactDistanceTestCount)
    {
        var candidateKey = candidateId.EntityKey;
        if (candidateKey == source.EntityKey || !state.TryGetFrameIndex(candidateKey, out var frameIndex))
        {
            return;
        }

        ref readonly var candidate = ref state.GetFrame(frameIndex);
        if (candidate.Vitals.CurrentHealth == 0)
        {
            return;
        }

        exactDistanceTestCount++;
        var candidateDistanceSquared = DistanceSquared(source, candidate);
        if (candidateDistanceSquared > LockRangeSquared ||
            (candidateDistanceSquared > nearestDistanceSquared) ||
            (candidateDistanceSquared == nearestDistanceSquared && candidateKey >= nearest.EntityKey))
        {
            return;
        }

        nearest = candidateId;
        nearestDistanceSquared = candidateDistanceSquared;
    }

    private static AABB3F ExpandedSourceBounds(ReadOnlySpan<ShipSnapshot> sources)
    {
        var minX = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        foreach (ref readonly var source in sources)
        {
            var bounds = source.Hull.Bounds;
            minX = MathF.Min(minX, bounds.MinX);
            maxX = MathF.Max(maxX, bounds.MaxX);
            minY = MathF.Min(minY, bounds.MinY);
            maxY = MathF.Max(maxY, bounds.MaxY);
            minZ = MathF.Min(minZ, bounds.MinZ);
            maxZ = MathF.Max(maxZ, bounds.MaxZ);
        }

        return new AABB3F
        {
            MinX = minX - LockRange,
            MaxX = maxX + LockRange,
            MinY = minY - LockRange,
            MaxY = maxY + LockRange,
            MinZ = minZ - LockRange,
            MaxZ = maxZ + LockRange,
        };
    }

    private static int BinCoordinate(float coordinate) => (int)MathF.Floor(coordinate / TemporaryBinSize);

    private readonly struct TemporaryBinKey : IEquatable<TemporaryBinKey>
    {
        public TemporaryBinKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        public bool Equals(TemporaryBinKey other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is TemporaryBinKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
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
