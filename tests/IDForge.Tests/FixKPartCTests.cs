using System.IO;
using IDForge.Core.Data;
using IDForge.Core.Issuing;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge.Tests;

/// <summary>Fix K-C — the stored original is a resized copy: long edge
/// <= 1200 px, JPEG q85. Cutout stays WebP-with-alpha; settings unchanged.</summary>
public class FixKPartCTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"idfkc_{Guid.NewGuid():N}");
    private readonly string _photos;
    private readonly string _out;
    private readonly CardStore _store;
    private readonly CardIssuer _issuer;

    public FixKPartCTests()
    {
        Directory.CreateDirectory(_dir);
        _photos = Path.Combine(_dir, "photos");
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_photos);
        _store = new CardStore(Path.Combine(_dir, "idcard.db"));
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
        using var p = new SKPaint { Color = new SKColor(200, 80, 80) };
        canvas.DrawOval(40, 20, 120, 140, p);
        canvas.Flush();
        return bmp;
    }

    /// <summary>A real photo-like JPEG larger than 1200 px on the long edge.</summary>
    private string WriteLargePhoto(int w = 3000, int h = 4000)
    {
        var p = Path.Combine(_dir, "big.jpg");
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var px = bmp.GetPixelSpan();
        for (var i = 0; i < px.Length; i += 4)
        {
            var n = (byte)((i / 4 * 31) & 31);
            px[i] = (byte)(40 + n); px[i + 1] = (byte)(90 + n); px[i + 2] = (byte)(140 + n);
            px[i + 3] = 255;
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(p, data.ToArray());
        return p;
    }

    [Fact]
    public void Stored_Original_Is_Resized_Long_Edge_At_Most_1200()
    {
        var cutout = MakeCutout();
        var src = WriteLargePhoto();
        var req = new IssueRequest(
            "10000020", "Resize Test", "B+", "Supervisor", "Permanent",
            new DateOnly(2025, 1, 15), cutout, null, "resizekey",
            PhotoInfo: new IssuePhotoInfo(src, "PlainWhite", 1.0, 0, 0));
        var outcome = _issuer.Issue(req);
        Assert.True(outcome.Success, outcome.Error);
        cutout.Dispose();

        var issue = outcome.Issue!;
        var origAbs = StoredPhotos.Resolve(_photos, issue.PhotoOriginalPath!);
        Assert.Equal(".jpg", Path.GetExtension(origAbs));

        using var stored = SKBitmap.Decode(origAbs);
        Assert.NotNull(stored);
        Assert.True(Math.Max(stored!.Width, stored.Height) <= 1200,
            $"stored original is {stored.Width}x{stored.Height}");
        // Aspect ratio preserved (3000x4000 -> 900x1200).
        Assert.Equal(3, stored.Width * 4 / stored.Height);

        // Cutout and settings are unchanged by the resize.
        var cutoutAbs = StoredPhotos.Resolve(_photos, issue.PhotoCutoutPath!);
        Assert.True(File.Exists(cutoutAbs));
        var ext = Path.GetExtension(cutoutAbs);
        Assert.True(ext is ".webp" or ".png", ext);
        var settings = StoredPhotos.ReadSettings(issue.PhotoSettingsJson);
        Assert.NotNull(settings);
        Assert.Equal("PlainWhite", settings!.BgMode);
    }

    [Fact]
    public void Stored_Original_Is_Much_Smaller_Than_Source()
    {
        var cutout = MakeCutout();
        var src = WriteLargePhoto();
        var srcKb = new FileInfo(src).Length / 1024.0;
        var outcome = _issuer.Issue(new IssueRequest(
            "10000021", "Size Test", "A+", "Intern", "Contractual",
            new DateOnly(2025, 1, 15), cutout, null, "sizekey2",
            PhotoInfo: new IssuePhotoInfo(src, "PlainWhite", 1.0, 0, 0)));
        Assert.True(outcome.Success, outcome.Error);
        cutout.Dispose();

        var origAbs = StoredPhotos.Resolve(_photos, outcome.Issue!.PhotoOriginalPath!);
        var storedKb = new FileInfo(origAbs).Length / 1024.0;
        var cutoutAbs = StoredPhotos.Resolve(_photos, outcome.Issue.PhotoCutoutPath!);
        var cutoutKb = new FileInfo(cutoutAbs).Length / 1024.0;
        var totalKb = storedKb + cutoutKb +
            new FileInfo(Path.Combine(Path.GetDirectoryName(origAbs)!, "settings.json")).Length / 1024.0;

        var report = $"source={srcKb:0.#} KB, stored original={storedKb:0.#} KB, " +
                     $"stored cutout={cutoutKb:0.#} KB, total per issue={totalKb:0.#} KB";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "idforge_kc_sizes.txt"), report);

        Assert.True(storedKb < srcKb / 4,
            $"stored original {storedKb:0.#} KB not <25% of source {srcKb:0.#} KB");
    }
}
