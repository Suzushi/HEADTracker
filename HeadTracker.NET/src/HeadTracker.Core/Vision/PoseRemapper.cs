using HeadTracker.Core.Configuration;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Port of the legacy PoseRemapper: recenters against the initial pose,
/// applies per-axis expo/bounds remapping and Accela smoothing at the output
/// rate (legacy 250 Hz). Axis layout follows the legacy convention
/// eul = (yaw, pitch, roll) degrees.
/// </summary>
public sealed class PoseRemapper
{
    private readonly TrackerSettings _settings;
    private readonly AccelaFilter _accela;
    private readonly AccelaFilter _accela2;

    // Optional adaptive low-pass on the Euler output (yaw/pitch/roll), applied at
    // the processing rate before Accela. Null when use_one_euro is off.
    private readonly OneEuroFilter? _oneEuroYaw;
    private readonly OneEuroFilter? _oneEuroPitch;
    private readonly OneEuroFilter? _oneEuroRoll;

    // Optional per-axis response curves; null means fall back to the legacy expo parameter.
    private readonly ResponseCurve? _curveTransX;
    private readonly ResponseCurve? _curveTransY;
    private readonly ResponseCurve? _curveTransZ;
    private readonly ResponseCurve? _curveEulYaw;
    private readonly ResponseCurve? _curveEulPitch;
    private readonly ResponseCurve? _curveEulRoll;

    private readonly object _gate = new();
    private Mat3 _initialR = Mat3.Identity;
    private Vec3 _initialT = Vec3.Zero;
    private bool _inited;
    private Vec3 _eulLast = Vec3.Zero;
    private Vec3 _tLast = Vec3.Zero;

    public bool UseAccelaPath => _settings.UseFt || _settings.UseNpclient;

    public PoseRemapper(TrackerSettings settings)
    {
        _settings = settings;
        _accela = new AccelaFilter(settings.AccelaRotSmoothing, settings.AccelaPosSmoothing,
            settings.AccelaRotDeadzone, settings.AccelaPosDeadzone);
        _accela2 = new AccelaFilter(settings.AccelaRotSmoothing, settings.AccelaPosSmoothing,
            settings.AccelaRotDeadzone, settings.AccelaPosDeadzone);

        if (settings.UseOneEuro)
        {
            _oneEuroYaw = new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta, settings.OneEuroDerivCutoff);
            _oneEuroPitch = new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta, settings.OneEuroDerivCutoff);
            _oneEuroRoll = new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta, settings.OneEuroDerivCutoff);
        }

        _curveTransX = ResponseCurve.TryParse(settings.CurveTransX);
        _curveTransY = ResponseCurve.TryParse(settings.CurveTransY);
        _curveTransZ = ResponseCurve.TryParse(settings.CurveTransZ);
        _curveEulYaw = ResponseCurve.TryParse(settings.CurveEulYaw);
        _curveEulPitch = ResponseCurve.TryParse(settings.CurveEulPitch);
        _curveEulRoll = ResponseCurve.TryParse(settings.CurveEulRoll);
    }

    /// <summary>Feed a world-frame pose (legacy passes R*Rface and the PnP translation).</summary>
    public void OnPose(in Mat3 rWorld, in Vec3 tWorld, double dt = 1.0 / 60.0)
    {
        lock (_gate)
        {
            if (!_inited)
            {
                _initialR = rWorld;
                _initialT = tWorld;
                _inited = true;
            }

            // Relative pose: Q = initial^-1 * current, T = initial^-1 * (T - T0)
            var initialInv = _initialR.Transpose();
            var q = QuatD.FromRotationMatrix(initialInv.Multiply(rWorld));
            var tRel = initialInv.Multiply(tWorld - _initialT);
            var eul = q.ToYprDegrees();

            if (UseAccelaPath)
            {
                tRel = new Vec3(
                    Remap(tRel.X, _settings.InpBoundX, _settings.OutBoundX, _settings.ExpoTransX, _curveTransX),
                    Remap(tRel.Y, _settings.InpBoundY, _settings.OutBoundY, _settings.ExpoTransY, _curveTransY),
                    Remap(tRel.Z, _settings.InpBoundZ, _settings.OutBoundZ, _settings.ExpoTransZ, _curveTransZ));
                eul = new Vec3(
                    Remap(eul.X, _settings.InpBoundYaw, _settings.OutBoundYaw, _settings.ExpoEulYaw, _curveEulYaw),
                    Remap(eul.Y, _settings.InpBoundPitch, _settings.OutBoundPitch, _settings.ExpoEulPitch, _curveEulPitch),
                    Remap(eul.Z, _settings.InpBoundRoll, _settings.OutBoundRoll, _settings.ExpoEulRoll, _curveEulRoll));
            }

            if (_oneEuroYaw != null)
            {
                eul = new Vec3(
                    _oneEuroYaw.Filter(eul.X, dt),
                    _oneEuroPitch!.Filter(eul.Y, dt),
                    _oneEuroRoll!.Filter(eul.Z, dt));
            }

            _eulLast = eul;
            _tLast = tRel;
        }
    }

    /// <summary>
    /// One step of the legacy 250 Hz output loop: Accela-filter the last mapped
    /// pose. Returns null before the first pose or while values are NaN.
    /// </summary>
    public Pose6D? Tick(double dt)
    {
        lock (_gate)
        {
            if (!_inited)
            {
                return null;
            }

            var eul = _eulLast;
            var t = _tLast;
            if (double.IsNaN(eul.X) || double.IsNaN(eul.Y) || double.IsNaN(eul.Z))
            {
                return null;
            }

            if (_settings.UseAccela)
            {
                (eul, t) = _accela.Filter(eul, t, dt);
                if (_settings.DoubleAccela)
                {
                    (eul, t) = _accela2.Filter(eul, t, dt);
                }
            }

            return new Pose6D(eul.X, eul.Y, eul.Z, t.X, t.Y, t.Z);
        }
    }

    /// <summary>Legacy UDP-only path: the mapped pose without bounds remap or filtering.</summary>
    public Pose6D? SnapshotUnfiltered()
    {
        lock (_gate)
        {
            return _inited
                ? new Pose6D(_eulLast.X, _eulLast.Y, _eulLast.Z, _tLast.X, _tLast.Y, _tLast.Z)
                : null;
        }
    }

    public void ResetCenter()
    {
        lock (_gate)
        {
            _inited = false;
            _accela.Center();
            _accela2.Center();
            _oneEuroYaw?.Reset();
            _oneEuroPitch?.Reset();
            _oneEuroRoll?.Reset();
        }
    }

    private static double Remap(double v, double inputBound, double outputBound, double expo, ResponseCurve? curve)
    {
        if (Math.Abs(inputBound) < 1e-9)
        {
            return 0;
        }
        double x = Math.Clamp(v / inputBound, -1, 1);
        double shaped = curve != null ? curve.Evaluate(x) : Expo(x, expo);
        return shaped * outputBound;
    }

    private static double Expo(double value, double e)
    {
        double x = Math.Clamp(value, -1, 1);
        double ec = Math.Clamp(e, 0, 1);
        return (1 - ec) * x + ec * x * x * x;
    }
}
