using SkiaSharp;

namespace IDForge.Core.Photo;

/// <summary>
/// Cleans up a cutout's alpha matte and edge colors after background removal:
///  - keeps only the connected component containing the face (drops detached blobs
///    and border-leak arcs),
///  - erodes the mask ~1px and feathers ~2px for a clean edge,
///  - clamps the outer border band so semi-transparent background bleed cannot
///    show as arcs in the photo window corners (fully opaque subject pixels that
///    genuinely touch the edge are kept),
///  - decontaminates edge colors: semi-transparent pixels get their RGB replaced
///    by a local interior-color estimate so the original background color does
///    not bleed through as a cyan/dark fringe.
/// Input and output are Bgra8888 straight-alpha (Unpremul).
/// </summary>
public static class CutoutPostProcess
{
    private const int BorderPx = 4;          // outer band suppressed for a<250
    private const int ComponentAlpha = 128;  // binarization threshold
    private const int DecontamRadius = 8;    // interior color estimate radius

    public static SKBitmap Process(SKBitmap cutout, SKRect? faceBox)
    {
        int w = cutout.Width, h = cutout.Height;
        int n = w * h;
        var px = cutout.GetPixelSpan();

        var alpha = new byte[n];
        for (int p = 0, i = 3; i < px.Length; p++, i += 4) alpha[p] = px[i];

        // 1. Connected components (8-conn) on alpha>=128; keep the one containing
        //    the face center, else the largest.
        KeepSubjectComponent(alpha, w, h, faceBox);

        // 2. Erode ~1px (3x3 min filter) then feather ~2px (two 3x3 box blurs).
        alpha = MinFilter3x3(alpha, w, h);
        alpha = BoxBlur3x3(alpha, w, h);
        alpha = BoxBlur3x3(alpha, w, h);

        // 3. Border clamp (after feather so blur cannot leak alpha back in):
        //    in the outer BorderPx band, only clearly-opaque pixels survive.
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (x < BorderPx || x >= w - BorderPx || y < BorderPx || y >= h - BorderPx)
            {
                int p = y * w + x;
                if (alpha[p] < 250) alpha[p] = 0;
            }
        }

        // 4. Decontaminate RGB of semi-transparent pixels toward interior colors.
        DecontaminateEdges(px, alpha, w, h);

