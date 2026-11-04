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
