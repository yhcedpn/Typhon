using System.Collections.ObjectModel;

namespace SpaceBattle;

public sealed record SpaceBattleRuntimeDiagnosticsSnapshot(
    ulong CompletedTicks,
    int ViewMembershipCount,
    int ShipRosterCount,
    int TickWorksetCount,
    IReadOnlyDictionary<long, int> OwnerLockIndex,
    IReadOnlyDictionary<long, int> TargetLockIndex,
    int DerivedAliveShipCount,
    int DerivedActiveLockCount,
    IReadOnlyDictionary<string, int> ConsumerProcessingCounts)
{
    public int OwnerLockIndexCount => OwnerLockIndex.Count;

    public int TargetLockIndexCount => TargetLockIndex.Count;

    internal static SpaceBattleRuntimeDiagnosticsSnapshot Create(
        ulong completedTicks,
        int viewMembershipCount,
        int shipRosterCount,
        int tickWorksetCount,
        IReadOnlyDictionary<long, int> ownerLockIndex,
        IReadOnlyDictionary<long, int> targetLockIndex,
        int derivedAliveShipCount,
        int derivedActiveLockCount,
        IReadOnlyDictionary<string, int> consumerProcessingCounts)
        => new(
            completedTicks,
            viewMembershipCount,
            shipRosterCount,
            tickWorksetCount,
            new ReadOnlyDictionary<long, int>(new Dictionary<long, int>(ownerLockIndex)),
            new ReadOnlyDictionary<long, int>(new Dictionary<long, int>(targetLockIndex)),
            derivedAliveShipCount,
            derivedActiveLockCount,
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(consumerProcessingCounts)));
}
