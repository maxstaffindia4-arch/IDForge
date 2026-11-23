using IDForge.Core;
using IDForge.Core.Photo;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

public class PhotoPipelineTests
{
    private static SKBitmap MakeSyntheticPhoto()
    {
        // 800x1000 portrait-ish: solid blue background, beige "person" shape
        var bmp = new SKBitmap(800, 1000, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(30, 80, 200)); // blue bg
        using var skin = new SKPaint { Color = new SKColor(220, 180, 140) };
        using var cloth = new SKPaint { Color = new SKColor(60, 60, 90) };
        // head
        canvas.DrawOval(new SKRect(300, 150, 500, 450), skin);
        // shoulders/body
        canvas.DrawRoundRect(new SKRect(180, 480, 620, 950), 60, 60, cloth);
        canvas.Flush();
        return bmp;
    }

    [Fact]
    public void Pipeline_Produces_Cutout_And_Window()
    {
        using var pipeline = new PhotoPipeline();
        using var photo = MakeSyntheticPhoto();
        var result = pipeline.Process(photo, BgMode.Model);

        Assert.Equal(photo.Width, result.Cutout.Width);
        Assert.Equal(photo.Height, result.Cutout.Height);
        Assert.True(result.AutoTransform.Scale > 0);

        using var window = PhotoPipeline.ComposeWindow(result.Cutout, result.AutoTransform);
        Assert.Equal(Config.PhotoW, window.Width);
        Assert.Equal(Config.PhotoH, window.Height);

        // Cutout should have some transparency (background removed)
        var span = result.Cutout.GetPixelSpan();
        int transparent = 0;
        for (int i = 3; i < span.Length; i += 4)
            if (span[i] < 100) transparent++;
        Assert.True(transparent > span.Length / 8, "expected substantial transparent background");
    }

