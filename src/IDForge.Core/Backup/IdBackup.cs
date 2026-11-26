using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace IDForge.Core.Backup;

/// <summary>
/// .idbackup — a zip archive containing a manifest, a database snapshot
/// (VACUUM INTO), the photo store, and optionally the card images from the
/// "Save cards to" folder. Records refer to files by name, so a restore on
/// a new PC works as long as the relative layout is recreated.
/// </summary>
public static class IdBackup
{
    public const string Extension = ".idbackup";
    public const string ManifestEntry = "manifest.json";
    public const string DbEntry = "db/idcard.db";
    public const string PhotosPrefix = "photos/";
    public const string CardsPrefix = "cards/";

    public sealed record Info(
        string Path,
        DateTime CreatedAt,
        int RecordCount,
        int PhotoCount,
        int CardImageCount,
        bool IncludeCardImages);

    private sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public int SchemaVersion { get; set; }
        public int RecordCount { get; set; }
        public int PhotoCount { get; set; }
        public int CardImageCount { get; set; }
        public bool IncludeCardImages { get; set; }
    }

    /// <summary>Writes a .idbackup file. Returns counts for reporting.</summary>
    public static Info Create(string dbPath, string photosDir, string? cardsDir,
        bool includeCards, string destPath)
    {
        if (!File.Exists(dbPath)) throw new FileNotFoundException("Database not found.", dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);

        // 1. Consistent DB snapshot via VACUUM INTO.
        var tmp = Path.Combine(Path.GetTempPath(), $"idforge_bk_{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tmp.Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }

            var recordCount = CountEmployees(tmp);
            var schemaVersion = SchemaVersion(tmp);

            // 2. Collect photo + card files (names only — stored relative).
            var photoFiles = Directory.Exists(photosDir)
                ? Directory.GetFiles(photosDir, "*", SearchOption.AllDirectories)
                    .Select(f => (Full: f, Rel: Path.GetRelativePath(photosDir, f)))
                    .ToList()
                : new List<(string Full, string Rel)>();
            var cardFiles = includeCards && !string.IsNullOrEmpty(cardsDir) && Directory.Exists(cardsDir)
                ? Directory.GetFiles(cardsDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(cardsDir, "*.pdf", SearchOption.TopDirectoryOnly))
                    .Select(f => (Full: f, Rel: Path.GetFileName(f)))
                    .ToList()
                : new List<(string Full, string Rel)>();

            // 3. Zip it all.
            if (File.Exists(destPath)) File.Delete(destPath);
            var manifest = new Manifest
            {
                Format = "idforge-backup",
                Version = 1,
                CreatedAt = DateTime.Now,
                SchemaVersion = schemaVersion,
                RecordCount = recordCount,
                PhotoCount = photoFiles.Count,
                CardImageCount = cardFiles.Count,
                IncludeCardImages = includeCards
            };
            using (var zip = ZipFile.Open(destPath, ZipArchiveMode.Create))
            {
                var mEntry = zip.CreateEntry(ManifestEntry);
                using (var w = new StreamWriter(mEntry.Open()))
                    w.Write(JsonSerializer.Serialize(manifest));

                zip.CreateEntryFromFile(tmp, DbEntry);
                foreach (var p in photoFiles)
                    zip.CreateEntryFromFile(p.Full, PhotosPrefix + p.Rel.Replace('\\', '/'));
                foreach (var c in cardFiles)
                    zip.CreateEntryFromFile(c.Full, CardsPrefix + c.Rel);
            }

            return new Info(destPath, manifest.CreatedAt, recordCount,
                photoFiles.Count, cardFiles.Count, includeCards);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Validates manifest + database schema version without extracting
    /// everything. Returns the info on success; throws on any problem.
    /// </summary>
    public static Info Validate(string path, int currentSchemaVersion)
    {
        var m = ReadManifest(path);
        if (m.Format != "idforge-backup" || m.Version != 1)
            throw new InvalidDataException("Not an IDForge backup file.");
        var tmp = Path.Combine(Path.GetTempPath(), $"idforge_v_{Guid.NewGuid():N}.db");
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var e = zip.GetEntry(DbEntry)
                    ?? throw new InvalidDataException("Backup is missing its database snapshot.");
                e.ExtractToFile(tmp, overwrite: true);
            }
            var snapSchema = SchemaVersion(tmp);
            if (snapSchema != m.SchemaVersion || snapSchema != currentSchemaVersion)
                throw new InvalidDataException(
                    $"Backup schema version {snapSchema} does not match this app ({currentSchemaVersion}).");
            return new Info(path, m.CreatedAt, m.RecordCount, m.PhotoCount,
                m.CardImageCount, m.IncludeCardImages);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Reads and validates the manifest. Throws on invalid files.</summary>
    public static Info Inspect(string path)
    {
        var m = ReadManifest(path);
        return new Info(path, m.CreatedAt, m.RecordCount, m.PhotoCount,
            m.CardImageCount, m.IncludeCardImages);
    }

    /// <summary>
    /// Validates a backup against the current database schema, then replaces
    /// dbPath, photosDir and (if present) cardsDir contents. Callers must have
    /// disposed any open store on dbPath and made a safety backup first.
    /// Returns the manifest info. Throws before touching anything on invalid
    /// input — extraction goes to a temp dir first.
    /// </summary>
    public static Info Restore(string path, string dbPath, string photosDir,
        string? cardsDir, int currentSchemaVersion)
    {
        var m = ReadManifest(path);
        if (m.Format != "idforge-backup" || m.Version != 1)
            throw new InvalidDataException("Not an IDForge backup file.");

        // Extract to temp and validate the DB inside before replacing anything.
        var tmp = Path.Combine(Path.GetTempPath(), $"idforge_rs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var zip = ZipFile.OpenRead(path))
                zip.ExtractToDirectory(tmp, overwriteFiles: true);

            var snapDb = Path.Combine(tmp, "db", "idcard.db");
            if (!File.Exists(snapDb))
                throw new InvalidDataException("Backup is missing its database snapshot.");
            var snapSchema = SchemaVersion(snapDb);
            if (snapSchema != m.SchemaVersion || snapSchema != currentSchemaVersion)
                throw new InvalidDataException(
                    $"Backup schema version {snapSchema} does not match this app ({currentSchemaVersion}).");

            // Pooled connections keep native handles (and WAL sidecars) alive
            // after Dispose; close them so the file swap sees a clean state.
            SqliteConnection.ClearAllPools();

            // Replace the database file.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
            File.Copy(snapDb, dbPath, overwrite: true);
            // Remove WAL/SHM sidecars so stale journal state can't corrupt.
            foreach (var ext in new[] { "-wal", "-shm" })
                try { File.Delete(dbPath + ext); } catch { }

            // Replace photos dir contents.
            ReplaceDir(Path.Combine(tmp, "photos"), photosDir);

            // Replace card images if the backup contains them.
            var cardsTmp = Path.Combine(tmp, "cards");
            if (Directory.Exists(cardsTmp) && !string.IsNullOrEmpty(cardsDir))
                ReplaceDir(cardsTmp, cardsDir);

            return new Info(path, m.CreatedAt, m.RecordCount, m.PhotoCount,
                m.CardImageCount, m.IncludeCardImages);
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    private static Manifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Backup file not found.", path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry)
                ?? throw new InvalidDataException("Not an IDForge backup (no manifest).");
            using var r = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<Manifest>(r.ReadToEnd())
                ?? throw new InvalidDataException("Backup manifest is empty.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException($"Cannot read backup: {ex.Message}", ex); }
    }

    private static void ReplaceDir(string src, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        if (!Directory.Exists(src)) return;
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    private static int CountEmployees(string dbPath)
    {
        try
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Employees";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    /// <summary>PRAGMA user_version — the app's schema version.</summary>
    public static int SchemaVersion(string dbPath)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
