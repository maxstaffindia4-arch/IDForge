using System.Text.Json;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge.Core.Issuing;

/// <summary>Photo settings persisted with each issue (settings.json).</summary>
public sealed record StoredPhotoSettings(
    string BgMode,
    double Zoom, double OffsetX, double OffsetY,
    double AutoScale, double AutoOffsetX, double AutoOffsetY,
    string ModelVersion,
    float TransformScale, float TransformOffsetX, float TransformOffsetY)
{
    /// <summary>The exact window transform used for this issue's photo.</summary>
    public WindowTransform Transform => new(TransformScale, TransformOffsetX, TransformOffsetY);
}

/// <summary>Locates and reads the per-issue stored photos (Fix J-D).</summary>
public static class StoredPhotos
{
    /// <summary>DB stores paths relative to the photos dir with '/' separators.</summary>
    public static string Resolve(string photoDir, string relPath)
        => Path.Combine(photoDir, relPath.Replace('/', Path.DirectorySeparatorChar));

    public static SKBitmap? LoadCutout(string photoDir, string? relPath)
    {
        if (relPath == null) return null;
        var p = Resolve(photoDir, relPath);
        return File.Exists(p) ? SKBitmap.Decode(p) : null;
    }

    public static StoredPhotoSettings? ReadSettings(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<StoredPhotoSettings>(json); }
        catch { return null; }
    }
}
