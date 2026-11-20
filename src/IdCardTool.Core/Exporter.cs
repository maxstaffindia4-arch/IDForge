using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace IdCardTool.Core;

public static class Exporter
{
    /// <summary>Saves the card as PNG with 300 DPI metadata. 637px @300dpi = 53.98mm.</summary>
    public static void ExportPng(SKBitmap card, string path)
    {
        using var image = SKImage.FromBitmap(card);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        SetPngDpi(path, 300);
    }

    /// <summary>Saves the card as a single-page PDF at CR80 (53.98 x 85.6 mm).</summary>
    public static void ExportPdf(SKBitmap card, string path)
    {
        // CR80 in points: 53.98mm x 85.6mm
        const double pageW = 153.0;    // 53.98mm
        const double pageH = 242.64;   // 85.6mm

        int w = card.Width, h = card.Height;
        var span = card.GetPixelSpan(); // BGRA
        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < span.Length; i += 4, j += 3)
        {
            rgb[j] = span[i + 2];     // R
            rgb[j + 1] = span[i + 1]; // G
            rgb[j + 2] = span[i];     // B
        }

        byte[] imgData;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(rgb, 0, rgb.Length);
            imgData = ms.ToArray();
        }

        var content = Encoding.ASCII.GetBytes(
            $"q {pageW.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0 0 " +
            $"{pageH.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0 0 cm /Im0 Do Q");

        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW.ToString(System.Globalization.CultureInfo.InvariantCulture)} {pageH.ToString(System.Globalization.CultureInfo.InvariantCulture)}] " +
                "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
            BuildImageObject(w, h, imgData),
            BuildStreamObject(content),
        };

        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);
        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));

        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = fs.Position;
            writer.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            writer.Write(objects[i]);
            writer.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
        }

        long xrefPos = fs.Position;
        writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n"));
        writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
        for (int i = 1; i <= objects.Count; i++)
            writer.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} 00000 n \n"));
        writer.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF"));
    }

    private static byte[] BuildImageObject(int w, int h, byte[] imgData)
    {
        var header = Encoding.ASCII.GetBytes(
            $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
            $"/Length {imgData.Length} >>\nstream\n");
        var footer = Encoding.ASCII.GetBytes("\nendstream");
        var result = new byte[header.Length + imgData.Length + footer.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(imgData, 0, result, header.Length, imgData.Length);
        Buffer.BlockCopy(footer, 0, result, header.Length + imgData.Length, footer.Length);
        return result;
    }

    private static byte[] BuildStreamObject(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n");
        var footer = Encoding.ASCII.GetBytes("\nendstream");
        var result = new byte[header.Length + content.Length + footer.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(content, 0, result, header.Length, content.Length);
        Buffer.BlockCopy(footer, 0, result, header.Length + content.Length, footer.Length);
        return result;
    }

    private static void SetPngDpi(string path, int dpi)
    {
        var bytes = File.ReadAllBytes(path);
        var patched = PngDpi.Patch(bytes, dpi);
        File.WriteAllBytes(path, patched);
    }
}

/// <summary>Injects/updates the PNG pHYs chunk for DPI metadata.</summary>
internal static class PngDpi
{
    public static byte[] Patch(byte[] png, int dpi)
    {
        // pixels per meter = dpi / 0.0254
        uint ppm = (uint)Math.Round(dpi / 0.0254);
        var chunk = new byte[4 + 4 + 9 + 4];
        Buffer.BlockCopy(BigEndianBE((uint)9), 0, chunk, 0, 4);
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("pHYs"), 0, chunk, 4, 4);
        Buffer.BlockCopy(BigEndianBE(ppm), 0, chunk, 8, 4);
        Buffer.BlockCopy(BigEndianBE(ppm), 0, chunk, 12, 4);
        chunk[16] = 1; // unit = meter
        uint crc = Crc32.Compute(chunk.AsSpan(4, 17));
        Buffer.BlockCopy(BigEndianBE(crc), 0, chunk, 17, 4);

        var result = new List<byte>();
        result.AddRange(png.AsSpan(0, 8).ToArray());
        int pos = 8;
        bool inserted = false;
        while (pos + 8 <= png.Length)
        {
            int len = ReadInt32(png, pos);
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int total = 12 + len;
            if (type == "IEND")
            {
                if (!inserted) { result.AddRange(chunk); inserted = true; }
                result.AddRange(png.AsSpan(pos, total).ToArray());
                pos += total;
                continue;
            }
            if (type == "pHYs") { pos += total; continue; } // drop existing
            result.AddRange(png.AsSpan(pos, total).ToArray());
            pos += total;
        }
        result.AddRange(png.AsSpan(pos).ToArray());
        return result.ToArray();
    }

    private static byte[] BigEndianBE(uint v) => new[]
    {
        (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v
    };

    private static int ReadInt32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
