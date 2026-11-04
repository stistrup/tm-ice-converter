using System.Numerics;

namespace TmIceConverter;

internal static class RoadLayout
{
    public static float[] PlatformUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 1 - positions[3 * i + 2] / 32);
        return uv;
    }

    // avoid the texture's blue band
    public static float[] PlainSnowUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        float length = 32;
        for (int i = 2; i < positions.Length; i += 3) length = MathF.Max(length, MathF.Ceiling(positions[i] / 32) * 32);
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 0.3f + 0.4f * positions[3 * i + 2] / length);
        return uv;
    }

    // ice streaks (texture u) along the road, bent around curves and S-bends; roads are found by
    // where their surface leaves the block (open edges on cell faces)
    public static void Apply(List<MeshPiece> pieces, HashSet<MeshPiece> roadLaid)
    {
        if (roadLaid.Count == 0) return;
        var edges = new Dictionary<((int, int, int), (int, int, int)), (Vector3 A, Vector3 B, int Count)>();
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
                    edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : (e0, e1, 1);
                }
            }
        }
        static float Cell(float x) => MathF.Round(x / 32) * 32;
        static bool OnFace(float x) => MathF.Abs(x - Cell(x)) < 0.05f;
        // (along x = const?, face coordinate) -> span along the face
        var openings = new Dictionary<(bool OnX, float At), (float Min, float Max)>();
        foreach (var (a, b, count) in edges.Values.Where(e => e.Count == 1))
            foreach (var onX in new[] { true, false })
            {
                var (fa, fb, sa, sb) = onX ? (a.X, b.X, a.Z, b.Z) : (a.Z, b.Z, a.X, b.X);
                if (!OnFace(fa) || !OnFace(fb) || Cell(fa) != Cell(fb)) continue;
                var key = (onX, Cell(fa));
                var (lo, hi) = (MathF.Min(sa, sb), MathF.Max(sa, sb));
                openings[key] = openings.TryGetValue(key, out var o) ? (MathF.Min(o.Min, lo), MathF.Max(o.Max, hi)) : (lo, hi);
            }
        var real = openings.Where(o => o.Value.Max - o.Value.Min > 4).ToList();
        var onXs = real.Where(o => o.Key.OnX).ToList();
        var onZs = real.Where(o => !o.Key.OnX).ToList();
        float Length(List<KeyValuePair<(bool OnX, float At), (float Min, float Max)>> l) => l.Sum(o => o.Value.Max - o.Value.Min);

        Func<Vector3, Vector2>? layout = null;
        // travel along z: openings on z = const faces
        if (onZs.Count >= 2 && (onXs.Count < 2 || Length(onZs) >= Length(onXs)) || onZs.Count == 1 && onXs.Count == 0)
            layout = Along(onZs, p => p.Z, p => p.X);
        else if (onXs.Count >= 2 || onXs.Count == 1 && onZs.Count == 0)
            layout = Along(onXs, p => p.X, p => p.Z);
        else if (onXs.Count == 1 && onZs.Count == 1)
        {
            var (ox, oz) = (onXs[0], onZs[0]);
            var center = new Vector2(ox.Key.At, oz.Key.At);
            var (rx0, rx1) = (ox.Value.Min - center.Y, ox.Value.Max - center.Y);
            var (rz0, rz1) = (oz.Value.Min - center.X, oz.Value.Max - center.X);
            // both openings radial, same radii: a quarter turn around the shared corner
            if (rx0 * rx1 >= 0 && rz0 * rz1 >= 0
                && MathF.Abs(MathF.Min(MathF.Abs(rx0), MathF.Abs(rx1)) - MathF.Min(MathF.Abs(rz0), MathF.Abs(rz1))) < 1.5f
                && MathF.Abs(MathF.Max(MathF.Abs(rx0), MathF.Abs(rx1)) - MathF.Max(MathF.Abs(rz0), MathF.Abs(rz1))) < 1.5f)
            {
                var (toX, toZ) = (MathF.Sign(rx0 + rx1), MathF.Sign(rz0 + rz1));
                var outer = MathF.Ceiling(MathF.Max(MathF.Abs(rx0), MathF.Abs(rx1)) / 32 - 0.01f) * 32;
                // like the S-bends: along the middle line (whole tiles end to end), across from it
                var tiles = MathF.Max(1, MathF.Round((outer - 16) * MathF.PI / 2 / 32));
                layout = p =>
                {
                    var d = new Vector2(p.X, p.Z) - center;
                    var angle = MathF.Atan2(d.X * toZ, d.Y * toX);
                    return new Vector2(angle / (MathF.PI / 2) * tiles, (outer - d.Length()) / 32);
                };
            }
        }
        if (layout is null) return;

        // across from the road's middle: S-bends (openings offset sideways) bend the streaks with them
        Func<Vector3, Vector2> Along(List<KeyValuePair<(bool OnX, float At), (float Min, float Max)>> ends, Func<Vector3, float> along, Func<Vector3, float> across)
        {
            var mids = ends.Select(o => (o.Value.Min + o.Value.Max) / 2).ToList();
            if (mids.Max() - mids.Min() < 1) return p => new Vector2(along(p) / 32, across(p) / 32);
            // the road's side edges (open edges off the cell faces)
            var sides = edges.Values.Where(e => e.Count == 1 && !(OnFace(along(e.A)) && OnFace(along(e.B)) && Cell(along(e.A)) == Cell(along(e.B))))
                .Select(e => (P: new Vector2(along(e.A), across(e.A)), Q: new Vector2(along(e.B), across(e.B)))).ToArray();
            // middle line, refined to cut the sides at right angles
            var (start, end) = (ends.Min(o => o.Key.At), ends.Max(o => o.Key.At));
            var line = Enumerable.Range(0, (int)(end - start) + 1).Select(k => new Vector2(start + k, MiddleAt(start + k))).ToArray();
            for (int pass = 0; pass < 4; pass++)
                for (int k = 0; k < line.Length; k++)
                {
                    var tangent = k == 0 || k == line.Length - 1 ? Vector2.UnitX : Vector2.Normalize(line[k + 1] - line[k - 1]);
                    var normal = new Vector2(-tangent.Y, tangent.X);
                    if (Hit(line[k], normal) is { } up && Hit(line[k], -normal) is { } down) line[k] += normal * (up - down) / 2;
                }
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

            float MiddleAt(float a)
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
            float? Hit(Vector2 from, Vector2 dir)
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
}
