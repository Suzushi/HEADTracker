namespace HeadTracker.Core.Vision;

/// <summary>
/// One-Euro filter (Casiez, Roussel &amp; Vogel, CHI 2012): an adaptive low-pass
/// that trades jitter against latency. At rest the cutoff stays low so residual
/// noise is smoothed away; as the signal speeds up the cutoff rises with it, so
/// fast head turns pass through with little lag. This is exactly the behaviour
/// wanted for head tracking -- a rock-still neutral gaze without a floaty feel
/// when looking around. One instance handles a single scalar axis.
/// </summary>
public sealed class OneEuroFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private bool _inited;
    private double _prevRaw;
    private double _prevFiltered;
    private double _prevDeriv;

    /// <param name="minCutoff">Cutoff in Hz at rest; lower = smoother/less jitter but laggier when still.</param>
    /// <param name="beta">Speed coefficient; higher = less lag when moving but more jitter passed through.</param>
    /// <param name="dCutoff">Cutoff in Hz applied to the derivative low-pass.</param>
    public OneEuroFilter(double minCutoff, double beta, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    /// <summary>Reset state so the next sample passes through unchanged (used on centering).</summary>
    public void Reset()
    {
        _inited = false;
        _prevDeriv = 0;
    }

    /// <summary>Filter one sample; <paramref name="dt"/> is the time in seconds since the previous sample.</summary>
    public double Filter(double x, double dt)
    {
        if (!_inited || dt <= 0)
        {
            _inited = true;
            _prevRaw = x;
            _prevFiltered = x;
            _prevDeriv = 0;
            return x;
        }

        // Filtered derivative of the raw signal estimates the current speed.
        double dx = (x - _prevRaw) / dt;
        double aD = Alpha(_dCutoff, dt);
        double dxh = aD * dx + (1 - aD) * _prevDeriv;

        // Cutoff grows with speed: still -> minCutoff (heavy smoothing), fast -> high (low lag).
        double cutoff = _minCutoff + _beta * Math.Abs(dxh);
        double a = Alpha(cutoff, dt);
        double xh = a * x + (1 - a) * _prevFiltered;

        _prevRaw = x;
        _prevDeriv = dxh;
        _prevFiltered = xh;
        return xh;
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}
