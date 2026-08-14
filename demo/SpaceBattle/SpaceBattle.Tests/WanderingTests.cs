using System.Diagnostics;
using System.Numerics;
using NUnit.Framework;
using Typhon.Engine;
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


    [Test]
    public void Runtime_CompletesTargetLossTurnAndEvasiveFlightCycle()
    {
        var definition = new SimulationDefinition(
            shipCount: 3,
            seed: 0x1234_5678_9ABC_DEF0UL,
            worldWidth: 10f,
            worldHeight: 10f,
            worldDepth: 10f,
            maximumHealth: 250,
            maximumCompletedTicks: 200);
        var snapshots = RunStateMachine(definition);

        Assert.That(snapshots, Has.Count.EqualTo((int)definition.MaximumCompletedTicks + 1));
        var afterDeath = ReadShip(snapshots[64], entityKey: 3);
        var invalidationTick = ReadShip(snapshots[65], entityKey: 3);
        var firstTurningTick = ReadShip(snapshots[66], entityKey: 3);
        var otherShipBeforeInvalidation = ReadShip(snapshots[64], entityKey: 2);
        var otherShipAfterInvalidation = ReadShip(snapshots[65], entityKey: 2);

        Assert.Multiple(() =>
        {
            Assert.That(afterDeath.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Attacking));
            Assert.That(invalidationTick.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
            Assert.That(invalidationTick.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Ready));
            Assert.That(invalidationTick.Targeting.TargetEntityId, Is.Zero);
            Assert.That(invalidationTick.Motion.Speed, Is.EqualTo(SpaceBattleCombat.WeaponSpeed));
            Assert.That(invalidationTick.Hull, Is.Not.EqualTo(afterDeath.Hull));
            Assert.That(otherShipAfterInvalidation.Vitals, Is.EqualTo(otherShipBeforeInvalidation.Vitals));
            Assert.That(firstTurningTick.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
            Assert.That(firstTurningTick.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Aligning));
            Assert.That(firstTurningTick.Motion.Speed, Is.Zero);
            Assert.That(firstTurningTick.Hull, Is.EqualTo(invalidationTick.Hull));
        });

        var firstFlyingTick = snapshots
            .Where(static pair => pair.Value.Ships.Any(static ship =>
                ship.EntityKey == 3 &&
                ship.Behavior.Mode == (byte)BehaviorMode.Turning &&
                ship.Behavior.Phase == (byte)BehaviorPhase.Flying))
            .Select(static pair => pair.Key)
            .Min();
        Assert.That(firstFlyingTick, Is.EqualTo(148));

        for (var tick = 66L; tick < firstFlyingTick; tick++)
        {
            var frame = ReadShip(snapshots[tick], entityKey: 3);
            var previous = ReadShip(snapshots[tick - 1], entityKey: 3);
            Assert.Multiple(() =>
            {
                Assert.That(frame.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
                Assert.That(frame.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Aligning));
                Assert.That(frame.Hull, Is.EqualTo(previous.Hull));
            });
        }

        var finalTurn = ReadShip(snapshots[firstFlyingTick], entityKey: 3);
        var beforeFlight = ReadShip(snapshots[firstFlyingTick - 1], entityKey: 3);
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
            var frame = ReadShip(snapshots[firstFlyingTick + offset], entityKey: 3);
            var previous = ReadShip(snapshots[firstFlyingTick + offset - 1], entityKey: 3);
            Assert.Multiple(() =>
            {
                Assert.That(frame.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Turning));
                Assert.That(frame.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Flying));
                Assert.That(frame.Behavior.TicksRemaining, Is.EqualTo(SpaceBattleMath.EvasiveFlightTicks - offset));
                Assert.That(frame.Hull, Is.Not.EqualTo(previous.Hull));
            });
        }

        var afterFlight = ReadShip(snapshots[firstFlyingTick + SpaceBattleMath.EvasiveFlightTicks + 1], entityKey: 3);
        Assert.Multiple(() =>
        {
            Assert.That(afterFlight.Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
            Assert.That(afterFlight.Behavior.Phase, Is.EqualTo((byte)BehaviorPhase.Ready));
            Assert.That(afterFlight.Hull, Is.EqualTo(ReadShip(snapshots[firstFlyingTick + SpaceBattleMath.EvasiveFlightTicks], entityKey: 3).Hull));
            Assert.That(ReadShip(snapshots[200], entityKey: 3).Behavior.Mode, Is.EqualTo((byte)BehaviorMode.Wandering));
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

    private static Dictionary<long, SpaceBattleSnapshot> RunStateMachine(SimulationDefinition definition)
    {
        var root = Path.Combine(Path.GetTempPath(), "SpaceBattle.Tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(root);
        try
        {
            var sink = new RecordingSink();
            SpaceBattleHost.BootstrapOnly(definition, root, CancellationToken.None, sink);
            using var engine = SpaceBattleDatabase.Open(definition, SpaceBattlePaths.DatabaseDirectory(root));
            using var state = new SpaceBattleSimulationState(engine, definition, sink, workerCount: 1);
            var snapshots = new Dictionary<long, SpaceBattleSnapshot>();
            using var snapshotsCompleted = new ManualResetEventSlim();
            var timing = new TickTiming();
            // 仅加速 runtime 调度，玩法仍使用配置中的 0.04 秒固定逻辑步长。
            var runtimeOptions = new RuntimeOptions
            {
                BaseTickRate = 1_000,
                WorkerCount = 1,
                EnableParallelFence = true,
                AdaptiveFenceCost = false,
                SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
                Overload = new OverloadOptions
                {
                    MinTickRateHz = 1_000,
                },
            };

            using var runtime = TyphonRuntime.Create(
                engine,
                schedule => BuildStateMachineSchedule(schedule, state, timing, snapshots, snapshotsCompleted),
                runtimeOptions);
            using var runtimeAborted = new ManualResetEventSlim();
            runtime.OnTickAborted += (_, _) => runtimeAborted.Set();
            runtime.Start();
            try
            {
                var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 15);
                while (!snapshotsCompleted.Wait(1))
                {
                    if (runtimeAborted.IsSet)
                    {
                        throw new InvalidOperationException("状态机测试运行时提前中止。");
                    }

                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        throw new TimeoutException("状态机测试运行时未在限定时间内完成。");
                    }
                }
            }
            finally
            {
                runtime.Shutdown();
            }

            return snapshots;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void BuildStateMachineSchedule(
        RuntimeSchedule schedule,
        SpaceBattleSimulationState state,
        TickTiming timing,
        Dictionary<long, SpaceBattleSnapshot> snapshots,
        ManualResetEventSlim snapshotsCompleted)
    {
        var dag = schedule.PublicTrack.DeclareDag("SpaceBattleStateMachine")
            .Phases(
                SpaceBattlePhases.Publish,
                SpaceBattlePhases.Behavior,
                SpaceBattlePhases.Damage,
                SpaceBattlePhases.Movement,
                SpaceBattlePhases.Reap,
                SpaceBattlePhases.Observe);
        dag.Add(new FramePrepareSystem(state));
        dag.Add(new PublishSystem(state));
        dag.Add(new BehaviorSystem(state));
        dag.Add(new DamageSystem(state));
        dag.Add(new DamageCleanupSystem(state));
        dag.Add(new MovementSystem(state));
        dag.Add(new ReapSystem(state));
        dag.Add(new AcquisitionCleanupSystem(state));
        dag.Add(new ObserveSystem(state, timing));
        dag.Add(new SnapshotCaptureSystem(state, snapshots, snapshotsCompleted));
    }

    private static ShipSnapshot ReadShip(SpaceBattleSnapshot snapshot, long entityKey) =>
        snapshot.Ships.Single(ship => ship.EntityKey == entityKey);

    private static ShipSnapshot Run(SimulationDefinition definition, string root, ulong expectedTicks)
    {
        var result = SpaceBattleHost.Run(definition with { MaximumCompletedTicks = expectedTicks }, root, CancellationToken.None, new RecordingSink());
        Assert.That(result.CompletedTicks, Is.EqualTo((long)expectedTicks));
        return SpaceBattleHost.ReadSnapshot(definition with { MaximumCompletedTicks = expectedTicks }, root).Ships.Single();
    }
    private sealed class SnapshotCaptureSystem : ChunkedCallbackSystem
    {
        private readonly SpaceBattleSimulationState _state;
        private readonly Dictionary<long, SpaceBattleSnapshot> _snapshots;
        private readonly ManualResetEventSlim _completed;

        public SnapshotCaptureSystem(
            SpaceBattleSimulationState state,
            Dictionary<long, SpaceBattleSnapshot> snapshots,
            ManualResetEventSlim completed)
        {
            _state = state;
            _snapshots = snapshots;
            _completed = completed;
        }

        protected override void Configure(SystemBuilder b) => b
            .Name("SnapshotCapture")
            .Priority(SystemPriority.Critical)
            .CanShed(false)
            .Phase(SpaceBattlePhases.Observe)
            .After("Observe")
            .ChunkedParallel(1);

        protected override void Execute(TickContext ctx)
        {
            _snapshots[ctx.TickNumber] = _state.BuildPublishedSnapshot();
            if ((ulong)ctx.TickNumber == _state.MaximumCompletedTicks)
            {
                _completed.Set();
            }
        }

    }
    private sealed class RecordingSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
