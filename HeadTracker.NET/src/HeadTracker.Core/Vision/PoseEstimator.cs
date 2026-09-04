using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>Result of the PnP pose solve: camera-frame rotation/translation and fit quality.</summary>
public readonly record struct PnpResult(bool Success, Mat3 R, Vec3 T, double ReprojectionRmsPx);

/// <summary>
/// Head pose from 2-D/3-D landmark correspondence via solvePnP. Replaces the
/// legacy Ceres bundle adjustment with solvePnP + reprojection-error gating
/// (iterative first, RANSAC fallback), per the rewrite plan.
/// </summary>
public sealed class PoseEstimator
{
    // Legacy stable-point subset of the 66 landmarks.
    private static readonly int[] Indices =
    {
        0, 1, 15, 16, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 39, 42, 45,
    };

    private readonly Mat _kMat;
    private readonly Mat _dMat;

    // Previous frame's PnP solution, reused as the extrinsic guess for the next
    // iterative solve. Seeding the solver keeps it temporally continuous; without
    // it solvePnP restarts from the frontal guess every frame and, on a near
    // symmetric frontal face, can flip between the two pitch solutions -- the
    // up/down "buzz" seen when looking straight at the screen.
    private Mat? _prevRvec;
    private Mat? _prevTvec;

    /// <summary>Reject solutions whose reprojection RMS exceeds this many pixels.</summary>
    public double MaxRmsPx { get; set; } = 4.0;

    public PoseEstimator(CameraIntrinsics intrinsics)
    {
        _kMat = intrinsics.KMat();
        _dMat = intrinsics.DMat();
    }

    /// <summary>Drop the temporal guess so the next solve starts fresh (face re-acquisition).</summary>
    public void Reset()
    {
        _prevRvec?.Dispose();
        _prevTvec?.Dispose();
        _prevRvec = null;
        _prevTvec = null;
    }

    public PnpResult Solve(Point2f[] points2D, Vec3[] modelPoints3D)
    {
        if (points2D.Length < Indices.Length || modelPoints3D.Length < Indices.Length)
        {
            return new PnpResult(false, Mat3.Identity, Vec3.Zero, double.MaxValue);
        }

        var img = new Point2f[Indices.Length];
        var obj = new Point3f[Indices.Length];
        for (int i = 0; i < Indices.Length; i++)
        {
            int idx = Indices[i];
            img[i] = points2D[idx];
            var p = modelPoints3D[idx];
            obj[i] = new Point3f((float)p.X, (float)p.Y, (float)p.Z);
        }

        // First attempt: iterative refinement from the default (frontal) guess.
        var result = SolveWith(obj, img, useRansac: false);
        if (result.Success && result.ReprojectionRmsPx <= MaxRmsPx)
        {
            return result;
        }

        // Fallback: RANSAC for outlier-heavy landmark sets.
        var ransac = SolveWith(obj, img, useRansac: true);
        return ransac.Success ? ransac : result;
    }

    private PnpResult SolveWith(Point3f[] obj, Point2f[] img, bool useRansac)
    {
        var rvec = new Mat();
        var tvec = new Mat();
        using var objMat = ToMat(obj);
        using var imgMat = ToMat(img);
        try
        {
            if (useRansac)
            {
                Cv2.SolvePnPRansac(objMat, imgMat, _kMat, _dMat, rvec, tvec);
            }
            else
            {
                // Seed the iterative solver with the previous frame's pose when we
                // have one; this is what keeps the solution continuous frame-to-frame.
                bool guess = _prevRvec != null && _prevTvec != null;
                if (guess)
                {
                    _prevRvec!.CopyTo(rvec);
                    _prevTvec!.CopyTo(tvec);
                }
                Cv2.SolvePnP(objMat, imgMat, _kMat, _dMat, rvec, tvec,
                    useExtrinsicGuess: guess, flags: SolvePnPMethod.Iterative);
            }
            if (rvec.Empty() || tvec.Empty())
            {
                rvec.Dispose();
                tvec.Dispose();
                return new PnpResult(false, Mat3.Identity, Vec3.Zero, double.MaxValue);
            }

            double rms = ReprojectionRms(objMat, img, rvec, tvec);

            using var rotMat = new Mat();
            Cv2.Rodrigues(rvec, rotMat);
            var r = new Mat3(
                rotMat.At<double>(0, 0), rotMat.At<double>(0, 1), rotMat.At<double>(0, 2),
                rotMat.At<double>(1, 0), rotMat.At<double>(1, 1), rotMat.At<double>(1, 2),
                rotMat.At<double>(2, 0), rotMat.At<double>(2, 1), rotMat.At<double>(2, 2));
            var t = new Vec3(tvec.At<double>(0), tvec.At<double>(1), tvec.At<double>(2));

            // Remember this solution as the next frame's extrinsic guess.
            _prevRvec?.Dispose();
            _prevTvec?.Dispose();
            _prevRvec = rvec.Clone();
            _prevTvec = tvec.Clone();

            rvec.Dispose();
            tvec.Dispose();
            return new PnpResult(true, r, t, rms);
        }
        catch (OpenCVException)
        {
            rvec.Dispose();
            tvec.Dispose();
            return new PnpResult(false, Mat3.Identity, Vec3.Zero, double.MaxValue);
        }
    }

    private double ReprojectionRms(Mat objMat, Point2f[] img, Mat rvec, Mat tvec)
    {
        using var projMat = new Mat();
        Cv2.ProjectPoints(objMat, rvec, tvec, _kMat, _dMat, projMat);
        projMat.GetArray(out Point2f[] projected);
        double sum = 0;
        for (int i = 0; i < img.Length; i++)
        {
            double dx = projected[i].X - img[i].X;
            double dy = projected[i].Y - img[i].Y;
            sum += dx * dx + dy * dy;
        }
        return Math.Sqrt(sum / img.Length);
    }

    /// <summary>Copies a contiguous point array into an Nx1 CV_32F Mat (2 or 3 channels).</summary>
    private static unsafe Mat ToMat<T>(T[] points) where T : unmanaged
    {
        int bytes = points.Length * sizeof(T);
        int channels = sizeof(T) / sizeof(float);
        var mat = new Mat(points.Length, 1, MatType.CV_32FC(channels));
        fixed (T* p = points)
        {
            Buffer.MemoryCopy(p, (void*)mat.Data, bytes, bytes);
        }
        return mat;
    }
}
