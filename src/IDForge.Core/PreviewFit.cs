namespace IDForge.Core;

/// <summary>
/// Letterbox fit for the card preview: computes the largest size that keeps the
/// whole card visible inside the available area (Stretch="Uniform" equivalent).
/// </summary>
public static class PreviewFit
{
    public const int CardW = 637;
    public const int CardH = 1012;

    /// <summary>Returns (scale, width, height) for the card inside availW x availH.</summary>
    public static (double Scale, double W, double H) Fit(double availW, double availH)
    {
        if (availW <= 0 || availH <= 0) return (0, 0, 0);
        double scale = Math.Min(availW / CardW, availH / CardH);
        return (scale, CardW * scale, CardH * scale);
    }
}
