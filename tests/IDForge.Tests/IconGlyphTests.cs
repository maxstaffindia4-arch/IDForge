using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>FIX 1: every icon codepoint used in XAML must exist in the embedded
/// Font Awesome font, and the IconFont resource must actually render glyphs
/// (not fallback boxes). Fails if the font resource is missing.</summary>
[Collection("Wpf")]
public class IconGlyphTests
{
    private static readonly Regex Entity = new(@"&#x([0-9A-Fa-f]+);", RegexOptions.Compiled);

    /// <summary>All PUA codepoints used in src/IDForge XAML — every one is an
    /// icon glyph (Font Awesome lives in E000–F8FF).</summary>
    private static SortedSet<int> XamlIconCodepoints()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IDForge"));
        var set = new SortedSet<int>();
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Entity.Matches(File.ReadAllText(file)))
            {
                var cp = Convert.ToInt32(m.Groups[1].Value, 16);
                if (cp is >= 0xE000 and <= 0xF8FF) set.Add(cp);
            }
        }
        return set;
    }

    /// <summary>GlyphTypeface for the font file the IconFont resource points at.
    /// Throws (test fails) when the resource or the family name is wrong.</summary>
    private static GlyphTypeface LoadIconGlyphTypeface()
    {
        WpfTestHost.EnsureApp();
        var ff = (FontFamily)Application.Current!.FindResource("IconFont");
        Assert.False(string.IsNullOrEmpty(ff.Source), "IconFont resource is empty");
        // Source: pack://application:,,,/IDForge;component/<path>#<family>
        var noFrag = ff.Source.Split('#')[0];
        var uri = new Uri(noFrag);
        Assert.Equal("ttf", Path.GetExtension(uri.AbsolutePath).TrimStart('.'));
        return new GlyphTypeface(uri);
    }

    [Fact]
    public void IconFont_Resource_Exists()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var ff = Assert.IsType<FontFamily>(Application.Current!.FindResource("IconFont"));
            Assert.Contains("fa-solid-900.ttf", ff.Source);
        });
    }

    [Fact]
    public void IconFont_Every_Xaml_Codepoint_Has_Glyph()
    {
        WpfSta.Run(() =>
        {
            var gt = LoadIconGlyphTypeface();
            var cps = XamlIconCodepoints();
            Assert.NotEmpty(cps);
            var missing = cps.Where(cp => !gt.CharacterToGlyphMap.ContainsKey(cp)).ToList();
            Assert.True(missing.Count == 0,
                $"Icon font has no glyph for: {string.Join(", ", missing.Select(c => $"U+{c:X4}"))}");
        });
    }

    /// <summary>Rendered check: each icon must paint pixels that differ from an
    /// unmapped character's fallback glyph — identical bitmaps mean tofu.</summary>
    [Fact]
    public void IconFont_Renders_Distinct_Glyphs()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var ff = (FontFamily)Application.Current!.FindResource("IconFont");
            string Render(int cp)
            {
                var tb = new TextBlock
                {
                    Text = char.ConvertFromUtf32(cp), FontFamily = ff,
                    FontSize = 16, Foreground = Brushes.Black
                };
                tb.Measure(new Size(100, 60));
                tb.Arrange(new Rect(new Point(0, 0), tb.DesiredSize));
                tb.UpdateLayout();
                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(tb.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(tb.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                rtb.Render(tb);
                var px = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
                rtb.CopyPixels(px, rtb.PixelWidth * 4, 0);
                ulong h = 14695981039346656037UL; int ink = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    h ^= px[i]; h *= 1099511628211UL;
                    h ^= px[i + 1]; h *= 1099511628211UL;
                    h ^= px[i + 2]; h *= 1099511628211UL;
                    h ^= px[i + 3]; h *= 1099511628211UL;
                    if (px[i + 3] > 0 && (px[i] < 200 || px[i + 1] < 200 || px[i + 2] < 200)) ink++;
                }
                Assert.True(ink > 0, $"U+{cp:X4} rendered no ink — font fallback?");
                return $"{ink}:{h:X16}";
            }
            var tofu = Render(0x0378);   // unassigned codepoint → fallback glyph
            foreach (var cp in XamlIconCodepoints())
                Assert.NotEqual(tofu, Render(cp));
        });
    }
}
