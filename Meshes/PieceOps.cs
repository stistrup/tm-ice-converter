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
        => SnowWhere(pieces, snow, (p, i) => height - p.Positions[3 * i + 1]);

    // cliff in snow at its foot: snow rests up to the height above flat ground where it's flat, less the steeper it gets, none up a wall
    public static List<MeshPiece> SnowFoot(List<MeshPiece> pieces, float height, string snow)
        => SnowWhere(pieces, snow, (p, i) => p.Positions[3 * i + 1] - FlatGround - height * Math.Max(0, p.Normals?[3 * i + 1] ?? 1));

    // a flat terrain block's top in its own frame, every vista
    private const float FlatGround = 2;

    // visible ground where the field is below zero becomes snow, cut along its zero line
    private static List<MeshPiece> SnowWhere(List<MeshPiece> pieces, string snow, Func<MeshPiece, int, float> field)
    {
        var result = new List<MeshPiece>();
        foreach (var p in pieces)
        {
            if (!p.Visible || p.Material == "Water" || p.Material == snow) { result.Add(p); continue; }
            var (white, rest) = Split(p, Enumerable.Range(0, p.Positions.Length / 3).Select(i => field(p, i)).ToArray());
            if (white is not null) result.Add(white with { Material = snow, Name = p.Name + "_snow" });
            if (rest is not null) result.Add(rest);
        }
        return result;
    }

    // triangles clipped along the zero line of a value per vertex, attributes interpolated; null for an empty side
    public static (MeshPiece? Below, MeshPiece? Above) Split(MeshPiece p, float[] value)
    {
        var count = p.Positions.Length / 3;
        var streams = new[] { p.Positions, p.Normals, p.Uv0, p.Uv1, p.Colors, value };
        var strides = streams.Select(s => s is null ? 0 : s.Length / count).ToArray();
        var sides = new[] { (Streams: streams.Select(_ => new List<float>()).ToArray(), Below: true), (Streams: streams.Select(_ => new List<float>()).ToArray(), Below: false) };
        float[] Vertex(int i) => streams.SelectMany((s, k) => s is null ? [] : s.AsSpan(i * strides[k], strides[k]).ToArray()).ToArray();
        void Emit(List<float>[] into, float[] v)
        {
            var at = 0;
            for (int k = 0; k < streams.Length; k++) { into[k].AddRange(v.AsSpan(at, strides[k]).ToArray()); at += strides[k]; }
        }
        for (int t = 0; t < p.Indices.Length; t += 3)
        {
            var tri = p.Indices[t..(t + 3)].Select(Vertex).ToArray();
            foreach (var (into, below) in sides)
            {
                // Sutherland-Hodgman against one side, then a fan; the value is the vertex's last float
                var poly = new List<float[]>();
                for (int i = 0; i < 3; i++)
                {
                    var (a, b) = (tri[i], tri[(i + 1) % 3]);
                    bool In(float[] v) => below ? v[^1] < 0 : v[^1] >= 0;
                    if (In(a)) poly.Add(a);
                    if (In(a) != In(b))
                    {
                        var f = a[^1] / (a[^1] - b[^1]);
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
