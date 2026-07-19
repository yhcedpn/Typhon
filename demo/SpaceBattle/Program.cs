namespace SpaceBattle;

internal static class Program
{
    private const string RunName = "default";

    public static int Main()
    {
        var databaseLocation = Path.Combine(AppContext.BaseDirectory, $"{RunName}.typhon");
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

        Console.WriteLine("模拟运行中。按 Ctrl+C 安全停止。");
        cancellation.Token.WaitHandle.WaitOne();

        return 0;
    }

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
