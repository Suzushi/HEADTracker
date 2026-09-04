using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;
using OpenCvSharp;

namespace HeadTracker.Core.Tests;

/// <summary>M5: charuco calibration service and the custom-intrinsics wiring.</summary>
public class CalibrationTests
{
    private static Mat FrontalBoard(CharucoCalibrator calib)
    {
        using Mat gray = calib.GenerateBoardImage(600, 840, 40);
        var board = new Mat();
        Cv2.CvtColor(gray, board, ColorConversionCodes.GRAY2BGR);
        return board;
    }

    [Fact]
    public void GenerateBoardImage_ProducesRequestedSize()
    {
        var calib = new CharucoCalibrator();
        using Mat img = calib.GenerateBoardImage(600, 840, 40);
        Assert.False(img.Empty());
        Assert.Equal(600, img.Width);
        Assert.Equal(840, img.Height);
    }

    [Fact]
    public void TryCapture_DetectsFrontalBoard_WithoutIdOverflow()
    {
        var calib = new CharucoCalibrator();
        using Mat board = FrontalBoard(calib);

        // A clean frontal board yields many charuco corners; if the rebuilt corner
        // grid were smaller than OpenCV's charuco id range this indexing would throw.
        int corners = calib.TryCapture(board, out Mat ann);
        ann.Dispose();

        Assert.True(corners >= CharucoCalibrator.MinCornersPerFrame, $"expected corners, got {corners}");
        Assert.Equal(1, calib.SampleCount);
    }

    [Fact]
    public void Calibrate_RejectsInsufficientSamples()
    {
        var calib = new CharucoCalibrator();
        using Mat board = FrontalBoard(calib);
        calib.TryCapture(board, out Mat ann);
        ann.Dispose();

        CalibrationResult res = calib.Calibrate(board.Width, board.Height);
        Assert.False(res.Success);
        Assert.Contains("at least", res.Message);
    }

    [Fact]
    public void Calibrate_WithEnoughViews_DoesNotThrow()
    {
        var calib = new CharucoCalibrator();
        using Mat board = FrontalBoard(calib);
        for (int i = 0; i < CharucoCalibrator.MinSamples; i++)
        {
            calib.TryCapture(board, out Mat ann);
            ann.Dispose();
        }
        Assert.True(calib.SampleCount >= CharucoCalibrator.MinSamples);

        // Repeated coplanar views make the solve ill-posed; it must return a graceful
        // result (Success or Failed), never an unhandled exception.
        CalibrationResult res = calib.Calibrate(board.Width, board.Height);
        Assert.NotNull(res);
    }

    [Fact]
    public void FromSettings_FallsBackToLegacy_WhenUncalibrated()
    {
        var s = new TrackerSettings();
        Assert.False(s.HasCustomCalibration);

        CameraIntrinsics ic = CameraIntrinsics.FromSettings(s, 640, 480);
        Assert.False(ic.IsCustom);
        Assert.Equal(553.61456617, ic.Fx, 3);
    }

    [Fact]
    public void FromSettings_UsesCustomK_ScaledByResolution_DistortionFixed()
    {
        var s = new TrackerSettings
        {
            CameraFx = 800, CameraFy = 810, CameraCx = 320, CameraCy = 240,
            DistK1 = 0.1, DistK2 = -0.2, DistP1 = 0.001, DistP2 = -0.001, DistK3 = 0.05,
            CalibratedWidth = 640, CalibratedHeight = 480,
        };
        Assert.True(s.HasCustomCalibration);

        CameraIntrinsics ic = CameraIntrinsics.FromSettings(s, 1280, 960);
        Assert.True(ic.IsCustom);
        Assert.Equal(1600, ic.Fx, 3);   // 800 * (1280/640)
        Assert.Equal(1620, ic.Fy, 3);   // 810 * (960/480)
        Assert.Equal(640, ic.Cx, 3);
        Assert.Equal(480, ic.Cy, 3);
        Assert.Equal(0.1, ic.Distortion[0], 6);  // distortion is resolution-independent
        Assert.Equal(0.05, ic.Distortion[4], 6);
    }

    [Fact]
    public void ClearCalibration_ResetsToLegacy()
    {
        var s = new TrackerSettings
        {
            CameraFx = 800, CameraFy = 810, CalibratedWidth = 640, CalibratedHeight = 480,
        };
        Assert.True(s.HasCustomCalibration);

        s.ClearCalibration();
        Assert.False(s.HasCustomCalibration);
        Assert.Equal(0, s.CameraFx);
        Assert.Equal(0, s.CalibratedWidth);
    }
}
