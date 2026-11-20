using SkiaSharp;

namespace IdCardTool.Core.Photo;

public sealed record PhotoPipelineResult(
    SKBitmap Cutout,               // RGBA cutout at source resolution (cached)
    WindowTransform AutoTransform, // rule-based placement
    SKRect? FaceBox,
    int FaceCount,
    string? Warning,
    string BgMethod);              // "plain-white" | "u2net" | "skipped-transparent" | "skipped-manual" | "fallback"

/// <summary>Background removal mode selected by the operator.</summary>
public enum BgMode { PlainWhite, Model, Keep }

/// <summary>
/// Full photo pipeline: validate -> load (EXIF) -> bg removal -> face detect -> auto-fit.
/// </summary>
public sealed class PhotoPipeline : IDisposable
{
    private readonly BgRemover? _bgRemover;
    private readonly FaceDetector? _faceDetector;
    private readonly bool _bgAvailable;

    public PhotoPipeline(string? bgModelPath = null, string? faceModelPath = null)
    {
        try { _bgRemover = new BgRemover(bgModelPath); _bgAvailable = true; }
        catch { _bgAvailable = false; }
        try { _faceDetector = new FaceDetector(faceModelPath); }
        catch { /* face detection optional */ }
    }

    public bool BackgroundRemovalAvailable => _bgAvailable;

    /// <summary>Validates a photo file. Returns warning string or null.</summary>
    public static string? ValidatePhoto(string path, out SKBitmap? image)
    {
        image = null;
        var fi = new FileInfo(path);
        if (!fi.Exists) return "File not found.";
        if (fi.Length > Config.MaxPhotoBytes) return "File exceeds 10 MB.";

        try { image = LoadWithExifOrientation(path); }
        catch (Exception ex) { return $"Cannot read image: {ex.Message}"; }

        var warning = CheckImage(image);
        if (warning != null && warning.StartsWith("Photo appears blank"))
        {
            image.Dispose();
            image = null;
        }
        return warning;
    }

    /// <summary>Blank + resolution checks for an already-decoded image. Image is kept on return.</summary>
    public static string? CheckImage(SKBitmap image)
    {
        if (IsEssentiallyBlank(image))
            return "Photo appears blank or failed to decode — try a different file.";
        int shortSide = Math.Min(image.Width, image.Height);
        if (shortSide < Config.MinPhotoShortSide)
            return $"Image is small ({image.Width}x{image.Height}); minimum {Config.MinPhotoShortSide}px recommended.";
        return null;
    }

    /// <summary>True if sampled pixels are all near-black (failed/blank decode).</summary>
    private static bool IsEssentiallyBlank(SKBitmap bmp)
    {
        var span = bmp.GetPixelSpan();
        if (span.Length < 4) return true;
        long sum = 0;
        int samples = 0;
        int step = Math.Max(4, (bmp.Width * bmp.Height) / 2000) * 4;
        for (int i = 0; i + 2 < span.Length && samples < 2000; i += step)
        {
            sum += span[i] + span[i + 1] + span[i + 2]; // B+G+R
            samples++;
        }
        return samples == 0 || sum < samples * 30L; // avg channel sum < ~10
    }

