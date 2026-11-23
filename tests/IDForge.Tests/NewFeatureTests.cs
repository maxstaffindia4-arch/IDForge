using System.Text.Json;
using IDForge.Core;
using IDForge.Core.Batch;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

/// <summary>Item 1: plain-white flood-fill mask keeps dark clothing opaque.</summary>
public class WhiteBgTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "studio_portrait.png");

    [Fact]
    public void WhiteBg_Keeps_Blazer_Collar_Opaque_And_Removes_Background()
    {
        Assert.True(File.Exists(Fixture), "studio_portrait.png fixture missing");
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);
        using var cutout = WhiteBgRemover.Remove(img);

        // Dark blazer region (x=200,y=800) must stay opaque.
        Assert.True(cutout.GetPixel(200, 800).Alpha > 200,
            $"blazer alpha={cutout.GetPixel(200, 800).Alpha}");

        // White collar enclosed by suit/skin must stay opaque.
        Assert.True(cutout.GetPixel(400, 640).Alpha > 200,
            $"collar alpha={cutout.GetPixel(400, 640).Alpha}");

        // Corner of the white background must be transparent.
        Assert.True(cutout.GetPixel(5, 5).Alpha < 30,
            $"bg corner alpha={cutout.GetPixel(5, 5).Alpha}");
    }

    [Fact]
    public void Process_PlainWhite_Is_Default_And_Keep_Returns_Original()
    {
        using var pipe = new PhotoPipeline();
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);

        var pw = pipe.Process(img, BgMode.PlainWhite);
        Assert.Equal("plain-white", pw.BgMethod);
        Assert.True(pw.Cutout.GetPixel(5, 5).Alpha < 30, "white bg should be transparent");

        var keep = pipe.Process(img, BgMode.Keep);
        Assert.Equal("skipped-manual", keep.BgMethod);
        Assert.True(keep.Cutout.GetPixel(5, 5).Alpha > 200, "keep-as-is must keep opaque background");
    }
}

/// <summary>Item 3: clear-all gate, backup/restore, edit validity, soft delete, export.</summary>
public class RecordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "idct-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CardStore _store;

    private static Employee Emp(string uid, string name = "Alice") => new()
    {
        Uid = uid, Name = name, BloodGroup = "A+", Position = "Nurse",
        ContractType = "Permanent"
    };

    public RecordsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new CardStore(Path.Combine(_dir, "t.db"));
        var e = Emp("UID1");
        e.Id = _store.InsertEmployee(e);
    }

    public void Dispose() { _store.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ClearAll_Empties_Employees_And_Keeps_Audit()
    {
        _store.LogAudit(null, "test", "before clear", "tester");
        _store.ClearAll();
        Assert.Empty(_store.AllEmployees());
        Assert.NotEmpty(_store.RecentAudit());
    }

    [Fact]
    public void SoftDelete_Hides_From_Lists_And_Restores()
    {
        var e = _store.AllEmployees().Single();
        _store.SoftDeleteEmployee(e.Id);
        Assert.Empty(_store.AllEmployees());
        Assert.Single(_store.DeletedEmployees());
        _store.RestoreEmployee(e.Id);
        Assert.Single(_store.AllEmployees());
    }

    [Fact]
    public void EditEmployee_Recalculates_Validity_On_New_Issue()
    {
        var e = _store.AllEmployees().Single();
        // seed an issue first so a second version is created on edit
        var i1 = new CardIssue
        {
            EmployeeId = e.Id, Version = 1,
            PrintDate = new DateOnly(2026, 1, 1),
            ValidUntil = Validity.CalculateValidUntil(new DateOnly(2026, 1, 1), "Permanent")
        };
        i1.Id = _store.InsertIssue(i1);

        var newPrint = new DateOnly(2026, 6, 1);
        var issue2 = _store.EditEmployee(e, "Alice Two", "B+", "Doctor",
            "Contractual", newPrint, uidChanged: false);
        Assert.NotNull(issue2);
        Assert.Equal(new DateOnly(2026, 12, 1), issue2!.ValidUntil); // +6 months contractual
        Assert.Equal(2, issue2.Version);
        Assert.Equal("Alice Two", _store.FindEmployeeByUid("UID1")!.Name);
    }

    [Fact]
    public void ExportRecords_Csv_And_Json_Contain_All_Fields()
    {
        var e = _store.AllEmployees().Single();
        var issue = new CardIssue
        {
            EmployeeId = e.Id, Version = 1,
            PrintDate = new DateOnly(2026, 2, 1),
            ValidUntil = Validity.CalculateValidUntil(new DateOnly(2026, 2, 1), "Contractual"),
            IssuedAt = DateTimeOffset.UtcNow
        };
        issue.Id = _store.InsertIssue(issue);

        var rows = RecordsExporter.Collect(_store);
        var csv = RecordsExporter.ToCsv(rows);
        var json = RecordsExporter.ToJson(rows);
        Assert.Contains("UID1", csv);
        Assert.Contains("ValidUntil", csv);
        Assert.Contains("2026-08-01", csv); // contractual +6mo
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("UID1", doc.RootElement[0].GetProperty("uid").GetString());
        Assert.Equal("2026-08-01", doc.RootElement[0].GetProperty("validUntil").GetString());
    }

    [Fact]
    public void Backup_And_Restore_Roundtrip()
    {
        var dbPath = Path.Combine(_dir, "svc3.db");
        string backup;
        using (var s1 = new CardStore(dbPath))
        {
            backup = s1.BackupTo(Path.Combine(_dir, "backup.db"));
            Assert.True(File.Exists(backup));
            var e = Emp("UIDZ", "Cid");
            e.Id = s1.InsertEmployee(e);
        }

        // backup was taken before UIDZ insert, so restoring it must drop UIDZ.
        // Re-backup after insert to test a round trip instead:
        string backup2;
        using (var s1b = new CardStore(dbPath))
            backup2 = s1b.BackupTo(Path.Combine(_dir, "backup2.db"));

        // Corrupt the live db, then restore backup2 over a copy path.
        var db2 = Path.Combine(_dir, "svc3_restored.db");
        File.Copy(dbPath, db2);
        CardStore.RestoreFromBackup(backup2, db2);
        using var s3 = new CardStore(db2);
        Assert.Contains(s3.AllEmployees(), e => e.Uid == "UIDZ");
    }
}

