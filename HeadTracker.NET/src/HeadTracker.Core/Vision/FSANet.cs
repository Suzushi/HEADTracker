using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>
/// FSA-Net (capsule variant) head pose estimator, a 1:1 port of the legacy
/// FSANet.cpp: the crop is resized to 64x64, min-max normalized to 0..255 and
/// fed as NHWC BGR floats; the [1,3] output is (yaw, pitch, roll) in degrees
/// and is returned in radians.
/// </summary>
public sealed class FSANet : IDisposable
{
    private const int InputSize = 64;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly float[] _inputBuffer = new float[InputSize * InputSize * 3];

    public FSANet(string modelPath)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>Infer (yaw, pitch, roll) in radians from a BGR face crop.</summary>
    public Vec3 Infer(Mat crop)
    {
        Preprocess(crop);

        var tensor = new DenseTensor<float>(_inputBuffer, new[] { 1, InputSize, InputSize, 3 });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs, new[] { _outputName });
        // [1,3] output: index it flat via the backing buffer (multi-dim indexer traps).
        var span = ((DenseTensor<float>)results.First().AsTensor<float>()).Buffer.Span;
        const double deg2rad = Math.PI / 180.0;
        return new Vec3(span[0] * deg2rad, span[1] * deg2rad, span[2] * deg2rad);
    }

    private unsafe void Preprocess(Mat crop)
    {
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Linear);

        // Legacy: cv::normalize(data, data, 0, 255, NORM_MINMAX) over the whole BGR image.
        using var flat = resized.Reshape(1, InputSize * InputSize * 3);
        Cv2.MinMaxIdx(flat, out double min, out double max);
        double scale = max - min < 1e-6 ? 0.0 : 255.0 / (max - min);

        byte* p = (byte*)resized.Ptr(0);
        int n = InputSize * InputSize * 3;
        for (int i = 0; i < n; i++)
        {
            _inputBuffer[i] = (float)((p[i] - min) * scale);
        }
    }

    public void Dispose() => _session.Dispose();
}
