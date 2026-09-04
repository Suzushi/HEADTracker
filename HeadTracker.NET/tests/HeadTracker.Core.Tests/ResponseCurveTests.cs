using HeadTracker.Core.Vision;

namespace HeadTracker.Core.Tests;

public class ResponseCurveTests
{
    [Fact]
    public void Endpoints_ArePinned()
    {
        var curve = new ResponseCurve(new[] { (0.0, 0.5) });

        Assert.Equal(-1, curve.Evaluate(-1), 9);
        Assert.Equal(1, curve.Evaluate(1), 9);

        var pts = curve.Points;
        Assert.Equal((-1, -1), (pts[0].X, pts[0].Y));
        Assert.Equal((1, 1), (pts[^1].X, pts[^1].Y));
    }

    [Fact]
    public void LinearCurve_IsIdentity()
    {
        // Endpoints pinned + a single midpoint at the origin are collinear.
        var curve = new ResponseCurve(new[] { (0.0, 0.0) });

        Assert.Equal(0.5, curve.Evaluate(0.5), 9);
        Assert.Equal(-0.7, curve.Evaluate(-0.7), 9);
        Assert.Equal(0.0, curve.Evaluate(0.0), 9);
    }

    [Fact]
    public void FromExpo_Zero_IsLinear()
    {
        var curve = ResponseCurve.FromExpo(0.0); // (1-0)*x + 0*x^3 == x

        Assert.Equal(0.5, curve.Evaluate(0.5), 6);
        Assert.Equal(-0.3, curve.Evaluate(-0.3), 6);
    }

    [Fact]
    public void FromExpo_One_ApproxCubic()
    {
        var curve = ResponseCurve.FromExpo(1.0); // 0*x + 1*x^3 == x^3

        // x = 0.5 is a sampled knot of FromExpo, so the value is exact.
        Assert.Equal(0.125, curve.Evaluate(0.5), 6);
        Assert.Equal(1.0, curve.Evaluate(1.0), 6);
    }

    [Fact]
    public void Evaluate_IsOddSymmetric()
    {
        var curve = ResponseCurve.FromExpo(0.6);

        foreach (double x in new[] { 0.1, 0.25, 0.4, 0.66, 0.9 })
        {
            Assert.Equal(-curve.Evaluate(x), curve.Evaluate(-x), 6);
        }
    }

    [Fact]
    public void Evaluate_DoesNotOvershoot_MonotonePoints()
    {
        // Monotone-increasing control points; a monotone cubic must never decrease
        // between them (the property that keeps the result inside the output bounds).
        var curve = new ResponseCurve(new[]
        {
            (-0.6, -0.2), (-0.2, 0.1), (0.3, 0.15), (0.7, 0.9),
        });

        double prev = curve.Evaluate(-1);
        for (int i = 1; i <= 200; i++)
        {
            double x = -1 + 2 * i / 200.0;
            double y = curve.Evaluate(x);
            Assert.True(y >= prev - 1e-9, $"curve decreased at x={x}: {y} < {prev}");
            Assert.InRange(y, -1, 1);
            prev = y;
        }
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var original = new ResponseCurve(new[] { (-0.5, -0.3), (0.2, 0.6) });
        var parsed = ResponseCurve.TryParse(original.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(original.PointCount, parsed!.PointCount);
        foreach (double x in new[] { -1, -0.5, -0.1, 0.2, 0.75, 1 })
        {
            Assert.Equal(original.Evaluate(x), parsed.Evaluate(x), 4);
        }
    }

    [Fact]
    public void TryParse_EmptyOrInvalid_ReturnsNull()
    {
        Assert.Null(ResponseCurve.TryParse(null));
        Assert.Null(ResponseCurve.TryParse(""));
        Assert.Null(ResponseCurve.TryParse("   "));
        Assert.Null(ResponseCurve.TryParse("garbage"));
        Assert.Null(ResponseCurve.TryParse("a,b;c,d"));

        Assert.NotNull(ResponseCurve.TryParse("-1,-1;0,0;1,1"));
    }

    [Fact]
    public void ClampsInputBeyondDomain()
    {
        var curve = new ResponseCurve(new[] { (0.0, 0.0) });

        Assert.Equal(1, curve.Evaluate(5.0), 9);
        Assert.Equal(-1, curve.Evaluate(-5.0), 9);
    }
}