        // 5. Rebuild output (same size, straight alpha).
        var output = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var dst = output.GetPixelSpan();
        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            dst[i] = px[i];
            dst[i + 1] = px[i + 1];
            dst[i + 2] = px[i + 2];
            dst[i + 3] = alpha[p];
        }
        return output;
    }

    /// <summary>Zeroes alpha outside the subject's connected component.</summary>
    private static void KeepSubjectComponent(byte[] alpha, int w, int h, SKRect? faceBox)
    {
        int n = w * h;
        var label = new int[n];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var queue = new Queue<int>(4096);

        int Label(int start)
        {
            int id = sizes.Count;
            sizes.Add(0);
            label[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                sizes[id]++;
                int x = p % w, y = p / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int q = ny * w + nx;
                    if (label[q] < 0 && alpha[q] >= ComponentAlpha)
                    {
                        label[q] = id;
                        queue.Enqueue(q);
                    }
                }
            }
            return id;
        }

        for (int p = 0; p < n; p++)
            if (label[p] < 0 && alpha[p] >= ComponentAlpha) Label(p);

        // Pick the component containing the face center; fall back to largest.
        int keepId = -1;
        if (faceBox.HasValue)
        {
            int fx = Math.Clamp((int)faceBox.Value.MidX, 0, w - 1);
            int fy = Math.Clamp((int)faceBox.Value.MidY, 0, h - 1);
            int faceLabel = label[fy * w + fx];
            if (faceLabel >= 0) keepId = faceLabel;
        }
        if (keepId < 0 && sizes.Count > 0)
            keepId = Enumerable.Range(0, sizes.Count).MaxBy(i => sizes[i]);

        if (keepId < 0) { Array.Clear(alpha); return; }   // nothing opaque at all
        for (int p = 0; p < n; p++)
            if (alpha[p] >= ComponentAlpha && label[p] != keepId) alpha[p] = 0;
        // Semi-transparent pixels (< ComponentAlpha) outside the kept component
        // are already alpha<128 noise; also zero them if unlabeled-adjacent blobs.
    }

    /// <summary>3x3 min filter: shrinks the matte by ~1px.</summary>
    private static byte[] MinFilter3x3(byte[] a, int w, int h)
    {
        var dst = new byte[a.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte m = 255;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                byte v = (nx < 0 || nx >= w || ny < 0 || ny >= h) ? (byte)0 : a[ny * w + nx];
                if (v < m) m = v;
            }
            dst[y * w + x] = m;
        }
        return dst;
    }

    private static byte[] BoxBlur3x3(byte[] a, int w, int h)
    {
        var tmp = new int[w * h];
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int sum = 0, cnt = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= h) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx;
                    if (xx < 0 || xx >= w) continue;
                    sum += a[yy * w + xx]; cnt++;
                }
            }
            tmp[y * w + x] = sum / cnt;
        }
        for (int i = 0; i < tmp.Length; i++) dst[i] = (byte)tmp[i];
        return dst;
    }

    /// <summary>
    /// Replaces RGB of semi-transparent pixels with an alpha-weighted local
    /// interior-color estimate (separable box blur, radius DecontamRadius).
    /// Fully opaque pixels keep their original color.
    /// </summary>
    private static void DecontaminateEdges(Span<byte> px, byte[] alpha, int w, int h)
    {
        int n = w * h;
        // weight planes: accumulate B,G,R weighted by alpha, plus weight itself.
        var wb = new float[n]; var wg = new float[n]; var wr = new float[n]; var wsum = new float[n];
        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            float a = alpha[p] / 255f;
            wb[p] = px[i] * a; wg[p] = px[i + 1] * a; wr[p] = px[i + 2] * a; wsum[p] = a;
        }
        BoxBlurFloat(wb, w, h); BoxBlurFloat(wg, w, h); BoxBlurFloat(wr, w, h); BoxBlurFloat(wsum, w, h);

        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            int a = alpha[p];
            if (a >= 250 || a == 0) continue;   // opaque keeps color; invisible doesn't matter
            if (wsum[p] < 1e-4f) continue;      // no interior nearby — leave as-is
            // Blend toward the interior estimate; stronger replacement for weaker alpha.
            float t = (255f - a) / 255f;
            px[i]     = (byte)Math.Clamp(px[i]     + (wb[p] / wsum[p] - px[i])     * t, 0, 255);
            px[i + 1] = (byte)Math.Clamp(px[i + 1] + (wg[p] / wsum[p] - px[i + 1]) * t, 0, 255);
            px[i + 2] = (byte)Math.Clamp(px[i + 2] + (wr[p] / wsum[p] - px[i + 2]) * t, 0, 255);
        }
    }

    /// <summary>Separable box blur (radius DecontamRadius) on a float plane, in place.
    /// Out-of-bounds taps are excluded (standard box blur, no edge replication).</summary>
    private static void BoxBlurFloat(float[] a, int w, int h)
    {
        int r = DecontamRadius;
        var tmp = new float[w * h];
        var col = new double[Math.Max(w, h) + 1];

        // horizontal: prefix sums per row
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            col[0] = 0;
            for (int x = 0; x < w; x++) col[x + 1] = col[x] + a[row + x];
            for (int x = 0; x < w; x++)
            {
                int l = Math.Max(0, x - r), rr = Math.Min(w - 1, x + r);
                tmp[row + x] = (float)((col[rr + 1] - col[l]) / (rr - l + 1));
            }
        }
        // vertical: prefix sums per column over tmp
        for (int x = 0; x < w; x++)
        {
            col[0] = 0;
            for (int y = 0; y < h; y++) col[y + 1] = col[y] + tmp[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int l = Math.Max(0, y - r), rr = Math.Min(h - 1, y + r);
                a[y * w + x] = (float)((col[rr + 1] - col[l]) / (rr - l + 1));
            }
        }
    }
}
