using HeadTracker.Core.Configuration;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Port of the legacy ExtendKalmanFilter12DOF_13 (KalmanFilter.h/.cpp):
/// 13-state EKF over q(x,y,z,w Eigen coeff order), T, angular velocity w and
/// linear velocity v, with a 7-D pose measurement and an optional 2-D planar
/// ground-speed measurement. All covariance formulas are copied 1:1.
/// Not thread-safe; driven from the pipeline thread only.
/// </summary>
public sealed class EkfFusion
{
    private const int N = 13;
    // Legacy FlightAgxSettings::cov_gspd_planar default (never persisted to yaml).
    private const double CovGspdPlanar = 0.01;

    private readonly TrackerSettings _s;

    // X[0..3] = q coeffs (x,y,z,w), X[4..6] = T, X[7..9] = w, X[10..12] = v
    private readonly double[] _x = new double[N];
    private readonly MatN _p = MatN.Identity(N);
    private readonly MatN _q = new(N, N);
    private readonly MatN _r = new(7, 7);
    private readonly MatN _r1 = new(2, 2);

    private bool _initialized;
    private double _tState;

    public bool Initialized => _initialized;
    public Vec3 AngularVelocity => new(_x[7], _x[8], _x[9]);
    public Vec3 LinearVelocity => new(_x[10], _x[11], _x[12]);

    public EkfFusion(TrackerSettings settings)
    {
        _s = settings;
        UpdateCov(settings.CovQLm);
    }

    public (QuatD Q, Vec3 T) RealtimePose => (StateQuat(), new Vec3(_x[4], _x[5], _x[6]));

    public void Reset()
    {
        _initialized = false;
        _tState = 0;
        Array.Clear(_x);
        _p.SetIdentity();
    }

    /// <summary>Legacy update_raw_pose_data(); type 0 = landmark/PnP (cov_Q_lm), 1 = FSA (cov_Q_fsa).</summary>
    public (QuatD Q, Vec3 T) UpdateRawPose(double t, in QuatD measQ, in Vec3 measT, int type)
    {
        if (!_initialized)
        {
            StoreQuat(measQ);
            _x[4] = measT.X; _x[5] = measT.Y; _x[6] = measT.Z;
            _x[7] = _x[8] = _x[9] = 0;
            _x[10] = _x[11] = _x[12] = 0;
            _p.SetIdentity();
            _initialized = true;
            return RealtimePose;
        }

        // Legacy sign flip: pick the quaternion hemisphere closer to the state.
        var zq = measQ;
        if (CoeffsDistance(measQ, +1) > CoeffsDistance(measQ, -1))
        {
            zq = new QuatD(-measQ.W, -measQ.X, -measQ.Y, -measQ.Z);
        }

        UpdateCov(type == 0 ? _s.CovQLm : _s.CovQFsa);
        Predict(t);

        // H0 = [I7 | 0]; residual y = Z - X[0..7]
        var y = new double[7]
        {
            zq.X - _x[0], zq.Y - _x[1], zq.Z - _x[2], zq.W - _x[3],
            measT.X - _x[4], measT.Y - _x[5], measT.Z - _x[6],
        };

        KalmanUpdate(H0(), y, _r);
        NormalizeQuat();
        return RealtimePose;
    }

    /// <summary>Legacy update_ground_speed(): 2-D planar velocity pseudo-measurement.</summary>
    public (QuatD Q, Vec3 T) UpdateGroundSpeed(double t, in Vec3 spd)
    {
        if (!_initialized)
        {
            return RealtimePose;
        }

        Predict(t);

        double l = Math.Abs(_s.CervicalFaceModelX);
        double ly = Math.Abs(_s.CervicalFaceModelY);

        var h1 = new MatN(2, N);
        h1[0, 8] = -l;
        h1[0, 10] = 1;
        h1[1, 7] = ly;
        h1[1, 11] = 1;

        // h1(x) = (vx - wy*l, vy + wx*ly)
        var y = new double[2]
        {
            spd.X - (_x[10] - _x[8] * l),
            spd.Y - (_x[11] + _x[7] * ly),
        };

        KalmanUpdate(h1, y, _r1);
        NormalizeQuat();
        return RealtimePose;
    }

    /// <summary>Legacy predict(): integrate to time t in ekf_predict_dt steps.</summary>
    public (QuatD Q, Vec3 T) Predict(double t)
    {
        for (double t1 = _tState; t1 < t; t1 += _s.EkfPredictDt)
        {
            double dt = _s.EkfPredictDt;
            if (t - t1 < dt)
            {
                dt = t - t1;
            }
            PredictByDt(dt);
        }
        _tState = t;
        return RealtimePose;
    }

