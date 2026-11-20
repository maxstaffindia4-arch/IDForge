using SkiaSharp;

namespace IDForge.Core.Photo;

/// <summary>Manual/auto transform applied to the cutout inside the photo window.</summary>
public sealed record WindowTransform(float Scale, float OffsetX, float OffsetY)
{
    public static WindowTransform Identity => new(1f, 0f, 0f);
}

/// <summary>
/// Composites a cutout (RGBA, same size as source photo) into the 313x382
/// photo window using a scale + translation transform.
/// The returned bitmap is exactly PhotoW x PhotoH, transparent where the
/// cutout doesn't cover — the renderer clips it to the rounded window.
/// </summary>
public static class WindowComposer
{
    /// <summary>Produces the 313x382 window image for a given transform.</summary>
    public static SKBitmap Compose(SKBitmap cutout, WindowTransform t)
    {
        var bmp = new SKBitmap(Config.PhotoW, Config.PhotoH,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var dest = new SKRect(t.OffsetX, t.OffsetY,
            t.OffsetX + cutout.Width * t.Scale,
            t.OffsetY + cutout.Height * t.Scale);
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        canvas.DrawBitmap(cutout, dest, sampling, paint);
        canvas.Flush();
        return bmp;
    }

    /// <summary>Alpha bounding box of the cutout, in cutout pixel coords.</summary>
    public static SKRect AlphaBounds(SKBitmap cutout)
    {
        var span = cutout.GetPixelSpan();
        int minX = cutout.Width, minY = cutout.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < cutout.Height; y++)
        for (int x = 0; x < cutout.Width; x++)
        {
            if (span[(y * cutout.Width + x) * 4 + 3] > 8)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? SKRect.Empty : new SKRect(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// Rule-based auto-fit (constants in Config):
    ///  - face height ~ Config.FaceHeightRatio of window height
    ///  - face horizontally centered
    ///  - head top (face box top minus estimated hair) near Config.HeadTopRatio
    ///    of window height
    /// The cutout is NOT forced to cover the window — the window is white, so
    /// gaps are acceptable and the operator can nudge if needed.
    /// </summary>
    public static WindowTransform AutoFit(SKBitmap cutout, SKRect faceBox)
    {
        float faceH = faceBox.Height;
        if (faceH <= 1) return CenterCover(cutout);

        float targetFacePx = Config.PhotoH * Config.FaceHeightRatio;
        float scale = targetFacePx / faceH;

        // Horizontal: face center -> window center
        float offsetX = Config.PhotoW / 2f - faceBox.MidX * scale;

        // Vertical: head top (hair above face box) -> Config.HeadTopRatio of window
        float headTop = faceBox.Top - faceH * Config.HairAboveFaceRatio;
        float offsetY = Config.PhotoH * Config.HeadTopRatio - headTop * scale;

        return new WindowTransform(scale, offsetX, offsetY);
    }

    /// <summary>Fallback when no face found: cover-fit the alpha bbox, centered.</summary>
    public static WindowTransform CenterCover(SKBitmap cutout)
    {
        var b = AlphaBounds(cutout);
        if (b == SKRect.Empty) return WindowTransform.Identity;
        float bw = b.Width, bh = b.Height;
        float scale = Math.Max(Config.PhotoW / bw, Config.PhotoH / bh) * 1.02f;
        float offsetX = Config.PhotoW / 2f - b.MidX * scale;
        float offsetY = Config.PhotoH / 2f - b.MidY * scale;
        return new WindowTransform(scale, offsetX, offsetY);
    }
}
