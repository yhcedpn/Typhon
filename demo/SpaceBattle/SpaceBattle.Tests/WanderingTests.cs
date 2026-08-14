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
                (ulong)(entityKey % 7),
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
    public void Host_TransitionsAfterTheFiftiethFlyingMoveAndMovesLockedShipsNextTick()
    {
        var definition = new SimulationDefinition(
            shipCount: 1,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: 10_000f,
            worldHeight: 10_000f,
            worldDepth: 10_000f,
            maximumHealth: 1_000,
            maximumCompletedTicks: 1);
        var root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(root);
        try
        {
            var initial = Run(definition, root, 1);
            var firstMove = Run(definition with { MaximumCompletedTicks = 2 }, root, 2);
            var fiftiethMove = Run(definition with { MaximumCompletedTicks = 51 }, root, 51);
            var lockedBeforeMove = Run(definition with { MaximumCompletedTicks = 52 }, root, 52);
            var lockedAfterMove = Run(definition with { MaximumCompletedTicks = 53 }, root, 53);

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
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ShipSnapshot Run(SimulationDefinition definition, string root, ulong expectedTicks)
    {
        var result = SpaceBattleHost.Run(definition with { MaximumCompletedTicks = expectedTicks }, root, CancellationToken.None, new RecordingSink());
        Assert.That(result.CompletedTicks, Is.EqualTo((long)expectedTicks));
        return SpaceBattleHost.ReadSnapshot(definition with { MaximumCompletedTicks = expectedTicks }, root).Ships.Single();
    }
    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