    private void PredictByDt(double dt)
    {
        var f = Fmat(dt);
        PropagateState(dt);
        // P = F P F^T + Q
        var fp = f.Mul(_p);
        var fpft = fp.Mul(f.Transpose());
        _p.CopyFrom(fpft.Add(_q));
        NormalizeQuat();
    }

    private void PropagateState(double dt)
    {
        // q' = q + 0.5 * (0,w) * q * dt   (Hamilton product, legacy w_dot_q)
        var q = StateQuat();
        var omg = new QuatD(0, _x[7], _x[8], _x[9]);
        var prod = omg.Multiply(q);
        _x[0] += 0.5 * prod.X * dt;
        _x[1] += 0.5 * prod.Y * dt;
        _x[2] += 0.5 * prod.Z * dt;
        _x[3] += 0.5 * prod.W * dt;

        _x[4] += _x[10] * dt;
        _x[5] += _x[11] * dt;
        _x[6] += _x[12] * dt;
        // w and v stay constant (13-state variant).
    }

    private MatN Fmat(double dt)
    {
        var f = new MatN(N, N);

        // [0:4,0:4] = I + 0.5*dt*Dwq_by_q(w)
        double wx = _x[7], wy = _x[8], wz = _x[9];
        double h = 0.5 * dt;
        f[0, 0] = 1; f[0, 1] = -h * wz; f[0, 2] = h * wy; f[0, 3] = h * wx;
        f[1, 0] = h * wz; f[1, 1] = 1; f[1, 2] = -h * wx; f[1, 3] = h * wy;
        f[2, 0] = -h * wy; f[2, 1] = h * wx; f[2, 2] = 1; f[2, 3] = h * wz;
        f[3, 0] = -h * wx; f[3, 1] = -h * wy; f[3, 2] = -h * wz; f[3, 3] = 1;

        // [0:4,7:10] = 0.5*dt*Dwq_by_w(q)
        double qx = _x[0], qy = _x[1], qz = _x[2], qw = _x[3];
        f[0, 7] = h * qw; f[0, 8] = h * qz; f[0, 9] = -h * qy;
        f[1, 7] = -h * qz; f[1, 8] = h * qw; f[1, 9] = h * qx;
        f[2, 7] = h * qy; f[2, 8] = -h * qx; f[2, 9] = h * qw;
        f[3, 7] = -h * qx; f[3, 8] = -h * qy; f[3, 9] = -h * qz;

        // [4:7,4:7] = I, [4:7,10:13] = I*dt
        f[4, 4] = 1; f[5, 5] = 1; f[6, 6] = 1;
        f[4, 10] = dt; f[5, 11] = dt; f[6, 12] = dt;

        // [7:13,7:13] = I
        f[7, 7] = 1; f[8, 8] = 1; f[9, 9] = 1;
        f[10, 10] = 1; f[11, 11] = 1; f[12, 12] = 1;
        return f;
    }

    private void UpdateCov(double covQ)
    {
        double dt = _s.EkfPredictDt;

        _r.Clear();
        for (int i = 0; i < 4; i++) _r[i, i] = covQ;
        for (int i = 0; i < 3; i++) _r[4 + i, 4 + i] = _s.CovT;

        _r1.Clear();
        for (int i = 0; i < 2; i++) _r1[i, i] = CovGspdPlanar;

        _q.Clear();
        double covQw = _s.CovW * Math.Pow(dt, 4) * 0.25;
        double covW = _s.CovW * dt * dt;
        double covT = _s.CovV * Math.Pow(dt, 4) * 0.25;
        double covV = _s.CovV * dt * dt * 0.5;
        double covTV = _s.CovV * Math.Pow(dt, 3);

        for (int i = 0; i < 4; i++) _q[i, i] = covQw;
        for (int i = 0; i < 3; i++) _q[7 + i, 7 + i] = covW;
        for (int i = 0; i < 3; i++) _q[4 + i, 4 + i] = covT;
        for (int i = 0; i < 3; i++) _q[10 + i, 10 + i] = covV;
        for (int i = 0; i < 3; i++)
        {
            _q[10 + i, 4 + i] = covTV;
            _q[4 + i, 10 + i] = covTV;
        }
    }

    private static MatN H0()
    {
        var h = new MatN(7, N);
        for (int i = 0; i < 7; i++) h[i, i] = 1;
        return h;
    }

