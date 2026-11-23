using IDForge.Core;
using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

/// <summary>Versioned photo cache: stale cutouts are ignored and DB refs cleared.</summary>
public class PhotoCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "idct-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CardStore _store;

    public PhotoCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new CardStore(Path.Combine(_dir, "t.db"));
    }

    public void Dispose() { _store.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void CacheFileName_Is_Version_Prefixed()
    {
        var name = Config.CacheFileName("UID20250318");
        Assert.StartsWith(Config.PipelineVersion + "_", name);
        Assert.EndsWith(".png", name);
        // A different pipeline version produces a different filename -> old cache unused.
        Assert.NotEqual($"v1_UID20250318.png", name);
    }

    [Fact]
    public void ClearPhotoPaths_Nulls_Missing_Cache_Refs()
    {
        var gone = Path.Combine(_dir, "photos", "v1_UID1.png");
        var kept = Path.Combine(_dir, "photos", "v2_UID2.png");
        var e1 = new Employee { Uid = "20250101", Name = "A", BloodGroup = "A+", Position = "Nurse", ContractType = "Permanent", PhotoPath = gone };
        var e2 = new Employee { Uid = "20250102", Name = "B", BloodGroup = "A+", Position = "Nurse", ContractType = "Permanent", PhotoPath = kept };
        e1.Id = _store.InsertEmployee(e1);
        e2.Id = _store.InsertEmployee(e2);

        _store.ClearPhotoPaths(new[] { gone });

        Assert.Null(_store.FindEmployeeByUid("20250101")!.PhotoPath);
        Assert.Equal(kept, _store.FindEmployeeByUid("20250102")!.PhotoPath);
    }
}
