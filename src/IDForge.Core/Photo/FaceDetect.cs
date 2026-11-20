using OpenCvSharp;
using SkiaSharp;

namespace IDForge.Core.Photo;

/// <summary>Face detection via OpenCV YuNet ONNX model.</summary>
public sealed class FaceDetector : IDisposable
{
    private readonly string _modelPath;
    private FaceDetectorYN? _detector;
    private Size _detectorSize;

    public FaceDetector(string? modelPath = null)
    {
        _modelPath = modelPath ?? Path.Combine(Config.AssetDir, "models", "face_detection_yunet_2023mar.onnx");
        if (!File.Exists(_modelPath))
            throw new FileNotFoundException($"Face detection model not found: {_modelPath}");
    }

    private FaceDetectorYN GetDetector(int w, int h)
    {
        var size = new Size(w, h);
        if (_detector == null || _detectorSize != size)
        {
            _detector?.Dispose();
            _detector = FaceDetectorYN.Create(_modelPath, "", size, 0.6f, 0.3f, 5000);
            _detectorSize = size;
        }
        return _detector;
    }

    /// <summary>Detects faces; returns boxes in pixel coords of the source image.</summary>
    public List<SKRect> Detect(SKBitmap image)
    {
        using var mat = BitmapToMat(image);
        var detector = GetDetector(mat.Width, mat.Height);
        using var facesMat = new Mat();
        detector.Detect(mat, facesMat);

        var boxes = new List<SKRect>();
        if (!facesMat.Empty())
        {
            for (int i = 0; i < facesMat.Rows; i++)
            {
                // YuNet outputs [x, y, w, h, ...] per row
                float x = facesMat.At<float>(i, 0);
                float y = facesMat.At<float>(i, 1);
                float w = facesMat.At<float>(i, 2);
                float h = facesMat.At<float>(i, 3);
                boxes.Add(new SKRect(x, y, x + w, y + h));
            }
        }
        return boxes;
    }

    private static Mat BitmapToMat(SKBitmap bmp)
    {
        var bytes = bmp.GetPixelSpan().ToArray(); // BGRA
        using var mat4 = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, bytes);
        var mat = new Mat();
        Cv2.CvtColor(mat4, mat, ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    /// <summary>Returns the largest face box, or null.</summary>
    public SKRect? DetectLargest(SKBitmap image)
    {
        var boxes = Detect(image);
        if (boxes.Count == 0) return null;
        return boxes.OrderByDescending(b => b.Width * b.Height).First();
    }

    public void Dispose() => _detector?.Dispose();
}
