using System.Numerics;

namespace TmIceConverter;

// texture layouts: platform tiles, plain cover, ice streaks along roads
internal static class RoadLayout
{
    public static float[] PlatformUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 1 - positions[3 * i + 2] / 32);
        return uv;
    }

    // avoid the texture's blue band
    public static float[] PlainCoverUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        float length = 32;
        for (int i = 2; i < positions.Length; i += 3) length = MathF.Max(length, MathF.Ceiling(positions[i] / 32) * 32);
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 0.3f + 0.4f * positions[3 * i + 2] / length);
        return uv;
    }

    private readonly record struct Edge(Vector3 A, Vector3 B, int Count);

    // a road end: where its surface meets a cell face (x = At or z = At), spanning Min..Max along it
    private readonly record struct Opening(bool OnX, float At, float Min, float Max)
    {
        public float Width => Max - Min;
    }

    // ice streaks (texture u) along the road, bent around curves and S-bends; roads are found by
    // where their surface leaves the block (open edges on cell faces)
    public static void Apply(List<MeshPiece> pieces, HashSet<MeshPiece> roadLaid)
    {
        if (roadLaid.Count == 0) return;
        var edges = Edges(roadLaid);
        var real = Openings(edges).Where(o => o.Width > 4).ToList();
        if (LayoutFor(real.Where(o => o.OnX).ToList(), real.Where(o => !o.OnX).ToList(), edges) is not { } layout) return;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!roadLaid.Contains(pieces[i])) continue;
            var pos = pieces[i].Positions;
            var uv = new float[pos.Length / 3 * 2];
            for (int v = 0; v < uv.Length / 2; v++)
                (uv[2 * v], uv[2 * v + 1]) = layout(new Vector3(pos[3 * v], pos[3 * v + 1], pos[3 * v + 2])) is var t ? (t.X, t.Y) : default;
            pieces[i] = pieces[i] with { Uv0 = uv };
        }
    }

    // edges of the upward faces, counted: open edges are in one face only
    private static List<Edge> Edges(HashSet<MeshPiece> roadLaid)
    {
        var edges = new Dictionary<((int, int, int), (int, int, int)), Edge>();
        (int, int, int) Key(Vector3 v) => ((int)MathF.Round(v.X * 100), (int)MathF.Round(v.Y * 100), (int)MathF.Round(v.Z * 100));
        foreach (var p in roadLaid)
        {
            Vector3 At(int v) => new(p.Positions[3 * v], p.Positions[3 * v + 1], p.Positions[3 * v + 2]);
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                var (a, b, c) = (At(p.Indices[t]), At(p.Indices[t + 1]), At(p.Indices[t + 2]));
                var n = Vector3.Cross(b - a, c - a);
                if (n.Length() < 1e-5f || MathF.Abs(n.Y) / n.Length() < 0.5f) continue;
                foreach (var (e0, e1) in new[] { (a, b), (b, c), (c, a) })
                {
                    var (k0, k1) = (Key(e0), Key(e1));
                    if (k0 == k1) continue;
                    var key = k0.CompareTo(k1) < 0 ? (k0, k1) : (k1, k0);
                    edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : new Edge(e0, e1, 1);
                }
            }
        }
        return [.. edges.Values];
    }

    private static float Cell(float x) => MathF.Round(x / 32) * 32;

    private static bool OnFace(float x) => MathF.Abs(x - Cell(x)) < 0.05f;

    private static List<Opening> Openings(List<Edge> edges)
    {
        var openings = new Dictionary<(bool OnX, float At), (float Min, float Max)>();
        foreach (var (a, b, count) in edges.Where(e => e.Count == 1))
            foreach (var onX in new[] { true, false })
            {
                var (fa, fb, sa, sb) = onX ? (a.X, b.X, a.Z, b.Z) : (a.Z, b.Z, a.X, b.X);
                if (!OnFace(fa) || !OnFace(fb) || Cell(fa) != Cell(fb)) continue;
                var key = (onX, Cell(fa));
                var (lo, hi) = (MathF.Min(sa, sb), MathF.Max(sa, sb));
                openings[key] = openings.TryGetValue(key, out var o) ? (MathF.Min(o.Min, lo), MathF.Max(o.Max, hi)) : (lo, hi);
            }
        return [.. openings.Select(o => new Opening(o.Key.OnX, o.Key.At, o.Value.Min, o.Value.Max))];
    }

    // block coordinates -> streak UV; null: no road shape found
    private static Func<Vector3, Vector2>? LayoutFor(List<Opening> onXs, List<Opening> onZs, List<Edge> edges)
    {
        static float Length(List<Opening> l) => l.Sum(o => o.Width);
        // travel along z: openings on z = const faces
        if (onZs.Count >= 2 && (onXs.Count < 2 || Length(onZs) >= Length(onXs)) || onZs.Count == 1 && onXs.Count == 0)
            return Along(onZs, edges, p => p.Z, p => p.X);
        if (onXs.Count >= 2 || onXs.Count == 1 && onZs.Count == 0)
            return Along(onXs, edges, p => p.X, p => p.Z);
        if (onXs.Count == 1 && onZs.Count == 1) return QuarterTurn(onXs[0], onZs[0]);
        return null;
    }

    // both openings radial, same radii: a quarter turn around the shared corner
    private static Func<Vector3, Vector2>? QuarterTurn(Opening ox, Opening oz)
    {
        var center = new Vector2(ox.At, oz.At);
        var (rx0, rx1) = (ox.Min - center.Y, ox.Max - center.Y);
        var (rz0, rz1) = (oz.Min - center.X, oz.Max - center.X);
        var radial = rx0 * rx1 >= 0 && rz0 * rz1 >= 0;
        var sameInner = MathF.Abs(MathF.Min(MathF.Abs(rx0), MathF.Abs(rx1)) - MathF.Min(MathF.Abs(rz0), MathF.Abs(rz1))) < 1.5f;
        var sameOuter = MathF.Abs(MathF.Max(MathF.Abs(rx0), MathF.Abs(rx1)) - MathF.Max(MathF.Abs(rz0), MathF.Abs(rz1))) < 1.5f;
        if (!(radial && sameInner && sameOuter)) return null;
        var (toX, toZ) = (MathF.Sign(rx0 + rx1), MathF.Sign(rz0 + rz1));
        var outer = MathF.Ceiling(MathF.Max(MathF.Abs(rx0), MathF.Abs(rx1)) / 32 - 0.01f) * 32;
        // like the S-bends: along the middle line (whole tiles end to end), across from it
        var tiles = MathF.Max(1, MathF.Round((outer - 16) * MathF.PI / 2 / 32));
        return p =>
        {
            var d = new Vector2(p.X, p.Z) - center;
            var angle = MathF.Atan2(d.X * toZ, d.Y * toX);
            return new Vector2(angle / (MathF.PI / 2) * tiles, (outer - d.Length()) / 32);
        };
    }

    // across from the road's middle: S-bends (openings offset sideways) bend the streaks with them
    private static Func<Vector3, Vector2> Along(List<Opening> ends, List<Edge> edges, Func<Vector3, float> along, Func<Vector3, float> across)
    {
        var mids = ends.Select(o => (o.Min + o.Max) / 2).ToList();
        if (mids.Max() - mids.Min() < 1) return p => new Vector2(along(p) / 32, across(p) / 32);
        // the road's side edges (open edges off the cell faces)
        var sides = edges.Where(e => e.Count == 1 && !(OnFace(along(e.A)) && OnFace(along(e.B)) && Cell(along(e.A)) == Cell(along(e.B))))
            .Select(e => (P: new Vector2(along(e.A), across(e.A)), Q: new Vector2(along(e.B), across(e.B)))).ToArray();
        var (start, end) = (ends.Min(o => o.At), ends.Max(o => o.At));
        var line = MiddleLine(sides, start, end);
        var length = new float[line.Length];
        for (int k = 1; k < line.Length; k++) length[k] = length[k - 1] + Vector2.Distance(line[k - 1], line[k]);
        // tiles stay whole at the block's ends
        var scale = (end - start) / length[^1];
        return p =>
        {
            var at = new Vector2(along(p), across(p));
            var (best, u, v) = (float.MaxValue, 0f, 0f);
            for (int k = 0; k + 1 < line.Length; k++)
            {
                var seg = line[k + 1] - line[k];
                var t = Vector2.Dot(at - line[k], seg) / seg.LengthSquared();
                if (k > 0) t = MathF.Max(t, 0);
                if (k + 2 < line.Length) t = MathF.Min(t, 1);
                var foot = line[k] + seg * t;
                var distance = Vector2.Distance(at, foot);
                if (distance >= best) continue;
                var dir = Vector2.Normalize(seg);
                (best, u, v) = (distance, start + (length[k] + t * seg.Length()) * scale, Vector2.Dot(at - foot, new Vector2(-dir.Y, dir.X)));
            }
            return new Vector2(u / 32, v / 32 + 0.5f);
        };
    }

    // middle line, a point per metre, refined to cut the sides at right angles
    private static Vector2[] MiddleLine((Vector2 P, Vector2 Q)[] sides, float start, float end)
    {
        var line = Enumerable.Range(0, (int)(end - start) + 1).Select(k => new Vector2(start + k, MiddleAt(sides, start + k))).ToArray();
        for (int pass = 0; pass < 4; pass++)
            for (int k = 0; k < line.Length; k++)
            {
                var tangent = k == 0 || k == line.Length - 1 ? Vector2.UnitX : Vector2.Normalize(line[k + 1] - line[k - 1]);
                var normal = new Vector2(-tangent.Y, tangent.X);
                if (Hit(sides, line[k], normal) is { } up && Hit(sides, line[k], -normal) is { } down) line[k] += normal * (up - down) / 2;
            }
        return line;
    }

    private static float MiddleAt((Vector2 P, Vector2 Q)[] sides, float a)
    {
        var (lo, hi) = (float.MaxValue, float.MinValue);
        foreach (var (p, q) in sides)
        {
            if (MathF.Abs(q.X - p.X) < 1e-4f) continue;
            var t = (a - p.X) / (q.X - p.X);
            if (t < -1e-3f || t > 1 + 1e-3f) continue;
            var x = p.Y + (q.Y - p.Y) * Math.Clamp(t, 0, 1);
            (lo, hi) = (MathF.Min(lo, x), MathF.Max(hi, x));
        }
        return lo <= hi ? (lo + hi) / 2 : 16;
    }

    // nearest side along the ray
    private static float? Hit((Vector2 P, Vector2 Q)[] sides, Vector2 from, Vector2 dir)
    {
        float? nearest = null;
        foreach (var (p, q) in sides)
        {
            var e = q - p;
            var den = dir.X * e.Y - dir.Y * e.X;
            if (MathF.Abs(den) < 1e-6f) continue;
            var w = p - from;
            var s = (w.X * e.Y - w.Y * e.X) / den;
            var t = (w.X * dir.Y - w.Y * dir.X) / den;
            if (s > 0 && t >= 0 && t <= 1 && (nearest is null || s < nearest)) nearest = s;
        }
        return nearest;
    }
}
