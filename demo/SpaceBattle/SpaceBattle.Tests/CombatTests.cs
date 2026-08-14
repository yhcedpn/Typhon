using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class CombatTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void WeaponPhase_IsStableDistributedAndPeriodic()
    {
        var phases = Enumerable.Range(1, 300)
            .Select(static entityKey => SpaceBattleCombat.FirstWeaponPhase(entityKey))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(phases, Has.All.InRange(0, SpaceBattleCombat.WeaponPeriodTicks - 1));
            for (var phase = 0; phase < SpaceBattleCombat.WeaponPeriodTicks; phase++)
            {
                Assert.That(phases, Does.Contain(phase));
            }
        });

        for (var entityKey = 1; entityKey <= 300; entityKey++)
        {
            var phase = SpaceBattleCombat.FirstWeaponPhase(entityKey);
            Assert.That(SpaceBattleCombat.FirstWeaponPhase(entityKey), Is.EqualTo(phase));
            var firingTick = phase;
            Assert.That(SpaceBattleCombat.IsWeaponUseTick(entityKey, firingTick), Is.True);
            Assert.That(SpaceBattleCombat.IsWeaponUseTick(entityKey, firingTick + 1), Is.False);
            Assert.That(SpaceBattleCombat.IsWeaponUseTick(entityKey, firingTick + SpaceBattleCombat.WeaponPeriodTicks), Is.True);
        }
    }

    [Test]
    public void WeaponDamage_UsesInclusive100BoundaryAndNoDamageOutsideIt()
    {
        const long entityKey = 17;
        var phase = SpaceBattleCombat.FirstWeaponPhase(entityKey);

        Assert.Multiple(() =>
        {
            Assert.That(SpaceBattleCombat.WeaponRange, Is.EqualTo(100f));
            Assert.That(SpaceBattleCombat.WeaponDamage, Is.EqualTo(250u));
            Assert.That(SpaceBattleCombat.WeaponPeriodTicks, Is.EqualTo(15));
            Assert.That(SpaceBattleCombat.WeaponSpeed, Is.EqualTo(200f));
            Assert.That(SpaceBattleCombat.DamageForDistance(100d * 100d), Is.EqualTo(250u));
            Assert.That(SpaceBattleCombat.DamageForDistance(100.0001d * 100.0001d), Is.Zero);
            Assert.That(SpaceBattleCombat.DamageForDistance(150d * 150d), Is.Zero);
            Assert.That(SpaceBattleCombat.DamageForDistance(200d * 200d), Is.Zero);
            Assert.That(SpaceBattleCombat.IsWeaponUseTick(entityKey, phase), Is.True);
        });
    }

    [Test]
    public void IncomingDamage_ReducesWorkerLanesAndClearsOnlyTouchedKeys()
    {
        var definition = CreateDefinition(shipCount: 3, maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        using var engine = SpaceBattleDatabase.Open(definition, SpaceBattlePaths.DatabaseDirectory(_root));
        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 3);
        state.PrepareTick(1);

        state.RecordIncomingDamage(workerId: 0, targetEntityKey: 1, damage: 250);
        state.RecordIncomingDamage(workerId: 0, targetEntityKey: 1, damage: 250);
        state.RecordIncomingDamage(workerId: 1, targetEntityKey: 1, damage: 250);
        state.RecordIncomingDamage(workerId: 2, targetEntityKey: 2, damage: 250);

        Assert.Multiple(() =>
        {
            Assert.That(state.ReduceIncomingDamage(1), Is.EqualTo(750u));
            Assert.That(state.ReduceIncomingDamage(2), Is.EqualTo(250u));
            Assert.That(state.IncomingDamageTouchedCount(0), Is.EqualTo(1));
            Assert.That(state.IncomingDamageTouchedCount(1), Is.EqualTo(1));
            Assert.That(state.IncomingDamageTouchedCount(2), Is.EqualTo(1));
            Assert.That(state.IncomingDamageTouchedKeys(0).ToArray(), Is.EqualTo(new long[] { 1 }));
        });

        state.ClearIncomingDamage();

        Assert.Multiple(() =>
        {
            Assert.That(state.ReduceIncomingDamage(1), Is.Zero);
            Assert.That(state.ReduceIncomingDamage(2), Is.Zero);
            Assert.That(state.IncomingDamageTouchedCount(0), Is.Zero);
            Assert.That(state.IncomingDamageTouchedCount(1), Is.Zero);
            Assert.That(state.IncomingDamageTouchedCount(2), Is.Zero);
            Assert.That(state.ReadIncomingDamage(0, 3), Is.Zero);
        });
    }

    [Test]
    public void FourShots_PullPushEquivalentAndBothDeathsStayPendingUntilReap()
    {
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        using var engine = SpaceBattleDatabase.Open(definition, SpaceBattlePaths.DatabaseDirectory(_root));
        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 2);
        state.PrepareTick(1);
        PublishFrames(state, engine, 2);

        for (var shot = 0; shot < 4; shot++)
        {
            state.RecordIncomingDamage(workerId: 0, targetEntityKey: 1, damage: SpaceBattleCombat.WeaponDamage);
            state.RecordIncomingDamage(workerId: 1, targetEntityKey: 2, damage: SpaceBattleCombat.WeaponDamage);
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.ReduceIncomingDamage(1), Is.EqualTo(1_000u));
            Assert.That(state.ReduceIncomingDamage(2), Is.EqualTo(1_000u));
        });
        state.ClearIncomingDamage();
        state.RecordIncomingDamage(workerId: 0, targetEntityKey: 1, damage: 1_000u);
        state.RecordIncomingDamage(workerId: 1, targetEntityKey: 2, damage: 1_000u);
        Assert.Multiple(() =>
        {
            Assert.That(state.ReduceIncomingDamage(1), Is.EqualTo(1_000u));
            Assert.That(state.ReduceIncomingDamage(2), Is.EqualTo(1_000u));
        });
        state.ClearIncomingDamage();

        state.UpdateFrameHealth(1, 0);
        state.UpdateFrameHealth(2, 0);
        Assert.That(state.TryGetFrameIndex(1, out var lethalFrameIndex), Is.True);
        Assert.That(MovementSystem.CanMove(state.GetFrame(lethalFrameIndex), pendingReap: true), Is.False);
        state.MarkForReap(workerId: 0, entityKey: 1);
        state.MarkForReap(workerId: 1, entityKey: 2);

        Span<EntityId> deaths = stackalloc EntityId[2];
        var deathCount = state.CopyPendingReaps(deaths);
        var deathKeys = deaths[..deathCount].ToArray().Select(static id => id.EntityKey).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(deathCount, Is.EqualTo(2));
            Assert.That(deathKeys, Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(state.BuildPublishedSnapshot().Ships, Is.Empty);
        });

        state.ClearIncomingDamage();
        Assert.Multiple(() =>
        {
            Assert.That(state.ReduceIncomingDamage(1), Is.Zero);
            Assert.That(state.ReduceIncomingDamage(2), Is.Zero);
            Assert.That(state.IsPendingReap(1), Is.True);
            Assert.That(state.IsPendingReap(2), Is.True);
        });
    }

    [Test]
    public void PendingDeaths_AreCopiedInFixedWorkerOrder()
    {
        var definition = CreateDefinition(shipCount: 3, maximumCompletedTicks: 1);
        SpaceBattleHost.BootstrapOnly(definition, _root, CancellationToken.None, new RecordingSink());
        using var engine = SpaceBattleDatabase.Open(definition, SpaceBattlePaths.DatabaseDirectory(_root));
        using var state = new SpaceBattleSimulationState(engine, definition, new RecordingSink(), workerCount: 2);
        state.PrepareTick(1);
        PublishFrames(state, engine, 3);

        state.MarkForReap(workerId: 1, entityKey: 2);
        state.MarkForReap(workerId: 0, entityKey: 1);
        state.MarkForReap(workerId: 1, entityKey: 3);

        Span<EntityId> destination = stackalloc EntityId[3];
        var count = state.CopyPendingReaps(destination);
        Assert.That(destination[..count].ToArray().Select(static id => id.EntityKey), Is.EqualTo(new long[] { 1, 2, 3 }));
        Assert.That(state.PendingReapCount, Is.EqualTo(3));

        state.CompleteReaps();
        Assert.That(state.PendingReapCount, Is.Zero);
    }

    [Test]
    public void CloseShips_ConsumePeriodicDamageAndAreReaped()
    {
        var definition = new SimulationDefinition(
            shipCount: 2,
            seed: SimulationDefinition.DefaultSeed,
            worldWidth: 1f,
            worldHeight: 1f,
            worldDepth: 1f,
            maximumHealth: 1_000,
            maximumCompletedTicks: 130);

        var result = SpaceBattleHost.Run(definition, _root, CancellationToken.None, new RecordingSink());

        Assert.That(result.RemainingShips, Is.LessThan(2));
        Assert.That(SpaceBattleHost.ReadShipCount(definition, _root), Is.EqualTo(result.RemainingShips));
    }

    private static SimulationDefinition CreateDefinition(int shipCount, ulong maximumCompletedTicks) =>
        new(
            shipCount: shipCount,
            seed: SimulationDefinition.DefaultSeed,
            worldWidth: 1_000f,
            worldHeight: 1_000f,
            worldDepth: 400f,
            maximumHealth: 1_000,
            maximumCompletedTicks: maximumCompletedTicks);

    private static void PublishFrames(SpaceBattleSimulationState state, DatabaseEngine engine, int shipCount)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        foreach (var entityId in transaction.QueryExact<Ship>().Execute().OrderBy(static id => id.EntityKey).Take(shipCount))
        {
            var entity = transaction.Open(entityId);
            state.PublishFrame(entityId, new ShipSnapshot(
                entityId.EntityKey,
                entity.Read(Ship.Hull),
                entity.Read(Ship.Motion),
                entity.Read(Ship.Vitals),
                entity.Read(Ship.Targeting),
                entity.Read(Ship.Behavior)));
        }
    }

    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
