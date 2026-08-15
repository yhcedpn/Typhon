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

        var sink = new ConsoleObservationSink();
        try
        {
            var result = SpaceBattleHost.Run(
                SimulationDefinition.Default,
                SpaceBattlePaths.ProductionDatabaseRoot,
                cancellation.Token,
                sink);

            Console.WriteLine(
                $"termination={FormatTermination(result.TerminationReason)} " +
                $"bootstrap_ships={Format(result.ShipCount)} " +
                $"completed_ticks={Format(result.CompletedTicks)} " +
                $"remaining_ships={Format(result.RemainingShips)} " +
                $"bootstrap_ms={Format(result.BootstrapDuration.TotalMilliseconds)} " +
                $"database={Path.GetFullPath(result.DatabaseDirectory)} " +
                $"trace={SpaceBattlePaths.ConfiguredTraceFilePath()}");
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
            Console.WriteLine(
                $"termination=cancelled completed_ticks=0 remaining_ships=0 " +
                $"database={Path.GetFullPath(SpaceBattlePaths.DatabaseDirectory(SpaceBattlePaths.ProductionDatabaseRoot))} " +
                $"trace={SpaceBattlePaths.ConfiguredTraceFilePath()}");
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

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class ConsoleObservationSink : ISpaceBattleObservationSink
    {
        private long _lastPrintedTick = -1;

        public void Publish(SpaceBattleObservation observation)
        {
            switch (observation)
            {
                case InitializationCompleted completed:
                    Console.WriteLine(
                        $"initialization_completed=true ships={Format(completed.ShipCount)} " +
                        $"bootstrap_ms={Format(completed.BootstrapDuration.TotalMilliseconds)} " +
                        $"database={Path.GetFullPath(SpaceBattlePaths.DatabaseDirectory(SpaceBattlePaths.ProductionDatabaseRoot))} " +
                        $"trace={SpaceBattlePaths.ConfiguredTraceFilePath()}");
                    break;
                case SimulationTickCompleted tick when
                    tick.Telemetry is not null &&
                    SpaceBattleTelemetrySampling.IsSampleTick(tick.TickNumber) &&
                    Interlocked.Exchange(ref _lastPrintedTick, tick.TickNumber) != tick.TickNumber:
                    Console.WriteLine();
                    Console.WriteLine(SpaceBattleTelemetryFormatter.FormatHumanReadable(tick.Telemetry));
                    break;
                case SimulationTelemetrySample sample when
                    SpaceBattleTelemetrySampling.IsSampleTick(sample.Telemetry.TickNumber) &&
                    Interlocked.Exchange(ref _lastPrintedTick, sample.Telemetry.TickNumber) != sample.Telemetry.TickNumber:
                    Console.WriteLine();
                    Console.WriteLine(SpaceBattleTelemetryFormatter.FormatHumanReadable(sample.Telemetry));
                    break;
            }
        }
    }
}
