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
        var definition = new SimulationDefinition(
            runName: "target-lock-test",
            shipCount: 64,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 100f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "locks.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var snapshot = simulation.WaitForSnapshot(252, TimeSpan.FromSeconds(15));

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
        var definition = new SimulationDefinition(
            runName: "target-lock-authorization-test",
            shipCount: 64,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 100f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "authorization.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var snapshot = simulation.WaitForSnapshot(303, TimeSpan.FromSeconds(20));
        var lockedOwners = snapshot.TargetLocks
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
    public void Start_WhenCombatEnds_ReleasesLocksBeforeFreeingTheirSlots()
    {
        var definition = new SimulationDefinition(
            runName: "target-lock-release-test",
            shipCount: 64,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 100f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "release.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var snapshots = simulation.WaitForSnapshots([502, 527], TimeSpan.FromSeconds(40));
        var releasingSnapshot = snapshots[0];
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

        var releasingLockIds = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.EntityKey)
            .ToHashSet();
        var releasingOwners = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.OwnerEntityKey)
            .ToHashSet();
        var releasedSnapshot = snapshots[1];
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

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
