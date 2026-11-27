using System.Runtime.CompilerServices;
using IDForge.Core;
using Xunit;

namespace IDForge.Tests;

/// <summary>
/// DATA SAFETY: the test suite must never touch the owner's real data.
/// IDFORGE_DATA_DIR is set here — before any test or AppPaths access — so
/// every CardStore/CardService/draft/settings path resolves to a throwaway
/// temp folder instead of %LOCALAPPDATA%\IDForge\idcard.db.
/// </summary>
internal static class TestIsolation
{
    public static string DataDir { get; } =
        Path.Combine(Path.GetTempPath(), "idforge-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("IDFORGE_DATA_DIR", DataDir);
        Directory.CreateDirectory(DataDir);
    }
}

public class TestIsolationTests
{
    /// <summary>Guard: fails the whole run if any test resolves the live
    /// profile directory. Runs before window tests matter because every
    /// WPF/store test depends on this.</summary>
    [Fact]
    public void DataDir_Is_Temporary_Not_LiveProfile()
    {
        var live = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDForge");
        var dataDir = Path.GetFullPath(AppPaths.DataDir);
        Assert.NotEqual(Path.GetFullPath(live), dataDir);
        Assert.DoesNotContain(Path.GetFullPath(live), dataDir,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetTempPath(), dataDir,
            StringComparison.OrdinalIgnoreCase);
    }
}
