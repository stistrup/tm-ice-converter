using System.Numerics;

namespace TmSurfaceEditor;

internal static class Geometry
{
    // closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5)
    public static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var (ab, ac, ap) = (b - a, c - a, p - a);
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)));
        var denom = 1 / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
    }
}