    [Fact]
    public void Cutout_Alpha_Is_Gray_Mask_Not_Periodic_Grid()
    {
        using var pipeline = new PhotoPipeline();
        using var photo = MakeSyntheticPhoto();
        var result = pipeline.Process(photo, BgMode.Model);
        var span = result.Cutout.GetPixelSpan();

        int opaque = 0, midAlpha = 0, total = 0;
        for (int i = 3; i < span.Length; i += 4)
        {
            total++;
            if (span[i] > 200) opaque++;
            if (span[i] > 16 && span[i] < 240) midAlpha++;
        }

        // A real resized mask has soft edges -> intermediate alpha values must exist.
        // The old BGRA bug produced strictly {0,255} alpha in ~exactly 25% of pixels.
        Assert.True(midAlpha > total / 1000,
            $"expected soft mask edges; got midAlpha={midAlpha}/{total}");
        Assert.True(opaque > total / 10 && opaque < total * 9 / 10,
            $"unexpected opaque fraction {opaque}/{total}");
        Assert.True(result.Cutout.ColorType == SKColorType.Bgra8888);
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", name);

    [Fact]
    public void Fixture_Smiling_Rgba_Loads()
    {
        var path = Fixture("Smiling_rgba.png");
        Assert.True(File.Exists(path), $"missing fixture {path}");
        var warning = PhotoPipeline.ValidatePhoto(path, out var image);
        Assert.NotNull(image);
        Assert.Null(warning);
        Assert.Equal(945, image!.Width);
        Assert.Equal(1181, image.Height);
        // not blank: center pixel should have real color
        var c = image.GetPixel(image.Width / 2, image.Height / 2);
        Assert.True(c.Red + c.Green + c.Blue > 30);
        image.Dispose();
    }

    [Theory]
    [InlineData("gray16.png")]       // 16-bit grayscale
    [InlineData("gray_trns.png")]    // 8-bit grayscale + tRNS
    [InlineData("interlaced.png")]   // Adam7 interlaced RGB
    public void Fixture_Exotic_Pngs_Load(string name)
    {
        var path = Fixture(name);
        Assert.True(File.Exists(path), $"missing fixture {path}");
        // These are small, so a size warning is fine — we only require a decoded image.
        var warning = PhotoPipeline.ValidatePhoto(path, out var image);
        Assert.NotNull(image);
        Assert.DoesNotContain("Cannot read", warning ?? "");
        Assert.DoesNotContain("blank", warning ?? "", StringComparison.OrdinalIgnoreCase);
        image!.Dispose();
    }

    [Fact]
    public void ValidatePhoto_Blank_Image_Returns_Error()
    {
        // All-black image: decoder "succeeds" but content is blank.
        var path = Path.Combine(Path.GetTempPath(), $"blank_{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(400, 500, SKColorType.Bgra8888, SKAlphaType.Opaque))
            {
                bmp.Erase(new SKColor(0, 0, 0));
                using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
            }
            var warning = PhotoPipeline.ValidatePhoto(path, out var image);
            Assert.NotNull(warning);
            Assert.Contains("blank", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Null(image);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AutoFit_Ratios_Load_From_Config_With_Provisional_Default()
    {
        // PROVISIONAL default pending owner calibration on real photos.
        Assert.Equal(0.48f, Config.FaceHeightRatio, 3);
        Assert.Equal(0.08f, Config.HeadTopRatio, 3);
        Assert.Equal(0.55f, Config.HairAboveFaceRatio, 3);
    }

    [Fact]
    public void AutoFit_Uses_Configured_Ratios_Without_Coverage_Enforcement()
    {
        using var cutout = MakeSyntheticPhoto();
        var face = new SKRect(300, 200, 500, 500); // 200px face box
        var t = WindowComposer.AutoFit(cutout, face);

        float expectedScale = Config.PhotoH * Config.FaceHeightRatio / face.Height;
        Assert.Equal(expectedScale, t.Scale, 3);

        float headTop = face.Top - face.Height * Config.HairAboveFaceRatio;
        float expectedY = Config.PhotoH * Config.HeadTopRatio - headTop * t.Scale;
        Assert.Equal(expectedY, t.OffsetY, 3);

        float expectedX = Config.PhotoW / 2f - face.MidX * t.Scale;
        Assert.Equal(expectedX, t.OffsetX, 3);
    }

    [Fact]
    public void Process_Skips_Rembg_When_Already_Transparent()
    {
        using var pipeline = new PhotoPipeline();
        var bmp = new SKBitmap(400, 500, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            using var p = new SKPaint { Color = new SKColor(200, 50, 50) };
            c.DrawRect(new SKRect(0, 0, 400, 250), p); // top half opaque only
            c.Flush();
        }
        var r = pipeline.Process(bmp);
        Assert.Equal("skipped-transparent", r.BgMethod);
        // bottom half must remain transparent (rembg did not repaint it)
        var span = r.Cutout.GetPixelSpan();
        Assert.True(span[(450 * 400 + 200) * 4 + 3] < 128, "expected transparent pixels preserved");
        r.Cutout.Dispose();
    }

    [Fact]
    public void Process_Manual_Skip_Uses_Image_As_Is()
    {
        using var pipeline = new PhotoPipeline();
        using var photo = MakeSyntheticPhoto(); // fully opaque
        var r = pipeline.Process(photo, removeBackground: false);
        Assert.Equal("skipped-manual", r.BgMethod);
        // opaque photo used as-is: no transparency introduced
        var span = r.Cutout.GetPixelSpan();
        Assert.True(span[3] > 200, "expected opaque cutout");
        r.Cutout.Dispose();
    }

    [Fact]
    public void Compose_Allows_Zoom_Below_Auto_And_Large_Offsets()
    {
        using var cutout = MakeSyntheticPhoto();
        var auto = WindowComposer.AutoFit(cutout, new SKRect(300, 200, 500, 500));
        // zoom to 40% of auto — previously impossible
        var t = new WindowTransform(auto.Scale * 0.4f, auto.OffsetX - Config.PhotoW, auto.OffsetY + Config.PhotoH);
        using var w = WindowComposer.Compose(cutout, t);
        Assert.Equal(Config.PhotoW, w.Width);
        Assert.Equal(Config.PhotoH, w.Height);
        // window may be partially empty now — that's allowed (white window shows through)
    }

    [Fact]
    public void WindowComposer_CenterCover_Covers_Window()
    {
        using var cutout = MakeSyntheticPhoto();
        var t = WindowComposer.CenterCover(cutout);
        using var window = WindowComposer.Compose(cutout, t);
        // Corners of window should be covered (alpha > 0) since CenterCover scales to cover
        var span = window.GetPixelSpan();
        int corners = 0, covered = 0;
        foreach (var (x, y) in new[] { (2, 2), (Config.PhotoW - 3, 2), (2, Config.PhotoH - 3), (Config.PhotoW - 3, Config.PhotoH - 3) })
        {
            corners++;
            if (span[(y * window.Width + x) * 4 + 3] > 0) covered++;
        }
        Assert.True(covered >= 2, "window edges should be covered by cutout");
    }
}
