using System.IO;
using HeadTracker.Core;
using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;
using OpenCvSharp;

namespace HeadTracker.App.Services;

/// <summary>
/// Owns the tracking lifecycle for the UI layer: loads settings, starts and
/// stops the camera + pipeline + publisher trio, and exposes a thread-safe
/// snapshot of the latest status for display. All heavy work happens on the
/// pipeline's own threads; the UI polls at its refresh rate.
/// </summary>
public sealed class TrackerService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _poseGate = new();

    private CameraCapture? _camera;
    private TrackingPipeline? _pipeline;
    private PosePublisher? _publisher;

    private Pose6D _lastOutput;
    private Vec3 _lastRawYpr;
    private Vec3 _lastRawT;
    private bool _previewEnabled = true;

    public TrackerService(string configPath)
    {
        ConfigPath = configPath;
        Settings = SettingsStore.Load(configPath);
    }

    public string ConfigPath { get; }
    public TrackerSettings Settings { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Reason the last <see cref="Start"/> failed, or null.</summary>
    public string? LastError { get; private set; }

    public double Fps => _pipeline?.FpsEstimate ?? 0;
    public bool FaceTracked => _pipeline?.FaceTracked ?? false;
    public double RmsPx => _pipeline?.LastReprojectionRmsPx ?? 0;
    public long Errors => _pipeline?.ErrorCount ?? 0;

    // [DIAG] capture-vs-processing telemetry surfaced in the status bar.
    public double CaptureFps => _pipeline?.CaptureFps ?? -1;
    public double ReadMs => _pipeline?.ReadMs ?? -1;
    public double ProcessMs => _pipeline?.ProcessMs ?? 0;
    public string Resolution => _pipeline != null ? $"{_pipeline.FrameWidth}\u00d7{_pipeline.FrameHeight}" : "--";
    /// <summary>[DIAG] The (backend/format/resolution) combo negotiation settled on.</summary>
    public string CaptureCombo { get; private set; } = "--";

    public (Pose6D Output, Vec3 RawYpr, Vec3 RawT) LatestPoses()
    {
        lock (_poseGate)
        {
            return (_lastOutput, _lastRawYpr, _lastRawT);
        }
    }

    public Mat? GetPreview() => _pipeline?.TryGetPreview();

    /// <summary>Forwards to the pipeline so DrawPreview is skipped while the main window is hidden
    /// or minimized. Latched and reapplied on Start so it survives a pipeline restart.</summary>
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            _previewEnabled = value;
            var pipeline = _pipeline;
            if (pipeline != null)
            {
                pipeline.PreviewEnabled = value;
            }
        }
    }

    public string OutputsDescription =>
        _publisher == null ? "" : string.Join(" + ", new[]
        {
            _publisher.FreeTrackActive ? "freetrack(shm)" : null,
            _publisher.UdpActive ? $"udp {Settings.UdpHost}:{Settings.Port}" : null,
        }.Where(s => s != null)!);

    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return true;
            }

            LastError = null;
            var camera = OpenCamera();
            if (camera == null)
            {
                return false;
            }

            try
            {
                string assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
                string csv = Path.Combine(assetRoot, "facetracknoir supported games.csv");
                _publisher = new PosePublisher(Settings, File.Exists(csv) ? csv : null);
                _pipeline = new TrackingPipeline(Settings, camera, assetRoot);
                _pipeline.PreviewEnabled = _previewEnabled;
                _pipeline.OutputPose += pose =>
                {
                    lock (_poseGate)
                    {
                        _lastOutput = pose;
                    }
                    _publisher?.Publish(in pose);
                };
                _pipeline.RawPose += (ypr, t) =>
                {
                    lock (_poseGate)
                    {
                        _lastRawYpr = ypr;
                        _lastRawT = t;
                    }
                };
                _pipeline.Start();
                _camera = camera;
                IsRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _pipeline?.Dispose();
                _pipeline = null;
                _publisher?.Dispose();
                _publisher = null;
                camera.Dispose();
                return false;
            }
        }
    }

    /// <summary>Opens the capture, auto-negotiating a (backend, format, resolution) combo that
    /// actually delivers the target frame rate when enabled; cameras are too mode-inconsistent
    /// to assume one works. Returns null and sets <see cref="LastError"/> when nothing opens.</summary>
    private CameraCapture? OpenCamera()
    {
        if (Settings.CaptureAutoNegotiate)
        {
            var outcome = new CameraNegotiator().Negotiate(Settings.CameraId, Settings, Settings.Fps);
            if (outcome.Camera != null)
            {
                CaptureCombo = CameraNegotiator.Describe(outcome.Combo);
                return outcome.Camera; // left open by the successful probe
            }

            // Nothing met the target: open the best-rate combo anyway (slow beats dead).
            var best = new CameraCapture();
            if (OpenCombo(best, outcome.Combo))
            {
                CaptureCombo = CameraNegotiator.Describe(outcome.Combo) + " (best-effort)";
                return best;
            }
            LastError = best.LastError ?? $"Cannot open camera {Settings.CameraId}";
            best.Dispose();
            return null;
        }

        var wanted = new CameraNegotiator.Combo(Settings.CaptureApi, Settings.CaptureFourcc,
            Settings.CaptureWidth, Settings.CaptureHeight);
        var forced = new CameraCapture();
        if (OpenCombo(forced, wanted))
        {
            CaptureCombo = CameraNegotiator.Describe(wanted) + " (forced)";
            return forced;
        }
        LastError = forced.LastError ?? $"Cannot open camera {Settings.CameraId}";
        forced.Dispose();
        return null;
    }

    private bool OpenCombo(CameraCapture camera, CameraNegotiator.Combo c) =>
        camera.Open(Settings.CameraId, c.Width, c.Height, Settings.Fps,
            Settings.EnableAutoExpo, Settings.CameraGain, Settings.CameraExpo, c.Api, c.Fourcc);

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }
            _pipeline?.Dispose();
            _pipeline = null;
            _publisher?.Dispose();
            _publisher = null;
            _camera?.Dispose();
            _camera = null;
            IsRunning = false;
            lock (_poseGate)
            {
                _lastOutput = Pose6D.Zero;
                _lastRawYpr = Vec3.Zero;
                _lastRawT = Vec3.Zero;
            }
        }
    }

    /// <summary>Tears the capture down and brings it back up: the software equivalent of
    /// physically restarting a webcam. Virtual cameras (Iriun, OBS, phone-as-webcam)
    /// occasionally latch a corrupt/green/tiled frame layout and only re-negotiate a
    /// clean format after a reopen. Works whether or not tracking is running; the
    /// DirectShow release race is covered by <see cref="CameraCapture.Open"/>'s retry.</summary>
    public bool RestartCamera()
    {
        Stop();
        return Start();
    }

    public void Recenter() => _pipeline?.ResetCenter();

    public void ForceRedetect() => _pipeline?.ResetDetection();

    /// <summary>Live mirror toggle; persisted to config.yaml so it survives restarts.</summary>
    public bool Mirror
    {
        get => Settings.MirrorCamera;
        set
        {
            Settings.MirrorCamera = value;
            var pipeline = _pipeline;
            if (pipeline != null)
            {
                pipeline.Mirror = value;
            }
            try
            {
                SettingsStore.Save(ConfigPath, Settings);
            }
            catch (IOException)
            {
                // A read-only config must not break the live toggle.
            }
        }
    }

    /// <summary>Persist new settings; restarts the pipeline when it is running.</summary>
    public void ApplySettings(TrackerSettings settings)
    {
        bool wasRunning = IsRunning;
        Stop();
        SettingsStore.Save(ConfigPath, settings);
        Settings = settings;
        if (wasRunning)
        {
            Start();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
