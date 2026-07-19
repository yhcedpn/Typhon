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

        Console.WriteLine("模拟运行中。按 Ctrl+C 安全停止。");
        cancellation.Token.WaitHandle.WaitOne();

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
