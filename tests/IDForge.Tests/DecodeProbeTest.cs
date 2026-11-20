using IDForge.Core.Photo;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

public class DecodeProbeTest
{
    private static readonly string Log =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbg_decode.txt");
    private const string Path = @"F:\Max Files\Other\Smiling(1).png";

    [Fact]
    public void Probe()
    {
        if (!File.Exists(Path)) return;   // probe needs a local sample photo
        File.WriteAllText(Log, "");
        using var fs = File.OpenRead(Path);
        using var codec = SKCodec.Create(fs)!;
        var ci = codec.Info;
        File.AppendAllText(Log, $"codec.Info: {ci.Width}x{ci.Height} color={ci.ColorType} alpha={ci.AlphaType} origin={codec.EncodedOrigin}\n");

        // try 1: native info
        var bmp1 = new SKBitmap(ci);
        var r1 = codec.GetPixels(bmp1.Info, bmp1.GetPixels());
        File.AppendAllText(Log, $"native GetPixels -> {r1}\n");

        // try 2: Bgra8888 Opaque (current code path)
        fs.Position = 0;
        using var codec2 = SKCodec.Create(fs)!;
        var info2 = new SKImageInfo(ci.Width, ci.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bmp2 = new SKBitmap(info2);
        var r2 = codec2.GetPixels(bmp2.Info, bmp2.GetPixels());
        File.AppendAllText(Log, $"bgra/opaque GetPixels -> {r2}\n");

        // try 3: Bgra8888 Premul
        fs.Position = 0;
        using var codec3 = SKCodec.Create(fs)!;
        var info3 = new SKImageInfo(ci.Width, ci.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp3 = new SKBitmap(info3);
        var r3 = codec3.GetPixels(bmp3.Info, bmp3.GetPixels());
        File.AppendAllText(Log, $"bgra/premul GetPixels -> {r3}\n");

        // try 4: SKBitmap.Decode
        var bmp4 = SKBitmap.Decode(Path);
        File.AppendAllText(Log, $"SKBitmap.Decode -> {(bmp4==null?"null":$"{bmp4.Width}x{bmp4.Height} {bmp4.ColorType}/{bmp4.AlphaType}")}\n");
        if (bmp4 != null) { var p = bmp4.GetPixel(bmp4.Width/2, bmp4.Height/2); File.AppendAllText(Log, $"  center={p}\n"); bmp4.Dispose(); }
    }
}