    /// <summary>Loads image applying EXIF orientation.</summary>
    public static SKBitmap LoadWithExifOrientation(string path)
    {
        using var fs = File.OpenRead(path);
        using var codec = SKCodec.Create(fs)
            ?? throw new InvalidOperationException("Unsupported image format.");
        var origin = codec.EncodedOrigin;

        // Decode in the codec's NATIVE pixel format first. Requesting a format the
        // codec cannot produce (e.g. Opaque from an alpha-bearing PNG) makes
        // GetPixels return InvalidConversion and leaves a black bitmap.
        var bmp = new SKBitmap(codec.Info);
        // Progressive/multi-scan files can return IncompleteInput; retry a
        // bounded number of times, then fail loudly instead of returning a
        // silently black bitmap.
        var rc = codec.GetPixels(bmp.Info, bmp.GetPixels());
        for (int attempt = 0; attempt < 8 && rc == SKCodecResult.IncompleteInput; attempt++)
            rc = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (rc != SKCodecResult.Success)
        {
            bmp.Dispose();
            throw new InvalidOperationException($"Image decode failed ({rc}). Try re-saving as a standard JPG or PNG.");
        }
        bmp = NormalizeToBgra(bmp);

        if (origin == SKEncodedOrigin.TopLeft) return bmp;

        // Apply EXIF orientation. Output dims swap for sideways orientations.
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? bmp.Height : bmp.Width;
        int oh = swap ? bmp.Width : bmp.Height;
        var oriented = new SKBitmap(ow, oh, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(oriented);
        var matrix = OriginToMatrix(origin, bmp.Width, bmp.Height);
        canvas.Concat(ref matrix);
        canvas.DrawBitmap(bmp, 0f, 0f);
        canvas.Flush();
        bmp.Dispose();
        return oriented;
    }

    /// <summary>Converts any decoded pixel format to Bgra8888 straight-alpha via canvas draw.</summary>
    private static SKBitmap NormalizeToBgra(SKBitmap src)
    {
        if (src.ColorType == SKColorType.Bgra8888 && src.AlphaType == SKAlphaType.Unpremul)
            return src;
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(src, 0f, 0f);
        canvas.Flush();
        src.Dispose();
        return dst;
    }

    /// <summary>Maps stored pixels to corrected positions for each EXIF orientation.</summary>
    private static SKMatrix OriginToMatrix(SKEncodedOrigin o, int w, int h) => o switch
    {
        // flip horizontal about center
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        // rotate 180
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        // flip vertical about center
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        // transpose (main diagonal)
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        // rotate 90 CW
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        // anti-transpose (anti-diagonal)
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        // rotate 90 CCW
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>Fraction of pixels with alpha below 128 (0..1).</summary>
    private static float TransparentFraction(SKBitmap image)
    {
        var span = image.GetPixelSpan();
        int transparent = 0, total = 0;
        for (int i = 3; i < span.Length; i += 4)
        {
            total++;
            if (span[i] < 128) transparent++;
        }
        return total == 0 ? 0f : transparent / (float)total;
    }

    /// <summary>Convenience overload: true = auto (plain-white default), false = keep as-is.</summary>
    public PhotoPipelineResult Process(SKBitmap image, bool removeBackground)
        => Process(image, removeBackground ? BgMode.PlainWhite : BgMode.Keep);

    /// <summary>
    /// Runs the pipeline on an already-loaded image.
    /// Images that are already mostly transparent always skip removal.
    /// </summary>
    public PhotoPipelineResult Process(SKBitmap image, BgMode mode = BgMode.PlainWhite)
    {
        // 1. Background removal
        SKBitmap cutout;
        string? warning = null;
        string bgMethod;
        bool alreadyTransparent = TransparentFraction(image) > Config.TransparentSkipThreshold;
        if (alreadyTransparent)
        {
            cutout = image.Copy();
            bgMethod = "skipped-transparent";
        }
        else
        {
            switch (mode)
            {
                case BgMode.Keep:
                    cutout = image.Copy();
                    bgMethod = "skipped-manual";
                    break;
                case BgMode.PlainWhite:
                    cutout = WhiteBgRemover.Remove(image);
                    bgMethod = "plain-white";
                    break;
                default: // Model
                    if (_bgRemover != null)
                    {
                        cutout = _bgRemover.RemoveBackground(image);
                        bgMethod = "u2net";
                    }
                    else
                    {
                        warning = "Background removal unavailable; photo used as-is.";
                        cutout = image.Copy();
                        bgMethod = "fallback";
                    }
                    break;
            }
        }

        // 2. Face detection on original
        SKRect? face = null;
        int faceCount = 0;
        if (_faceDetector != null)
        {
            var faces = _faceDetector.Detect(image);
            faceCount = faces.Count;
            face = faces.Count > 0
                ? faces.OrderByDescending(b => b.Width * b.Height).First()
                : null;
            if (faces.Count == 0) warning = "No face detected; photo centered automatically.";
            else if (faces.Count > 1) warning = "Multiple faces found; largest used.";
        }

        // 3. Auto-fit
        var transform = face.HasValue
            ? WindowComposer.AutoFit(cutout, face.Value)
            : WindowComposer.CenterCover(cutout);

        return new PhotoPipelineResult(cutout, transform, face, faceCount, warning, bgMethod);
    }

    /// <summary>Composes the window image (313x382) for a transform.</summary>
    public static SKBitmap ComposeWindow(SKBitmap cutout, WindowTransform t)
        => WindowComposer.Compose(cutout, t);

    public void Dispose()
    {
        _bgRemover?.Dispose();
        _faceDetector?.Dispose();
    }
}
