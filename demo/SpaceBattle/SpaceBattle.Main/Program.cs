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
                $"termination={FormatTermination(result.TerminationReason)} " +
                $"bootstrap_ships={result.ShipCount.ToString(CultureInfo.InvariantCulture)} " +
                $"completed_ticks={result.CompletedTicks.ToString(CultureInfo.InvariantCulture)} " +
                $"remaining_ships={result.RemainingShips.ToString(CultureInfo.InvariantCulture)} " +
                $"bootstrap_ms={result.BootstrapDuration.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"database={result.DatabaseDirectory}");
            if (result.IsFatal)
            {
                Console.Error.WriteLine($"fatal_system={result.FailedSystemName ?? "<unknown>"}");
                Console.Error.WriteLine("fatal_exception=");
                Console.Error.WriteLine(result.FatalExceptionText ?? "<missing exception>");
            }

            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // 仅作为 bootstrap 之外的最后防线；正常 Ctrl+C 会由 Host 返回结构化结果。
            Console.WriteLine("termination=cancelled completed_ticks=0 remaining_ships=0");
            return 0;
        }
    }

    private static string FormatTermination(SpaceBattleTerminationReason reason) => reason switch
    {
        SpaceBattleTerminationReason.Draw => "draw",
        SpaceBattleTerminationReason.Winner => "winner",
        SpaceBattleTerminationReason.TickLimit => "tick_limit",
        SpaceBattleTerminationReason.Cancelled => "cancelled",
        SpaceBattleTerminationReason.Fatal => "fatal",
        SpaceBattleTerminationReason.BootstrapOnly => "bootstrap_only",
        _ => "unknown",
    };

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
