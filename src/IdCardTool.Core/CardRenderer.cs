using SkiaSharp;

namespace IdCardTool.Core;

/// <summary>Card data needed to render the front side.</summary>
public sealed record CardData(
    string Name,
    string BloodGroup,
    string Position,
    string Uid,
    string ValidityText,
    SKBitmap? PhotoWindow); // already fitted to 313x382 with transparency, or null

public static class CardRenderer
{
    private static readonly SKTypeface RobotoMedium = LoadFont("Roboto-Medium.ttf");
    private static readonly SKTypeface RobotoExtraBold = LoadFont("Roboto-ExtraBold.ttf");
    private static readonly SKTypeface SourceSansRegular = LoadFont("SourceSans3-Regular.ttf");
    private static readonly SKTypeface SourceSansSemiBold = LoadFont("SourceSans3-SemiBold.ttf");

    private static SKTypeface LoadFont(string file)
    {
        var path = Path.Combine(Config.FontDir, file);
        return File.Exists(path)
            ? SKTypeface.FromFile(path)
            : SKTypeface.Default;
    }

    private sealed class TextStyle : IDisposable
    {
        public required SKFont Font;
        public required SKPaint Paint;
        public void Dispose() { Font.Dispose(); Paint.Dispose(); }
    }

    private static TextStyle MakeText(SKTypeface typeface, float size, SKColor color)
    {
        var font = new SKFont(typeface, size, 1f, 0f);
        var paint = new SKPaint { Color = color, IsAntialias = true };
        return new TextStyle { Font = font, Paint = paint };
    }

    /// <summary>Renders the full 637x1012 card.</summary>
    public static SKBitmap Render(CardData data)
    {
        var bmp = new SKBitmap(Config.CardWidth, Config.CardHeight,
            SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(Config.White);

        using var background = SKBitmap.Decode(Config.BackgroundPath);
        if (background != null)
            canvas.DrawBitmap(background, 0f, 0f);

        DrawPhotoWindow(canvas, data.PhotoWindow);
        DrawBloodPill(canvas, data.BloodGroup);
        DrawName(canvas, data.Name);
        DrawPosition(canvas, data.Position);
        DrawIdLine(canvas, data.Uid);
        DrawBarcode(canvas, data.Uid);
        DrawValidity(canvas, data.ValidityText);

        canvas.Flush();
        return bmp;
    }

    private static void DrawPhotoWindow(SKCanvas canvas, SKBitmap? photo)
    {
        var rect = new SKRect(Config.PhotoX, Config.PhotoY,
            Config.PhotoX + Config.PhotoW, Config.PhotoY + Config.PhotoH);

        using var path = new SKPath();
        path.AddRoundRect(rect, Config.PhotoCornerRadius, Config.PhotoCornerRadius);

        // White window backing
        using (var paint = new SKPaint { Color = Config.White, IsAntialias = true })
            canvas.DrawPath(path, paint);

        if (photo != null)
        {
            canvas.Save();
            canvas.ClipPath(path, antialias: true);
            canvas.DrawBitmap(photo, Config.PhotoX, Config.PhotoY);
            canvas.Restore();
        }
    }

    private static void DrawBloodPill(SKCanvas canvas, string bloodGroup)
    {
        var text = $"Blood Group: {bloodGroup}";
        using var style = MakeText(RobotoExtraBold, 16.7f, Config.Accent);
        var textW = style.Font.MeasureText(text, style.Paint);
        var pillW = Math.Max(Config.PillMinW, textW + 24f);
        var pillX = Config.CenterX - pillW / 2f;
        var pillRect = new SKRect(pillX, Config.PillY, pillX + pillW, Config.PillY + Config.PillH);

        using (var path = new SKPath())
        {
            path.AddRoundRect(pillRect, Config.PillH / 2f, Config.PillH / 2f);
            using var fill = new SKPaint { Color = new SKColor(0xF6, 0xE3, 0xE2), IsAntialias = true };
            canvas.DrawPath(path, fill);
        }
        DrawCenteredText(canvas, style, text, Config.CenterX, Config.PillY, Config.PillH);
    }

    private static void DrawName(SKCanvas canvas, string name)
    {
        using var style = MakeText(RobotoMedium, Config.NameMaxSize, Config.Ink);
        var font = style.Font;

        // Shrink from 50px to 34px until the name fits the 427px box
        float size = Config.NameMaxSize;
        float width = font.MeasureText(name, style.Paint);
        while (size > Config.NameMinSize && width > Config.NameBoxW - 8)
        {
            size -= 1f;
            font.Size = size;
            width = font.MeasureText(name, style.Paint);
        }
        if (width > Config.NameBoxW - 8)
            throw new ArgumentException($"Name '{name}' does not fit the card name box.");

        DrawCenteredText(canvas, style, name, Config.CenterX, Config.NameY, Config.NameH);
    }

    private static void DrawPosition(SKCanvas canvas, string position)
    {
        using var style = MakeText(SourceSansSemiBold, 33f, Config.Accent);
        DrawCenteredText(canvas, style, position, Config.CenterX, Config.PositionY, Config.PositionH);
    }

    private static void DrawIdLine(SKCanvas canvas, string uid)
    {
        using var style = MakeText(SourceSansSemiBold, 33f, Config.Ink);
        DrawCenteredText(canvas, style, Validity.FormatIdLine(uid), Config.CenterX, Config.IdY, Config.IdH);
    }

    private static void DrawBarcode(SKCanvas canvas, string uid)
    {
        using var barcode = Barcode.Generate(uid);
        canvas.DrawBitmap(barcode, Config.BarcodeX, Config.BarcodeY);
    }

    private static void DrawValidity(SKCanvas canvas, string validityText)
    {
        using var style = MakeText(SourceSansRegular, 25f, Config.Accent);
        DrawCenteredText(canvas, style, validityText, Config.CenterX, Config.ValidityY, Config.ValidityH);
    }

    /// <summary>Draws text horizontally centered at centerX and vertically centered in the box.</summary>
    private static void DrawCenteredText(SKCanvas canvas, TextStyle style,
        string text, float centerX, float boxTop, float boxHeight)
    {
        SKRect bounds;
        style.Font.MeasureText(text, out bounds, style.Paint);
        float baseline = boxTop + boxHeight / 2f - (bounds.Top + bounds.Bottom) / 2f;
        canvas.DrawText(text, centerX, baseline, SKTextAlign.Center, style.Font, style.Paint);
    }
}
