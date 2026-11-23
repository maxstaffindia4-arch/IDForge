using System.IO;
using System.Windows.Media.Imaging;
using IDForge.Core;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge;

/// <summary>Orchestrates store + pipeline + render + export for the UI.</summary>
public sealed class CardService : IDisposable
{
    public CardStore Store { get; private set; }
    public PhotoPipeline Pipeline { get; }
    public string PhotoCacheDir { get; }

    // In-memory cache keyed by (path, file stamp, bg mode, pipeline version).
    // Bump PipelineVersion whenever processing logic changes.
    private const int PipelineVersion = 1;
    private readonly string _dbPath;
    private readonly Dictionary<string, PhotoJob> _processCache = new();

    public CardService(string? dbPath = null)
    {
        AppPaths.MigrateLegacyDatabase();
        _dbPath = dbPath ?? AppPaths.DbPath;
        Store = new CardStore(_dbPath);
        Pipeline = new PhotoPipeline();
        PhotoCacheDir = AppPaths.PhotosDir;
        Directory.CreateDirectory(PhotoCacheDir);
        CleanupStalePhotoCache();
    }

    public static string DefaultDbPath => AppPaths.DbPath;

    /// <summary>Renders a card preview bitmap.</summary>
    public static SKBitmap RenderCard(string name, string blood, string position,
        string uid, string contractType, DateOnly printDate, SKBitmap? windowImage)
    {
        var validUntil = Validity.CalculateValidUntil(printDate, contractType);
        return CardRenderer.Render(new CardData(
            name, blood, position, uid,
            Validity.FormatValidity(validUntil),
            windowImage));
    }

    /// <summary>Processes an uploaded photo: returns cutout (cached) + auto transform + window image.</summary>
    /// <summary>Convenience overload: true = auto (plain-white default), false = keep as-is.</summary>
    public PhotoJob ProcessPhoto(string path, bool removeBackground)
        => ProcessPhoto(path, removeBackground ? BgMode.PlainWhite : BgMode.Keep);

    public PhotoJob ProcessPhoto(string path, BgMode mode = BgMode.PlainWhite)
    {
        var key = ProcessCacheKey(path, mode);
        if (_processCache.TryGetValue(key, out var hit) && hit.Cutout != null)
        {
            // Cache owns its bitmaps; hand the caller fresh copies.
            var win = hit.AutoTransform != null
                ? PhotoPipeline.ComposeWindow(hit.Cutout, hit.AutoTransform)
                : null;
            return new PhotoJob(hit.Cutout.Copy(), hit.AutoTransform, win, hit.Warning, hit.BgMethod);
        }

        var warning = PhotoPipeline.ValidatePhoto(path, out var image);
        if (image == null && warning != null && warning.StartsWith("Cannot read image"))
        {
            // SkiaSharp rejected the file — try the WPF decoder, which handles
            // more PNG variants (odd bit depths, tRNS, unusual profiles).
            image = DecodeWithWpf(path);
            if (image != null)
            {
                var w2 = PhotoPipeline.CheckImage(image);
                if (w2 != null && w2.StartsWith("Photo appears blank"))
                {
                    image.Dispose();
                    image = null;
                }
                warning = w2 ?? warning;
            }
        }
        if (image == null)
            return new PhotoJob(null, null, null, warning ?? "Cannot load photo.");

        var result = Pipeline.Process(image, mode);
        image.Dispose();
        var window = PhotoPipeline.ComposeWindow(result.Cutout, result.AutoTransform);
        var job = new PhotoJob(result.Cutout, result.AutoTransform, window,
            result.Warning ?? warning, result.BgMethod);
        if (job.Cutout != null)
            _processCache[key] = new PhotoJob(job.Cutout.Copy(), job.AutoTransform, null, job.Warning, job.BgMethod);
        return job;
    }

