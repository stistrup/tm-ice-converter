using System.Numerics;

namespace TmIceConverter;

// connected snow on a terrain cell, and where each patch meets the cell's edge
internal static class CoverPatches
{
    public const float AtEdge = 0.05f;
    private const float OnPoint = 0.05f;

    public record struct Box(float X0, float X1, float Z0, float Z1);

    // side bits -X, +X, -Z, +Z
    public record struct Edge(int Side, Vector2 A, Vector2 B);

    public static Box BoxOf(IEnumerable<MeshPiece> pieces)
    {
        var (x0, x1, z0, z1) = (float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
        foreach (var p in pieces)
            for (int i = 0; i < p.Positions.Length; i += 3)
                (x0, x1, z0, z1) = (Math.Min(x0, p.Positions[i]), Math.Max(x1, p.Positions[i]), Math.Min(z0, p.Positions[i + 2]), Math.Max(z1, p.Positions[i + 2]));
        return new(x0, x1, z0, z1);
    }

    public static IEnumerable<Vector3[]> Triangles(MeshPiece p)
    {
        Vector3 At(int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
        for (int t = 0; t < p.Indices.Length; t += 3) yield return [At(p.Indices[t]), At(p.Indices[t + 1]), At(p.Indices[t + 2])];
    }

    // patch of each triangle (joined by shared corners, 1 mm)
    public static int[] Of(IReadOnlyList<Vector3[]> tris)
    {
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var corners = new Dictionary<(int, int, int), int>();
        for (int n = 0; n < tris.Count; n++)
            foreach (var v in tris[n])
            {
                var key = ((int)MathF.Round(v.X * 1000), (int)MathF.Round(v.Y * 1000), (int)MathF.Round(v.Z * 1000));
                if (corners.TryGetValue(key, out var m)) parent[Find(n)] = Find(m);
                else corners[key] = n;
            }
        return Enumerable.Range(0, tris.Count).Select(Find).ToArray();
    }

    // triangle sides lying on the cell's edge
    public static IEnumerable<Edge> Edges(Vector3[] tri, Box box)
    {
        for (int i = 0; i < 3; i++)
        {
            var (a, b) = (tri[i], tri[(i + 1) % 3]);
            int On(Vector3 v) => (v.X - box.X0 < AtEdge ? 1 : 0) | (box.X1 - v.X < AtEdge ? 2 : 0) | (v.Z - box.Z0 < AtEdge ? 4 : 0) | (box.Z1 - v.Z < AtEdge ? 8 : 0);
            var both = On(a) & On(b);
            for (int side = 0; side < 4; side++)
                if ((both >> side & 1) != 0 && Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) > AtEdge) yield return new(side, new(a.X, a.Z), new(b.X, b.Z));
        }
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    // patches reaching the edge stay snow only through a keep point; patches off the edge stay snow
    public static IEnumerable<MeshPiece> Keep(List<(MeshPiece White, MeshPiece From)> cut, IReadOnlyList<Vector2> keep, string snow, Box box)
    {
        var tris = cut.SelectMany(c => Triangles(c.White)).ToList();
        var patch = Of(tris);
        var (onEdge, kept) = (new HashSet<int>(), new HashSet<int>());
        for (int n = 0; n < tris.Count; n++)
            foreach (var e in Edges(tris[n], box))
            {
                onEdge.Add(patch[n]);
                if (keep.Any(k => DistanceToSegment(k, e.A, e.B) < OnPoint)) kept.Add(patch[n]);
            }
        var start = 0;
        foreach (var (white, from) in cut)
        {
            var count = white.Indices.Length / 3;
            var snowy = Enumerable.Range(0, count).ToLookup(t => !onEdge.Contains(patch[start + t]) || kept.Contains(patch[start + t]));
            int[] Of(bool k) => snowy[k].SelectMany(t => white.Indices[(3 * t)..(3 * t + 3)]).ToArray();
            if (Of(true) is { Length: > 0 } inSnow) yield return white with { Material = snow, Name = from.Name + "_snow", Indices = inSnow };
            if (Of(false) is { Length: > 0 } ground) yield return white with { Indices = ground };
            start += count;
        }
    }
}
