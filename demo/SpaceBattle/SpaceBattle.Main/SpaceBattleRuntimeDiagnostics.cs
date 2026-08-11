using System.Collections.ObjectModel;

namespace SpaceBattle;

public sealed record SpaceBattleRuntimeDiagnosticsSnapshot(
    ulong CompletedTicks,
    int ViewMembershipCount,
    int CombatViewMembershipCount,
    int ShipRosterCount,
    int TickWorksetCount,
    IReadOnlyDictionary<long, int> OwnerLockIndex,
    IReadOnlyDictionary<long, int> TargetLockIndex,
    int DerivedAliveShipCount,
    int DerivedActiveLockCount,
    long RuntimeShipViewRefreshCount,
    long CombatShipViewRefreshCount,
    long RuntimeShipViewAddedCount,
    long CombatShipViewAddedCount,
    long RuntimeShipViewRemovedCount,
    long CombatShipViewRemovedCount,
    IReadOnlyDictionary<string, int> ConsumerProcessingCounts)
{
    public int OwnerLockIndexCount => OwnerLockIndex.Count;

    public int TargetLockIndexCount => TargetLockIndex.Count;

    internal static SpaceBattleRuntimeDiagnosticsSnapshot Create(
        ulong completedTicks,
        int viewMembershipCount,
        int combatViewMembershipCount,
        int shipRosterCount,
        int tickWorksetCount,
        IReadOnlyDictionary<long, int> ownerLockIndex,
        IReadOnlyDictionary<long, int> targetLockIndex,
        int derivedAliveShipCount,
        int derivedActiveLockCount,
        long runtimeShipViewRefreshCount,
        long combatShipViewRefreshCount,
        long runtimeShipViewAddedCount,
        long combatShipViewAddedCount,
        long runtimeShipViewRemovedCount,
        long combatShipViewRemovedCount,
        IReadOnlyDictionary<string, int> consumerProcessingCounts)
        => new(
            completedTicks,
            viewMembershipCount,
            combatViewMembershipCount,
            shipRosterCount,
            tickWorksetCount,
            new ReadOnlyDictionary<long, int>(new Dictionary<long, int>(ownerLockIndex)),
            new ReadOnlyDictionary<long, int>(new Dictionary<long, int>(targetLockIndex)),
            derivedAliveShipCount,
            derivedActiveLockCount,
            runtimeShipViewRefreshCount,
            combatShipViewRefreshCount,
            runtimeShipViewAddedCount,
            combatShipViewAddedCount,
            runtimeShipViewRemovedCount,
            combatShipViewRemovedCount,
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(consumerProcessingCounts)));
}
