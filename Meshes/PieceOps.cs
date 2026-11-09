using System.Numerics;

namespace TmIceConverter;

internal static class PieceOps
{
    public static float Area(MeshPiece p)
    {
        Vector3 At(int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
        return Enumerable.Range(0, p.Indices.Length / 3).Sum(t => Vector3.Cross(At(p.Indices[3 * t + 1]) - At(p.Indices[3 * t]), At(p.Indices[3 * t + 2]) - At(p.Indices[3 * t])).Length() / 2);
    }

    public static MeshPiece Turned(MeshPiece p, Matrix4x4 m)
    {
        static float[] Map(float[] a, Func<Vector3, Vector3> f)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i += 3) (r[i], r[i + 1], r[i + 2]) = f(new Vector3(a[i], a[i + 1], a[i + 2])) is var v ? (v.X, v.Y, v.Z) : default;
            return r;
        }
        return p with { Positions = Map(p.Positions, v => Vector3.Transform(v, m)), Normals = p.Normals is { } n ? Map(n, v => Vector3.TransformNormal(v, m)) : null };
    }

    // shore in snow: visible ground above the height becomes snow, cut along it; below keeps its own material
    public static List<MeshPiece> SnowAbove(List<MeshPiece> pieces, float height, string snow)
    {
        var result = new List<MeshPiece>();
        foreach (var p in pieces)
        {
            if (!p.Visible || p.Material == "Water" || p.Material == snow) { result.Add(p); continue; }
            var (above, below) = SplitAt(p, height);
            if (above is not null) result.Add(above with { Material = snow, Name = p.Name + "_snow" });
            if (below is not null) result.Add(below);
        }
        return result;
    }

    // triangles clipped against the plane y = height, attributes interpolated; null for an empty side
    public static (MeshPiece? Above, MeshPiece? Below) SplitAt(MeshPiece p, float height)
    {
        var count = p.Positions.Length / 3;
        var streams = new[] { p.Positions, p.Normals, p.Uv0, p.Uv1, p.Colors };
        var strides = streams.Select(s => s is null ? 0 : s.Length / count).ToArray();
        var sides = new[] { (Streams: streams.Select(_ => new List<float>()).ToArray(), Up: true), (Streams: streams.Select(_ => new List<float>()).ToArray(), Up: false) };
        float[] Vertex(int i) => streams.SelectMany((s, k) => s is null ? [] : s.AsSpan(i * strides[k], strides[k]).ToArray()).ToArray();
        void Emit(List<float>[] into, float[] v)
        {
            var at = 0;
            for (int k = 0; k < streams.Length; k++) { into[k].AddRange(v.AsSpan(at, strides[k]).ToArray()); at += strides[k]; }
        }
        for (int t = 0; t < p.Indices.Length; t += 3)
        {
            var tri = p.Indices[t..(t + 3)].Select(Vertex).ToArray();
            foreach (var (into, up) in sides)
            {
                // Sutherland-Hodgman against one half-space, then a fan
                var poly = new List<float[]>();
                for (int i = 0; i < 3; i++)
                {
                    var (a, b) = (tri[i], tri[(i + 1) % 3]);
                    bool In(float[] v) => up ? v[1] >= height : v[1] < height;
                    if (In(a)) poly.Add(a);
                    if (In(a) != In(b))
                    {
                        var f = (height - a[1]) / (b[1] - a[1]);
                        poly.Add(a.Zip(b, (x, y) => x + (y - x) * f).ToArray());
                    }
                }
                for (int i = 1; i + 1 < poly.Count; i++) { Emit(into, poly[0]); Emit(into, poly[i]); Emit(into, poly[i + 1]); }
            }
        }
        MeshPiece? Side(List<float>[] s)
        {
            if (s[0].Count == 0) return null;
            float[]? Of(int k) => streams[k] is null ? null : s[k].ToArray();
            return p with { Positions = s[0].ToArray(), Normals = Of(1), Uv0 = Of(2), Uv1 = Of(3), Colors = Of(4), Indices = Enumerable.Range(0, s[0].Count / 3).ToArray() };
        }
        return (Side(sides[0].Streams), Side(sides[1].Streams));
    }

    // drop the foot at terrain level (flickers)
    public static List<MeshPiece> WithoutFoot(List<MeshPiece> skin)
    {
        static bool IsSnow(MeshPiece p) => p.Material.StartsWith("PlatformIce_Deco") || p.Material == "CustomSnow";
        Vector3 At(MeshPiece p, int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
        bool Flat(MeshPiece p, int t) => Vector3.Cross(At(p, p.Indices[t + 1]) - At(p, p.Indices[t]), At(p, p.Indices[t + 2]) - At(p, p.Indices[t])) is var n
            && n.LengthSquared() > 0 && Vector3.Normalize(n).Y > 0.99f;
        float Top(MeshPiece p, int t) => Math.Max(At(p, p.Indices[t]).Y, Math.Max(At(p, p.Indices[t + 1]).Y, At(p, p.Indices[t + 2]).Y));
        var flat = skin.Where(IsSnow).SelectMany(p => Enumerable.Range(0, p.Indices.Length / 3).Select(i => 3 * i).Where(t => Flat(p, t)).Select(t => Top(p, t))).ToList();
        if (flat.Count == 0) return skin;
        var foot = flat.Min() + 0.05f;
        return skin.Select(p => !IsSnow(p) ? p : p with { Indices = Enumerable.Range(0, p.Indices.Length / 3).Select(i => 3 * i).Where(t => !(Flat(p, t) && Top(p, t) <= foot)).SelectMany(t => p.Indices[t..(t + 3)]).ToArray() })
            .Where(p => p.Indices.Length > 0).ToList();
    }
}
