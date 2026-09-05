using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;

namespace HeadTracker.Core.Tests;

public class PoseMathTests
{
    [Fact]
    public void WrapAngle_FoldsIntoMinusPiPlusPi()
    {
        Assert.Equal(-Math.PI / 2, PoseMath.WrapAngle(3 * Math.PI / 2), 9);
        Assert.Equal(Math.PI / 2, PoseMath.WrapAngle(-3 * Math.PI / 2), 9);
        Assert.Equal(0.5, PoseMath.WrapAngle(0.5), 9);
    }

    [Fact]
    public void Signum_ZeroReturnsPositiveOne_PerLegacy()
    {
        Assert.Equal(1, PoseMath.Signum(0));
        Assert.Equal(1, PoseMath.Signum(3));
        Assert.Equal(-1, PoseMath.Signum(-3));
    }

    [Fact]
    public void Quaternion_IdentityRotation_GivesZeroAngles()
    {
        var ypr = QuatD.FromRotationMatrix(Mat3.Identity).ToYprDegrees();
        Assert.Equal(0, ypr.X, 9);
        Assert.Equal(0, ypr.Y, 9);
        Assert.Equal(0, ypr.Z, 9);
    }

    [Fact]
    public void Quaternion_ZAxis90_MatchesLegacyEulerFormulas()
    {
        // Rz(90 deg); with the legacy quat2eulers formulas this lands in slot X.
        var rz90 = new Mat3(0, -1, 0, 1, 0, 0, 0, 0, 1);
        var ypr = QuatD.FromRotationMatrix(rz90).ToYprDegrees();
        Assert.Equal(90, ypr.X, 6);
        Assert.Equal(0, ypr.Y, 6);
        Assert.Equal(0, ypr.Z, 6);
    }

    [Fact]
    public void Quaternion_XAxis90_MatchesLegacyEulerFormulas()
    {
        var rx90 = new Mat3(1, 0, 0, 0, 0, -1, 0, 1, 0);
        var ypr = QuatD.FromRotationMatrix(rx90).ToYprDegrees();
        Assert.Equal(0, ypr.X, 6);
        Assert.Equal(0, ypr.Y, 6);
        Assert.Equal(90, ypr.Z, 6);
    }

    [Fact]
    public void Mat3_Multiply_RFaceIsOrthogonal()
    {
        var product = Mat3.RFace.Multiply(Mat3.RFace.Transpose());
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double expected = i == j ? 1 : 0;
                Assert.Equal(expected, new[] { product.M00, product.M01, product.M02,
                    product.M10, product.M11, product.M12,
                    product.M20, product.M21, product.M22 }[i * 3 + j], 9);
            }
        }
    }
}

public class AccelaFilterTests
{
    private static AccelaFilter Default() => new(rotSmoothing: 0.08, posSmoothing: 0.03,
        rotDeadzone: 3.0, posDeadzone: 0.03);

    [Fact]
    public void FirstSample_PassesThroughUnchanged()
    {
        var f = Default();
        var (eul, t) = f.Filter(new Vec3(10, -5, 2), new Vec3(0.05, 0.01, -0.2), 0.004);
        Assert.Equal(10, eul.X, 9);
        Assert.Equal(-5, eul.Y, 9);
        Assert.Equal(2, eul.Z, 9);
        Assert.Equal(0.05, t.X, 9);
        Assert.Equal(0.01, t.Y, 9);
        Assert.Equal(-0.2, t.Z, 9);
    }

    [Fact]
    public void MovementInsideDeadzone_DoesNotMoveOutput()
    {
        var f = Default();
        f.Filter(new Vec3(10, 0, 0), new Vec3(0.05, 0, 0), 0.004);
        var (eul, t) = f.Filter(new Vec3(11.5, 0, 0), new Vec3(0.06, 0, 0), 0.004);
        Assert.Equal(10, eul.X, 9);
        Assert.Equal(0.05, t.X, 9);
    }

    [Fact]
    public void RotationWrap_179ToMinus179_IsTreatedAsSmallPositiveStep()
    {
        var f = Default();
        f.Filter(new Vec3(179, 0, 0), Vec3.Zero, 0.004);
        // delta wraps to +2 deg, which is inside the 3 deg rotation deadzone
        var (eul, _) = f.Filter(new Vec3(-179, 0, 0), Vec3.Zero, 0.004);
        Assert.Equal(179, eul.X, 9);
    }

