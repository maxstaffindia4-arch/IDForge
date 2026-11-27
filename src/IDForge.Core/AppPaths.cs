namespace IDForge.Core;

/// <summary>
/// Writable per-user data directory. Installed apps live under Program Files
/// (read-only), so all mutable state goes to %LOCALAPPDATA%\IDForge.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();
    public static string DbPath => Path.Combine(DataDir, "idcard.db");
    public static string PhotosDir => Path.Combine(DataDir, "photos");
    public static string OutputDir => Path.Combine(DataDir, "output");
    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CrashLogPath => Path.Combine(DataDir, "crash.log");

    private static string ResolveDataDir()
    {
        // IDFORGE_DATA_DIR redirects all mutable state — used by the test
        // suite (never touch the owner's real data) and for running a
        // portable/scratch instance.
        var env = Environment.GetEnvironmentVariable("IDFORGE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var dir0 = Path.GetFullPath(env);
            Directory.CreateDirectory(dir0);
            return dir0;
        }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDForge");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// One-time migration: if a database exists next to the exe (dev/portable
    /// runs) and none exists yet in the data dir, copy it over.
    /// </summary>
    public static void MigrateLegacyDatabase()
    {
        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "idcard.db");
            if (File.Exists(legacy) && !File.Exists(DbPath))
                File.Copy(legacy, DbPath);
        }
        catch { }
    }
}
