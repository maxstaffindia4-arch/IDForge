using SkiaSharp;

namespace IDForge.Core.Photo;

/// <summary>
/// Background removal for studio photos on plain white backgrounds.
/// Flood-fills from the image border through near-white pixels; everything not
/// reached is subject and stays opaque. Keeps dark clothing (blazers) that
/// rembg/u2net tends to eat. Applies a 3x3 morphological close and a ~1px
/// feather on the edge.
/// </summary>
public static class WhiteBgRemover
{
    /// <summary>Returns an RGBA cutout (Bgra8888/Unpremul) at source resolution.</summary>
    public static SKBitmap Remove(SKBitmap source)
    {
        int w = source.Width, h = source.Height;
        var src = source.GetPixelSpan().ToArray();

        // 1. White-ish test: all channels > threshold and low saturation.
        bool IsWhite(int i)
        {
            int b = src[i], g = src[i + 1], r = src[i + 2];
            return b > Config.WhiteBgMinChannel && g > Config.WhiteBgMinChannel
                && r > Config.WhiteBgMinChannel
                && Math.Max(b, Math.Max(g, r)) - Math.Min(b, Math.Min(g, r)) <= Config.WhiteBgMaxDelta;
        }

        // 2. BFS flood from all border pixels that are white-ish.
        var bg = new bool[w * h];
        var queue = new Queue<int>(w * 2 + h * 2);
        void TryEnqueue(int p) { if (!bg[p] && IsWhite(p * 4)) { bg[p] = true; queue.Enqueue(p); } }
        for (int x = 0; x < w; x++) { TryEnqueue(x); TryEnqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { TryEnqueue(y * w); TryEnqueue(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int x = p % w, y = p / w;
            if (x > 0) TryEnqueue(p - 1);
            if (x < w - 1) TryEnqueue(p + 1);
            if (y > 0) TryEnqueue(p - w);
            if (y < h - 1) TryEnqueue(p + w);
        }

        // 3. Subject mask = !bg; morphological close (dilate then erode, 3x3)
        //    fills 1px holes/gaps in the subject.
        var subject = new bool[w * h];
        for (int p = 0; p < subject.Length; p++) subject[p] = !bg[p];
        var dilated = Dilation(subject, w, h);
        var closed = Erosion(dilated, w, h);

        // 4. Feather: two 3x3 box-blur passes on alpha (1px soft edge).
        var alpha = new byte[w * h];
        for (int p = 0; p < alpha.Length; p++) alpha[p] = closed[p] ? (byte)255 : (byte)0;
        alpha = BoxBlur3x3(alpha, w, h);
        alpha = BoxBlur3x3(alpha, w, h);

        // 5. Compose output.
        var output = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var dst = output.GetPixelSpan();
        for (int p = 0, i = 0; p < alpha.Length; p++, i += 4)
        {
            dst[i] = src[i];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i + 2];
            dst[i + 3] = alpha[p];
        }
        return output;
    }

    private static bool[] Dilation(bool[] m, int w, int h)
    {
        var d = new bool[m.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool v = false;
            for (int dy = -1; dy <= 1 && !v; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && nx < w && ny >= 0 && ny < h && m[ny * w + nx]) { v = true; break; }
            }
            d[y * w + x] = v;
        }
        return d;
    }

    private static bool[] Erosion(bool[] m, int w, int h)
    {
        var e = new bool[m.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool v = true;
            for (int dy = -1; dy <= 1 && v; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h || !m[ny * w + nx]) { v = false; break; }
            }
            e[y * w + x] = v;
        }
        return e;
    }

    private static byte[] BoxBlur3x3(byte[] a, int w, int h)
    {
        var tmp = new int[w * h];
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int sum = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= h) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx;
                    if (xx < 0 || xx >= w) continue;
                    sum += a[yy * w + xx]; n++;
                }
            }
            tmp[y * w + x] = sum / n;
        }
        for (int i = 0; i < tmp.Length; i++) dst[i] = (byte)tmp[i];
        return dst;
    }
}
