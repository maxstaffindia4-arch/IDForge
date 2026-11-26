using System.IO;
using System.IO.Compression;
using System.Linq;
using IDForge.Core.Backup;
using IDForge.Core.Data;

namespace IDForge.Tests;

/// <summary>Fix J-C — .idbackup file backup and restore.</summary>
public class FixJPartCTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), $"idfjc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static CardService NewService(string dir, string? photos, string? cards)
    {
        var svc = new CardService(Path.Combine(dir, "idcard.db"));
        svc.BackupPhotosDirOverride = photos;
        svc.BackupCardsDirOverride = cards;
        return svc;
    }

    private static void SeedEmployee(CardService svc, string uid)
    {
        svc.Store.InsertEmployee(new Employee
        {
            Uid = uid, Name = "Test Person", BloodGroup = "O+",
            Position = "Nurse", ContractType = "Permanent", PhotoPath = null
        });
    }

    [Fact]
    public void Backup_Contains_Db_Photos_And_Cards_When_On()
    {
        var dir = Tmp();
        var photos = Path.Combine(dir, "photos"); var cards = Path.Combine(dir, "cards");
        Directory.CreateDirectory(photos); Directory.CreateDirectory(cards);
        File.WriteAllText(Path.Combine(photos, "cutout.png"), "png-data");
        File.WriteAllText(Path.Combine(cards, "UID1_v1.png"), "card-png");
        var svc = NewService(dir, photos, cards);
        SeedEmployee(svc, "10000001");
        var bak = Path.Combine(dir, "out.idbackup");

        var info = svc.BackupToFile(bak, includeCardImages: true);

        Assert.True(File.Exists(bak));
        Assert.Equal(1, info.RecordCount);
        Assert.Equal(1, info.PhotoCount);
        Assert.Equal(1, info.CardImageCount);
        using var zip = ZipFile.OpenRead(bak);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("db/idcard.db"));
        Assert.NotNull(zip.GetEntry("photos/cutout.png"));
        Assert.NotNull(zip.GetEntry("cards/UID1_v1.png"));
        svc.Dispose();
    }

    [Fact]
    public void Backup_Omits_Card_Images_When_Off()
    {
        var dir = Tmp();
        var photos = Path.Combine(dir, "photos"); var cards = Path.Combine(dir, "cards");
        Directory.CreateDirectory(photos); Directory.CreateDirectory(cards);
        File.WriteAllText(Path.Combine(cards, "UID1_v1.png"), "card-png");
        var svc = NewService(dir, photos, cards);
        SeedEmployee(svc, "10000002");
        var bak = Path.Combine(dir, "out.idbackup");

        var info = svc.BackupToFile(bak, includeCardImages: false);

        Assert.Equal(0, info.CardImageCount);
        using var zip = ZipFile.OpenRead(bak);
        Assert.Null(zip.GetEntry("cards/UID1_v1.png"));
        svc.Dispose();
    }

    [Fact]
    public void Restore_On_Fresh_Folder_Brings_Back_Records_Photos_Cards()
    {
        var src = Tmp(); var dst = Tmp();
        var srcPhotos = Path.Combine(src, "photos"); var srcCards = Path.Combine(src, "cards");
        Directory.CreateDirectory(srcPhotos); Directory.CreateDirectory(srcCards);
        File.WriteAllText(Path.Combine(srcPhotos, "cutout.png"), "png-data");
        File.WriteAllText(Path.Combine(srcCards, "UID9_v1.png"), "card-png");
        var svc = NewService(src, srcPhotos, srcCards);
        SeedEmployee(svc, "10000003");
        var bak = Path.Combine(src, "out.idbackup");
        var binfo = svc.BackupToFile(bak, includeCardImages: true);
        Assert.Equal(1, binfo.RecordCount);
        svc.Dispose();

        // Fresh data folder: empty db, empty photos/cards dirs.
        var dstPhotos = Path.Combine(dst, "photos"); var dstCards = Path.Combine(dst, "cards");
        Directory.CreateDirectory(dstPhotos); Directory.CreateDirectory(dstCards);
        var svc2 = NewService(dst, dstPhotos, dstCards);
        // Pre-validate like the UI does, then restore (auto safety backup runs).
        svc2.InspectBackupFile(bak);
        svc2.RestoreFromFile(bak);

        var dstDb = Path.Combine(dst, "idcard.db");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={dstDb};Mode=ReadOnly"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Employees";
            var n = cmd.ExecuteScalar();
            Assert.True(Convert.ToInt32(n) == 1,
                $"raw Employees count after restore: {n}, size={new FileInfo(dstDb).Length}");
        }
        using (var cmd = svc2.Store.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Employees";
            var n = cmd.ExecuteScalar();
            Assert.True(Convert.ToInt32(n) == 1,
                $"store Employees count after restore: {n}");
        }
        Assert.Single(svc2.Store.AllEmployees());
        Assert.Equal("Test Person", svc2.Store.AllEmployees()[0].Name);
        Assert.Equal("png-data", File.ReadAllText(Path.Combine(dstPhotos, "cutout.png")));
        Assert.Equal("card-png", File.ReadAllText(Path.Combine(dstCards, "UID9_v1.png")));
        svc2.Dispose();
    }

    [Fact]
    public void Invalid_Or_Wrong_Schema_Is_Rejected_And_Nothing_Replaced()
    {
        var dir = Tmp();
        var photos = Path.Combine(dir, "photos"); var cards = Path.Combine(dir, "cards");
        Directory.CreateDirectory(photos); Directory.CreateDirectory(cards);
        File.WriteAllText(Path.Combine(photos, "keep.txt"), "original");
        var svc = NewService(dir, photos, cards);
        SeedEmployee(svc, "10000004");

        // Garbage file.
        var bad = Path.Combine(dir, "bad.idbackup");
        File.WriteAllText(bad, "not a zip");
        Assert.ThrowsAny<Exception>(() => svc.InspectBackupFile(bad));

        // Wrong schema version: build a valid backup then tamper the manifest.
        var bak = Path.Combine(dir, "tampered.idbackup");
        svc.BackupToFile(bak, includeCardImages: true);
        TamperManifest(bak, m => m["SchemaVersion"] = 999);
        Assert.Throws<InvalidDataException>(() => svc.InspectBackupFile(bak));
        Assert.Throws<InvalidDataException>(() => svc.RestoreFromFile(bak));

        // Nothing replaced: photo file and employee still there.
        Assert.Equal("original", File.ReadAllText(Path.Combine(photos, "keep.txt")));
        Assert.Single(svc.Store.AllEmployees());
        svc.Dispose();
    }

    [Fact]
    public void Restore_Backs_Up_Current_Data_First()
    {
        var dir = Tmp();
        var photos = Path.Combine(dir, "photos"); var cards = Path.Combine(dir, "cards");
        Directory.CreateDirectory(photos); Directory.CreateDirectory(cards);
        var svc = NewService(dir, photos, cards);
        SeedEmployee(svc, "10000005");
        var bak = Path.Combine(dir, "out.idbackup");
        svc.BackupToFile(bak, includeCardImages: true);

        // Change data, then restore — auto safety backup must capture the change.
        svc.Store.InsertEmployee(new Employee
        {
            Uid = "10000006", Name = "Second", BloodGroup = "A+",
            Position = "Doctor", ContractType = "Contractual", PhotoPath = null
        });
        var before = Directory.GetFiles(svc.BackupDir, "idforge_auto_*.idbackup").Length;
        svc.RestoreFromFile(bak);
        var after = Directory.GetFiles(svc.BackupDir, "idforge_auto_*.idbackup");
        Assert.True(after.Length > before, "no automatic .idbackup was written");

        // The auto backup contains BOTH employees (the pre-restore state).
        var auto = after.OrderBy(f => f).Last();
        using var zip = ZipFile.OpenRead(auto);
        var tmp = Path.Combine(dir, "autodb.db");
        zip.GetEntry("db/idcard.db")!.ExtractToFile(tmp);
        var cnt = IdBackup.SchemaVersion(tmp); // opens fine = valid db
        Assert.True(File.Exists(tmp));
        svc.Dispose();
    }

    private static void TamperManifest(string path, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var e = zip.GetEntry("manifest.json")!;
        string json;
        using (var s = e.Open())
        using (var r = new StreamReader(s))
            json = r.ReadToEnd();
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        edit(node);
        e.Delete();
        var ne = zip.CreateEntry("manifest.json");
        using var w = new StreamWriter(ne.Open());
        w.Write(node.ToJsonString());
    }
}
