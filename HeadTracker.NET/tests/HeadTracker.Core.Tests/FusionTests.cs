using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;
using OpenCvSharp;

namespace HeadTracker.Core.Tests;

/// <summary>M4: EKF fusion port, FSA-Net port and the new rotation-math helpers.</summary>
public class FusionTests
{
    private static TrackerSettings TestSettings() => new()
    {
        UseEkf = true,
        CovQLm = 0.001,
        CovQFsa = 0.083,
        CovT = 0.0105,
        CovV = 1.748,
        CovW = 7.63,
        EkfPredictDt = 0.01,
        CervicalFaceModelX = 0.16,
        CervicalFaceModelY = 0.16,
    };

    // --- math helpers --------------------------------------------------------

    [Fact]
    public void Rz90_ToQuat_Back_ToMatrix_RoundTrips()
    {
        var rz = Mat3.Rz(Math.PI / 2);
        var q = QuatD.FromRotationMatrix(rz);
        var back = q.ToRotationMatrix();
        Assert.True(Math.Abs(back.M00 - rz.M00) < 1e-9);
        Assert.True(Math.Abs(back.M01 - rz.M01) < 1e-9);
        Assert.True(Math.Abs(back.M10 - rz.M10) < 1e-9);
        Assert.True(Math.Abs(back.M22 - rz.M22) < 1e-9);
    }

    [Fact]
    public void AxisAngle_Helpers_Match_Expected_Layouts()
    {
        // Rx(90 deg) maps +Y onto +Z.
        var v = Mat3.Rx(Math.PI / 2).Multiply(new Vec3(0, 1, 0));
        Assert.True(Math.Abs(v.Z - 1) < 1e-9);
        // Ry(90 deg) maps +Z onto +X.
        v = Mat3.Ry(Math.PI / 2).Multiply(new Vec3(0, 0, 1));
        Assert.True(Math.Abs(v.X - 1) < 1e-9);
        // Rz(90 deg) maps +X onto +Y.
        v = Mat3.Rz(Math.PI / 2).Multiply(new Vec3(1, 0, 0));
        Assert.True(Math.Abs(v.Y - 1) < 1e-9);
    }

    // --- EKF -----------------------------------------------------------------

    [Fact]
    public void Ekf_FirstUpdate_ReturnsMeasurementUnchanged()
    {
        var ekf = new EkfFusion(TestSettings());
        var q = QuatD.FromRotationMatrix(Mat3.Rz(0.3));
        var t = new Vec3(0.02, -0.01, 0.55);

        var (qOut, tOut) = ekf.UpdateRawPose(0.0, q, t, 0);

        Assert.True(ekf.Initialized);
        Assert.Equal(t.X, tOut.X, 9);
        Assert.Equal(t.Y, tOut.Y, 9);
        Assert.Equal(t.Z, tOut.Z, 9);
        Assert.True(Math.Abs(qOut.W - q.W) < 1e-12);
    }

    [Fact]
    public void Ekf_StaticMeasurement_Converges()
    {
        var ekf = new EkfFusion(TestSettings());
        var q = QuatD.Identity;
        var t = new Vec3(0, 0, 0.5);

        for (int i = 0; i < 200; i++)
        {
            ekf.UpdateRawPose(i * 0.03, q, t, 0);
        }

        var (qOut, tOut) = ekf.RealtimePose;
        Assert.True(Math.Abs(tOut.Z - 0.5) < 1e-3);
        Assert.True(Math.Abs(tOut.X) < 1e-3);
        // Quaternion must stay (near) identity: |x|,|y|,|z| tiny, w ~ +1.
        Assert.True(Math.Abs(qOut.X) < 1e-3 && Math.Abs(qOut.Y) < 1e-3 && Math.Abs(qOut.Z) < 1e-3);
        Assert.True(qOut.W > 0.999);
        // Velocities must have decayed to ~zero for a static target.
        Assert.True(ekf.AngularVelocity.X + ekf.AngularVelocity.Y + ekf.AngularVelocity.Z < 1e-2);
    }

    [Fact]
    public void Ekf_RotatingMeasurement_EstimatesAngularVelocity()
    {
        var ekf = new EkfFusion(TestSettings());
        var t = new Vec3(0, 0, 0.5);

        // 1 rad/s about the camera Z axis, sampled every 50 ms.
        for (int i = 0; i <= 60; i++)
        {
            double angle = i * 0.05;
            ekf.UpdateRawPose(angle, QuatD.FromRotationMatrix(Mat3.Rz(angle)), t, 0);
        }

        var w = ekf.AngularVelocity;
        Assert.True(Math.Abs(w.Z) > 0.4, $"w.Z too small: {w.Z}");
        Assert.True(Math.Abs(w.Z) < 1.8, $"w.Z too large: {w.Z}");
        Assert.True(Math.Abs(w.X) < 0.5 && Math.Abs(w.Y) < 0.5);

        // Predicting 100 ms ahead must advance the rotation further.
        var lastAngle = 60 * 0.05;
        var (qPred, _) = ekf.Predict(lastAngle + 0.1);
        var qLast = QuatD.FromRotationMatrix(Mat3.Rz(lastAngle));
        double dot = Math.Abs(qPred.W * qLast.W + qPred.X * qLast.X + qPred.Y * qLast.Y + qPred.Z * qLast.Z);
        Assert.True(dot < 0.999, "prediction did not advance past the last measurement");
        Assert.True(dot > 0.98, $"prediction overshot: dot={dot}");
    }

    [Fact]
    public void Ekf_GroundSpeedUpdate_StaysFinite()
    {
        var ekf = new EkfFusion(TestSettings());
        ekf.UpdateRawPose(0, QuatD.Identity, new Vec3(0, 0, 0.5), 0);
        for (int i = 1; i <= 20; i++)
        {
            ekf.UpdateRawPose(i * 0.03, QuatD.Identity, new Vec3(0, 0, 0.5), 0);
            var (q, t) = ekf.UpdateGroundSpeed(i * 0.03, new Vec3(0.05, -0.02, 0));
            Assert.True(double.IsFinite(q.W) && double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.Z));
        }
    }

    // --- FSA-Net ---------------------------------------------------------------

    [Fact]
    public void FsaNet_Inference_ReturnsFiniteRadians()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "fsanet_capsule.onnx");
        Assert.True(File.Exists(model), $"model missing at {model}");

        using var fsa = new FSANet(model);
        using var crop = new Mat(120, 90, MatType.CV_8UC3, new Scalar(180, 140, 110));
        Cv2.Rectangle(crop, new Rect(20, 30, 50, 60), new Scalar(60, 70, 90), -1);

        var ypr = fsa.Infer(crop);
        Assert.True(double.IsFinite(ypr.X) && double.IsFinite(ypr.Y) && double.IsFinite(ypr.Z));
        // Model outputs degrees converted to radians; any sane pose is within +/-180 deg.
        Assert.True(Math.Abs(ypr.X) <= Math.PI + 1e-6);
        Assert.True(Math.Abs(ypr.Y) <= Math.PI + 1e-6);
        Assert.True(Math.Abs(ypr.Z) <= Math.PI + 1e-6);
    }

    [Fact]
    public void FsaNet_SameInput_IsDeterministic()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "fsanet_capsule.onnx");
        using var fsa = new FSANet(model);
        using var crop = new Mat(64, 64, MatType.CV_8UC3, new Scalar(100, 150, 200));

        var a = fsa.Infer(crop);
        var b = fsa.Infer(crop);
        Assert.True(Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Z - b.Z) < 1e-6);
    }
}
