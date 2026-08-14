using System.Text.Json;
namespace SpaceBattle;

internal static class SpaceBattlePaths
{
    public const string DatabaseName = "space-battle";
    public const string TraceFileName = "space-battle.typhon-trace";

    public static string TelemetryConfigurationPath =>
        Path.Combine(AppContext.BaseDirectory, "typhon.telemetry.json");

    public static string DefaultTraceFilePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "trace", TraceFileName));

    public static string ResolveTraceFilePath(string configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultTraceFilePath
            : Path.GetFullPath(configuredPath);

    public static string ConfiguredTraceFilePath()
    {
        var configured = Environment.GetEnvironmentVariable("TYPHON__PROFILER__TRACE");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var currentFile = Path.Combine(Directory.GetCurrentDirectory(), "typhon.telemetry.json");
            var configFile = File.Exists(currentFile)
                ? currentFile
                : TelemetryConfigurationPath;
            configured = ReadTracePath(configFile);
        }

        return ResolveTraceFilePath(configured);
    }

    private static string ReadTracePath(string configFile)
    {
        if (!File.Exists(configFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(configFile),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            if (document.RootElement.TryGetProperty("Typhon", out var typhon) &&
                typhon.TryGetProperty("Profiler", out var profiler) &&
                profiler.TryGetProperty("Trace", out var trace) &&
                trace.ValueKind == JsonValueKind.String)
            {
                return trace.GetString();
            }
        }
        catch (JsonException)
        {
            // 配置错误由引擎按默认关闭处理；路径打印仍使用固定候选路径。
        }

        return null;
    }

    public static string ProductionDatabaseRoot => Path.Combine(AppContext.BaseDirectory, "data");

    public static string DatabaseDirectory(string databaseRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        return Path.Combine(Path.GetFullPath(databaseRoot), $"{DatabaseName}.typhon");
    }

    public static void ReplaceDatabaseDirectory(string databaseRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        var fullRoot = Path.GetFullPath(databaseRoot);
        Directory.CreateDirectory(fullRoot);

        var databaseDirectory = DatabaseDirectory(fullRoot);
        if (File.Exists(databaseDirectory))
        {
            File.Delete(databaseDirectory);
        }

        if (Directory.Exists(databaseDirectory))
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }
}