    [Fact]
    public void LargeStep_ConvergesTowardInput()
    {
        var f = Default();
        f.Filter(Vec3.Zero, Vec3.Zero, 0.004);
        var target = new Vec3(20, 0, 0);
        var (eul, _) = (Vec3.Zero, Vec3.Zero);
        for (int i = 0; i < 2000; i++)
        {
            (eul, _) = f.Filter(target, Vec3.Zero, 0.004);
        }
        Assert.True(Math.Abs(eul.X - 20) < 3.01, $"expected convergence near 20, got {eul.X}");
    }

    [Fact]
    public void Center_RestoresFirstRunPassthrough()
    {
        var f = Default();
        f.Filter(new Vec3(5, 0, 0), Vec3.Zero, 0.004);
        f.Center();
        var (eul, _) = f.Filter(new Vec3(30, 0, 0), Vec3.Zero, 0.004);
        Assert.Equal(30, eul.X, 9);
    }
}

public class PoseRemapperTests
{
    [Fact]
    public void FirstPose_CentersToZero()
    {
        var remapper = new PoseRemapper(new TrackerSettings());
        Assert.False(remapper.UseAccelaPath);
        remapper.OnPose(Mat3.Identity, new Vec3(0.1, 0.2, 0.3));
        var pose = remapper.SnapshotUnfiltered()!.Value;
        Assert.Equal(0, pose.Yaw, 9);
        Assert.Equal(0, pose.Pitch, 9);
        Assert.Equal(0, pose.Roll, 9);
        Assert.Equal(0, pose.Tx, 9);
        Assert.Equal(0, pose.Ty, 9);
        Assert.Equal(0, pose.Tz, 9);
    }

    [Fact]
    public void Translation_IsRelativeToCenter_UdpPathUnmapped()
    {
        var remapper = new PoseRemapper(new TrackerSettings());
        remapper.OnPose(Mat3.Identity, new Vec3(0.1, 0.2, 0.3));
        remapper.OnPose(Mat3.Identity, new Vec3(0.2, 0.2, 0.3));
        var pose = remapper.SnapshotUnfiltered()!.Value;
        Assert.Equal(0.1, pose.Tx, 6);
        Assert.Equal(0, pose.Ty, 6);
        Assert.Equal(0, pose.Tz, 6);
    }

    [Fact]
    public void FreetrackPath_ScalesTranslationToOutputBound()
    {
        var settings = new TrackerSettings { UseFt = true };
        var remapper = new PoseRemapper(settings);
        Assert.True(remapper.UseAccelaPath);

        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(Mat3.Identity, new Vec3(settings.InpBoundX, 0, 0));
        var pose = remapper.Tick(0.004)!.Value;
        Assert.Equal(settings.OutBoundX, pose.Tx, 6);

        // beyond the input bound the expo clamps at +/-1
        remapper.OnPose(Mat3.Identity, new Vec3(2 * settings.InpBoundX, 0, 0));
        pose = remapper.Tick(0.004)!.Value;
        Assert.Equal(settings.OutBoundX, pose.Tx, 6);
    }

    [Fact]
    public void FreetrackPath_AppliesCubicExpo()
    {
        var settings = new TrackerSettings { UseFt = true, ExpoTransX = 1.0 };
        var remapper = new PoseRemapper(settings);
        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(Mat3.Identity, new Vec3(0.5 * settings.InpBoundX, 0, 0));
        var pose = remapper.Tick(0.004)!.Value;
        Assert.Equal(0.125 * settings.OutBoundX, pose.Tx, 6);
    }

    [Fact]
    public void FreetrackPath_ScalesYawToOutputBound()
    {
        var settings = new TrackerSettings { UseFt = true };
        var remapper = new PoseRemapper(settings);
        var rz90 = new Mat3(0, -1, 0, 1, 0, 0, 0, 0, 1);
        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(rz90, Vec3.Zero);
        var pose = remapper.Tick(0.004)!.Value;
        // 90 deg raw yaw exceeds inp_bound_yaw, so it clamps to the full output bound
        Assert.Equal(settings.OutBoundYaw, pose.Yaw, 6);
    }

