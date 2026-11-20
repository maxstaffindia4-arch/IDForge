using IDForge.Core;
using IDForge.Core.Photo;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

/// <summary>Edge-halo / border-artifact cleanup for model and plain-white cutouts.</summary>
public class CutoutEdgeTests
{
    /// <summary>Synthetic cutout: opaque subject square, cyan fringe pixels on its
    /// edge, a detached blob, and semi-transparent slivers along the image border.</summary>
    private static SKBitmap MakeDirtyCutout(int w = 200, int h = 200)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        // Subject: opaque warm square 60..140
        using (var p = new SKPaint { Color = new SKColor(100, 120, 150) })
            canvas.DrawRect(new SKRect(60, 60, 141, 141), p);
        // Cyan fringe just outside the right edge (simulates bg bleed)
        using (var p = new SKPaint { Color = new SKColor(0, 220, 255, 140) })
            canvas.DrawRect(new SKRect(141, 62, 143, 139), p);
        // Detached blob top-left
        using (var p = new SKPaint { Color = SKColors.Blue })
            canvas.DrawRect(new SKRect(10, 10, 21, 21), p);
        // Border slivers: semi-transparent dark arcs along top corners
        using (var p = new SKPaint { Color = new SKColor(60, 10, 10, 160) })
        {
            canvas.DrawRect(new SKRect(0, 0, 30, 2), p);
            canvas.DrawRect(new SKRect(w - 30, 0, w, 2), p);
        }
        canvas.Flush();
        return bmp;
    }

    [Fact]
    public void PostProcess_Drops_Blob_And_Border_And_Decontaminates()
    {
        using var dirty = MakeDirtyCutout();
        using var clean = CutoutPostProcess.Process(dirty, new SKRect(80, 80, 120, 120));
        var s = clean.GetPixelSpan();

        // Detached blob gone
        Assert.Equal(0, s[(15 * 200 + 15) * 4 + 3]);
        // Border slivers gone (a<250 in outer band)
        Assert.Equal(0, s[0 * 4 + 3]);
        Assert.Equal(0, s[5 * 4 + 3]);
        // Subject interior still opaque
        Assert.Equal(255, s[(100 * 200 + 100) * 4 + 3]);
        // Cyan fringe pixel: alpha reduced/kept semi but color pulled toward warm interior
        int fi = (100 * 200 + 141) * 4;
        Assert.True(s[fi + 3] < 250, "fringe should not stay strongly opaque");
        // no cyan-only color left at the fringe (B and G high, R low is cyan)
        Assert.False(s[fi + 0] > 200 && s[fi + 2] < 60 && s[fi + 3] > 0,
            $"cyan fringe remains: BGRA=({s[fi]},{s[fi + 1]},{s[fi + 2]},{s[fi + 3]})");
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "studio_portrait.png");

    [Fact]
    public void Model_Cutout_Border_Has_No_Opaque_Pixels()
    {
        Assert.True(File.Exists(Fixture), "studio_portrait.png fixture missing");
        using var pipe = new PhotoPipeline();
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);
        var r = pipe.Process(img, BgMode.Model);
        using var cut = r.Cutout;

        var s = cut.GetPixelSpan();
        int w = cut.Width, h = cut.Height;
        // Top edge: the subject's head does not reach the top of the frame, so the
        // whole top band must be free of opaque pixels (this is where the corner
        // arcs appeared). Bottom/side edges may legitimately contain the subject.
        for (int y = 0; y < Math.Min(4, h); y++)
        for (int x = 0; x < w; x++)
        {
            int a = s[(y * w + x) * 4 + 3];
            Assert.True(a < 250, $"opaque pixel in top band at ({x},{y}): a={a}");
        }
        // Top corner squares (30x30): nothing visible at all.
        for (int y = 0; y < 30; y++)
        for (int x = 0; x < 30; x++)
        {
            Assert.True(s[(y * w + x) * 4 + 3] < 200, $"corner artifact at ({x},{y})");
            Assert.True(s[(y * w + (w - 1 - x)) * 4 + 3] < 200, $"corner artifact at ({w - 1 - x},{y})");
        }
    }

    [Fact]
    public void Model_Window_Corners_Have_No_Dark_Pixels()
    {
        using var pipe = new PhotoPipeline();
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);
        var r = pipe.Process(img, BgMode.Model);
        using var win = PhotoPipeline.ComposeWindow(r.Cutout, r.AutoTransform);
        var s = win.GetPixelSpan();
        int w = win.Width, h = win.Height;

        // Only the TOP corners: head top sits ~8% down, so anything painted near
        // y=2 must be white window/background — never a dark/cyan arc. Bottom
        // corners legitimately show dark clothing, so they are not checked.
        foreach (var (x, y) in new[] { (2, 2), (w - 3, 2), (w / 2, 2) })
        {
            int i = (y * w + x) * 4;
            int a = s[i + 3];
            // Transparent is fine (window bg shows); if painted it must not be dark.
            if (a > 40)
            {
                int lum = s[i] + s[i + 1] + s[i + 2];   // BGRA sum
                Assert.True(lum > 300, $"dark pixel at window corner ({x},{y}): BGRA=({s[i]},{s[i + 1]},{s[i + 2]},{a})");
            }
        }
    }

    [Fact]
    public void Model_Edge_Pixels_Have_No_Cyan_Fringe()
    {
        using var pipe = new PhotoPipeline();
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);
        var r = pipe.Process(img, BgMode.Model);
        using var cut = r.Cutout;
        var s = cut.GetPixelSpan();
        int w = cut.Width, h = cut.Height;

        int checked_ = 0, cyan = 0;
        for (int y = 1; y < h - 1; y += 3)
        for (int x = 1; x < w - 1; x += 3)
        {
            int i = (y * w + x) * 4;
            int a = s[i + 3];
            if (a < 40 || a > 240) continue;    // only the soft edge band
            // cyan fringe signature: strong B+G, weak R
            if (s[i] > 180 && s[i + 1] > 180 && s[i + 2] < 90) cyan++;
            checked_++;
        }
        Assert.True(checked_ > 50, $"expected soft edge pixels, found {checked_}");
        Assert.Equal(0, cyan);
    }

    [Fact]
    public void PlainWhite_Still_Passes_Blazer_Collar_Check()
    {
        // Regression guard: post-processing must not eat the dark blazer / white collar.
        using var pipe = new PhotoPipeline();
        using var img = PhotoPipeline.LoadWithExifOrientation(Fixture);
        var r = pipe.Process(img, BgMode.PlainWhite);
        using var cut = r.Cutout;
        Assert.True(cut.GetPixel(200, 800).Alpha > 200, "blazer must stay opaque");
        Assert.True(cut.GetPixel(400, 640).Alpha > 200, "collar must stay opaque");
        Assert.True(cut.GetPixel(5, 5).Alpha < 30, "white bg must be transparent");
    }
}