    private static string ProcessCacheKey(string path, BgMode mode)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{PipelineVersion}|{mode}|{path}|{(fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "missing")}";
        }
        catch { return $"{PipelineVersion}|{mode}|{path}|err"; }
    }

    /// <summary>Last-resort decode via WPF BitmapDecoder -> BGRA pixels.</summary>
    private static SKBitmap? DecodeWithWpf(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            int w = bi.PixelWidth, h = bi.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            var pixels = new byte[w * h * 4];
            bi.CopyPixels(pixels, w * 4, 0);
            var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            pixels.AsSpan().CopyTo(bmp.GetPixelSpan());
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Recomposes window image after manual nudge.</summary>
    public static SKBitmap ComposeWindow(SKBitmap cutout, WindowTransform t)
        => PhotoPipeline.ComposeWindow(cutout, t);

    /// <summary>Persists the cutout for an employee (cache for reprints).</summary>
    public string CachePhoto(SKBitmap cutout, string uid)
    {
        var digits = Uid.Normalize(uid);
        var safe = digits != null && Uid.IsValid(digits)
            ? Uid.FileToken(digits)
            : string.Concat(uid.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var path = Path.Combine(PhotoCacheDir, Config.CacheFileName(safe));
        using var image = SKImage.FromBitmap(cutout);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>
    /// Deletes cached cutouts written by an older pipeline version (unversioned or
    /// different vN_ prefix) and clears DB references to them so they regenerate.
    /// </summary>
    private void CleanupStalePhotoCache()
    {
        try
        {
            var prefix = Config.PipelineVersion + "_";
            var stale = Directory.GetFiles(PhotoCacheDir, "*.png")
                .Where(f => !Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            if (stale.Count == 0) return;
            foreach (var f in stale) File.Delete(f);
            Store.ClearPhotoPaths(stale);
        }
        catch { /* cache cleanup is best-effort */ }
    }

    public static SKBitmap? LoadCachedPhoto(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        var bmp = SKBitmap.Decode(path);
        if (bmp == null) return null;
        // Reconstruct transform is not stored; for reprint we re-derive a cover fit
        // (photo already cut out, so cover-fit centers it reasonably)
        return bmp;
    }

    /// <summary>Saves employee + card issue, returns issue id. Throws on duplicate UID.</summary>
    public (Employee employee, CardIssue issue) SaveCard(
        string uid, string name, string blood, string position, string contract,
        DateOnly printDate, string? photoPath)
    {
        var existing = Store.FindEmployeeByUid(uid);
        Employee emp;
        if (existing == null)
        {
            emp = new Employee
            {
                Uid = uid, Name = name, BloodGroup = blood,
                Position = position, ContractType = contract,
                PhotoPath = photoPath
            };
            emp.Id = Store.InsertEmployee(emp);
        }
        else
        {
            emp = existing;
            emp.Name = name; emp.BloodGroup = blood; emp.Position = position;
            emp.ContractType = contract; emp.PhotoPath = photoPath ?? emp.PhotoPath;
            Store.UpdateEmployee(emp);
        }

        var validUntil = Validity.CalculateValidUntil(printDate, emp.ContractType);
        var issue = new CardIssue
        {
            EmployeeId = emp.Id,
            Version = Store.NextIssueVersion(emp.Id),
            PrintDate = printDate,
            ValidUntil = validUntil
        };
        issue.Id = Store.InsertIssue(issue);
        return (emp, issue);
    }

    /// <summary>Renders and exports a card for an employee+issue. Returns output paths.
    /// pngPath, when given, is the exact destination for the PNG (PDF goes alongside).</summary>
    public (string pdf, string png) ExportCard(Employee emp, CardIssue issue, SKBitmap? cutout = null, string? pngPath = null)
    {
        string pdf, png;
        if (pngPath != null)
        {
            png = Path.GetFullPath(pngPath);
            var chosenDir = Path.GetDirectoryName(png);
            if (!string.IsNullOrEmpty(chosenDir)) Directory.CreateDirectory(chosenDir);
            pdf = Path.ChangeExtension(png, ".pdf");
        }
        else
        {
            var outDir = AppPaths.OutputDir;
            Directory.CreateDirectory(outDir);
            var empDigits = Uid.Normalize(emp.Uid);
            var safe = empDigits != null && Uid.IsValid(empDigits)
                ? Uid.FileToken(empDigits)
                : string.Concat(emp.Uid.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            pdf = Path.Combine(outDir, $"{safe}_v{issue.Version}.pdf");
            png = Path.Combine(outDir, $"{safe}_v{issue.Version}.png");
        }

        SKBitmap? window = null;
        if (cutout != null)
            window = PhotoPipeline.ComposeWindow(cutout, WindowComposer.CenterCover(cutout));
        else if (emp.PhotoPath != null)
        {
            var cached = LoadCachedPhoto(emp.PhotoPath);
            if (cached != null) window = PhotoPipeline.ComposeWindow(cached, WindowComposer.CenterCover(cached));
        }

        using var card = RenderCard(emp.Name, emp.BloodGroup, emp.Position,
            emp.Uid, emp.ContractType, issue.PrintDate, window);
        Exporter.ExportPng(card, png);
        Exporter.ExportPdf(card, pdf);
        return (pdf, png);
    }

    // ---------- records management ----------

    public string BackupDir => AppPaths.BackupsDir;

    /// <summary>Consistent snapshot via VACUUM INTO. Returns backup path.</summary>
    public string BackupDatabase()
    {
        // Second-resolution names collide when backing up twice quickly —
        // VACUUM INTO refuses an existing file, so suffix until unique.
        string path;
        var i = 0;
        do
        {
            path = Path.Combine(BackupDir,
                $"idcard_{DateTime.Now:yyyyMMdd_HHmmss}{(i == 0 ? "" : $"_{i}")}.db");
            i++;
        } while (File.Exists(path));
        path = Store.BackupTo(path);
        Store.LogAudit(null, "backup", $"Database backed up to {path}");
        return path;
    }

    /// <summary>Restores a backup file. Backs up the current DB first.</summary>
    public string RestoreDatabase(string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found.", backupPath);
        var safety = BackupDatabase();
        Store.Dispose();
        CardStore.RestoreFromBackup(backupPath, _dbPath);
        Store = new CardStore(_dbPath);
        Store.LogAudit(null, "restore", $"Restored from {backupPath} (safety copy: {safety})");
        return safety;
    }

    /// <summary>Exports all records (including deleted) to CSV or JSON file.</summary>
    public string ExportRecords(string path, bool json)
    {
        var rows = RecordsExporter.Collect(Store, includeDeleted: true);
        var text = json ? RecordsExporter.ToJson(rows) : RecordsExporter.ToCsv(rows);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Edits an employee record. If contract type or print date changed, a new
    /// card issue version is created with recalculated validity. UID changes are
    /// allowed (caller must have confirmed + checked duplicates).
    /// </summary>
    public CardIssue? EditRecord(Employee emp, string name, string blood, string position,
        string contract, DateOnly printDate, bool uidChanged)
        => Store.EditEmployee(emp, name, blood, position, contract, printDate, uidChanged);

    public void Dispose()
    {
        Store.Dispose();
        Pipeline.Dispose();
    }
}

public sealed record PhotoJob(
    SKBitmap? Cutout,
    WindowTransform? AutoTransform,
    SKBitmap? WindowImage,
    string? Warning,
    string? BgMethod = null);
