using HeadTracker.Core.Configuration;
using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Pinhole camera intrinsics. The legacy app hardcodes PS3Eye calibration at
/// 640x480; other resolutions are obtained by proportional scaling.
/// </summary>
public sealed class CameraIntrinsics
{
    public double Fx { get; }
    public double Fy { get; }
    public double Cx { get; }
    public double Cy { get; }
    public double[] Distortion { get; }

    /// <summary>True when this came from a user charuco calibration rather than the legacy defaults.</summary>
    public bool IsCustom { get; }

    private CameraIntrinsics(double fx, double fy, double cx, double cy, double[] distortion, bool isCustom = false)
    {
        Fx = fx;
        Fy = fy;
        Cx = cx;
        Cy = cy;
        Distortion = distortion;
        IsCustom = isCustom;
    }

    /// <summary>Legacy PS3Eye calibration scaled to the actual capture resolution.</summary>
    public static CameraIntrinsics ForResolution(int width, int height)
    {
        double sx = width / 640.0;
        double sy = height / 480.0;
        return new CameraIntrinsics(
            553.61456617 * sx,
            556.75788726 * sy,
            308.32781287 * sx,
            252.73270154 * sy,
            new[] { -0.10055392, 0.19422527, 0.00414563, -0.00049292, -0.02306945 });
    }

    /// <summary>
    /// Intrinsics for the live frame: the user's charuco calibration when present
    /// (K scaled from the calibrated resolution; distortion is resolution-independent),
    /// otherwise the legacy PS3Eye defaults.
    /// </summary>
    public static CameraIntrinsics FromSettings(TrackerSettings settings, int width, int height)
    {
        if (!settings.HasCustomCalibration)
        {
            return ForResolution(width, height);
        }

        double sx = width / (double)settings.CalibratedWidth;
        double sy = height / (double)settings.CalibratedHeight;
        return new CameraIntrinsics(
            settings.CameraFx * sx,
            settings.CameraFy * sy,
            settings.CameraCx * sx,
            settings.CameraCy * sy,
            new[] { settings.DistK1, settings.DistK2, settings.DistP1, settings.DistP2, settings.DistK3 },
            isCustom: true);
    }

    public Mat KMat()
    {
        var k = new Mat(3, 3, MatType.CV_64F);
        k.Set<double>(0, 0, Fx);
        k.Set<double>(1, 1, Fy);
        k.Set<double>(0, 2, Cx);
        k.Set<double>(1, 2, Cy);
        k.Set<double>(2, 2, 1.0);
        return k;
    }

    public Mat DMat()
    {
        var d = new Mat(1, Distortion.Length, MatType.CV_64F);
        for (int i = 0; i < Distortion.Length; i++)
        {
            d.Set<double>(0, i, Distortion[i]);
        }
        return d;
    }
}
