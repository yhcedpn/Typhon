namespace SpaceBattle;

internal static class Program
{
    private const string RunName = "default";
    private const bool EnableDeepProfiling = false;

    public static int Main()
    {
        var databaseLocation = Path.Combine(AppContext.BaseDirectory, $"{RunName}.typhon");
        ConfigureDeepProfiling(databaseLocation);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var simulation = SpaceBattleHost.Start(
            SimulationDefinition.Default,
            databaseLocation,
            cancellation.Token,
            new ConsoleObservationSink());

        if (simulation.StartupResult.StartupAction == SimulationStartupAction.Resumed)
        {
            Console.WriteLine($"恢复运行 {RunName}，当前存活飞船数 {simulation.StartupResult.ShipCount:N0}。");
        }

        SpaceBattleRuntimeConfiguration runtimeConfiguration = simulation.RuntimeConfiguration;
        Console.WriteLine(
            $"运行资源：逻辑处理器 {Environment.ProcessorCount}，Typhon worker {runtimeConfiguration.EffectiveWorkerCount}，" +
            $"page cache {runtimeConfiguration.PageCacheSizeBytes / 1024 / 1024} MiB，" +
            $"内存封套 {runtimeConfiguration.MemoryBudgetBytes / 1024 / 1024} MiB，" +
            $"overload {runtimeConfiguration.CurrentOverloadLevel}。");

        Console.WriteLine("模拟运行中。按 Ctrl+C 安全暂停。");
        while (!cancellation.IsCancellationRequested &&
               !simulation.WaitForTerminal(TimeSpan.FromMilliseconds(100)))
        {
        }

        if (!cancellation.IsCancellationRequested)
        {
            InitialWorldSnapshot snapshot = simulation.GetSnapshot();
            Console.WriteLine(DescribeTerminalResult(snapshot.Run));
            return snapshot.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        if (simulation.WaitForTerminal(TimeSpan.FromSeconds(5)))
        {
            InitialWorldSnapshot snapshot = simulation.GetSnapshot();
            Console.WriteLine(DescribeTerminalResult(snapshot.Run));
            return snapshot.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        if (!simulation.WaitForPause(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("等待 Ctrl+C 时正在执行的模拟 tick 完成超时。");
        }

        InitialWorldSnapshot snapshotAfterPause = simulation.GetSnapshot();
        if (snapshotAfterPause.Run.Status != SimulationRunStatus.Running)
        {
            Console.WriteLine(DescribeTerminalResult(snapshotAfterPause.Run));
            return snapshotAfterPause.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        Console.WriteLine("已在完成当前模拟 tick 后暂停；下次启动将继续运行。");
        return 0;
    }

    private static void ConfigureDeepProfiling(string databaseLocation)
    {
        if (!IsDeepProfilingEnabled())
        {
            return;
        }

        // 必须先于 Typhon.Engine 的静态 telemetry 初始化设置，避免 JIT 固化关闭的事件门控。
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__ENABLED", "true");
        Environment.SetEnvironmentVariable(
            "TYPHON__PROFILER__TRACE",
            SpaceBattleProductionSettings.GetProfilerTracePath(databaseLocation));
    }

    private static bool IsDeepProfilingEnabled() => EnableDeepProfiling;

    private static string DescribeTerminalResult(SimulationRunSnapshot run) => run.Outcome switch
    {
        SimulationRunOutcome.Winner => $"模拟完成：胜者飞船 {run.WinnerEntityKey}，完成 tick {run.CompletedTicks:N0}。",
        SimulationRunOutcome.Draw => $"模拟完成：平局，完成 tick {run.CompletedTicks:N0}。",
        SimulationRunOutcome.TimedOut => $"模拟超时：在 {run.CompletedTicks:N0} tick 后仍有 {run.AliveShipCount:N0} 艘飞船存活。",
        _ => throw new InvalidOperationException($"终态运行缺少有效结果：{run.Outcome}。"),
    };

    private sealed class ConsoleObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
            if (observation is InitializationCompleted completed)
            {
                Console.WriteLine(
                    $"初始化完成：{completed.ShipCount:N0} 艘飞船，耗时 {completed.Duration.TotalSeconds:F2} 秒。");
            }
        }
    }
}
