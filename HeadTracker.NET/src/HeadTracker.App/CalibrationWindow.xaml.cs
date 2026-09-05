using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeadTracker.App.Services;
using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;
using Microsoft.Win32;
using OpenCvSharp;

namespace HeadTracker.App;

/// <summary>
/// Charuco camera-calibration wizard. It owns the camera exclusively (the tracking
/// pipeline is stopped while it is open), shows a live charuco-corner preview, lets
/// the user capture the board from several angles, solves K/D, and writes the result
/// into config.yaml. On close the camera is released and tracking is resumed if it
/// was running before.
/// </summary>
public partial class CalibrationWindow : System.Windows.Window
{
    private readonly TrackerService _service;
    private readonly CharucoCalibrator _calib = new();
    private readonly bool _wasRunning;

    private CameraCapture? _camera;
    private DispatcherTimer? _timer;
    private CalibrationResult? _result;

    public CalibrationWindow(TrackerService service)
    {
        InitializeComponent();
        _service = service;
        _wasRunning = service.IsRunning;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Exclusive camera access: stop the tracking pipeline before opening our own.
        _service.Stop();

        var s = _service.Settings;
        _camera = new CameraCapture();
        if (!_camera.Open(s.CameraId, s.CaptureWidth, s.CaptureHeight, s.Fps, s.EnableAutoExpo, s.CameraGain, s.CameraExpo, s.CaptureApi, s.CaptureFourcc))
        {
            _camera.Dispose();
            _camera = null;
            LiveStatus.Text = Loc.Tr("calib_camera_failed");
            PrintBoardButton.IsEnabled = false;
            CaptureButton.IsEnabled = false;
            CalibrateButton.IsEnabled = false;
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += OnFrame;
        _timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var camera = _camera;
        if (camera == null)
        {
            return;
        }

        using var frame = camera.GrabLatest();
        if (frame == null)
        {
            return;
        }

        int corners = _calib.Peek(frame, out Mat ann);
        using (ann)
        {
            PreviewImage.Source = ToBitmap(ann);
        }

        LiveStatus.Text = corners >= CharucoCalibrator.MinCornersPerFrame
            ? string.Format(Loc.Tr("calib_corners_ok"), corners)
            : string.Format(Loc.Tr("calib_corners_none"), corners);
    }

    private void OnPrintBoard(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "charuco_board.png" };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // ~A4 at 150 dpi; OpenCV keeps the squares square, so print at 100% scale.
            using Mat board = _calib.GenerateBoardImage(1240, 1754, 60);
            Cv2.ImWrite(dlg.FileName, board);
            MessageBox.Show(this, Loc.Tr("calib_board_saved"), Loc.Tr("calib_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Tr("calib_title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        if (_calib.CaptureLast())
        {
            SamplesList.Items.Add(string.Format(Loc.Tr("calib_sample_item"), _calib.SampleCount, _calib.LastCornerCount));
            SamplesList.ScrollIntoView(SamplesList.Items[^1]);
            SamplesText.Text = string.Format(Loc.Tr("calib_samples"), _calib.SampleCount);
            // A new sample invalidates any previous solve.
            _result = null;
            SaveButton.IsEnabled = false;
            ResultText.Text = "";
        }
        else
        {
            ResultText.Text = Loc.Tr("calib_capture_failed");
        }
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        if (_camera == null)
        {
            return;
        }

        CalibrateButton.IsEnabled = false;
        CalibrationResult res;
        try
        {
            res = _calib.Calibrate(_camera.FrameWidth, _camera.FrameHeight);
        }
        finally
        {
            CalibrateButton.IsEnabled = true;
        }

        if (res.Success)
        {
            _result = res;
            ResultText.Text = string.Format(Loc.Tr("calib_result_ok"), res.Rms, res.Fx, res.Fy);
            SaveButton.IsEnabled = true;
        }
        else
        {
            _result = null;
            SaveButton.IsEnabled = false;
            ResultText.Text = string.Format(Loc.Tr("calib_result_fail"), res.Message);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_result == null || _camera == null)
        {
            return;
        }

        var s = _service.Settings;
        s.CameraFx = _result.Fx;
        s.CameraFy = _result.Fy;
        s.CameraCx = _result.Cx;
        s.CameraCy = _result.Cy;
        s.DistK1 = _result.Distortion[0];
        s.DistK2 = _result.Distortion[1];
        s.DistP1 = _result.Distortion[2];
        s.DistP2 = _result.Distortion[3];
        s.DistK3 = _result.Distortion[4];
        s.CalibratedWidth = _camera.FrameWidth;
        s.CalibratedHeight = _camera.FrameHeight;
        s.CalibrationRms = _result.Rms;

        if (!TrySaveConfig())
        {
            return;
        }
        MessageBox.Show(this, Loc.Tr("calib_saved"), Loc.Tr("calib_title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _service.Settings.ClearCalibration();
        if (!TrySaveConfig())
        {
            return;
        }
        ResultText.Text = Loc.Tr("calib_cleared");
    }

    private bool TrySaveConfig()
    {
        try
        {
            SettingsStore.Save(_service.ConfigPath, _service.Settings);
            return true;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Tr("calib_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _camera?.Dispose();
        _camera = null;

        if (_wasRunning)
        {
            // The camera was just released; give DirectShow a moment before the
            // pipeline reopens it, then resume tracking in the background.
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                _service.Start();
            });
        }
    }

    private static BitmapSource ToBitmap(Mat mat)
    {
        int stride = (int)mat.Step();
        var pixels = new byte[stride * mat.Rows];
        Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        var bmp = BitmapSource.Create(mat.Cols, mat.Rows, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
