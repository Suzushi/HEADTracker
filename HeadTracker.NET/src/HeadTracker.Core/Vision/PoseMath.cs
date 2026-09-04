namespace HeadTracker.Core.Vision;

/// <summary>Minimal double-precision 3-vector used by the pose pipeline.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
}

/// <summary>Minimal double-precision 3x3 matrix (row-major), enough for rotations.</summary>
public readonly record struct Mat3(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>Legacy Rface (face frame -> camera frame).</summary>
    public static readonly Mat3 RFace = new(0, 1, 0, 0, 0, -1, -1, 0, 0);

    /// <summary>Legacy Rcam.</summary>
    public static readonly Mat3 RCam = new(0, 0, -1, -1, 0, 0, 0, 1, 0);

    public Mat3 Multiply(in Mat3 b) => new(
        M00 * b.M00 + M01 * b.M10 + M02 * b.M20,
        M00 * b.M01 + M01 * b.M11 + M02 * b.M21,
        M00 * b.M02 + M01 * b.M12 + M02 * b.M22,
        M10 * b.M00 + M11 * b.M10 + M12 * b.M20,
        M10 * b.M01 + M11 * b.M11 + M12 * b.M21,
        M10 * b.M02 + M11 * b.M12 + M12 * b.M22,
        M20 * b.M00 + M21 * b.M10 + M22 * b.M20,
        M20 * b.M01 + M21 * b.M11 + M22 * b.M21,
        M20 * b.M02 + M21 * b.M12 + M22 * b.M22);

    public Vec3 Multiply(in Vec3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    public Mat3 Transpose() => new(
        M00, M10, M20,
        M01, M11, M21,
        M02, M12, M22);

    /// <summary>Rotation about X (legacy Eigen::AngleAxisd(a, UnitX)).</summary>
    public static Mat3 Rx(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new Mat3(1, 0, 0, 0, c, -s, 0, s, c);
    }

    /// <summary>Rotation about Y (legacy Eigen::AngleAxisd(a, UnitY)).</summary>
    public static Mat3 Ry(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new Mat3(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    /// <summary>Rotation about Z (legacy Eigen::AngleAxisd(a, UnitZ)).</summary>
    public static Mat3 Rz(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new Mat3(c, -s, 0, s, c, 0, 0, 0, 1);
    }
}

/// <summary>Double-precision unit quaternion (w, x, y, z).</summary>
public record struct QuatD(double W, double X, double Y, double Z)
{
    public static readonly QuatD Identity = new(1, 0, 0, 0);

    public static QuatD FromRotationMatrix(in Mat3 m)
    {
        // Standard Shepperd method, mirroring Eigen::Quaterniond(Matrix3d).
        double trace = m.M00 + m.M11 + m.M22;
        QuatD q;
        if (trace > 0)
        {
            double s = 0.5 / Math.Sqrt(trace + 1.0);
            q = new QuatD(0.25 / s, (m.M21 - m.M12) * s, (m.M02 - m.M20) * s, (m.M10 - m.M01) * s);
        }
        else if (m.M00 > m.M11 && m.M00 > m.M22)
        {
            double s = 2.0 * Math.Sqrt(1.0 + m.M00 - m.M11 - m.M22);
            q = new QuatD((m.M21 - m.M12) / s, 0.25 * s, (m.M01 + m.M10) / s, (m.M02 + m.M20) / s);
        }
        else if (m.M11 > m.M22)
        {
            double s = 2.0 * Math.Sqrt(1.0 + m.M11 - m.M00 - m.M22);
            q = new QuatD((m.M02 - m.M20) / s, (m.M01 + m.M10) / s, 0.25 * s, (m.M12 + m.M21) / s);
        }
        else
        {
            double s = 2.0 * Math.Sqrt(1.0 + m.M22 - m.M00 - m.M11);
            q = new QuatD((m.M10 - m.M01) / s, (m.M02 + m.M20) / s, (m.M12 + m.M21) / s, 0.25 * s);
        }
        return q.Normalize();
    }

    public readonly QuatD Normalize()
    {
        double n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
        return n < 1e-12 ? Identity : new QuatD(W / n, X / n, Y / n, Z / n);
    }

    public readonly QuatD Conjugate() => new(W, -X, -Y, -Z);

    /// <summary>Standard quaternion -> rotation matrix (mirrors Eigen toRotationMatrix).</summary>
    public readonly Mat3 ToRotationMatrix()
    {
        double xx = X * X, yy = Y * Y, zz = Z * Z;
        double wx = W * X, wy = W * Y, wz = W * Z;
        double xy = X * Y, xz = X * Z, yz = Y * Z;
        return new Mat3(
            1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy),
            2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx),
            2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy));
    }

    public readonly QuatD Multiply(in QuatD b) => new(
        W * b.W - X * b.X - Y * b.Y - Z * b.Z,
        W * b.X + X * b.W + Y * b.Z - Z * b.Y,
        W * b.Y - X * b.Z + Y * b.W + Z * b.X,
        W * b.Z + X * b.Y - Y * b.X + Z * b.W);

    /// <summary>
    /// Port of the legacy quat2eulers(): returns (yaw, pitch, roll) in degrees
    /// in the legacy ypr slot convention ypr = (x:yaw-ish, y, z).
    /// </summary>
    public readonly Vec3 ToYprDegrees()
    {
        double z = Math.Atan2(2 * (W * X + Y * Z), 1 - 2 * (X * X + Y * Y));
        double y = Math.Asin(Math.Clamp(2 * (W * Y - Z * X), -1.0, 1.0));
        double x = Math.Atan2(2 * (W * Z + X * Y), 1 - 2 * (Y * Y + Z * Z));
        const double rad2deg = 180.0 / Math.PI;
        return new Vec3(x * rad2deg, y * rad2deg, z * rad2deg);
    }
}

/// <summary>Shared scalar helpers ported from the legacy utils.h.</summary>
public static class PoseMath
{
    public static double WrapAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2 * Math.PI;
        while (angle < -Math.PI) angle += 2 * Math.PI;
        return angle;
    }

    /// <summary>Legacy signum(): returns -1 for negatives, +1 otherwise (including zero).</summary>
    public static double Signum(double x) => x < 0 ? -1 : 1;

    public static double Clamp(double v, double min, double max) => Math.Clamp(v, min, max);
}
