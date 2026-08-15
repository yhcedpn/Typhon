using System.Numerics;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class WanderingTests
{
    [Test]
    public void RuntimeRandom_IsPureAndProducesUniformSphereDirectionsAndBoundedSpeeds()
    {
        const ulong seed = 0x1234_5678_9ABC_DEF0UL;
        var first = SpaceBattleMath.RandomDirection(seed, 17, 23, SpaceBattleRandomPurpose.WanderHeading);
        var second = SpaceBattleMath.RandomDirection(seed, 17, 23, SpaceBattleRandomPurpose.WanderHeading);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.Length(), Is.EqualTo(1f).Within(1e-5f));
        Assert.That(first.X, Is.InRange(-1f, 1f));
        Assert.That(first.Y, Is.InRange(-1f, 1f));
        Assert.That(first.Z, Is.InRange(-1f, 1f));

        for (var entityKey = 1; entityKey <= 128; entityKey++)
        {
            var direction = SpaceBattleMath.RandomDirection(
                seed,
                entityKey,
                (long)(entityKey % 7),
                SpaceBattleRandomPurpose.WanderHeading);
            var speed = SpaceBattleMath.RandomWanderSpeed(seed, entityKey, 23);
            Assert.That(direction.Length(), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(speed, Is.InRange(0f, SpaceBattleMath.MaximumWanderSpeed));
            Assert.That(float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z), Is.True);
            Assert.That(float.IsFinite(speed), Is.True);
        }

        var changedByTick = SpaceBattleMath.RandomDirection(seed, 17, 24, SpaceBattleRandomPurpose.WanderHeading);
        var changedByPurpose = SpaceBattleMath.RandomDirection(seed, 17, 23, SpaceBattleRandomPurpose.InitialWanderHeading);
        Assert.That(changedByTick, Is.Not.EqualTo(first));
        Assert.That(changedByPurpose, Is.Not.EqualTo(first));
    }

    [Test]
    public void TurnTowards_UsesTheLimitAndSnapsOnTheFinalStep()
    {
        var current = Vector3.UnitX;
        var target = Vector3.UnitY;
        const float maximumStep = 0.04f;

        var turned = SpaceBattleMath.TurnTowards(current, target, maximumStep, out var remaining);
        Assert.That(SpaceBattleMath.AngleBetween(current, turned), Is.EqualTo(maximumStep).Within(1e-5f));
        Assert.That(remaining, Is.EqualTo((MathF.PI / 2f) - maximumStep).Within(1e-5f));

        var final = SpaceBattleMath.TurnTowards(turned, target, remaining + 0.001f, out var finalRemaining);
        Assert.That(final, Is.EqualTo(target));
        Assert.That(finalRemaining, Is.Zero);
    }

    [Test]
    public void RandomTurnTarget_UsesAThreeDimensionalGreatCircleAndBoundedArc()
    {
        const ulong seed = 0x1234_5678_9ABC_DEF0UL;
        var current = Vector3.Normalize(new Vector3(1f, 2f, 3f));
        var hasShortArc = false;
        var hasLongArc = false;

        for (var entityKey = 1; entityKey <= 512; entityKey++)
        {
            var target = SpaceBattleMath.RandomTurnTarget(seed, entityKey, 17, current, out var turnRadians);
            Assert.Multiple(() =>
            {
                Assert.That(turnRadians, Is.InRange(SpaceBattleMath.MinimumTurnRadians, SpaceBattleMath.MaximumTurnRadians));
                Assert.That(target.Length(), Is.EqualTo(1f).Within(1e-5f));
                Assert.That(Vector3.Dot(current, target), Is.EqualTo(MathF.Cos(turnRadians)).Within(1e-5f));
            });

            hasShortArc |= turnRadians < MathF.PI;
            hasLongArc |= turnRadians > MathF.PI;
        }

        Assert.Multiple(() =>
        {
            Assert.That(hasShortArc, Is.True);
            Assert.That(hasLongArc, Is.True);
        });
    }

    [Test]
    public void TurnAlongGreatCircle_CompletesLongArcWithExactShortFinalStep()
    {
        var current = Vector3.UnitX;
        const float turnRadians = 4.81f;
        var target = Vector3.Normalize(
            (current * MathF.Cos(turnRadians)) + (Vector3.UnitY * MathF.Sin(turnRadians)));
        var remaining = turnRadians;
        var distanceTravelled = 0f;
        var finalStep = 0f;

        for (var step = 0; step < 200; step++)
        {
            var before = current;
            current = SpaceBattleMath.TurnAlongGreatCircle(
                current,
                target,
                remaining,
                maximumRadians: 0.04f,
                out var nextRemaining);
            finalStep = SpaceBattleMath.AngleBetween(before, current);
            distanceTravelled += finalStep;
            remaining = nextRemaining;
            if (remaining == 0f)
            {
                break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Is.Zero);
            Assert.That(current, Is.EqualTo(target));
            Assert.That(finalStep, Is.LessThan(0.04f));
            Assert.That(distanceTravelled, Is.EqualTo(turnRadians).Within(1e-3f));
        });
    }

    [Test]
    public void MoveBounds_ReflectsEachAxisWithLargeAndSimultaneousOverrun()
    {
        var current = new AABB3F
        {
            MinX = 9f,
            MaxX = 9f,
            MinY = 1f,
            MaxY = 1f,
            MinZ = 5f,
            MaxZ = 5f,
        };
        var moved = SpaceBattleMath.MoveBounds(
            current,
            new Vector3(1f, -1f, 1f),
            speed: 27f,
            deltaSeconds: 1f,
            worldWidth: 10f,
            worldHeight: 10f,
            worldDepth: 10f,
            out var heading);

        Assert.Multiple(() =>
        {
            Assert.That(moved.MinX, Is.EqualTo(4f).Within(1e-5f));
            Assert.That(moved.MinY, Is.EqualTo(6f).Within(1e-5f));
            Assert.That(moved.MinZ, Is.EqualTo(8f).Within(1e-5f));
            Assert.That(heading.X, Is.EqualTo(-1f));
            Assert.That(heading.Y, Is.EqualTo(1f));
            Assert.That(heading.Z, Is.EqualTo(-1f));
        });

        var extreme = SpaceBattleMath.MoveBounds(
            current,
            Vector3.Normalize(new Vector3(-1f, 1f, -1f)),
            speed: 10_007f,
            deltaSeconds: 1f,
            worldWidth: 10f,
            worldHeight: 10f,
            worldDepth: 10f,
            out var extremeHeading);
        Assert.Multiple(() =>
        {
            Assert.That(extreme.MinX, Is.InRange(0f, 10f));
            Assert.That(extreme.MinY, Is.InRange(0f, 10f));
            Assert.That(extreme.MinZ, Is.InRange(0f, 10f));
            Assert.That(float.IsFinite(extreme.MinX) && float.IsFinite(extreme.MinY) && float.IsFinite(extreme.MinZ), Is.True);
            Assert.That(extremeHeading.Length(), Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public void Runtime_TransitionsAfterTheFiftiethFlyingMoveAndMovesLockedShipsNextTick()
    {
        var definition = new SimulationDefinition(
            shipCount: 1,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: 10_000f,
            worldHeight: 10_000f,
            worldDepth: 10_000f,
            maximumHealth: 1_000,
            maximumCompletedTicks: 53);
        var root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        var snapshots = SpaceBattleTestRuntime.CaptureSnapshots(definition, root);
        var initial = ReadShip(snapshots[1], entityKey: 1);
        var firstMove = ReadShip(snapshots[2], entityKey: 1);
        var fiftiethMove = ReadShip(snapshots[51], entityKey: 1);
        var lockedBeforeMove = ReadShip(snapshots[52], entityKey: 1);
        var lockedAfterMove = ReadShip(snapshots[53], entityKey: 1);

        Assert.Multiple(() =>
        {
            Assert.That(initial.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
            Assert.That(initial.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Flying));
            Assert.That(initial.Behavior.TicksRemaining, Is.EqualTo(SpaceBattleMath.WanderFlightTicks));
            Assert.That(initial.Motion.Speed, Is.InRange(0f, SpaceBattleMath.MaximumWanderSpeed));
            Assert.That(firstMove.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
            Assert.That(firstMove.Behavior.TicksRemaining, Is.EqualTo(SpaceBattleMath.WanderFlightTicks - 1));
            Assert.That(fiftiethMove.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
            Assert.That(fiftiethMove.Behavior.TicksRemaining, Is.Zero);
            Assert.That(lockedBeforeMove.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Tracking));
            Assert.That(lockedBeforeMove.Hull, Is.EqualTo(fiftiethMove.Hull));
            Assert.That(lockedBeforeMove.Motion.Speed, Is.EqualTo(fiftiethMove.Motion.Speed));
            Assert.That(lockedAfterMove.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Tracking));
            Assert.That(lockedAfterMove.Hull, Is.Not.EqualTo(lockedBeforeMove.Hull));
        });
    }


    [Test]
    public void Runtime_CompletesTargetLossTurnAndEvasiveFlightCycle()
    {
        // 引擎簇 Vitals 缺陷修复后伤害全量生效：3 船互锁场景中观察者会被反杀、无法完成
        // target-loss 周期。改为 2 船互锁（1 击死）：先开火方存活并经历完整周期，动态定位
        // 观察者（第一个 target loss 的船），其余断言保持原语义。
        var definition = new SimulationDefinition(
            shipCount: 2,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: 10f,
            worldHeight: 10f,
            worldDepth: 10f,
            maximumHealth: 250,
            maximumCompletedTicks: 200);
        var root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        var snapshots = SpaceBattleTestRuntime.CaptureSnapshots(definition, root);

        Assert.That(snapshots, Has.Count.EqualTo((int)definition.MaximumCompletedTicks + 1));
        // 伤害结算修复后时序不再依赖绝对 tick：动态定位「目标失效察觉」（第一次 Turning+Ready
        // 且清空目标）的观察者与 tick，其余断言保持原语义。
        var observerKey = snapshots.Values
            .SelectMany(static snapshot => snapshot.Ships)
            .Where(static ship =>
                ship.Behavior.Mode == (byte)BehaviorMode.Turning &&
                ship.Behavior.Phase == (byte)BehaviorPhase.Ready &&
                ship.Targeting.TargetRawEntityId == 0)
            .Select(static ship => ship.EntityKey)
            .First();
        var invalidationTick = snapshots
            .Where(pair => pair.Value.Ships.Any(ship =>
                ship.EntityKey == observerKey &&
                ship.Behavior.Mode == (byte)BehaviorMode.Turning &&
                ship.Behavior.Phase == (byte)BehaviorPhase.Ready &&
                ship.Targeting.TargetRawEntityId == 0))
            .Select(static pair => pair.Key)
            .Min();
        var afterDeath = ReadShip(snapshots[invalidationTick - 1], observerKey);
        var firstTurningTick = invalidationTick + 1;

        Assert.Multiple(() =>
        {
            Assert.That(afterDeath.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Attacking));
            Assert.That(ReadShip(snapshots[invalidationTick], observerKey).Targeting.TargetRawEntityId, Is.Zero);
            Assert.That(ReadShip(snapshots[invalidationTick], observerKey).Motion.Speed, Is.EqualTo(SpaceBattleCombat.AttackSpeed));
            Assert.That(ReadShip(snapshots[invalidationTick], observerKey).Hull, Is.Not.EqualTo(afterDeath.Hull));
            Assert.That(ReadShip(snapshots[firstTurningTick], observerKey).Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
            Assert.That(ReadShip(snapshots[firstTurningTick], observerKey).Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Aligning));
            Assert.That(ReadShip(snapshots[firstTurningTick], observerKey).Motion.Speed, Is.Zero);
            Assert.That(ReadShip(snapshots[firstTurningTick], observerKey).Hull, Is.EqualTo(ReadShip(snapshots[invalidationTick], observerKey).Hull));
        });

        var firstFlyingTick = snapshots
            .Where(pair => pair.Value.Ships.Any(ship =>
                ship.EntityKey == observerKey &&
                ship.Behavior.Mode == (byte)BehaviorMode.Turning &&
                ship.Behavior.Phase == (byte)BehaviorPhase.Flying))
            .Select(static pair => pair.Key)
            .Min();
        Assert.That(firstFlyingTick, Is.GreaterThan(firstTurningTick));

        for (var tick = firstTurningTick; tick < firstFlyingTick; tick++)
        {
            var frame = ReadShip(snapshots[tick], observerKey);
            var previous = ReadShip(snapshots[tick - 1], observerKey);
            Assert.Multiple(() =>
            {
                Assert.That(frame.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
                Assert.That(frame.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Aligning));
                Assert.That(frame.Hull, Is.EqualTo(previous.Hull));
            });
        }

        var finalTurn = ReadShip(snapshots[firstFlyingTick], observerKey);
        var beforeFlight = ReadShip(snapshots[firstFlyingTick - 1], observerKey);
        Assert.Multiple(() =>
        {
            Assert.That(finalTurn.Behavior.TicksRemaining, Is.EqualTo(SpaceBattleMath.EvasiveFlightTicks));
            Assert.That(finalTurn.Motion.Speed, Is.EqualTo(SpaceBattleMath.EvasiveSpeed));
            Assert.That(finalTurn.Hull, Is.EqualTo(beforeFlight.Hull));
            Assert.That(finalTurn.Motion.CurrentHeadingX, Is.EqualTo(finalTurn.Motion.TargetHeadingX));
            Assert.That(finalTurn.Motion.CurrentHeadingY, Is.EqualTo(finalTurn.Motion.TargetHeadingY));
            Assert.That(finalTurn.Motion.CurrentHeadingZ, Is.EqualTo(finalTurn.Motion.TargetHeadingZ));
        });

        for (var offset = 1; offset <= SpaceBattleMath.EvasiveFlightTicks; offset++)
        {
            var frame = ReadShip(snapshots[firstFlyingTick + offset], observerKey);
            var previous = ReadShip(snapshots[firstFlyingTick + offset - 1], observerKey);
            Assert.Multiple(() =>
            {
                Assert.That(frame.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
                Assert.That(frame.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Flying));
                Assert.That(frame.Behavior.TicksRemaining, Is.EqualTo(SpaceBattleMath.EvasiveFlightTicks - offset));
                Assert.That(frame.Hull, Is.Not.EqualTo(previous.Hull));
            });
        }

        var afterFlight = ReadShip(snapshots[firstFlyingTick + SpaceBattleMath.EvasiveFlightTicks + 1], observerKey);
        Assert.Multiple(() =>
        {
            Assert.That(afterFlight.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
            Assert.That(afterFlight.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Ready));
            Assert.That(afterFlight.Hull, Is.EqualTo(ReadShip(snapshots[firstFlyingTick + SpaceBattleMath.EvasiveFlightTicks], observerKey).Hull));
            Assert.That(ReadShip(snapshots[200], observerKey).Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
        });

        var validModes = Enum.GetValues<BehaviorMode>().Select(static mode => (byte)mode).ToHashSet();
        foreach (var snapshot in snapshots.Values)
        {
            foreach (var ship in snapshot.Ships)
            {
                var activeModes = validModes.Count(mode => mode == ship.Behavior.Mode);
                Assert.That(activeModes, Is.EqualTo(1), $"EntityKey={ship.EntityKey} 出现未知或重叠行为模式。");
            }
        }
    }

    private static ShipSnapshot ReadShip(SpaceBattleSnapshot snapshot, long entityKey) =>
        snapshot.Ships.Single(ship => ship.EntityKey == entityKey);

}
