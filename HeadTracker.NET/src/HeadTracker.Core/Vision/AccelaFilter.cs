namespace HeadTracker.Core.Vision;

/// <summary>
/// Port of opentrack's Accela smoothing filter (lib/accela_filter). Keeps the
/// legacy semantics: per-axis deadzones, shared 3-axis delta normalization,
/// 360-degree wrap for rotation and dt-scaled output accumulation.
/// Input/output order is [Tx, Ty, Tz, Yaw, Pitch, Roll].
/// </summary>
public sealed class AccelaFilter
{
    private const double FullTurn = 360.0;
    private const double HalfTurn = 180.0;

    private readonly double _rotSmoothing;
    private readonly double _posSmoothing;
    private readonly double _rotDeadzone;
    private readonly double _posDeadzone;

    private readonly double[] _lastOutput = new double[6];
    private readonly double[] _deltas = new double[6];
    private bool _firstRun = true;

    public AccelaFilter(double rotSmoothing, double posSmoothing, double rotDeadzone, double posDeadzone)
    {
        _rotSmoothing = rotSmoothing;
        _posSmoothing = posSmoothing;
        _rotDeadzone = rotDeadzone;
        _posDeadzone = posDeadzone;
    }

    /// <summary>Reset state so the next sample passes through unchanged (used on centering).</summary>
    public void Center() => _firstRun = true;

    public (Vec3 Eul, Vec3 T) Filter(Vec3 eul, Vec3 t, double dt)
    {
        Span<double> input = stackalloc double[6];
        Span<double> output = stackalloc double[6];

        input[0] = t.X;
        input[1] = t.Y;
        input[2] = t.Z;
        input[3] = eul.X; // yaw
        input[4] = eul.Y; // pitch
        input[5] = eul.Z; // roll

        if (_firstRun)
        {
            _firstRun = false;
            for (int i = 0; i < 6; i++)
            {
                _lastOutput[i] = input[i];
            }
            return (eul, t);
        }

        // rotation: wrap-aware delta, deadzone, normalize by smoothing threshold
        for (int i = 3; i < 6; i++)
        {
            double d = input[i] - _lastOutput[i];
            if (Math.Abs(d) > HalfTurn)
            {
                d -= Math.CopySign(FullTurn, d);
            }

            d = Math.Abs(d) > _rotDeadzone ? d - Math.CopySign(_rotDeadzone, d) : 0;
            _deltas[i] = d / _rotSmoothing;
        }
        DoDeltas(_deltas.AsSpan(3, 3), output.Slice(3, 3));

        // translation: deadzone + threshold only
        for (int i = 0; i < 3; i++)
        {
            double d = input[i] - _lastOutput[i];
            d = Math.Abs(d) > _posDeadzone ? d - Math.CopySign(_posDeadzone, d) : 0;
            _deltas[i] = d / _posSmoothing;
        }
        DoDeltas(_deltas.AsSpan(0, 3), output.Slice(0, 3));

        for (int k = 0; k < 6; k++)
        {
            output[k] = output[k] * dt + _lastOutput[k];
            if (Math.Abs(output[k]) > HalfTurn)
            {
                output[k] -= Math.CopySign(FullTurn, output[k]);
            }
            _lastOutput[k] = output[k];
        }

        return (
            new Vec3(output[3], output[4], output[5]),
            new Vec3(output[0], output[1], output[2]));
    }

    /// <summary>
    /// Legacy do_deltas(): distribute the spline value (identity here; the
    /// legacy spline was commented out) across the three axes proportionally.
    /// </summary>
    private static void DoDeltas(ReadOnlySpan<double> deltas, Span<double> output)
    {
        Span<double> norm = stackalloc double[3];
        double dist = 0;
        for (int k = 0; k < 3; k++)
        {
            dist += deltas[k] * deltas[k];
        }
        dist = Math.Sqrt(dist);

        double value = dist; // identity spline, as in the legacy filter

        for (int k = 0; k < 3; k++)
        {
            norm[k] = dist > 1e-6 ? Math.Clamp(Math.Abs(deltas[k]) / dist, 0.0, 1.0) : 0;
        }

        double n = norm[0] + norm[1] + norm[2];
        if (n > 1e-6)
        {
            double inv = 1.0 / n;
            for (int k = 0; k < 3; k++)
            {
                norm[k] *= inv;
            }
        }
        else
        {
            norm.Clear();
        }

        for (int k = 0; k < 3; k++)
        {
            output[k] = PoseMath.Signum(deltas[k]) * norm[k] * value;
        }
    }
}
