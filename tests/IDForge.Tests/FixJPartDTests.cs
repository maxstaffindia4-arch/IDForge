using System.IO;
using IDForge.Core.Data;
using IDForge.Core.Issuing;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge.Tests;

/// <summary>Fix J-D — photos stored with each issued card.</summary>
public class FixJPartDTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"idfjc_d_{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _photos;
    private readonly string _out;
    private readonly CardStore _store;
    private readonly CardIssuer _issuer;

    public FixJPartDTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "idcard.db");
        _photos = Path.Combine(_dir, "photos");
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_photos);
        _store = new CardStore(_dbPath);
        _issuer = new CardIssuer(_store, _out, _photos);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SKBitmap MakeCutout()
    {
        var bmp = new SKBitmap(200, 240, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var p = new SKPaint { Color = new SKColor(200, 80, 80), IsAntialias = true };
        canvas.DrawOval(40, 20, 120, 140, p);
        using var p2 = new SKPaint { Color = new SKColor(80, 80, 200), IsAntialias = true };
        canvas.DrawRect(60, 160, 80, 70, p2);
        canvas.Flush();
        return bmp;
    }

    private string WriteOriginal(string name = "photo.jpg")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[4096]); // content irrelevant — it is copied, not decoded
        return p;
    }

    private IssueRequest Req(SKBitmap cutout, string? original, string key = "k1") => new(
        "10000010", "Test Person", "O+", "Manager", "Permanent",
        new DateOnly(2025, 1, 15), cutout, null, key,
        PhotoInfo: new IssuePhotoInfo(original, "PlainWhite", 1.5, 10, -5, 1.2, 3, -2));

    [Fact]
    public void Issue_Stores_Cutout_Original_And_Settings()
    {
        var cutout = MakeCutout();
        var original = WriteOriginal();
        var outcome = _issuer.Issue(Req(cutout, original));
        Assert.True(outcome.Success, outcome.Error);
        var issue = outcome.Issue!;

        Assert.False(string.IsNullOrEmpty(issue.PhotoCutoutPath));
        Assert.False(string.IsNullOrEmpty(issue.PhotoOriginalPath));
        Assert.False(string.IsNullOrEmpty(issue.PhotoSettingsJson));

        var cutoutAbs = StoredPhotos.Resolve(_photos, issue.PhotoCutoutPath!);
        var origAbs = StoredPhotos.Resolve(_photos, issue.PhotoOriginalPath!);
        var settingsAbs = Path.Combine(Path.GetDirectoryName(cutoutAbs)!, "settings.json");
        Assert.True(File.Exists(cutoutAbs), cutoutAbs);
        Assert.True(File.Exists(origAbs));
        Assert.True(File.Exists(settingsAbs));
        Assert.Equal(4096, new FileInfo(origAbs).Length);

        // Cutout decodes and keeps its size; extension is webp or png.
        var ext = Path.GetExtension(cutoutAbs);
        Assert.True(ext is ".webp" or ".png", ext);
        using var decoded = SKBitmap.Decode(cutoutAbs);
        Assert.NotNull(decoded);
        Assert.Equal(200, decoded!.Width);

        var settings = StoredPhotos.ReadSettings(issue.PhotoSettingsJson);
        Assert.NotNull(settings);
        Assert.Equal("PlainWhite", settings!.BgMode);
        Assert.Equal(1.5, settings.Zoom);
        Assert.Equal(10, settings.OffsetX);
        Assert.Equal(-5, settings.OffsetY);
        Assert.Equal("v2", settings.ModelVersion); // Config.PipelineVersion
        cutout.Dispose();
    }

    [Fact]
    public void Deleting_Original_Leaves_Record_And_Reprint_Intact()
    {
        var cutout = MakeCutout();
        var original = WriteOriginal();
        var outcome = _issuer.Issue(Req(cutout, original, "k2"));
        Assert.True(outcome.Success, outcome.Error);
        var issue = outcome.Issue!;
        var emp = outcome.Employee!;

        // Move/delete the source photo AND the legacy cache file.
        File.Delete(original);
        if (emp.PhotoPath != null && File.Exists(emp.PhotoPath)) File.Delete(emp.PhotoPath);

        var svc = new CardService(_dbPath) { PhotoCacheDirOverride = _photos };
        try
        {
            // Stored copies still resolve.
            Assert.True(File.Exists(StoredPhotos.Resolve(_photos, issue.PhotoCutoutPath!)));
            Assert.True(File.Exists(StoredPhotos.Resolve(_photos, issue.PhotoOriginalPath!)));

            var png2 = Path.Combine(_dir, "reprint.png");
            var (pdf, png) = svc.ExportCard(emp, issue, pngPath: png2);
            Assert.True(File.Exists(png) && new FileInfo(png).Length > 0);

            // Reprint output matches the original issue (WebP is lossy, so
            // allow tiny per-channel drift but not structural differences).
            using var a = SKBitmap.Decode(outcome.PngPath!)!;
            using var b = SKBitmap.Decode(png)!;
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            var sa = a.GetPixelSpan(); var sb = b.GetPixelSpan();
            var offPixels = 0; var bigDiff = 0;
            for (var i = 0; i < sa.Length; i += 4)
            {
                var d = Math.Max(Math.Abs(sa[i] - sb[i]),
                    Math.Max(Math.Abs(sa[i + 1] - sb[i + 1]), Math.Abs(sa[i + 2] - sb[i + 2])));
                if (d > 4) offPixels++;
                if (d > 40) bigDiff++;
            }
            var total = sa.Length / 4;
            Assert.True(bigDiff * 1000 < total,
                $"reprint differs strongly on {bigDiff}/{total} px ({offPixels} px >4)");
        }
        finally { svc.Dispose(); }
        cutout.Dispose();
    }

    [Fact]
    public void Failed_Photo_Store_Fails_Issue_And_Writes_Nothing()
    {
        // photoDir is an existing FILE -> Directory.CreateDirectory throws.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var issuer = new CardIssuer(_store, _out, blocker);
        var cutout = MakeCutout();
        var outcome = issuer.Issue(Req(cutout, WriteOriginal("p2.jpg"), "k3"));

        Assert.False(outcome.Success);
        Assert.Equal("photo-store", outcome.Step);
        Assert.Empty(_store.AllEmployees());
        Assert.Empty(Directory.GetFiles(_out));   // card png/pdf cleaned up too
        cutout.Dispose();
    }

    [Fact]
    public void Issue_Without_PhotoInfo_Stores_No_Photo()
    {
        var cutout = MakeCutout();
        var req = Req(cutout, null, "none") with { PhotoInfo = null };
        var outcome = _issuer.Issue(req);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Null(outcome.Issue!.PhotoCutoutPath);
        Assert.Null(outcome.Issue.PhotoOriginalPath);
        Assert.Null(outcome.Issue.PhotoSettingsJson);
        cutout.Dispose();
    }

    [Fact]
    public void Stored_Photo_Sizes_For_Typical_Photo()
    {
        // ~12 MP photo-like image: smooth gradient + deterministic noise.
        var w = 3000; var h = 4000;
        using var photo = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var px = photo.GetPixelSpan();
        for (var i = 0; i < px.Length; i += 4)
        {
            var p = i / 4;
            var x = p % w; var y = p / w;
            var n = (byte)((x * 31 + y * 17) & 15);
            px[i] = (byte)Math.Min(255, x / 10 + n);      // B
            px[i + 1] = (byte)Math.Min(255, y / 12 + n);  // G
            px[i + 2] = (byte)(100 + n);                  // R
            px[i + 3] = 255;
        }
        var srcJpg = Path.Combine(_dir, "typical.jpg");
        using (var img = SKImage.FromBitmap(photo))
        using (var data = img.Encode(SKEncodedImageFormat.Jpeg, 90))
            File.WriteAllBytes(srcJpg, data.ToArray());

        // Cutout = photo detail with a transparent margin (alpha exercised).
        var cutout = new SKBitmap(1500, 2000, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(cutout))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(photo, new SKRect(150, 200, 1350, 1800),
                new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.Flush();
        }

        var outcome = _issuer.Issue(new IssueRequest(
            "10000011", "Size Test", "A+", "Intern", "Contractual",
            new DateOnly(2025, 1, 15), cutout, null, "sizekey",
            PhotoInfo: new IssuePhotoInfo(srcJpg, "Model", 1.0, 0, 0)));
        Assert.True(outcome.Success, outcome.Error);

        var issue = outcome.Issue!;
        var cutoutAbs = StoredPhotos.Resolve(_photos, issue.PhotoCutoutPath!);
        var origAbs = StoredPhotos.Resolve(_photos, issue.PhotoOriginalPath!);
        var cutoutKb = new FileInfo(cutoutAbs).Length / 1024.0;
        var origKb = new FileInfo(origAbs).Length / 1024.0;
        var report =
            $"typical photo: original={new FileInfo(srcJpg).Length / 1024.0:0.#} KB ({Path.GetExtension(srcJpg)}), " +
            $"stored original={origKb:0.#} KB, stored cutout={cutoutKb:0.#} KB ({Path.GetExtension(cutoutAbs)})";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "idforge_photo_sizes.txt"), report);
        Assert.True(cutoutKb > 0 && origKb > 0);
        cutout.Dispose();
    }
}
