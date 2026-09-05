using System.Diagnostics;
using HeadTracker.Core.Configuration;
using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>
/// The full tracking pipeline: frame source -> SCRFD face detection -> CSRT ROI
/// tracking -> OpenSeeFace landmarks -> PnP pose -> center/remap/Accela output.
/// Single-threaded processing (the legacy async detect thread is folded into the
/// main loop; SCRFD is fast enough for periodic in-line re-detection).
/// </summary>
public sealed class TrackingPipeline : IDisposable
{
    private const double MinRoiArea = 10.0;
    private const double OutputPeriodMs = 1000.0 / 250.0; // legacy POSE_OUTPUT_FREQ

    // Drift guard: when the tracked ROI slides off the face, the landmark heatmap
    // peaks collapse. Below this mean confidence for MinConfFramesToReset consecutive
    // frames we declare the track lost and force a full-frame re-acquisition.
    private const double MinMeanConfidence = 0.30;
    private const int MinConfFramesToReset = 3;

    private readonly TrackerSettings _settings;
    private readonly IFrameSource _source;
    private readonly ScrfdDetector _scrfd;
    private readonly RoiTracker _tracker = new();
    private readonly LandmarkDetector _landmark;
    private readonly PoseEstimator _pnp;
    private readonly PoseRemapper _remapper;
    private readonly CameraIntrinsics _intrinsics;
    private readonly EkfFusion? _ekf;
    private readonly FSANet? _fsa;

    private Thread? _processThread;
    private Thread? _outputThread;
    private volatile bool _running;
    private readonly Stopwatch _clock = new();

    private readonly object _previewGate = new();
    private Mat? _preview;

    private Rect2d _lastRoi;
    private Rect2d _lmRoi;
    private bool _lmRoiInited;
    private bool _firstSolvePose = true;
    private int _lowConfFrames;
    private int _frameCount;
    private double _fpsEma;
    private long _lastProcessTicks;
    private long _errorCount;
    private Exception? _lastError;
    private volatile bool _mirror;
    private volatile bool _paused;
    private volatile bool _previewEnabled = true;

    /// <summary>Final output pose (remapped + filtered), at the legacy 250 Hz when freetrack is active.</summary>
    public event Action<Pose6D>? OutputPose;

    /// <summary>Raw unfiltered pose (yaw/pitch/roll degrees, translation meters) for UI display.</summary>
    public event Action<Vec3, Vec3>? RawPose;

    public double FpsEstimate => _fpsEma;
    public double LastReprojectionRmsPx { get; private set; }

    /// <summary>[DIAG] Capture-layer telemetry: is a low frame rate caused by the camera/USB/DSHOW
    /// side (CaptureFps low, ReadMs high) or the CPU side (ProcessMs high)?</summary>
    public int FrameWidth => _source.FrameWidth;
    public int FrameHeight => _source.FrameHeight;
    public double CaptureFps => (_source as CameraCapture)?.CaptureFps ?? -1;
    public double ReadMs => (_source as CameraCapture)?.ReadMs ?? -1;
    public double ProcessMs { get; private set; }
    public PoseRemapper Remapper => _remapper;
    public bool FaceTracked => !_firstSolvePose && _lastRoi.Width * _lastRoi.Height >= MinRoiArea;

    /// <summary>Frames that threw inside processing (kept alive on purpose; surfaced for diagnostics).</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public Exception? LastError => _lastError;

    /// <summary>Horizontally mirror every frame (phone front cameras are selfie-mirrored). Live-toggleable.</summary>
    public bool Mirror
    {
        get => _mirror;
        set => _mirror = value;
    }