    [Fact]
    public void InvertTransX_FlipsFreetrackOutput_SeenByUiAndGame()
    {
        // The flip lives on the remapper OUTPUT -- the same value the main-window x/y/z
        // readout and Publish()->senders consume -- so a ticked box must show up here.
        // Regression guard: it used to live inside a sender, leaving this (and the UI)
        // un-flipped, which is why the checkbox looked dead.
        var settings = new TrackerSettings { UseFt = true, InvertTransX = true };
        var remapper = new PoseRemapper(settings);
        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(Mat3.Identity, new Vec3(settings.InpBoundX, 0, 0));
        var pose = remapper.Tick(0.004)!.Value;
        Assert.Equal(-settings.OutBoundX, pose.Tx, 6); // mapped to +OutBoundX, then flipped
    }

    [Fact]
    public void InvertEulYaw_FlipsYawOutput()
    {
        var settings = new TrackerSettings { UseFt = true, InvertEulYaw = true };
        var remapper = new PoseRemapper(settings);
        var rz90 = new Mat3(0, -1, 0, 1, 0, 0, 0, 0, 1);
        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(rz90, Vec3.Zero);
        var pose = remapper.Tick(0.004)!.Value;
        Assert.Equal(-settings.OutBoundYaw, pose.Yaw, 6); // clamped to +bound, then flipped
    }

    [Fact]
    public void InvertTransY_FlipsUdpPathOutput_SeenByUiAndGame()
    {
        var settings = new TrackerSettings { InvertTransY = true }; // UseFt off: UDP-only path
        var remapper = new PoseRemapper(settings);
        Assert.False(remapper.UseAccelaPath);
        remapper.OnPose(Mat3.Identity, new Vec3(0.1, 0.2, 0.3)); // center
        remapper.OnPose(Mat3.Identity, new Vec3(0.1, 0.3, 0.3)); // +0.1 m in Y
        var pose = remapper.SnapshotUnfiltered()!.Value;
        Assert.Equal(0, pose.Tx, 6);
        Assert.Equal(-0.1, pose.Ty, 6); // flipped by InvertTransY
        Assert.Equal(0, pose.Tz, 6);
    }

    [Fact]
    public void ResetCenter_NextPoseBecomesNewCenter()
    {
        var remapper = new PoseRemapper(new TrackerSettings());
        remapper.OnPose(Mat3.Identity, Vec3.Zero);
        remapper.OnPose(Mat3.Identity, new Vec3(0.2, 0, 0));
        remapper.ResetCenter();
        remapper.OnPose(Mat3.Identity, new Vec3(0.2, 0, 0));
        var pose = remapper.SnapshotUnfiltered()!.Value;
        Assert.Equal(0, pose.Tx, 9);
    }

    [Fact]
    public void Tick_BeforeAnyPose_ReturnsNull()
    {
        var remapper = new PoseRemapper(new TrackerSettings());
        Assert.Null(remapper.Tick(0.004));
        Assert.Null(remapper.SnapshotUnfiltered());
    }

    [Fact]
    public void OneEuro_SmoothsHeadAngles_IndependentOfMappedGain()
    {
        // One-Euro's cutoff is minCutoff + beta*|dx/dt|, so it is NOT scale invariant. It used to
        // run after the bounds/expo gain, which multiplied the derivative by that gain (~6.9x with
        // the default bounds) and pushed the cutoff up with it until the filter passed the at-rest
        // buzz through untouched -- jitter reached the game however strong the settings looked.
        // Filtering the head angles instead leaves the gain to do nothing but scale the result.
        double[] headYaw = { 0.0, 0.4, -0.3, 0.5, 0.1, -0.2, 0.3 };

        double[] raw = Run(headYaw, oneEuro: false, outBoundYaw: 30);
        double[] smoothed = Run(headYaw, oneEuro: true, outBoundYaw: 30);
        double[] doubledGain = Run(headYaw, oneEuro: true, outBoundYaw: 60);

        // The gain stage is now downstream of the filter: double it and the output doubles exactly.
        for (int i = 0; i < headYaw.Length; i++)
        {
            Assert.Equal(smoothed[i] * 2, doubledGain[i], 9);
        }

        // And the filter really does attenuate a jittery sequence rather than passing it through.
        Assert.True(Spread(smoothed) < Spread(raw),
            $"one-euro did not attenuate: spread {Spread(smoothed):F4} vs raw {Spread(raw):F4}");
    }

