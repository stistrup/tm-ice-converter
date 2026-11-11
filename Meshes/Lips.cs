using System.Numerics;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// the road's side faces under its borders
internal sealed class Lips
{
    private const float LipTouch = 0.02f;
    // lips run from below the road up to the border: 0.8 m (tech, bump), 1 m (dirt); flush borders touch flat faces
    private const float LipMinHeight = 0.5f, LipMaxHeight = 1.5f;
    // steeper than 60°
    private const float LipMaxNormalY = 0.5f;

    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> borders = [];

    public void Collect(CPlugStaticObjectModel som, Matrix4x4 world)
    {
        if (som.Mesh is not { } mesh) return;
        var geoms = mesh.ShadedGeoms ?? [];
        var hasLods = geoms.Any(g => g.LodMask != 0);
        var bestLod = geoms.Select(g => g.LodMask).Where(m => m != 0).Select(m => m & -m).DefaultIfEmpty(1).Min();
        foreach (var geom in geoms.Where(g => g.LodMask == 0 ? !hasLods : (g.LodMask & bestLod) != 0))
        {
            if (!MeshData.MaterialName(mesh, geom.MaterialIndex).Contains("Border")) continue;
            if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles { VertexStreams: [{ Positions: { } pos }, ..], IndexBuffer.Indices: { } idx }) continue;
            var p = MeshData.Transform(pos, world);
            Vector3 At(int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
            for (int t = 0; t + 2 < idx.Length; t += 3) borders.Add((At(idx[t]), At(idx[t + 1]), At(idx[t + 2])));
        }
    }

    // faces with a corner on a border's surface, as tall as a lip
    public (int[] Others, int[] Lip) Split(float[] positions, int[] indices)
    {
        if (borders.Count == 0) return (indices, []);
        Vector3 At(int i) => new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        bool OnBorder(Vector3 q) => borders.Any(b => Geometry.DistanceToTriangle(q, b.A, b.B, b.C) < LipTouch);
        var touching = new Dictionary<int, bool>();
        bool Touches(int v) => touching.TryGetValue(v, out var on) ? on : touching[v] = OnBorder(At(v));
        var (rest, lip) = (new List<int>(), new List<int>());
        bool LipHigh(int t)
        {
            var ys = indices[t..(t + 3)].Select(v => positions[3 * v + 1]).ToArray();
            // steep only: sloped road beside a border rises as much
            var n = Vector3.Normalize(Vector3.Cross(At(indices[t + 1]) - At(indices[t]), At(indices[t + 2]) - At(indices[t])));
            return ys.Max() - ys.Min() is >= LipMinHeight and <= LipMaxHeight && MathF.Abs(n.Y) < LipMaxNormalY;
        }
        for (int t = 0; t + 2 < indices.Length; t += 3)
            (LipHigh(t) && (Touches(indices[t]) || Touches(indices[t + 1]) || Touches(indices[t + 2])) ? lip : rest).AddRange(indices[t..(t + 3)]);
        return ([.. rest], [.. lip]);
    }
}
