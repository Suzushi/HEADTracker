using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>A single SCRFD face detection in source-image coordinates.</summary>
public readonly record struct FaceDetection(Rect2d Box, float Score)
{
    /// <summary>Five facial keypoints (eyes, nose, mouth corners), or null if the model has no kps head.</summary>
    public Point2f[]? KeyPoints { get; init; }
}

/// <summary>
/// SCRFD face detector running the fixed-shape 640x640 bnkps ONNX model.
/// Preprocessing follows insightface: keep-ratio letterbox, RGB, (x-127.5)/128.
/// </summary>
public sealed class ScrfdDetector : IDisposable
{
    private const int InputSize = 640;
    private static readonly int[] Strides = { 8, 16, 32 };
    private const int NumAnchors = 2;

    private readonly InferenceSession _session;
    private readonly float[] _inputBuffer = new float[3 * InputSize * InputSize];
    private readonly string[] _outputNames;

    public double ScoreThreshold { get; set; } = 0.45;
    public double NmsThreshold { get; set; } = 0.45;

    public ScrfdDetector(string modelPath)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
        _outputNames = _session.OutputMetadata.Keys.ToArray();
    }

    /// <summary>Detect faces in a BGR frame, optionally restricted to a crop region.</summary>
    public List<FaceDetection> Detect(Mat frame, Rect? region = null)
    {
        Rect crop = region is { Width: > 0, Height: > 0 }
            ? IntersectRect(new Rect(region.Value.X, region.Value.Y, region.Value.Width, region.Value.Height), frame)
            : new Rect(0, 0, frame.Cols, frame.Rows);

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            return new List<FaceDetection>();
        }

        using var src = new Mat(frame, crop);
        Preprocess(src);

        var tensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, InputSize, InputSize });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input.1", tensor) };
        using var results = _session.Run(inputs, _outputNames);
        var outputs = results.Select(r => r.AsTensor<float>()).ToArray();

        // keep-ratio letterbox parameters used to map detections back to crop pixels
        double hwScale = (double)src.Rows / src.Cols;
        int newH, newW, padH, padW;
        if (hwScale > 1)
        {
            newH = InputSize;
            newW = (int)(InputSize / hwScale);
            padH = 0;
            padW = (InputSize - newW) / 2;
        }
        else
        {
            newW = InputSize;
            newH = (int)(InputSize * hwScale);
            padW = 0;
            padH = (InputSize - newH) / 2;
        }

        var candidates = new List<FaceDetection>();
        bool hasKps = _outputNames.Any(n => n.StartsWith("kps_", StringComparison.Ordinal));
        for (int l = 0; l < Strides.Length; l++)
        {
            int stride = Strides[l];
            int side = InputSize / stride;
            // Outputs are [1, N, C] row-major; index them flat via the backing buffer.
            var scores = ToSpan(outputs[l]);
            var bboxes = ToSpan(outputs[l + Strides.Length]);
            var kps = hasKps ? ToSpan(outputs[l + 2 * Strides.Length]) : ReadOnlySpan<float>.Empty;

            for (int row = 0; row < side; row++)
            {
                for (int col = 0; col < side; col++)
                {
                    for (int a = 0; a < NumAnchors; a++)
                    {
                        int idx = (row * side + col) * NumAnchors + a;
                        float score = scores[idx];
                        if (score < ScoreThreshold)
                        {
                            continue;
                        }

                        double cx = col * stride;
                        double cy = row * stride;
                        double x1 = (cx - bboxes[idx * 4 + 0] * stride - padW) / newW * src.Cols;
                        double y1 = (cy - bboxes[idx * 4 + 1] * stride - padH) / newH * src.Rows;
                        double x2 = (cx + bboxes[idx * 4 + 2] * stride - padW) / newW * src.Cols;
                        double y2 = (cy + bboxes[idx * 4 + 3] * stride - padH) / newH * src.Rows;

                        Point2f[]? pts = null;
                        if (!kps.IsEmpty)
                        {
                            pts = new Point2f[5];
                            for (int k = 0; k < 5; k++)
                            {
                                float kx = kps[idx * 10 + k * 2];
                                float ky = kps[idx * 10 + k * 2 + 1];
                                pts[k] = new Point2f(
                                    (float)((cx + kx * stride - padW) / newW * src.Cols + crop.X),
                                    (float)((cy + ky * stride - padH) / newH * src.Rows + crop.Y));
                            }
                        }

                        candidates.Add(new FaceDetection(
                            new Rect2d(x1 + crop.X, y1 + crop.Y, x2 - x1, y2 - y1), score)
                        {
                            KeyPoints = pts,
                        });
                    }
                }
            }
        }

        return Nms(candidates);
    }

    private void Preprocess(Mat src)
    {
        double hwScale = (double)src.Rows / src.Cols;
        int newH, newW, padTop, padLeft;
        if (hwScale > 1)
        {
            newH = InputSize;
            newW = (int)(InputSize / hwScale);
            padTop = 0;
            padLeft = (InputSize - newW) / 2;
        }
        else
        {
            newW = InputSize;
            newH = (int)(InputSize * hwScale);
            padLeft = 0;
            padTop = (InputSize - newH) / 2;
        }

        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, padTop, InputSize - newH - padTop, padLeft,
            InputSize - newW - padLeft, BorderTypes.Constant, new Scalar(0, 0, 0));
        using var rgb = new Mat();
        Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);

        // (pixel - 127.5) / 128, HWC -> CHW. Channels are already RGB-ordered.
        int plane = InputSize * InputSize;
        unsafe
        {
            for (int y = 0; y < InputSize; y++)
            {
                byte* row = (byte*)rgb.Ptr(y);
                int rowStart = y * InputSize;
                for (int x = 0; x < InputSize; x++)
                {
                    int i = rowStart + x;
                    int c = 3 * x;
                    _inputBuffer[i] = (row[c] - 127.5f) / 128f;
                    _inputBuffer[plane + i] = (row[c + 1] - 127.5f) / 128f;
                    _inputBuffer[2 * plane + i] = (row[c + 2] - 127.5f) / 128f;
                }
            }
        }
    }

    private static ReadOnlySpan<float> ToSpan(Tensor<float> tensor) => ((DenseTensor<float>)tensor).Buffer.Span;

    private List<FaceDetection> Nms(List<FaceDetection> dets)
    {
        dets.Sort((a, b) => b.Score.CompareTo(a.Score));
        var keep = new List<FaceDetection>();
        var suppressed = new bool[dets.Count];
        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }
            keep.Add(dets[i]);
            for (int j = i + 1; j < dets.Count; j++)
            {
                if (!suppressed[j] && IoU(dets[i].Box, dets[j].Box) > NmsThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }
        return keep;
    }

    private static double IoU(Rect2d a, Rect2d b)
    {
        var inter = a & b;
        double interArea = Math.Max(0.0, inter.Width) * Math.Max(0.0, inter.Height);
        if (interArea <= 0)
        {
            return 0;
        }
        double union = a.Width * a.Height + b.Width * b.Height - interArea;
        return union <= 0 ? 0 : interArea / union;
    }

    private static Rect IntersectRect(Rect r, Mat frame)
    {
        int x = Math.Clamp(r.X, 0, frame.Cols - 1);
        int y = Math.Clamp(r.Y, 0, frame.Rows - 1);
        int w = Math.Clamp(r.X + r.Width, 0, frame.Cols) - x;
        int h = Math.Clamp(r.Y + r.Height, 0, frame.Rows) - y;
        return new Rect(x, y, w, h);
    }

    public void Dispose() => _session.Dispose();
}