    [Fact]
    public void OneEuro_SmoothsTranslation_WithItsOwnCutoff()
    {
        // Position is in metres and moves orders of magnitude slower than the head rotates, so it
        // gets its own cutoff/beta. Prove the translation axes are filtered at all and that they
        // answer to one_euro_pos_min_cutoff rather than to the rotation setting.
        double[] slideMetres = { 0.0, 0.004, -0.003, 0.005, 0.001, -0.002, 0.003 };

        double[] RunPos(double posMinCutoff)
        {
            var settings = new TrackerSettings
            {
                UseFt = true,
                UseAccela = false,
                UseOneEuro = true,
                OneEuroPosMinCutoff = posMinCutoff,
            };
            var remapper = new PoseRemapper(settings);
            var result = new double[slideMetres.Length];
            for (int i = 0; i < slideMetres.Length; i++)
            {
                remapper.OnPose(Mat3.Identity, new Vec3(slideMetres[i], 0, 0), 1.0 / 30.0);
                result[i] = remapper.Tick(0.004)!.Value.Tx;
            }
            return result;
        }

        double spreadSteady = Spread(RunPos(1.0));   // configured default
        double spreadWideOpen = Spread(RunPos(40.0)); // cutoff above the sample rate: no smoothing
        Assert.True(spreadSteady < spreadWideOpen,
            $"one_euro_pos_min_cutoff is not wired to the translation axes: {spreadSteady:F6} vs {spreadWideOpen:F6}");
    }

    /// <summary>Runs a yaw sequence through the mapped (freetrack) path at 30 Hz.</summary>
    private static double[] Run(double[] headYaw, bool oneEuro, double outBoundYaw)
    {
        var settings = new TrackerSettings
        {
            UseFt = true,        // take the mapped path: bounds + expo
            UseAccela = false,   // so Tick hands back the mapped pose untouched
            UseOneEuro = oneEuro,
            OutBoundYaw = outBoundYaw,
        };
        var remapper = new PoseRemapper(settings);
        var result = new double[headYaw.Length];
        for (int i = 0; i < headYaw.Length; i++)
        {
            double a = headYaw[i] * Math.PI / 180.0;
            remapper.OnPose(new Mat3(Math.Cos(a), -Math.Sin(a), 0, Math.Sin(a), Math.Cos(a), 0, 0, 0, 1),
                Vec3.Zero, 1.0 / 30.0);
            result[i] = remapper.Tick(0.004)!.Value.Yaw;
        }
        return result;
    }

    private static double Spread(double[] v) => v.Max() - v.Min();
}

public class CameraIntrinsicsTests
{
    [Fact]
    public void BaseResolution_MatchesLegacyPs3EyeCalibration()
    {
        var k = CameraIntrinsics.ForResolution(640, 480);
        Assert.Equal(553.61456617, k.Fx, 5);
        Assert.Equal(556.75788726, k.Fy, 5);
        Assert.Equal(308.32781287, k.Cx, 5);
        Assert.Equal(252.73270154, k.Cy, 5);
    }

    [Fact]
    public void LargerResolution_ScalesProportionally()
    {
        var k = CameraIntrinsics.ForResolution(1280, 960);
        Assert.Equal(553.61456617 * 2, k.Fx, 5);
        Assert.Equal(252.73270154 * 2, k.Cy, 5);
    }

    [Fact]
    public void AspectChange_KeepsFocalUniformAndShiftsCyByHalfCrop()
    {
        // 16:9 live from the 4:3 calibration: focal scales by the WIDTH ratio only (per-axis
        // scaling stretched fy by 1080/480 and blew the 1080p reprojection RMS up to ~27px),
        // and cy shifts by half the vertically cropped rows (480*3-1080=360).
        var k = CameraIntrinsics.ForResolution(1920, 1080);
        const double s = 3.0;
        Assert.Equal(553.61456617 * s, k.Fx, 5);
        Assert.Equal(556.75788726 * s, k.Fy, 5);
        Assert.Equal(308.32781287 * s, k.Cx, 5);
        Assert.Equal(252.73270154 * s - 180.0, k.Cy, 5);
    }

