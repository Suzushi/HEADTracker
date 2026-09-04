using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>
/// DirectShow capture with a dedicated grab thread. The thread keeps only the
/// most recent frame; the consumer calls <see cref="GrabLatest"/> to fetch it.
/// </summary>
public sealed class CameraCapture : IFrameSource
{
    private readonly object _gate = new();
    private VideoCapture? _capture;
    private Thread? _thread;
    private volatile bool _running;
    private Mat? _latest;
    private long _sequence;
    private long _consumedSequence = -1;

    public bool IsOpen => _capture != null;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public double ActualFps { get; private set; }
    public string? LastError { get; private set; }

    public bool Open(int cameraId, int width, int height, double fps, bool autoExpo, double gain, double expo,
        string? api = "dshow", string? fourcc = "")
    {
        // A camera that was just released (Settings "Save & Apply" or the calibration
        // wizard closing) can take a few hundred ms to become free again; a DirectShow
        // reopen inside that window fails with "cannot open camera". Retry briefly on
        // the same backend before giving up. Open runs on a worker thread, so sleeping
        // here never blocks the UI.
        const int attempts = 5;
        const int retryDelayMs = 200;

        VideoCapture? cap = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cap = new VideoCapture(cameraId, ParseApi(api));
            if (cap.IsOpened())
            {
                break;
            }
            cap.Dispose();
            cap = null;
            if (attempt < attempts - 1)
            {
                Thread.Sleep(retryDelayMs);
            }
        }

        if (cap == null)
        {
            LastError = $"Cannot open camera {cameraId} (in use by another app or unavailable)";
            return false;
        }

        // Request an explicit pixel format when asked. Virtual cameras (Iriun, OBS,
        // phone-as-webcam) often deliver a raw YUV layout that the DSHOW backend
        // mis-converts into a tiled/green frame; MJPG or YUY2 usually renders clean.
        int fcc = ParseFourcc(fourcc);
        if (fcc != 0)
        {
            cap.Set(VideoCaptureProperties.FourCC, fcc);
        }

        cap.Set(VideoCaptureProperties.FrameWidth, width);
        cap.Set(VideoCaptureProperties.FrameHeight, height);
        cap.Set(VideoCaptureProperties.Fps, fps);
        // Legacy semantics: auto exposure toggle, then gain/exposure 0..1 -> 0..255.
        cap.Set(VideoCaptureProperties.AutoExposure, autoExpo ? 1.0 : 0.0);
        cap.Set(VideoCaptureProperties.Gain, PoseMath.Clamp(gain, 0.0, 1.0) * 255);
        cap.Set(VideoCaptureProperties.Exposure, PoseMath.Clamp(expo, 0.0, 1.0) * 255);

        _capture = cap;
        FrameWidth = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        FrameHeight = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        ActualFps = cap.Get(VideoCaptureProperties.Fps);

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "HeadTrackerCapture" };
        _thread.Start();
        return true;
    }

    private static VideoCaptureAPIs ParseApi(string? api) => (api ?? "").Trim().ToLowerInvariant() switch
    {
        "msmf" => VideoCaptureAPIs.MSMF,
        "any" => VideoCaptureAPIs.ANY,
        _ => VideoCaptureAPIs.DSHOW,
    };

    private static int ParseFourcc(string? fourcc)
    {
        string s = (fourcc ?? "").Trim();
        return s.Length == 4 ? VideoWriter.FourCC(s[0], s[1], s[2], s[3]) : 0;
    }

    /// <summary>Returns the newest frame not yet consumed, or null if none is available.</summary>
    public Mat? GrabLatest()
    {
        lock (_gate)
        {
            if (_latest == null || _sequence == _consumedSequence)
            {
                return null;
            }
            _consumedSequence = _sequence;
            return _latest.Clone();
        }
    }

    public void SetGain(double gain) => _capture?.Set(VideoCaptureProperties.Gain, PoseMath.Clamp(gain, 0.0, 1.0) * 255);
    public void SetExposure(double expo) => _capture?.Set(VideoCaptureProperties.Exposure, PoseMath.Clamp(expo, 0.0, 1.0) * 255);
    public void SetAutoExposure(bool auto) => _capture?.Set(VideoCaptureProperties.AutoExposure, auto ? 1.0 : 0.0);

    private void CaptureLoop()
    {
        var frame = new Mat();
        while (_running)
        {
            var cap = _capture;
            if (cap == null || !cap.Read(frame) || frame.Empty())
            {
                Thread.Sleep(5);
                continue;
            }

            lock (_gate)
            {
                _latest?.Dispose();
                _latest = frame.Clone();
                _sequence++;
            }
        }
        frame.Dispose();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        _capture?.Dispose();
        _capture = null;
        lock (_gate)
        {
            _latest?.Dispose();
            _latest = null;
        }
    }
}
