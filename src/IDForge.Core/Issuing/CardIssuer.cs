using System.Text.Json;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge.Core.Issuing;

/// <summary>Everything needed to issue one card.</summary>
public sealed record IssueRequest(
    string Uid,
    string Name,
    string BloodGroup,
    string Position,
    string ContractType,
    DateOnly PrintDate,
    SKBitmap? Cutout,               // processed RGBA cutout, or null
    WindowTransform? Transform,     // photo placement; null = center-cover
    string IssueKey,                // idempotency key, e.g. "single:{uid}:{ticks}"
    string? Detail = null,
    /// <summary>Batch issues require the UID to be new to Records.</summary>
    bool RequireNewUid = false,
    /// <summary>Write the PDF alongside the PNG (default off — PNG is the primary output).</summary>
    bool IncludePdf = false,
    /// <summary>User-chosen PNG destination; null = auto name in the issuer's output dir.</summary>
    string? OutputFile = null,
    /// <summary>Photo details to store with the issue (Fix J-D); null = don't store.</summary>
    IssuePhotoInfo? PhotoInfo = null);

/// <summary>
/// Operator-facing photo settings stored with each issue so re-issue and
/// reprint reproduce the same photo. Zoom/offsets are the manual nudge;
/// Auto* is the auto-fit the nudge is relative to.
/// </summary>
public sealed record IssuePhotoInfo(
    string? OriginalPath,
    string BgMode,
    double Zoom, double OffsetX, double OffsetY,
    double AutoScale = 1.0, double AutoOffsetX = 0.0, double AutoOffsetY = 0.0);

public sealed record IssueOutcome(
    bool Success,
    string? Step,        // validate | render | verify | save — set on failure
    string? Error,
    Employee? Employee,
    CardIssue? Issue,
    string? PngPath,
    string? PdfPath,
    bool AlreadyIssued = false);

/// <summary>
/// The single issue path (plan §5): revalidate -> render -> verify files ->
/// one DB transaction. Any failure before the transaction leaves Records
/// untouched; a DB failure rolls back and removes the written files.
/// </summary>
public sealed class CardIssuer
{
    private readonly CardStore _store;
    private readonly string _outputDir;
    private readonly string _photoDir;

    public CardIssuer(CardStore store, string outputDir, string? photoDir = null)
    {
        _store = store;
        _outputDir = outputDir;
        _photoDir = photoDir ?? AppPaths.PhotosDir;
        Directory.CreateDirectory(outputDir);
    }

