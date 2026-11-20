using System.Runtime.InteropServices;
using IDForge.Core;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace IDForge.Tests;

public class BarcodeTests
{
    [Fact]
    public void Generates_Exact_Size()
    {
        using var bmp = Barcode.Generate("UID20250318");
        Assert.Equal(Config.BarcodeW, bmp.Width);
        Assert.Equal(Config.BarcodeH, bmp.Height);
    }

    [Theory]
    [InlineData("UID20250318")]
    [InlineData("UID99999999")]
    [InlineData("UID1")]
    public void RoundTrip_Decodes_To_Uid(string uid)
    {
        using var bmp = Barcode.Generate(uid);
        var decoded = Decode(bmp);
        Assert.Equal(uid, decoded);
    }

    private static string? Decode(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var bgra = new byte[h * bmp.RowBytes];
        Marshal.Copy(bmp.GetPixels(), bgra, 0, bgra.Length);
        var lum = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * bmp.RowBytes + x * 4;
            // BGRA order
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            lum[y * w + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }
        var source = new RGBLuminanceSource(lum, w, h);
        var binary = new BinaryBitmap(new HybridBinarizer(source));
        var result = new MultiFormatReader().decode(binary);
        return result?.Text;
    }
}
