using IDForge.Core;
using Xunit;

namespace IDForge.Tests;

/// <summary>Card preview must always show the whole card at any window size.</summary>
public class PreviewFitTests
{
    // Chrome overhead: window margins (16) + tab header (~26) + tab margin (16).
    private const double Chrome = 58;

    public static IEnumerable<object[]> WorkAreas()
    {
        // workArea height, available preview width after 380px form column + margins
        yield return new object[] { 728, 1366 - 380 - 24 };  // 1366x768 - taskbar(~40)
        yield return new object[] { 1020, 1920 - 380 - 24 }; // 1920x1080 - taskbar(~60)
    }

    [Theory]
    [MemberData(nameof(WorkAreas))]
    public void Whole_Card_Fits_Inside_Available_Area(double workAreaH, double availW)
    {
        double availH = workAreaH * 0.95 - Chrome;   // window sized to 95% of work area
        var (scale, w, h) = PreviewFit.Fit(availW, availH);

        Assert.True(scale > 0);
        Assert.True(w <= availW + 0.01, $"width {w} exceeds {availW}");
        Assert.True(h <= availH + 0.01, $"height {h} exceeds {availH} — card bottom cut off");
        // aspect ratio preserved (637x1012)
        Assert.Equal((double)PreviewFit.CardH / PreviewFit.CardW, h / w, 3);
    }

    [Fact]
    public void Fit_At_700px_Usable_Height_Shows_Full_Card()
    {
        var (_, _, h) = PreviewFit.Fit(940, 700 - Chrome);
        Assert.True(h <= 700 - Chrome + 0.01);
    }
}