    public IssueOutcome Issue(IssueRequest req)
    {
        // Normalize first: storage and filenames always use bare digits.
        req = req with { Uid = Uid.Normalize(req.Uid) ?? req.Uid.Trim() };

        // Idempotent replay first: same key already issued -> report, don't
        // duplicate — even if the UID now exists in Records.
        var prior = _store.FindIssueByKey(req.IssueKey);
        if (prior != null)
        {
            var emp = _store.FindEmployeeById(prior.EmployeeId);
            var v = prior.Version;
            var replayToken = FileToken(req.Uid);
            var pdfPath = Path.Combine(_outputDir, $"{replayToken}_v{v}.pdf");
            return new IssueOutcome(true, null, null, emp, prior,
                Path.Combine(_outputDir, $"{replayToken}_v{v}.png"),
                File.Exists(pdfPath) ? pdfPath : null,
                AlreadyIssued: true);
        }

        // 1. Revalidate
        var invalid = Validate(req);
        if (invalid != null)
            return new IssueOutcome(false, "validate", invalid, null, null, null, null);

        // 2. Render + export (version guessed from current state; the DB
        //    transaction recomputes it — filenames use the same rule).
        string token = FileToken(req.Uid);
        var existingEmp = _store.FindEmployeeByUid(req.Uid);
        int version = existingEmp == null ? 1 : _store.NextIssueVersion(existingEmp.Id);
        string png, pdf;
        if (req.OutputFile != null)
        {
            png = Path.GetFullPath(req.OutputFile);
            var chosenDir = Path.GetDirectoryName(png);
            if (!string.IsNullOrEmpty(chosenDir)) Directory.CreateDirectory(chosenDir);
            pdf = Path.ChangeExtension(png, ".pdf");
        }
        else
        {
            png = Path.Combine(_outputDir, $"{token}_v{version}.png");
            pdf = Path.Combine(_outputDir, $"{token}_v{version}.pdf");
        }

        SKBitmap? window = null;
        try
        {
            if (req.Cutout != null)
            {
                var t = req.Transform ?? WindowComposer.CenterCover(req.Cutout);
                window = PhotoPipeline.ComposeWindow(req.Cutout, t);
            }
            var validUntil = Validity.CalculateValidUntil(req.PrintDate, req.ContractType);
            using var card = CardRenderer.Render(new CardData(
                req.Name, req.BloodGroup, req.Position, req.Uid,
                Validity.FormatValidity(validUntil), window));
            Exporter.ExportPng(card, png);
            if (req.IncludePdf) Exporter.ExportPdf(card, pdf);
        }
        catch (Exception ex)
        {
            Cleanup(png, pdf);
            return new IssueOutcome(false, "render", ex.Message, null, null, null, null);
        }
        finally { window?.Dispose(); }

        // 3. Verify files exist and are non-empty.
        var pdfExpected = req.IncludePdf;
        if (!File.Exists(png) || new FileInfo(png).Length == 0 ||
            (pdfExpected && (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)))
        {
            Cleanup(png, pdf);
            return new IssueOutcome(false, "verify", "Output file missing or empty.", null, null, null, null);
        }
        if (!pdfExpected) pdf = null!;

        // 3b. Store the approved cutout, original photo, and settings (J-D).
        //     Written before the DB transaction; any failure aborts the issue.
        IssuePhotoRefs? photoRefs = null;
        string? photoDirAbs = null;
        if (req.PhotoInfo != null)
        {
            try
            {
                photoDirAbs = Path.Combine(_photoDir, "issues", $"{token}_v{version}");
                photoRefs = StoreIssuePhotos(req, photoDirAbs);
            }
            catch (Exception ex)
            {
                Cleanup(png, pdf);
                return new IssueOutcome(false, "photo-store", ex.Message, null, null, null, null);
            }
        }

        // 4. Single DB transaction.
        try
        {
            var (emp, issue, created) = _store.SaveIssuedCard(
                req.Uid, req.Name, req.BloodGroup, req.Position, req.ContractType,
                req.PrintDate,
                req.Cutout != null ? CacheCutout(req.Cutout, req.Uid) : null,
                req.IssueKey, req.Detail, photoRefs);
            // Record the real output paths on the issue (best-effort update).
            _store.SetIssueOutputs(issue.Id, pdf, png);
            return new IssueOutcome(true, null, null, emp, issue, png, pdf,
                AlreadyIssued: !created);
        }
        catch (Exception ex)
        {
            Cleanup(png, pdf);
            if (photoDirAbs != null)
                try { Directory.Delete(photoDirAbs, recursive: true); } catch { }
            return new IssueOutcome(false, "save", ex.Message, null, null, null, null);
        }
    }

