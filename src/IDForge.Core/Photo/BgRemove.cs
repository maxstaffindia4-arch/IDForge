using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace IDForge.Core.Photo;

/// <summary>
/// Background removal via ONNX Runtime (rembg u2net model).
/// Model: assets/models/u2net.onnx
/// </summary>
public sealed class BgRemover : IDisposable
{
    private const int ModelSize = 320;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public BgRemover(string? modelPath = null)
    {
        modelPath ??= Path.Combine(Config.AssetDir, "models", "u2net.onnx");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Background removal model not found: {modelPath}");
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>Returns an RGBA bitmap: original RGB with model alpha matte.</summary>
    public SKBitmap RemoveBackground(SKBitmap source)
    {
        int w = source.Width, h = source.Height;

        // Build NCHW float input, ImageNet-normalized, resized to 320x320
        var input = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });
        using (var resized = source.Resize(new SKImageInfo(ModelSize, ModelSize),
                   new SKSamplingOptions(SKFilterMode.Linear)))
        {
            var span = resized.GetPixelSpan(); // BGRA
            for (int y = 0; y < ModelSize; y++)
            for (int x = 0; x < ModelSize; x++)
            {
                int i = (y * ModelSize + x) * 4;
                float r = span[i + 2] / 255f;
                float g = span[i + 1] / 255f;
                float b = span[i] / 255f;
                // ImageNet normalization (rembg u2net session stats)
                input[0, 0, y, x] = (r - 0.485f) / 0.229f;
                input[0, 1, y, x] = (g - 0.456f) / 0.224f;
                input[0, 2, y, x] = (b - 0.406f) / 0.225f;
            }
        }

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_inputName, input)
        });
        var maskTensor = results.First().AsTensor<float>();
        var dims = maskTensor.Dimensions.ToArray();
        int mh = dims.Length >= 4 ? dims[2] : dims[^2];
        int mw = dims.Length >= 4 ? dims[3] : dims[^1];

        // Build RGBA output at original resolution
        var output = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var outSpan = output.GetPixelSpan();
        var srcSpan = source.GetPixelSpan();

        // Resize mask back to source size (bilinear via SKBitmap)
        using var maskSmall = new SKBitmap(mw, mh, SKColorType.Gray8, SKAlphaType.Opaque);
        var maskSpan = maskSmall.GetPixelSpan();
        for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
        {
            float v = Math.Clamp(maskTensor[0, 0, y, x], 0f, 1f);
            maskSpan[y * mw + x] = (byte)(v * 255f);
        }
        // SKBitmap.Resize honors the SKImageInfo color type; request Gray8
        // explicitly or the mask returns as BGRA and this indexing reads
        // B/G/R/A bytes cyclically (every 4th pixel forced opaque).
        using var maskFull = maskSmall.Resize(
            new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear));
        var maskFullSpan = maskFull.GetPixelSpan(); // Gray8 = 1 byte/px

        for (int i = 0, j = 0; i < outSpan.Length; i += 4, j++)
        {
            byte a = maskFullSpan[j];
            outSpan[i] = srcSpan[i];       // B
            outSpan[i + 1] = srcSpan[i + 1]; // G
            outSpan[i + 2] = srcSpan[i + 2]; // R
            outSpan[i + 3] = a;
        }
        return output;
    }

    public void Dispose() => _session.Dispose();
}
