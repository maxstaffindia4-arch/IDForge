using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace IdCardTool.Core;

public static class Barcode
{
    /// <summary>Generates a Code 128 barcode for the UID at exactly 570x70 px.</summary>
    public static SKBitmap Generate(string uid, int width = Config.BarcodeW, int height = Config.BarcodeH)
    {
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
        var pixelData = writer.Write(uid);

        // PixelData.Pixels is a BGRA byte array, 4 bytes/px, tightly packed
        var bmp = new SKBitmap(pixelData.Width, pixelData.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque);
        pixelData.Pixels.CopyTo(bmp.GetPixelSpan());
        return bmp;
    }
}
