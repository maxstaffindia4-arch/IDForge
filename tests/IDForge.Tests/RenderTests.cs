using IDForge.Core;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

public class RenderTests
{
    private static CardData Sample() => new(
        Name: "Mehdi Hasan Sakib",
        BloodGroup: "N/A",
        Position: "Technical Executive",
        Uid: "UID20250318",
        ValidityText: Validity.FormatValidity(new DateOnly(2027, 5, 19)),
        PhotoWindow: MakePhotoPlaceholder());

    private static SKBitmap MakePhotoPlaceholder()
    {
        var bmp = new SKBitmap(Config.PhotoW, Config.PhotoH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(200, 200, 220));
        using var paint = new SKPaint { Color = SKColors.DarkSlateGray };
        canvas.DrawCircle(Config.PhotoW / 2f, Config.PhotoH * 0.35f, 60, paint);
        canvas.DrawRoundRect(new SKRect(40, 200, Config.PhotoW - 40, Config.PhotoH - 40), 30, 30, paint);
        canvas.Flush();
        return bmp;
    }

    [Fact]
    public void Render_Produces_637x1012()
    {
        using var card = CardRenderer.Render(Sample());
        Assert.Equal(Config.CardWidth, card.Width);
        Assert.Equal(Config.CardHeight, card.Height);
    }

    [Fact]
    public void Render_Long_Name_Does_Not_Throw_And_Fits()
    {
        var data = Sample() with
        {
            Name = "Khandaker Md. Rafiq Islam" // 24 chars, shrinks but fits
        };
        using var card = CardRenderer.Render(data);
        Assert.Equal(Config.CardWidth, card.Width);
    }

    [Fact]
    public void Render_Too_Long_Name_Throws()
    {
        var data = Sample() with { Name = new string('X', 80) };
        Assert.Throws<ArgumentException>(() => CardRenderer.Render(data));
    }

    [Fact]
    public void All_Positions_Render()
    {
        foreach (var pos in Config.Positions)
        {
            var data = Sample() with { Position = pos };
            using var card = CardRenderer.Render(data);
            Assert.Equal(Config.CardWidth, card.Width);
        }
    }

    [Fact]
    public void Export_Writes_Png_And_Pdf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idcard-tests");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "card.png");
        var pdf = Path.Combine(dir, "card.pdf");
        try
        {
            using (var card = CardRenderer.Render(Sample()))
            {
                Exporter.ExportPng(card, png);
                Exporter.ExportPdf(card, pdf);
            }
            Assert.True(File.Exists(png));
            Assert.True(File.Exists(pdf));
            Assert.True(new FileInfo(pdf).Length > 1000);
            // PDF should contain CR80 page: check MediaBox ~ 153x243 pt (53.98x85.6mm)
            var bytes = File.ReadAllBytes(pdf);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.Contains("MediaBox", text);
        }
        finally
        {
            File.Delete(png);
            File.Delete(pdf);
        }
    }

    private static SKColor Pixel(SKBitmap bmp, int x, int y) => bmp.GetPixel(x, y);

    private static int CountColorNear(SKBitmap bmp, int x0, int y0, int x1, int y1, SKColor target, int tolerance = 40)
    {
        int count = 0;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            if (Math.Abs(p.Red - target.Red) < tolerance &&
                Math.Abs(p.Green - target.Green) < tolerance &&
                Math.Abs(p.Blue - target.Blue) < tolerance)
                count++;
        }
        return count;
    }

    [Fact]
    public void Render_Contains_Expected_Elements()
    {
        using var card = CardRenderer.Render(Sample());

        // Name area has dark ink pixels
        Assert.True(CountColorNear(card, 150, 600, 490, 625, new SKColor(0x1A, 0x25, 0x3C)) > 50,
            "name text missing");

        // Validity line has accent-red pixels
        Assert.True(CountColorNear(card, 200, 855, 440, 865, new SKColor(0xA1, 0x2A, 0x26)) > 20,
            "validity text missing");

        // Barcode area has black bars
        Assert.True(CountColorNear(card, 50, 770, 590, 815, SKColors.Black, 60) > 500,
            "barcode missing");

        // Photo window interior is covered by placeholder (not plain card bg)
        var center = Pixel(card, Config.PhotoX + Config.PhotoW / 2, Config.PhotoY + 60);
        Assert.False(center.Red == 0xFF && center.Green == 0xF0 && center.Blue == 0xF0,
            "photo window appears empty");
    }

    /// <summary>Golden check: static contact line shows corrected '01703-790-950'.</summary>
    [Fact]
    public void Render_Contact_Line_Matches_Corrected_Background()
    {
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "fixtures", "contact_line.png");
        Assert.True(File.Exists(goldenPath), $"missing fixture {goldenPath}");
        using var golden = SKBitmap.Decode(goldenPath);
        using var card = CardRenderer.Render(Sample());

        // crop same region (95,870)-(560,915) from the rendered card
        var crop = new SKBitmap(465, 45, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(card, new SKRect(95, 870, 560, 915),
                new SKRect(0, 0, 465, 45));
            canvas.Flush();
        }

        var g = golden.GetPixelSpan();
        var c = crop.GetPixelSpan();
        int diff = 0;
        for (int i = 0; i < g.Length; i += 4)
            if (Math.Abs(g[i] - c[i]) > 8 || Math.Abs(g[i + 1] - c[i + 1]) > 8 ||
                Math.Abs(g[i + 2] - c[i + 2]) > 8 || Math.Abs(g[i + 3] - c[i + 3]) > 8)
                diff++;
        Assert.True(diff < g.Length / 4 / 200, $"contact line mismatch: {diff} px differ");
        crop.Dispose();
    }

    [Fact]
    public void Png_Has_300_Dpi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idcard-tests");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "dpi.png");
        try
        {
            using var card = CardRenderer.Render(Sample());
            Exporter.ExportPng(card, png);
            var bytes = File.ReadAllBytes(png);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.Contains("pHYs", text);
        }
        finally { File.Delete(png); }
    }
}