    /// <summary>Legacy pause(): keep grabbing frames but freeze processing and pose updates.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>When false, <see cref="DrawPreview"/> is skipped entirely (no per-frame full-frame
    /// clone or 66-point overlay). The UI clears this while the main window is hidden or minimized
    /// so an invisible preview costs nothing -- a real saving when a game is eating the CPU.</summary>
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set => _previewEnabled = value;
    }

    /// <param name="assetRoot">Directory containing scrfd_500m_bnkps_shape640x640.onnx and landmark_models/.</param>
    public TrackingPipeline(TrackerSettings settings, IFrameSource source, string assetRoot)
    {
        _settings = settings;
        _source = source;
        _scrfd = new ScrfdDetector(Path.Combine(assetRoot, "scrfd_500m_bnkps_shape640x640.onnx"));
        var landmarkDir = Path.Combine(assetRoot, "landmark_models");
        _landmark = new LandmarkDetector(landmarkDir, Path.Combine(landmarkDir, "model_66.txt"),
            settings.LandmarkDetectMethod, settings.CervicalFaceModel);
        _pnp = new PoseEstimator(_intrinsics = CameraIntrinsics.FromSettings(settings, source.FrameWidth, source.FrameHeight));
        _remapper = new PoseRemapper(settings);
        _mirror = settings.MirrorCamera;
        if (settings.UseEkf)
        {
            _ekf = new EkfFusion(settings);
            // Deviation from legacy (which gates FSA on landmark_detect_method < 0, a dead
            // path guarded by an assert): FSA-Net runs as the second EKF measurement when
            // fusion is enabled, so fsa_pnp_mixture_rate-era configs gain real fusion.
            if (settings.UseFsa)
            {
                _fsa = new FSANet(Path.Combine(assetRoot, "fsanet_capsule.onnx"));
            }
        }
    }

    public void Start()
    {
        if (_running || !_source.IsOpen)
        {
            return;
        }
        _running = true;
        _clock.Restart();
        // AboveNormal keeps the tracking + publish threads scheduled when a CPU-heavy game
        // (e.g. MSFS) saturates the cores, which is exactly when smooth tracking matters most.
        _processThread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "HeadTrackerPipeline",
            Priority = ThreadPriority.AboveNormal,
        };
        _processThread.Start();
        if (_remapper.UseAccelaPath)
        {
            _outputThread = new Thread(OutputLoop)
            {
                IsBackground = true,
                Name = "HeadTrackerOutput",
                Priority = ThreadPriority.AboveNormal,
            };
            _outputThread.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        _processThread?.Join(1000);
        _outputThread?.Join(1000);
        _processThread = null;
        _outputThread = null;
    }

    /// <summary>Re-center the neutral pose (legacy center hotkey action).</summary>
    public void ResetCenter() => _remapper.ResetCenter();

    /// <summary>Force full-frame re-detection on the next frame.</summary>
    public void ResetDetection()
    {
        _firstSolvePose = true;
        _lastRoi = default;
        _lmRoiInited = false;
        _lowConfFrames = 0;
    }

    /// <summary>Latest annotated preview frame (BGR), or null when preview is off/empty.</summary>
    public Mat? TryGetPreview()
    {
        lock (_previewGate)
        {
            return _preview?.Clone();
        }
    }

    private void ProcessLoop()
    {
        while (_running)
        {
            var frame = _source.GrabLatest();
            if (frame == null)
            {
                Thread.Sleep(2);
                continue;
            }

            if (_paused)
            {
                frame.Dispose();
                Thread.Sleep(2);
                continue;
            }

            if (_mirror)
            {
                Cv2.Flip(frame, frame, FlipMode.Y);
            }

            long ticks = _clock.ElapsedTicks;
            double dt = _lastProcessTicks == 0 ? 0.03 : (ticks - _lastProcessTicks) / (double)Stopwatch.Frequency;
            _lastProcessTicks = ticks;
            if (dt > 0)
            {
                double inst = 1.0 / dt;
                _fpsEma = _fpsEma == 0 ? inst : _fpsEma * 0.95 + inst * 0.05;
            }

            long procStart = Stopwatch.GetTimestamp();
            try
            {
                Process(frame, dt);
            }
            catch (Exception ex)
            {
                // Keep the pipeline alive; a single bad frame must not kill tracking.
                _lastError = ex;
                Interlocked.Increment(ref _errorCount);
            }
            finally
            {
                ProcessMs = (Stopwatch.GetTimestamp() - procStart) * 1000.0 / Stopwatch.Frequency;
                frame.Dispose();
            }
        }
    }

    private void Process(Mat frame, double dt)
    {
        _frameCount++;
        Vec3 trackSpd = Vec3.Zero;
        bool haveSpeed = false;

        if (_firstSolvePose)
        {
            // Re-acquisition: drop the PnP temporal guess so a stale pose cannot drag the
            // fresh solve toward the previous (possibly far-off) head position.
            _pnp.Reset();
            var det = SelectBest(_scrfd.Detect(frame), default);
            if (!det.Found)
            {
                DrawPreview(frame, null, null);
                return;
            }

            _lastRoi = det.Box;
            _tracker.Init(frame, ToRect(_lastRoi));
        }
        else
        {
            // Periodic re-detection near the current ROI, like the legacy detect thread.
            int duration = Math.Max(1, _settings.DetectDuration);
            if (_frameCount % duration == 0)
            {
                var search = CropRoi(_lastRoi, frame, 0.4);
                var det = SelectBest(_scrfd.Detect(frame, ToRect(search)), _lastRoi);
                if (!det.Found)
                {
                    // Nothing near the tracked ROI: the tracker has drifted onto the
                    // background (fast head turns, e.g. alt-tabbing). Legacy widens the
                    // search to the whole frame here; without this the drift is permanent
                    // because CSRT keeps reporting success on background texture.
                    det = SelectBest(_scrfd.Detect(frame), _lastRoi);
                }
                if (det.Found)
                {
                    _tracker.Init(frame, ToRect(det.Box));
                    _lastRoi = det.Box;
                }
            }

            var roiInt = ToRect(_lastRoi);
            bool ok = _tracker.Update(frame, ref roiInt);
            var roi = new Rect2d(roiInt.X, roiInt.Y, roiInt.Width, roiInt.Height);

            if (ok && dt > 0)
            {
                // Legacy track_spd: ROI center velocity in px/s, feeds the ground-speed EKF update.
                trackSpd = new Vec3(
                    (roi.X + roi.Width / 2 - _lastRoi.X - _lastRoi.Width / 2) / dt,
                    (roi.Y + roi.Height / 2 - _lastRoi.Y - _lastRoi.Height / 2) / dt,
                    0);
                haveSpeed = true;
            }

            if (!ok)
            {
                var det = SelectBest(_scrfd.Detect(frame), default);
                if (det.Found)
                {
                    roi = det.Box;
                    _tracker.Init(frame, ToRect(roi));
                    ok = true;
                }
            }

            if (!ok)
            {
                DrawPreview(frame, null, null);
                _firstSolvePose = true;
                return;
            }

            _lastRoi = roi;
        }

        if (_lastRoi.Width * _lastRoi.Height < MinRoiArea)
        {
            _firstSolvePose = true;
            return;
        }

        // Landmark ROI: 20% expansion smoothed over time (legacy mixture_roi).
        var lmRoi = CropRoi(_lastRoi, frame, 0.2);
        _lmRoi = _lmRoiInited ? MixtureRoi(_lmRoi, lmRoi, _settings.RoiFilterRate) : lmRoi;
        _lmRoiInited = true;

        var lm = _landmark.Detect(frame, ToRect(_lmRoi));
        if (lm == null)
        {
            DrawPreview(frame, _lastRoi, null);
            return;
        }

        // Drift guard: a collapsed heatmap (ROI on background/wall) yields a wildly
        // wrong pose (e.g. yaw -144°) that would jerk the in-game camera. When the
        // mean landmark confidence stays very low for a few frames, drop the output
        // (the game holds the last good pose) and force a full-frame re-acquisition.
        if (MeanConfidence(lm.Confidences) < MinMeanConfidence)
        {
            if (++_lowConfFrames >= MinConfFramesToReset)
            {
                DrawPreview(frame, _lastRoi, lm.Points2D);
                ResetDetection();
                return;
            }
        }
        else
        {
            _lowConfFrames = 0;
        }

        var pnp = _pnp.Solve(lm.Points2D, lm.ModelPoints3D);
        DrawPreview(frame, _lastRoi, lm.Points2D);
        if (!pnp.Success)
        {
            return;
        }

        LastReprojectionRmsPx = pnp.ReprojectionRmsPx;
        if (_firstSolvePose)
        {
            _firstSolvePose = false;
        }

        double t = _clock.ElapsedTicks / (double)Stopwatch.Frequency;
        Mat3 rWorld;
        Vec3 tOut = pnp.T;

        if (_ekf != null)
        {
            // Legacy loop(): PnP pose as measurement 0, FSA pose as measurement 1,
            // optional planar ground speed, then predict to the measurement time.
            _ekf.UpdateRawPose(t, QuatD.FromRotationMatrix(pnp.R), pnp.T, 0);
            if (_fsa != null && RunFsa(frame, out var fsaQ))
            {
                _ekf.UpdateRawPose(t, fsaQ, pnp.T, 1);
            }
            if (_settings.EnableFaceSpdEst && haveSpeed)
            {
                _ekf.UpdateGroundSpeed(t, EstimateGroundSpeed(pnp.T.Z, _lastRoi, trackSpd, dt));
            }
            var (qf, tf) = _ekf.Predict(t);
            rWorld = qf.ToRotationMatrix().Multiply(Mat3.RFace);
            tOut = tf;
        }
        else
        {
            // Legacy pose_callback: world rotation is R * Rface.
            rWorld = pnp.R.Multiply(Mat3.RFace);
        }

        _remapper.OnPose(rWorld, tOut, dt);
        RawPose?.Invoke(QuatD.FromRotationMatrix(pnp.R.Multiply(Mat3.RFace)).ToYprDegrees(), pnp.T);

        if (!_remapper.UseAccelaPath)
        {
            // Legacy UDP-only path: per-detection output without remap/filter.
            if (_remapper.SnapshotUnfiltered() is { } pose)
            {
                OutputPose?.Invoke(pose);
            }
        }
    }

    /// <summary>Legacy FSA branch: rotation from FSA-Net ypr corrected by the ROI off-axis angle.</summary>
    private bool RunFsa(Mat frame, out QuatD q)
    {
        q = default;
        var rect = ToRect(_lmRoi);
        if (rect.Width < 8 || rect.Height < 8)
        {
            return false;
        }
        using var crop = new Mat(frame, rect);
        var yprRaw = _fsa!.Infer(crop);
        var corr = EulByCrop(_lastRoi);
        double yaw = yprRaw.X - corr.X;
        double pitch = yprRaw.Y - corr.Y;
        double roll = yprRaw.Z - corr.Z;
        var r = Mat3.RCam.Transpose()
            .Multiply(Mat3.Rz(yaw))
            .Multiply(Mat3.Ry(pitch + _settings.PitchOffsetFsaPnp))
            .Multiply(Mat3.Rx(-roll))
            .Multiply(Mat3.RFace.Transpose());
        q = QuatD.FromRotationMatrix(r);
        return true;
    }

    /// <summary>Legacy eul_by_crop(): apparent yaw/pitch of the ROI center from the pinhole model.</summary>
    private Vec3 EulByCrop(Rect2d roi)
    {
        var un = Undistort(new[]
        {
            new Point2f((float)(roi.X + roi.Width / 2), (float)(roi.Y + roi.Height / 2)),
        });
        double yaw = Math.Atan2(un[0].X, 1);
        double pitch = Math.Atan2(un[0].Y, 1 / Math.Cos(yaw));
        return new Vec3(-yaw, -pitch, 0);
    }

    /// <summary>Legacy estimate_ground_speed_by_tracker(): planar 3D velocity from ROI motion at depth z.</summary>
    private Vec3 EstimateGroundSpeed(double z, Rect2d roi, in Vec3 trackSpd, double dt)
    {
        if (dt <= 0)
        {
            return Vec3.Zero;
        }
        double cx = roi.X + roi.Width / 2;
        double cy = roi.Y + roi.Height / 2;
        var un = Undistort(new[]
        {
            new Point2f((float)(cx + trackSpd.X * dt), (float)(cy + trackSpd.Y * dt)),
            new Point2f((float)cx, (float)cy),
        });
        double inv = 1.0 / dt;
        return new Vec3(
            (un[0].X * z - un[1].X * z) * inv,
            (un[0].Y * z - un[1].Y * z) * inv,
            0);
    }

    private unsafe Point2f[] Undistort(Point2f[] pts)
    {
        // OpenCvSharp 4.13 only exposes the InputArray/OutputArray overload; fill
        // a CV_32FC2 Mat directly (a handful of points, called once per frame).
        using var k = _intrinsics.KMat();
        using var d = _intrinsics.DMat();
        using var src = new Mat(pts.Length, 1, MatType.CV_32FC2);
        float* sp = (float*)src.Data;
        for (int i = 0; i < pts.Length; i++)
        {
            sp[2 * i] = pts[i].X;
            sp[2 * i + 1] = pts[i].Y;
        }
        using var dst = new Mat();
        Cv2.UndistortPoints(src, dst, k, d, null, null);
        var result = new Point2f[pts.Length];
        float* dp = (float*)dst.Data;
        for (int i = 0; i < pts.Length; i++)
        {
            result[i] = new Point2f(dp[2 * i], dp[2 * i + 1]);
        }
        return result;
    }

    private void OutputLoop()
    {
        long last = _clock.ElapsedTicks;
        double nextMs = OutputPeriodMs;
        while (_running)
        {
            double nowMs = _clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (nowMs < nextMs)
            {
                Thread.Sleep(1);
                continue;
            }
            nextMs += OutputPeriodMs;
            if (nowMs - nextMs > 50)
            {
                nextMs = nowMs + OutputPeriodMs; // fell behind; resync
            }

            double dt = (_clock.ElapsedTicks - last) / (double)Stopwatch.Frequency;
            last = _clock.ElapsedTicks;
            try
            {
                if (_remapper.Tick(dt) is { } pose)
                {
                    OutputPose?.Invoke(pose);
                }
            }
            catch (Exception ex)
            {
                // A failing subscriber (e.g. publisher) must not kill the output thread.
                _lastError = ex;
                Interlocked.Increment(ref _errorCount);
            }
        }
    }

    /// <summary>Legacy box selection: largest area, preferring overlap with the predicted ROI.</summary>
    private static (Rect2d Box, bool Found) SelectBest(List<FaceDetection> dets, Rect2d predictRoi)
    {
        if (dets.Count == 0)
        {
            return (default, false);
        }

        var best = dets[0].Box;
        double overlap = IntersectionArea(best, predictRoi);
        double area = Area(best);
        foreach (var d in dets)
        {
            double a = Area(d.Box);
            double o = IntersectionArea(d.Box, predictRoi);
            if (a > area && (o > overlap || overlap <= 0))
            {
                best = d.Box;
                overlap = o;
            }
        }
        return (best, true);
    }

    private static double Area(Rect2d r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);

    /// <summary>Mean heatmap confidence across all landmarks; collapses when the ROI is off-face.</summary>
    private static double MeanConfidence(float[] confs)
    {
        if (confs.Length == 0)
        {
            return 0;
        }
        double sum = 0;
        for (int i = 0; i < confs.Length; i++)
        {
            sum += confs[i];
        }
        return sum / confs.Length;
    }

    private static double IntersectionArea(Rect2d a, Rect2d b)
    {
        var i = a & b;
        return Area(i);
    }

    /// <summary>Port of the legacy crop_roi(): expand by rate and clamp to the frame.</summary>
    private static Rect2d CropRoi(Rect2d predictRoi, Mat frame, double rate)
    {
        double exX = predictRoi.Width * rate;
        double exY = predictRoi.Height * rate;
        double x = Math.Max(0, predictRoi.X - exX);
        double y = Math.Max(0, predictRoi.Y - exY);
        double w = predictRoi.Width + exX * 2;
        double h = predictRoi.Height + exY * 2;
        if (x + w > frame.Cols)
        {
            w = frame.Cols - x - 1;
        }
        if (y + h > frame.Rows)
        {
            h = frame.Rows - y - 1;
        }
        return new Rect2d(x, y, w, h);
    }

    private static Rect2d MixtureRoi(Rect2d a, Rect2d b, double rate) => new(
        a.X * rate + b.X * (1 - rate),
        a.Y * rate + b.Y * (1 - rate),
        a.Width * rate + b.Width * (1 - rate),
        a.Height * rate + b.Height * (1 - rate));

    private static Rect ToRect(Rect2d r)
    {
        int x = Math.Max(0, (int)r.X);
        int y = Math.Max(0, (int)r.Y);
        return new Rect(x, y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
    }

    private void DrawPreview(Mat frame, Rect2d? roi, Point2f[]? landmarks)
    {
        // Skip the per-frame full-frame clone + 66-point overlay entirely when nobody is
        // watching (main window hidden/minimized while gaming). Pure savings, auto-resumes.
        if (!_previewEnabled)
        {
            return;
        }

        // Ownership of this Mat transfers to _preview; it is disposed by the
        // next DrawPreview call or by Dispose(), always under _previewGate.
        var show = frame.Clone();
        if (roi is { } r)
        {
            Cv2.Rectangle(show, ToRect(r), new Scalar(0, 200, 255), 1);
        }
        if (landmarks != null)
        {
            foreach (var p in landmarks)
            {
                Cv2.Circle(show, new Point((int)p.X, (int)p.Y), 1, new Scalar(0, 255, 0), -1);
            }
        }
        lock (_previewGate)
        {
            _preview?.Dispose();
            _preview = show;
        }
    }

    public void Dispose()
    {
        Stop();
        _tracker.Dispose();
        _scrfd.Dispose();
        _landmark.Dispose();
        _fsa?.Dispose();
        lock (_previewGate)
        {
            _preview?.Dispose();
            _preview = null;
        }
    }
}