    private string? Validate(IssueRequest req)
    {
        if (!Uid.IsValid(req.Uid))
            return $"UID must be digits only ({Uid.MinLength}-{Uid.MaxLength} characters).";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length < 2)
            return "Name is required.";
        if (!Config.BloodGroups.Contains(req.BloodGroup)) return "Invalid blood group.";
        if (!Config.Positions.Contains(req.Position)) return "Invalid position.";
        if (!Config.ContractTypes.Contains(req.ContractType)) return "Invalid contract type.";
        if (req.Cutout == null) return "Photo is required — a card cannot be issued without one.";
        if (req.RequireNewUid && _store.FindEmployeeByUid(req.Uid) != null)
            return $"UID {req.Uid} already exists in Records.";
        return null;
    }

    /// <summary>Output file token: "UID" + digits, e.g. UID20250318_v2.png.</summary>
    private static string FileToken(string uid) => Uid.FileToken(Uid.Normalize(uid) ?? uid.Trim());

    private void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>
    /// Writes the per-issue photo set into &lt;photoDir&gt;/issues/&lt;token&gt;_v&lt;n&gt;/:
    /// cutout (WebP with alpha, PNG fallback), a copy of the original photo,
    /// and settings.json. Returns DB-relative paths. Throws on any failure —
    /// the caller aborts the issue before touching Records.
    /// </summary>
    private IssuePhotoRefs StoreIssuePhotos(IssueRequest req, string dirAbs)
    {
        Directory.CreateDirectory(dirAbs);
        var pi = req.PhotoInfo!;
        var relDir = Path.GetRelativePath(_photoDir, dirAbs).Replace('\\', '/');

        // 1. Cutout — WebP keeps transparency and is much smaller; PNG fallback.
        string cutoutFile;
        using (var image = SKImage.FromBitmap(req.Cutout!))
        {
            SKData? webp = null;
            try { webp = image.Encode(SKEncodedImageFormat.Webp, 90); } catch { }
            if (webp != null && webp.Size > 0)
            {
                cutoutFile = "cutout.webp";
                File.WriteAllBytes(Path.Combine(dirAbs, cutoutFile), webp.ToArray());
                webp.Dispose();
            }
            else
            {
                webp?.Dispose();
                using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidOperationException("Could not encode stored cutout.");
                cutoutFile = "cutout.png";
                File.WriteAllBytes(Path.Combine(dirAbs, cutoutFile), png.ToArray());
            }
        }

        // 2. Original photo — resized copy (long edge <= 1200 px, JPEG q85;
        // Fix K-C). Enough detail to re-process later at ~a fifth of the size.
        // A missing or undecodable source fails the issue.
        string? originalFile = null;
        if (!string.IsNullOrEmpty(pi.OriginalPath))
        {
            if (!File.Exists(pi.OriginalPath))
                throw new FileNotFoundException("Original photo not found.", pi.OriginalPath);
            using var src = SKBitmap.Decode(pi.OriginalPath)
                ?? throw new InvalidOperationException("Could not decode original photo.");
            const int maxEdge = 1200;
            var scale = Math.Min(1.0, maxEdge / (double)Math.Max(src.Width, src.Height));
            using var bmp = scale < 1.0
                ? src.Resize(new SKImageInfo(
                        Math.Max(1, (int)Math.Round(src.Width * scale)),
                        Math.Max(1, (int)Math.Round(src.Height * scale)),
                        src.ColorType, src.AlphaType),
                    new SKSamplingOptions(SKFilterMode.Linear))
                : src.Copy();
            using var image = SKImage.FromBitmap(bmp);
            using var jpg = image.Encode(SKEncodedImageFormat.Jpeg, 85)
                ?? throw new InvalidOperationException("Could not encode stored original.");
            originalFile = "original.jpg";
            File.WriteAllBytes(Path.Combine(dirAbs, originalFile), jpg.ToArray());
        }

        // 3. Settings used for this issue.
        var t = req.Transform ?? WindowComposer.CenterCover(req.Cutout!);
        var settingsJson = JsonSerializer.Serialize(new StoredPhotoSettings(
            pi.BgMode, pi.Zoom, pi.OffsetX, pi.OffsetY,
            pi.AutoScale, pi.AutoOffsetX, pi.AutoOffsetY, Config.PipelineVersion,
            t.Scale, t.OffsetX, t.OffsetY));
        File.WriteAllText(Path.Combine(dirAbs, "settings.json"), settingsJson);

        return new IssuePhotoRefs(
            $"{relDir}/{cutoutFile}",
            originalFile == null ? null : $"{relDir}/{originalFile}",
            settingsJson);
    }

    /// <summary>Copies the cutout into the photo cache; returns cached path.</summary>
    private string CacheCutout(SKBitmap cutout, string uid)
    {
        var dir = _photoDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{FileToken(uid)}.png");
        using var image = SKImage.FromBitmap(cutout);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
