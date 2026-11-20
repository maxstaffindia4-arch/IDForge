using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace IDForge.Core;

public static class Barcode
{
    /// <summary>
    /// Generates a Code 128 barcode at exactly 570x70 px. The payload is always
    /// "UID" + digits — pass either "20250318" or "UID20250318".
    /// </summary>
    public static SKBitmap Generate(string uid, int width = Config.BarcodeW, int height = Config.BarcodeH)
    {
        var digits = Uid.Normalize(uid) ?? uid.Trim();
        var payload = Uid.BarcodeContent(digits);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = width,
                Height = height,
                Margin = 10,
            }
        };
        var pixelData = writer.Write(payload);

        // PixelData.Pixels is a BGRA byte array, 4 bytes/px, tightly packed
        var bmp = new SKBitmap(pixelData.Width, pixelData.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque);
        pixelData.Pixels.CopyTo(bmp.GetPixelSpan());
        return bmp;
    }
}
