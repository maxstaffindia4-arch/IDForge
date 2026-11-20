using System.Runtime.InteropServices;
using IDForge.Core;
using IDForge.Core.Data;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace IDForge.Tests;

/// <summary>UID digits-only input, normalization, barcode payload, migration.</summary>
public class UidTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public UidTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"uidtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- normalization ----------

    [Theory]
    [InlineData("20250318", "20250318")]
    [InlineData("UID20250318", "20250318")]
    [InlineData("uid20250318", "20250318")]
    [InlineData("ID. 20250318", "20250318")]
    [InlineData("ID:20250318", "20250318")]
    [InlineData("  UID 2025 0318 ", "20250318")]
    public void Normalize_Strips_Prefixes_And_Spaces(string input, string expected)
        => Assert.Equal(expected, Uid.Normalize(input));

    [Theory]
    [InlineData("2025", true)]
    [InlineData("20250318", true)]
    [InlineData("123456789012345", true)]
    [InlineData("123", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("20A50318", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_Digits_4_To_15(string? input, bool expected)
        => Assert.Equal(expected, Uid.IsValid(Uid.Normalize(input)));

    [Fact]
    public void Card_Id_Line_And_Barcode_Use_Digits()
    {
        Assert.Equal("ID. 20250318", Validity.FormatIdLine("20250318"));
        Assert.Equal("UID20250318", Uid.BarcodeContent("20250318"));
        Assert.Equal("UID20250318", Uid.FileToken("20250318"));
    }

    [Fact]
    public void Barcode_Generated_From_Digits_Decodes_As_UID_Plus_Digits()
    {
        using var bmp = Barcode.Generate("20250318");
        Assert.Equal("UID20250318", Decode(bmp));
        // And the legacy full form produces the identical payload.
        using var bmp2 = Barcode.Generate("UID20250318");
        Assert.Equal("UID20250318", Decode(bmp2));
    }

    // ---------- migration ----------

    private static Employee Emp(string uid) => new()
    {
        Uid = uid, Name = "T", BloodGroup = "N/A",
        Position = "Intern", ContractType = "Permanent"
    };

    [Fact]
    public void Migration_Converts_Prefixed_Uids_And_Is_Idempotent()
    {
        string db1;
        {
            using var store = new CardStore(_db);
            store.InsertEmployee(Emp("UID20250318"));
            store.InsertEmployee(Emp("20250401"));
            var sid = store.CreateBatchSession();
            store.SaveBatchRow(new BatchRowEntry { SessionId = sid, Uid = "UID20250402", Name = "B", Position = "Intern" });
        }
        // Reopen -> migration runs.
        UidMigrationResult mig;
        {
            using var store = new CardStore(_db);
            mig = store.LastUidMigration!;
            Assert.True(mig.HasChanges);
            Assert.Equal(1, mig.EmployeesMigrated);
            Assert.Equal(1, mig.BatchRowsMigrated);
            Assert.NotNull(mig.BackupPath);
            Assert.True(File.Exists(mig.BackupPath));
            Assert.Contains(mig.Changes, c => c.Contains("'UID20250318' -> '20250318'"));
            Assert.Equal("20250318", store.FindEmployeeByUid("20250318")!.Uid);
            Assert.Null(store.FindEmployeeByUid("UID20250318"));
        }
        // Second open: nothing left to migrate, no new backup.
        {
            using var store = new CardStore(_db);
            Assert.False(store.LastUidMigration!.HasChanges);
            Assert.Empty(store.LastUidMigration.Conflicts);
        }
        db1 = _db;
        Assert.True(File.Exists(db1));
    }

    [Fact]
    public void Migration_Stops_On_Conflict()
    {
        {
            using var store = new CardStore(_db);
            store.InsertEmployee(Emp("UID20250601"));
            store.InsertEmployee(Emp("20250601"));   // collides after normalization
        }
        using var store2 = new CardStore(_db);
        var mig = store2.LastUidMigration!;
        Assert.False(mig.HasChanges);
        Assert.NotEmpty(mig.Conflicts);
        // Nothing was rewritten.
        Assert.NotNull(store2.FindEmployeeByUid("UID20250601"));
        Assert.NotNull(store2.FindEmployeeByUid("20250601"));
    }

    private static string? Decode(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var bgra = new byte[h * bmp.RowBytes];
        Marshal.Copy(bmp.GetPixels(), bgra, 0, bgra.Length);
        var lum = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * bmp.RowBytes + x * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            lum[y * w + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }
        var source = new RGBLuminanceSource(lum, w, h);
        var binary = new BinaryBitmap(new HybridBinarizer(source));
        return new MultiFormatReader().decode(binary)?.Text;
    }
}
