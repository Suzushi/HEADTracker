using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>66 facial landmarks in frame coordinates plus the matching 3-D model points.</summary>
public sealed class LandmarkResult
{
    public required Point2f[] Points2D { get; init; }
    public required float[] Confidences { get; init; }
    public required Vec3[] ModelPoints3D { get; init; }
}

/// <summary>
/// OpenSeeFace landmark detector (ONNX heatmaps), a faithful port of the legacy
/// LandmarkDetector including its normalization, heatmap decoding (logit offsets,
/// swapped grid axes) and the 66-point 3-D face model.
/// </summary>
public sealed class LandmarkDetector : IDisposable
{
    public const int FeatureCount = 66;
    private const int OutputChannels = 198;
    private const double MinRoiArea = 10.0;

    private static readonly string[] ModelFiles =
    {
        "lm_modelU_opt.onnx", // level 0
        "lm_modelV_opt.onnx", // level 1
        "lm_model1_opt.onnx", // level 2
        "lm_model2_opt.onnx", // level 3
        "lm_model3_opt.onnx", // level 4
    };

    // Legacy normalization: mean/std pre-divided, then std scaled by 255.
    private static readonly double[] MeanDivStd = { 0.485 / 0.229, 0.456 / 0.224, 0.406 / 0.225 };
    private static readonly double[] StdTimes255 = { 0.229 * 255.0, 0.224 * 255.0, 0.225 * 255.0 };

    private readonly InferenceSession _session;
    private readonly int _netSize;
    private readonly int _outSize;
    private readonly float[] _inputBuffer;
    private readonly Vec3[] _modelPoints;

    public int NetSize => _netSize;
    public int OutputSize => _outSize;
    public Vec3[] ModelPoints => _modelPoints;

    /// <param name="modelDir">Directory containing the lm_model*_opt.onnx files.</param>
    /// <param name="model66Path">Path to the legacy model_66.txt 3-D point table.</param>
    /// <param name="level">Landmark model level 0..4.</param>
    /// <param name="cervicalFaceModel">Legacy cervical_face_model z offset.</param>
    public LandmarkDetector(string modelDir, string model66Path, int level, double cervicalFaceModel)
    {
        level = Math.Clamp(level, 0, ModelFiles.Length - 1);
        _netSize = level <= 1 ? 112 : 224;
        _outSize = level <= 1 ? 14 : 28;

        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(Path.Combine(modelDir, ModelFiles[level]), options);
        _inputBuffer = new float[3 * _netSize * _netSize];
        _modelPoints = LoadModelPoints(model66Path, cervicalFaceModel);
    }