    [Fact]
    public void CustomCalibration_UsesSameCropModel()
    {
        var s = new TrackerSettings
        {
            CameraFx = 1000,
            CameraFy = 1000,
            CameraCx = 640,
            CameraCy = 360,
            CalibratedWidth = 1280,
            CalibratedHeight = 720,
        };
        var k = CameraIntrinsics.FromSettings(s, 1920, 1080);
        Assert.Equal(1500, k.Fx, 5);
        Assert.Equal(1500, k.Fy, 5);
        Assert.Equal(960, k.Cx, 5);
        Assert.Equal(540, k.Cy, 5); // same aspect: no crop shift
        Assert.True(k.IsCustom);
    }
}

public class OneEuroFilterTests
{
    [Fact]
    public void FirstSample_PassesThroughUnchanged()
    {
        var f = new OneEuroFilter(minCutoff: 1.2, beta: 0.25);
        Assert.Equal(7.5, f.Filter(7.5, 1.0 / 30.0), 9);
    }

    [Fact]
    public void Reset_RestoresFirstRunPassthrough()
    {
        var f = new OneEuroFilter(1.2, 0.25);
        f.Filter(0, 1.0 / 30.0);
        f.Filter(10, 1.0 / 30.0);
        f.Reset();
        Assert.Equal(-4, f.Filter(-4, 1.0 / 30.0), 9);
    }

    [Fact]
    public void ConstantInput_ConvergesToThatConstant()
    {
        var f = new OneEuroFilter(1.2, 0.25);
        double y = 0;
        for (int i = 0; i < 600; i++)
        {
            y = f.Filter(20, 1.0 / 60.0);
        }
        Assert.Equal(20, y, 3);
    }

    [Fact]
    public void AlternatingNoise_OutputVarianceIsReduced()
    {
        // A still gaze with +/-1 deg frame-to-frame noise: the low cutoff must pull the
        // output far tighter than the raw input. This is the at-rest "buzz" the filter kills.
        var f = new OneEuroFilter(minCutoff: 1.2, beta: 0.0);
        var raw = new double[400];
        var filtered = new double[400];
        for (int i = 0; i < 400; i++)
        {
            raw[i] = i % 2 == 0 ? 1.0 : -1.0;
            filtered[i] = f.Filter(raw[i], 1.0 / 30.0);
        }
        double rawSd = StdDev(raw[100..]);
        double filteredSd = StdDev(filtered[100..]);
        Assert.True(filteredSd < rawSd * 0.5,
            $"expected filtered jitter well below raw; raw={rawSd}, filtered={filteredSd}");
    }

    [Fact]
    public void HighBeta_TracksRampWithLessLagThanLowBeta()
    {
        // The adaptive property: on a moving signal a larger beta raises the cutoff with
        // speed, so the filter follows the ramp with markedly less lag than a fixed (beta=0)
        // low-pass -- low latency during turns without giving up at-rest smoothing.
        double lagHigh = RampLag(beta: 2.0);
        double lagLow = RampLag(beta: 0.0);
        Assert.True(lagHigh < lagLow, $"high beta should lag less on a ramp: high={lagHigh}, low={lagLow}");
    }

    private static double RampLag(double beta)
    {
        var f = new OneEuroFilter(1.2, beta);
        double x = 0, y = 0;
        for (int i = 0; i < 300; i++)
        {
            x += 2.0;
            y = f.Filter(x, 1.0 / 60.0);
        }
        return Math.Abs(x - y);
    }

    private static double StdDev(double[] v)
    {
        double mean = 0;
        foreach (var x in v)
        {
            mean += x;
        }
        mean /= v.Length;
        double acc = 0;
        foreach (var x in v)
        {
            acc += (x - mean) * (x - mean);
        }
        return Math.Sqrt(acc / v.Length);
    }
}
