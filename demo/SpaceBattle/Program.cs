namespace SpaceBattle;

internal static class Program
{
    private const string RunName = "default";

    public static int Main()
    {
        var databaseLocation = Path.Combine(AppContext.BaseDirectory, $"{RunName}.typhon");
        SpaceBattleHost.Run(
            SimulationDefinition.Default,
            databaseLocation,
            CancellationToken.None,
            new ConsoleObservationSink());
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
