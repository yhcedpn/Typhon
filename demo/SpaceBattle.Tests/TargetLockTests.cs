using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class TargetLockTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "SpaceBattle.Tests",
            TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public void Start_CombatShipsCreateOneAcquiringLockForAnInRangeRosterCandidate()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "locks.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot snapshot = simulation.WaitForSnapshot(252, TimeSpan.FromSeconds(15));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TargetLocks, Is.Not.Empty);
            Assert.That(snapshot.TargetLocks.Select(static targetLock => targetLock.OwnerEntityKey),
                Is.Unique);
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(
                targetLock => targetLock.Status == TargetLockStatus.Acquiring &&
                    targetLock.TicksRemaining == BehaviorRules.LockAcquisitionDurationTicks));
        });

        foreach (var targetLock in snapshot.TargetLocks)
        {
            var owner = snapshot.Ships.Single(ship => ship.EntityKey == targetLock.OwnerEntityKey);
            var target = snapshot.Ships.Single(ship => ship.EntityKey == targetLock.TargetEntityKey);
            var distanceSquared =
                ((target.Position.X - owner.Position.X) * (target.Position.X - owner.Position.X)) +
                ((target.Position.Y - owner.Position.Y) * (target.Position.Y - owner.Position.Y)) +
                ((target.Position.Z - owner.Position.Z) * (target.Position.Z - owner.Position.Z));

            Assert.That(distanceSquared, Is.LessThanOrEqualTo(
                BehaviorRules.LockRange * BehaviorRules.LockRange));
        }
    }

    [Test]
    public void Start_WhenAcquisitionCompletes_LockedTargetAuthorizesItsOwnerWeapon()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-authorization-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "authorization.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot snapshot = simulation.WaitForSnapshot(303, TimeSpan.FromSeconds(20));
        HashSet<long> lockedOwners = snapshot.TargetLocks
            .Where(static targetLock => targetLock.Status == TargetLockStatus.Locked)
            .Select(static targetLock => targetLock.OwnerEntityKey)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(lockedOwners, Is.Not.Empty);
            Assert.That(snapshot.Ships
                    .Where(ship => lockedOwners.Contains(ship.EntityKey))
                    .Select(static ship => ship.WeaponEnabled),
                Is.All.True);
        });
    }

    [Test]
    public void Start_WhenLockedWeaponsFire_ResolvesDamageAndDeathsInTheSameTick()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "damage-resolution-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "damage-resolution.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [299, 300, 301, 302, 303],
            TimeSpan.FromSeconds(20));
        int resolutionSnapshotIndex = snapshots
            .Select(static (snapshot, index) => (Snapshot: snapshot, Index: index))
            .First(item => item.Snapshot.KillParticipations.Count > 0)
            .Index;
        InitialWorldSnapshot previousSnapshot = snapshots[resolutionSnapshotIndex - 1];
        InitialWorldSnapshot snapshot = snapshots[resolutionSnapshotIndex];
        HashSet<long> liveShipKeys = snapshot.Ships.Select(static ship => ship.EntityKey).ToHashSet();
        HashSet<long> previousLiveShipKeys = previousSnapshot.Ships
            .Select(static ship => ship.EntityKey)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Ships.Count, Is.LessThan(definition.ShipCount));
            Assert.That(snapshot.Ships.Select(static ship => ship.Health), Has.All.GreaterThan(0));
            Assert.That(snapshot.KillParticipations, Is.Not.Empty);
            Assert.That(snapshot.KillParticipations.Select(static participation => participation.TargetEntityKey),
                Has.All.Matches<long>(targetEntityKey =>
                    previousLiveShipKeys.Contains(targetEntityKey) &&
                    !liveShipKeys.Contains(targetEntityKey)));
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(targetLock =>
                liveShipKeys.Contains(targetLock.OwnerEntityKey) &&
                liveShipKeys.Contains(targetLock.TargetEntityKey)));
        });
    }

    [Test]
    public void Start_WhenShipsAreDamaged_SurvivorsEscapeAndCancelTheirOwnLocks()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "escape-reaction-test",
            maximumHealth: 1_000);
        string databaseLocation = Path.Combine(_temporaryDirectory, "escape-reaction.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot reactionSnapshot = simulation.WaitForSnapshots(
                Enumerable.Range(299, 12).Select(static tick => (ulong)tick).ToArray(),
                TimeSpan.FromSeconds(20))
            .First(snapshot => snapshot.Ships.Any(ship => ship.Mode == BehaviorMode.Escaping));
        ShipSnapshot[] escapingShips = reactionSnapshot.Ships
            .Where(static ship => ship.Mode == BehaviorMode.Escaping)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(escapingShips, Is.Not.Empty);
            Assert.That(escapingShips, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Health < definition.MaximumHealth &&
                ship.ModeTicksRemaining <= BehaviorRules.EscapingDurationTicks &&
                ship.TrackingTargetIsNull &&
                !ship.WeaponEnabled &&
                ship.AfterburnerEnabled &&
                ship.Motion.Speed == BehaviorRules.EscapingSpeed));
            Assert.That(reactionSnapshot.TargetLocks
                    .Where(targetLock => escapingShips.Any(ship => ship.EntityKey == targetLock.OwnerEntityKey)),
                Has.All.Matches<TargetLockSnapshot>(targetLock =>
                    targetLock.Status == TargetLockStatus.Releasing &&
                    targetLock.TicksRemaining == BehaviorRules.LockReleaseDurationTicks));
        });
    }

    [Test]
    public void Start_WhenWeaponsKillShips_ReportsEveryKillParticipationForTheSameTick()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "kill-participation-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "kill-participation.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [300, 301, 302, 303],
            TimeSpan.FromSeconds(20));
        InitialWorldSnapshot resolutionSnapshot = snapshots
            .First(snapshot => snapshot.KillParticipations.Count > 0);
        HashSet<long> liveShipKeys = resolutionSnapshot.Ships
            .Select(static ship => ship.EntityKey)
            .ToHashSet();
        IReadOnlyList<(long Attacker, long Target)> participations = resolutionSnapshot.KillParticipations
            .Select(static participation => (participation.AttackerEntityKey, participation.TargetEntityKey))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(participations, Is.Not.Empty);
            Assert.That(participations, Is.Unique);
            Assert.That(resolutionSnapshot.KillParticipations.Select(static participation => participation.TargetEntityKey),
                Has.All.Matches<long>(targetEntityKey => !liveShipKeys.Contains(targetEntityKey)));
        });

        HashSet<long> woundedShipKeys = resolutionSnapshot.Ships
            .Where(static ship => ship.Mode == BehaviorMode.Escaping)
            .Select(static ship => ship.EntityKey)
            .ToHashSet();
        ShipSnapshot[] disengagingParticipants = resolutionSnapshot.KillParticipations
            .Select(static participation => participation.AttackerEntityKey)
            .Distinct()
            .Where(attackerEntityKey => !woundedShipKeys.Contains(attackerEntityKey))
            .Where(liveShipKeys.Contains)
            .Select(attackerEntityKey => resolutionSnapshot.Ships.Single(ship => ship.EntityKey == attackerEntityKey))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(disengagingParticipants, Is.Not.Empty);
            Assert.That(disengagingParticipants, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Mode == BehaviorMode.Disengaging &&
                ship.ModeTicksRemaining == BehaviorRules.DisengagingDurationTicks &&
                ship.Motion.Speed == BehaviorRules.DisengagingSpeed &&
                !ship.WeaponEnabled &&
                !ship.AfterburnerEnabled));
        });
    }

    [Test]
    public void Start_WhenCombatEnds_ReleasesLocksBeforeFreeingTheirSlots()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-release-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "release.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [502, 527],
            TimeSpan.FromSeconds(40));
        InitialWorldSnapshot releasingSnapshot = snapshots[0];
        Assert.Multiple(() =>
        {
            Assert.That(releasingSnapshot.TargetLocks, Is.Not.Empty);
            Assert.That(releasingSnapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(
                targetLock => targetLock.Status == TargetLockStatus.Releasing &&
                    targetLock.TicksRemaining == BehaviorRules.LockReleaseDurationTicks));
            Assert.That(releasingSnapshot.Ships
                    .Where(ship => releasingSnapshot.TargetLocks.Any(targetLock =>
                        targetLock.OwnerEntityKey == ship.EntityKey))
                    .Select(static ship => ship.WeaponEnabled),
                Is.All.False);
        });

        HashSet<long> releasingLockIds = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.EntityKey)
            .ToHashSet();
        HashSet<long> releasingOwners = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.OwnerEntityKey)
            .ToHashSet();
        InitialWorldSnapshot releasedSnapshot = snapshots[1];
        Assert.Multiple(() =>
        {
            Assert.That(releasedSnapshot.TargetLocks
                    .Where(targetLock => releasingLockIds.Contains(targetLock.EntityKey)),
                Is.Empty);
            Assert.That(releasedSnapshot.TargetLocks
                    .Where(targetLock => releasingOwners.Contains(targetLock.OwnerEntityKey)),
                Is.Empty);
        });
    }

    private static SimulationDefinition CreateCombatDefinition(
        string runName,
        uint maximumHealth = 1_000) => new(
        runName: runName,
        shipCount: 64,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 100f,
        maximumHealth: maximumHealth,
        stagingTicks: 0,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