    /// <summary>Shared linear-update step: y must already be Z - h(X).</summary>
    private void KalmanUpdate(MatN h, double[] y, MatN rMat)
    {
        int m = h.Rows;
        // S = H P H^T + R
        var phT = _p.Mul(h.Transpose());          // N x m
        var s = h.Mul(phT).Add(rMat);             // m x m
        var sInv = s.Inverse();
        var k = phT.Mul(sInv);                    // N x m

        for (int i = 0; i < N; i++)
        {
            double acc = 0;
            for (int j = 0; j < m; j++)
            {
                acc += k[i, j] * y[j];
            }
            _x[i] += acc;
        }

        // P = (I - K H) P
        var kh = k.Mul(h);                        // N x N
        var ikh = MatN.Identity(N).Sub(kh);
        _p.CopyFrom(ikh.Mul(_p));
    }

    private double CoeffsDistance(in QuatD other, double sign) => Math.Sqrt(
        Math.Pow(sign * other.X - _x[0], 2) + Math.Pow(sign * other.Y - _x[1], 2) +
        Math.Pow(sign * other.Z - _x[2], 2) + Math.Pow(sign * other.W - _x[3], 2));

    private QuatD StateQuat() => new(_x[3], _x[0], _x[1], _x[2]);

    private void StoreQuat(in QuatD q)
    {
        _x[0] = q.X; _x[1] = q.Y; _x[2] = q.Z; _x[3] = q.W;
    }

    private void NormalizeQuat()
    {
        double n = Math.Sqrt(_x[0] * _x[0] + _x[1] * _x[1] + _x[2] * _x[2] + _x[3] * _x[3]);
        if (n < 1e-12)
        {
            _x[0] = _x[1] = _x[2] = 0; _x[3] = 1;
            return;
        }
        _x[0] /= n; _x[1] /= n; _x[2] /= n; _x[3] /= n;
    }
}

/// <summary>Small dense row-major matrix, just enough for the fixed-size EKF math.</summary>
internal sealed class MatN
{
    private readonly double[] _d;

    public int Rows { get; }
    public int Cols { get; }

    public MatN(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;
        _d = new double[rows * cols];
    }

    public double this[int r, int c]
    {
        get => _d[r * Cols + c];
        set => _d[r * Cols + c] = value;
    }

    public static MatN Identity(int n)
    {
        var m = new MatN(n, n);
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    public void Clear() => Array.Clear(_d);

    public void SetIdentity()
    {
        Clear();
        for (int i = 0; i < Rows; i++) this[i, i] = 1;
    }

    public void CopyFrom(MatN other)
    {
        Array.Copy(other._d, _d, _d.Length);
    }

    public MatN Transpose()
    {
        var t = new MatN(Cols, Rows);
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                t[c, r] = this[r, c];
            }
        }
        return t;
    }

    public MatN Mul(MatN b)
    {
        var r = new MatN(Rows, b.Cols);
        for (int i = 0; i < Rows; i++)
        {
            for (int k = 0; k < Cols; k++)
            {
                double a = this[i, k];
                if (a == 0) continue;
                for (int j = 0; j < b.Cols; j++)
                {
                    r[i, j] += a * b[k, j];
                }
            }
        }
        return r;
    }

    public MatN Add(MatN b)
    {
        var r = new MatN(Rows, Cols);
        for (int i = 0; i < _d.Length; i++) r._d[i] = _d[i] + b._d[i];
        return r;
    }

    public MatN Sub(MatN b)
    {
        var r = new MatN(Rows, Cols);
        for (int i = 0; i < _d.Length; i++) r._d[i] = _d[i] - b._d[i];
        return r;
    }

    /// <summary>Gauss-Jordan inverse with partial pivoting (m is small, 2x2 or 7x7).</summary>
    public MatN Inverse()
    {
        int n = Rows;
        var a = new double[n, 2 * n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                a[r, c] = this[r, c];
            }
            a[r, n + r] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            }
            if (pivot != col)
            {
                for (int c = 0; c < 2 * n; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }
            }

            double d = a[col, col];
            if (Math.Abs(d) < 1e-15)
            {
                d = d < 0 ? -1e-15 : 1e-15; // keep the filter alive on degenerate S
            }
            for (int c = 0; c < 2 * n; c++) a[col, c] /= d;

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                if (f == 0) continue;
                for (int c = 0; c < 2 * n; c++) a[r, c] -= f * a[col, c];
            }
        }

        var inv = new MatN(n, n);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                inv[r, c] = a[r, n + c];
            }
        }
        return inv;
    }
}
