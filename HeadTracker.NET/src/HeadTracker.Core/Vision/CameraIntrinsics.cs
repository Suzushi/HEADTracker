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
    public static CameraIntrinsics ForResolution(int width, int height) =>
        ScaleTo(553.61456617, 556.75788726, 308.32781287, 252.73270154, 640, 480, width, height,
            new[] { -0.10055392, 0.19422527, 0.00414563, -0.00049292, -0.02306945 }, isCustom: false);

    /// <summary>
    /// Scales intrinsics captured at (w0,h0) to a live (w,h) frame. Capture modes of one camera
    /// share the horizontal field of view with square pixels, so the focal scales uniformly by
    /// the width ratio and any vertical difference is a symmetric crop: cy shifts by half the
    /// cropped rows. Per-axis scaling (the old behaviour) stretched fy by the height ratio
    /// whenever the aspect changed (4:3 calibration -> 16:9 live), which is what blew the 1080p
    /// reprojection RMS up to ~27px; at equal aspect the crop term is zero and this is identity.
    /// </summary>
    private static CameraIntrinsics ScaleTo(double fx, double fy, double cx, double cy,
        int w0, int h0, int w, int h, double[] distortion, bool isCustom)
    {
        double s = w / (double)w0;
        double croppedRows = h0 * s - h; // >0: live frame is a vertical crop of the scaled calib frame
        return new CameraIntrinsics(fx * s, fy * s, cx * s, cy * s - croppedRows / 2.0, distortion, isCustom);
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

        return ScaleTo(settings.CameraFx, settings.CameraFy, settings.CameraCx, settings.CameraCy,
            settings.CalibratedWidth, settings.CalibratedHeight, width, height,
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