    private static Vec3[] LoadModelPoints(string path, double cervicalFaceModel)
    {
        var points = new List<Vec3>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }
            double px = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double py = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double pz = double.Parse(parts[2], CultureInfo.InvariantCulture);
            // Legacy: Point3d(px, -py, -(pz + cervical_face_model))
            points.Add(new Vec3(px, -py, -(pz + cervicalFaceModel)));
        }
        return points.ToArray();
    }

    /// <summary>Detect landmarks within <paramref name="roi"/> of <paramref name="frame"/> (BGR).</summary>
    public LandmarkResult? Detect(Mat frame, Rect roi)
    {
        if (roi.Width * roi.Height < MinRoiArea)
        {
            return null;
        }

        roi = Intersect(roi, frame);
        if (roi.Width * roi.Height < MinRoiArea)
        {
            return null;
        }

        using var crop = new Mat(frame, roi);
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(_netSize, _netSize), 0, 0, InterpolationFlags.Linear);
        using var f32 = new Mat();
        resized.ConvertTo(f32, MatType.CV_32F);
        using var rgb = new Mat();
        Cv2.CvtColor(f32, rgb, ColorConversionCodes.BGR2RGB);
        NormalizeAndTranspose(rgb);

        var tensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, _netSize, _netSize });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", tensor) };
        using var results = _session.Run(inputs, new[] { "output" });
        var output = results.Single().AsTensor<float>();

        var (points, confs) = DecodeHeatmaps(((DenseTensor<float>)output).Buffer.Span, roi.X, roi.Y,
            (double)roi.Height / _netSize, (double)roi.Width / _netSize);

        return new LandmarkResult
        {
            Points2D = points,
            Confidences = confs,
            ModelPoints3D = _modelPoints,
        };
    }

    private unsafe void NormalizeAndTranspose(Mat rgbFloat)
    {
        // Legacy: divide by (std*255), subtract mean/std, then HWC->CHW.
        // The Mat is already converted BGR->RGB, so channel 0 is R.
        int plane = _netSize * _netSize;
        for (int y = 0; y < _netSize; y++)
        {
            float* row = (float*)rgbFloat.Ptr(y);
            int rowStart = y * _netSize;
            for (int x = 0; x < _netSize; x++)
            {
                int i = rowStart + x;
                int c = 3 * x;
                _inputBuffer[i] = (float)(row[c] / StdTimes255[0] - MeanDivStd[0]);
                _inputBuffer[plane + i] = (float)(row[c + 1] / StdTimes255[1] - MeanDivStd[1]);
                _inputBuffer[2 * plane + i] = (float)(row[c + 2] / StdTimes255[2] - MeanDivStd[2]);
            }
        }
    }

    /// <summary>
    /// Port of proc_heatmaps. Note the legacy axis quirk is preserved: the grid
    /// argmax row feeds lm_y and the column feeds lm_x, with scale_x coming from
    /// the ROI height and scale_y from the ROI width.
    /// </summary>
    private (Point2f[] Points, float[] Confs) DecodeHeatmaps(ReadOnlySpan<float> heatmaps, int x0, int y0,
        double scaleX, double scaleY)
    {
        // heatmaps is [1, 198, out, out] row-major; indexed flat.
        int heatmapSize = _outSize * _outSize;
        var points = new Point2f[FeatureCount];
        var confs = new float[FeatureCount];
        double res = _netSize - 1;

        for (int lm = 0; lm < FeatureCount; lm++)
        {
            int offset = heatmapSize * lm;
            int argmax = 0;
            float maxVal = float.NegativeInfinity;
            for (int i = 0; i < heatmapSize; i++)
            {
                float v = heatmaps[offset + i];
                if (v > maxVal)
                {
                    maxVal = v;
                    argmax = i;
                }
            }

            int gx = argmax / _outSize;
            int gy = argmax % _outSize;

            float offX = (float)(res * Logit(heatmaps[FeatureCount * heatmapSize + offset + argmax]));
            float offY = (float)(res * Logit(heatmaps[2 * FeatureCount * heatmapSize + offset + argmax]));

            float lmY = (float)(y0 + scaleX * (res * ((double)gx / (_outSize - 1)) + offX));
            float lmX = (float)(x0 + scaleY * (res * ((double)gy / (_outSize - 1)) + offY));

            points[lm] = new Point2f(lmX, lmY);
            confs[lm] = maxVal;
        }
        return (points, confs);
    }

    /// <summary>Legacy logit(): log-odds of a heatmap value, scaled by the grid downsample.</summary>
    private double Logit(float p)
    {
        if (p >= 1.0)
        {
            p = 0.99999f;
        }
        else if (p <= 0.0)
        {
            p = 0.0000001f;
        }

        double v = p / (1 - p);
        return Math.Log(v) / (_outSize == 7 ? 8 : 16);
    }

    private static Rect Intersect(Rect r, Mat frame)
    {
        int x = Math.Clamp(r.X, 0, frame.Cols - 1);
        int y = Math.Clamp(r.Y, 0, frame.Rows - 1);
        int w = Math.Clamp(r.X + r.Width, 0, frame.Cols) - x;
        int h = Math.Clamp(r.Y + r.Height, 0, frame.Rows) - y;
        return new Rect(x, y, w, h);
    }

    public void Dispose() => _session.Dispose();
}
