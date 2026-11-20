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
    string? OutputFile = null);

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

        // 4. Single DB transaction.
        try
        {
            var (emp, issue, created) = _store.SaveIssuedCard(
                req.Uid, req.Name, req.BloodGroup, req.Position, req.ContractType,
                req.PrintDate,
                req.Cutout != null ? CacheCutout(req.Cutout, req.Uid) : null,
                req.IssueKey, req.Detail);
            // Record the real output paths on the issue (best-effort update).
            _store.SetIssueOutputs(issue.Id, pdf, png);
            return new IssueOutcome(true, null, null, emp, issue, png, pdf,
                AlreadyIssued: !created);
        }
        catch (Exception ex)
        {
            Cleanup(png, pdf);
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
