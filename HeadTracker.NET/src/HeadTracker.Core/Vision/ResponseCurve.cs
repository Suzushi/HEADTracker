using System.Globalization;
using System.Text;

namespace HeadTracker.Core.Vision;

/// <summary>
/// An editable input→output response curve on the normalized domain [-1, 1], used by
/// <see cref="PoseRemapper"/> to shape each axis. It replaces the single legacy "expo"
/// parameter with a set of user-editable control points (see the curve editor).
///
/// The endpoints are pinned to (-1,-1) and (1,1) so a full-deflection input always maps
/// to a full-deflection output and the existing output-bound scaling keeps its meaning.
/// The interior is interpolated with a monotone cubic (Fritsch–Carlson), which never
/// overshoots between control points — important because the result is scaled straight
/// into the game's output bounds. The type is immutable and therefore thread-safe.
/// </summary>
public sealed class ResponseCurve
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _m; // per-point Hermite tangents

    public ResponseCurve(IEnumerable<(double X, double Y)> points)
    {
        var pts = Sanitize(points);
        _x = new double[pts.Count];
        _y = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            _x[i] = pts[i].X;
            _y[i] = pts[i].Y;
        }
        _m = ComputeMonotoneTangents(_x, _y);
    }

    public int PointCount => _x.Length;
    public IReadOnlyList<double> Xs => _x;
    public IReadOnlyList<double> Ys => _y;

    /// <summary>The control points as (x, y) tuples, endpoints included (a fresh list).</summary>
    public List<(double X, double Y)> Points
    {
        get
        {
            var list = new List<(double X, double Y)>(_x.Length);
            for (int i = 0; i < _x.Length; i++)
            {
                list.Add((_x[i], _y[i]));
            }
            return list;
        }
    }

    /// <summary>Shape a normalized input; input is clamped to [-1,1] and output clamped to [-1,1].</summary>
    public double Evaluate(double xIn)
    {
        double x = Math.Clamp(xIn, -1, 1);
        int n = _x.Length;

        int i = n - 2;
        for (int k = 0; k < n - 1; k++)
        {
            if (x <= _x[k + 1])
            {
                i = k;
                break;
            }
        }

        double h = _x[i + 1] - _x[i];
        if (h < 1e-12)
        {
            return Math.Clamp(_y[i], -1, 1);
        }

        double t = (x - _x[i]) / h;
        double t2 = t * t;
        double t3 = t2 * t;
        double h00 = 2 * t3 - 3 * t2 + 1;
        double h10 = t3 - 2 * t2 + t;
        double h01 = -2 * t3 + 3 * t2;
        double h11 = t3 - t2;
        double y = h00 * _y[i] + h10 * h * _m[i] + h01 * _y[i + 1] + h11 * h * _m[i + 1];
        return Math.Clamp(y, -1, 1);
    }

    /// <summary>Build a curve reproducing the legacy expo shape (1-e)·x + e·x³.</summary>
    public static ResponseCurve FromExpo(double expo)
    {
        double e = Math.Clamp(expo, 0, 1);
        const int segments = 8;
        var pts = new List<(double X, double Y)>(segments + 1);
        for (int k = 0; k <= segments; k++)
        {
            double x = -1 + 2 * k / (double)segments;
            pts.Add((x, (1 - e) * x + e * x * x * x));
        }
        return new ResponseCurve(pts);
    }

    /// <summary>Serialize to a compact "-1,-1;x,y;...;1,1" string (invariant culture).</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _x.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }
            sb.Append(_x[i].ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(_y[i].ToString("0.####", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Parse a serialized curve; returns null for empty/invalid input so callers can
    /// fall back to the expo path.</summary>
    public static ResponseCurve? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        var pts = new List<(double X, double Y)>();
        foreach (var pair in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = pair.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (xy.Length != 2)
            {
                continue;
            }
            if (double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                pts.Add((x, y));
            }
        }
        return pts.Count == 0 ? null : new ResponseCurve(pts);
    }

    private static List<(double X, double Y)> Sanitize(IEnumerable<(double X, double Y)> points)
    {
        var list = new List<(double X, double Y)> { (-1, -1), (1, 1) };
        if (points != null)
        {
            foreach (var p in points)
            {
                double x = Math.Clamp(p.X, -1, 1);
                double y = Math.Clamp(p.Y, -1, 1);
                if (Math.Abs(x + 1) < 1e-9 || Math.Abs(x - 1) < 1e-9)
                {
                    continue; // endpoints are pinned; ignore user points there
                }
                list.Add((x, y));
            }
        }

        list.Sort((a, b) => a.X.CompareTo(b.X));

        var result = new List<(double X, double Y)>();
        foreach (var p in list)
        {
            if (result.Count > 0 && Math.Abs(result[^1].X - p.X) < 1e-9)
            {
                result[^1] = (result[^1].X, (result[^1].Y + p.Y) * 0.5);
            }
            else
            {
                result.Add(p);
            }
        }
        result[0] = (-1, -1);
        result[^1] = (1, 1);
        return result;
    }

    private static double[] ComputeMonotoneTangents(double[] x, double[] y)
    {
        int n = x.Length;
        var m = new double[n];
        if (n < 2)
        {
            return m;
        }

        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = x[i + 1] - x[i];
            d[i] = Math.Abs(dx) < 1e-12 ? 0 : (y[i + 1] - y[i]) / dx;
        }

        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (d[i - 1] * d[i] <= 0)
            {
                m[i] = 0; // local extremum or flat segment
            }
            else
            {
                double w1 = 2 * (x[i + 1] - x[i]);
                double w2 = 2 * (x[i] - x[i - 1]);
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
        }

        // Fritsch–Carlson monotonicity guard: keep (m[i]/d, m[i+1]/d) inside the radius-3 circle.
        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(d[i]) < 1e-12)
            {
                m[i] = 0;
                m[i + 1] = 0;
            }
            else
            {
                double a = m[i] / d[i];
                double b = m[i + 1] / d[i];
                double s = a * a + b * b;
                if (s > 9)
                {
                    double tau = 3 / Math.Sqrt(s);
                    m[i] = tau * a * d[i];
                    m[i + 1] = tau * b * d[i];
                }
            }
        }
        return m;
    }
}
