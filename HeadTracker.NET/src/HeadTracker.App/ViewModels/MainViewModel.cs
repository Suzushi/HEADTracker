using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadTracker.App.Services;

namespace HeadTracker.App.ViewModels;

/// <summary>
/// Main window view model. Polls <see cref="TrackerService"/> on a UI timer
/// (~20 Hz) instead of marshaling every pipeline event, which keeps the render
/// thread free while the pipeline runs at camera rate.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TrackerService _service;
    private readonly DispatcherTimer _uiTimer;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private bool _previewVisible = true;

    public MainViewModel(TrackerService service, Action openSettings, Action exitApplication)
    {
        _service = service;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
        _mirrorOn = service.Mirror;
        LanguageService.LanguageChanged += OnLanguageChanged;
        RefreshStatus();
    }

    private void OnLanguageChanged()
    {
        // Re-translate code-built strings; XAML DynamicResources update themselves.
        OnPropertyChanged(nameof(MirrorButtonText));
        if (IsRunning && !IsStarting)
        {
            ApplyRunningStatus();
        }
        RefreshStatus();
    }

    public TrackerService Service => _service;

    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isStarting;

    [ObservableProperty]
    private bool _mirrorOn;

    public string MirrorButtonText => MirrorOn ? Loc.Tr("mirror_on") : Loc.Tr("mirror_off");

    partial void OnMirrorOnChanged(bool value)
    {
        _service.Mirror = value;
        OnPropertyChanged(nameof(MirrorButtonText));
    }

    [ObservableProperty]
    private string _statusText = Loc.Tr("status_idle");

    /// <summary>Non-empty only while the global re-center hotkey cannot do its job (registration
    /// refused, or this process is not elevated while an elevated game holds the foreground).
    /// Rendered under the hotkey hint; owned by MainWindow, which does the Win32 registration.</summary>
    [ObservableProperty]
    private string _hotkeyWarning = "";

    [ObservableProperty]
    private string _perfText = Loc.Tr("perf_format").Replace("{0}", "--").Replace("{1}", "--").Replace("{2}", "--").Replace("{3}", "0");

    [ObservableProperty]
    private string _outputPoseText = "yaw --  pitch --  roll --";

    [ObservableProperty]
    private string _outputTransText = "x --  y --  z --";

    [ObservableProperty]
    private string _rawPoseText = "raw yaw --  pitch --  roll --";

    [RelayCommand]
    private async Task StartAsync()
    {
        IsStarting = true;
        StatusText = Loc.Tr("status_starting");
        bool ok;
        string? error;
        try
        {
            // Camera open + ONNX session creation take seconds; keep the UI responsive and
            // every other control unreachable behind the modal shield while probing runs.
            ok = await BusyWindow.RunBlockedAsync(() => _service.Start());
            error = _service.LastError;
        }
        finally
        {
            IsStarting = false;
        }

        if (ok)
        {
            IsRunning = true;
            ApplyRunningStatus();
        }
        else
        {
            IsRunning = false;
            StatusText = string.Format(Loc.Tr("status_start_failed"), error);
        }
    }

    private void ApplyRunningStatus()
    {
        StatusText = _service.OutputsDescription is { Length: > 0 } outputs
            ? string.Format(Loc.Tr("status_tracking"), outputs)
            : Loc.Tr("status_tracking_noout");
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        StatusText = Loc.Tr("status_stopping");
        await Task.Run(() => _service.Stop());
        IsRunning = false;
        StatusText = Loc.Tr("status_stopped");
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsStarting)
        {
            return;
        }
        if (IsRunning)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    [RelayCommand]
    private async Task RestartCameraAsync()
    {
        if (IsStarting)
        {
            return;
        }
        IsStarting = true;
        StatusText = Loc.Tr("status_restarting_camera");
        bool ok;
        string? error;
        try
        {
            // Teardown + reopen + ONNX session recreation take seconds; keep the UI alive.
            ok = await BusyWindow.RunBlockedAsync(() => _service.RestartCamera());
            error = _service.LastError;
        }
        finally
        {
            IsStarting = false;
        }

        if (ok)
        {
            IsRunning = true;
            ApplyRunningStatus();
        }
        else
        {
            IsRunning = false;
            StatusText = string.Format(Loc.Tr("status_start_failed"), error);
        }
    }

    [RelayCommand]
    private void Recenter()
    {
        _service.Recenter();
        StatusText = IsRunning ? Loc.Tr("status_recentered") : StatusText;
    }

    /// <summary>
    /// Status feedback for a re-center that was performed off the UI thread, by the global hotkey
    /// thread. Translation happens inside the dispatcher callback too: <c>Loc.Tr</c> reads WPF
    /// application resources, which are not safe to touch from another thread.
    /// </summary>
    public void NotifyRecentered() =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
            StatusText = IsRunning ? Loc.Tr("status_recentered") : StatusText);

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void Exit() => _exitApplication();

    private void OnUiTick(object? sender, EventArgs e) => RefreshStatus();

    /// <summary>Called by the window on visibility/state changes. Stops preview rendering here and
    /// tells the pipeline to skip DrawPreview while the window is hidden (to tray) or minimized.</summary>
    public void SetPreviewVisible(bool visible)
    {
        _previewVisible = visible;
        _service.PreviewEnabled = visible;
    }

    private void RefreshStatus()
    {
        if (IsRunning != _service.IsRunning)
        {
            IsRunning = _service.IsRunning;
        }

        if (IsRunning)
        {
            var (output, rawYpr, rawT) = _service.LatestPoses();
            PerfText = $"[DIAG cap {_service.CaptureFps:F1} read {_service.ReadMs:F0}ms proc {_service.Fps:F1} pms {_service.ProcessMs:F0} res {_service.Resolution} via {_service.CaptureCombo}]  " +
                       string.Format(Loc.Tr("perf_format"),
                       _service.Fps.ToString("F1"),
                       Loc.Tr(_service.FaceTracked ? "yes" : "no"),
                       _service.RmsPx.ToString("F2"),
                       _service.Errors);
            OutputPoseText = $"yaw {output.Yaw,7:F2}  pitch {output.Pitch,7:F2}  roll {output.Roll,7:F2}";
            OutputTransText = $"x {output.Tx,6:F2}  y {output.Ty,6:F2}  z {output.Tz,6:F2}";
            RawPoseText = $"raw yaw {rawYpr.X,6:F1}  pitch {rawYpr.Y,6:F1}  roll {rawYpr.Z,6:F1}  " +
                          $"t=({rawT.X:F2},{rawT.Y:F2},{rawT.Z:F2})";

            if (_previewVisible)
            {
                using var frame = _service.GetPreview();
                if (frame != null)
                {
                    // Preview frames are continuous BGR 8UC3; copy into a frozen Bgr24 source.
                    int stride = (int)frame.Step();
                    var pixels = new byte[stride * frame.Rows];
                    Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
                    var bmp = BitmapSource.Create(frame.Cols, frame.Rows, 96, 96,
                        PixelFormats.Bgr24, null, pixels, stride);
                    bmp.Freeze();
                    Preview = bmp;
                }
            }
        }
        else
        {
            PerfText = Loc.Tr("perf_format").Replace("{0}", "--").Replace("{1}", "--").Replace("{2}", "--").Replace("{3}", "0");
            OutputPoseText = "yaw --  pitch --  roll --";
            OutputTransText = "x --  y --  z --";
            RawPoseText = "raw yaw --  pitch --  roll --";
            Preview = null;
        }
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        LanguageService.LanguageChanged -= OnLanguageChanged;
        _service.Dispose();
    }
}
