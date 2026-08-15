using System.Diagnostics;
using Typhon.Engine;

namespace SpaceBattle.Tests;

/// <summary>以固定逻辑步长快速运行 SpaceBattle 测试，并按逻辑帧捕获快照。</summary>
internal static class SpaceBattleTestRuntime
{
    public const int AcceleratedTickRate = 1_000;

    public static SpaceBattleRunResult Run(
        SimulationDefinition definition,
        string databaseRoot,
        ISpaceBattleObservationSink observationSink = null) =>
        SpaceBattleHost.Run(
            definition with { TickRate = AcceleratedTickRate, WorkerCount = 1 },
            databaseRoot,
            CancellationToken.None,
            observationSink ?? NullObservationSink.Instance);

    public static Dictionary<long, SpaceBattleSnapshot> CaptureSnapshots(
        SimulationDefinition definition,
        string databaseRoot)
    {
        Directory.CreateDirectory(databaseRoot);
        try
        {
            var acceleratedDefinition = definition with { TickRate = AcceleratedTickRate };
            SpaceBattleHost.BootstrapOnly(
                acceleratedDefinition,
                databaseRoot,
                CancellationToken.None,
                NullObservationSink.Instance);
            using var engine = SpaceBattleDatabase.Open(
                acceleratedDefinition,
                SpaceBattlePaths.DatabaseDirectory(databaseRoot));
            using var state = new SpaceBattleSimulationState(
                engine,
                acceleratedDefinition,
                NullObservationSink.Instance,
                workerCount: 1);
            var snapshots = new Dictionary<long, SpaceBattleSnapshot>();
            using var snapshotsCompleted = new ManualResetEventSlim();
            using var runtimeAborted = new ManualResetEventSlim();
            var timing = new TickTiming();
            using var runtime = TyphonRuntime.Create(
                engine,
                schedule => BuildSchedule(schedule, state, timing, snapshots, snapshotsCompleted),
                new RuntimeOptions
                {
                    BaseTickRate = AcceleratedTickRate,
                    WorkerCount = 1,
                    EnableParallelFence = true,
                    AdaptiveFenceCost = false,
                    SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
                    Overload = new OverloadOptions
                    {
                        MinTickRateHz = AcceleratedTickRate,
                    },
                });
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
            if (Directory.Exists(databaseRoot))
            {
                Directory.Delete(databaseRoot, recursive: true);
            }
        }
    }

    private static void BuildSchedule(
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
        dag.Add(new ObserveSystem(state, timing));
        dag.Add(new SnapshotCaptureSystem(state, snapshots, snapshotsCompleted));
    }

    private sealed class SnapshotCaptureSystem(
        SpaceBattleSimulationState state,
        Dictionary<long, SpaceBattleSnapshot> snapshots,
        ManualResetEventSlim completed) : ChunkedCallbackSystem
    {
        protected override void Configure(SystemBuilder b) => b
            .Name("SnapshotCapture")
            .Priority(SystemPriority.Critical)
            .CanShed(false)
            .Phase(SpaceBattlePhases.Observe)
            .After("Observe")
            .ChunkedParallel(1);

        protected override void Execute(TickContext ctx)
        {
            snapshots[ctx.TickNumber] = state.Frames.BuildPublishedSnapshot();
            if ((ulong)ctx.TickNumber == state.MaximumCompletedTicks)
            {
                completed.Set();
            }
        }
    }

    private sealed class NullObservationSink : ISpaceBattleObservationSink
    {
        public static NullObservationSink Instance { get; } = new();

        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
