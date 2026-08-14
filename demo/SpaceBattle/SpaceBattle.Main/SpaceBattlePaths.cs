namespace SpaceBattle;

internal static class SpaceBattlePaths
{
    public const string DatabaseName = "space-battle";

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
