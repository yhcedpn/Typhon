using System.Globalization;

namespace SpaceBattle;

internal static class Program
{
    public static int Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var result = SpaceBattleHost.Run(
                SimulationDefinition.Default,
                SpaceBattlePaths.ProductionDatabaseRoot,
                cancellation.Token,
                new ConsoleObservationSink());

            Console.WriteLine(
                $"bootstrap_ships={result.ShipCount.ToString(CultureInfo.InvariantCulture)} " +
                $"bootstrap_ms={result.BootstrapDuration.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"database={result.DatabaseDirectory}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("bootstrap_cancelled=true");
            return 0;
        }
    }

    private sealed class ConsoleObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
            if (observation is InitializationCompleted completed)
            {
                Console.WriteLine(
                    $"initialization_completed=true ships={completed.ShipCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"bootstrap_ms={completed.BootstrapDuration.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
            }
        }
    }
}