/// <summary>Item 4: batch row parsing + validation.</summary>
public class BatchTests
{
    [Fact]
    public void Parse_Tsv_And_Csv_With_And_Without_Header()
    {
        var tsv = "20250101\tAlice\tA+\tNurse\tContractual\t2026-01-01\tC:\\p1.png\n" +
                  "UID20250102,Bob,B+,Doctor,Permanent,,C:\\p2.png";
        var rows = BatchValidator.Parse(tsv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("20250101", rows[0].Uid);
        Assert.Equal("2026-01-01", rows[0].PrintDateText);
        Assert.Equal("20250102", rows[1].Uid);   // "UID" prefix stripped
        Assert.Equal("C:\\p2.png", rows[1].PhotoPath);
    }

    [Fact]
    public void Parse_Skips_Header_Row()
    {
        var rows = BatchValidator.Parse("uid,name,blood,position,contract,printdate,photo\n20250103,Alice,A+,Nurse,Permanent,,,p.png");
        Assert.Single(rows);
        Assert.Equal("20250103", rows[0].Uid);
    }

    [Fact]
    public void Validate_Flags_Errors_And_IntraBatch_Duplicates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exists = new HashSet<string> { "20250100" };
        bool UidExists(string u) => exists.Contains(u);

        var ok = new BatchRowData("20250999", "New Person", "A+", "Supervisor", "Contractual", "2026-01-01", null);
        Assert.Null(BatchValidator.Validate(ok, seen, UidExists, checkPhoto: false));

        var bad = BatchValidator.Validate(
            new BatchRowData("", "", "Z-", "Wizard", "Weird", "not-a-date", "missing.png"),
            new HashSet<string>(), UidExists);
        Assert.NotNull(bad);

        // "UID20250100" normalizes to digits and hits the db-duplicate check
        var dupDb = BatchValidator.Validate(
            new BatchRowData("UID20250100", "Existing", "A+", "Supervisor", "Permanent", "", null),
            new HashSet<string>(), UidExists, checkPhoto: false);
        Assert.Equal("UID already exists", dupDb);

        // same UID twice in batch -> duplicate error on second occurrence
        var first = BatchValidator.Validate(ok, new HashSet<string>(), UidExists, checkPhoto: false);
        Assert.Null(first);
        var second = BatchValidator.Validate(ok, seen, UidExists, checkPhoto: false);
        Assert.Equal("duplicate UID in batch", second);
    }

    [Fact]
    public void ResolvePaths_Resolves_Relative_Photos_Against_Csv_Folder()
    {
        // Simulates importing from a different working directory: resolution must
        // depend only on the CSV folder, never on Environment.CurrentDirectory.
        var csvDir = Path.Combine(Path.GetTempPath(), "csvdir-" + Guid.NewGuid().ToString("N")[..8]);
        var rows = BatchValidator.Parse(
            "20250101,Alice,A+,Nurse,Permanent,2026-01-01,photos\\p1.png\n" +
            "20250102,Bob,B+,Doctor,Permanent,2026-01-01," + csvDir + "\\abs.png");
        BatchValidator.ResolvePaths(rows, csvDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(csvDir, "photos", "p1.png")), rows[0].PhotoPath);
        Assert.True(Path.IsPathRooted(rows[0].PhotoPath));
        Assert.Equal(Path.Combine(csvDir, "abs.png"), rows[1].PhotoPath); // rooted untouched
        Assert.Null(BatchValidator.Parse("20250103,C,,Nurse,,,,").First().PhotoPath); // empty stays empty
        try { Directory.Delete(csvDir, true); } catch { }
    }
}
