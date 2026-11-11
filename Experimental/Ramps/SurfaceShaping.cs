using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// ice raised to meet what's next to it or under it
internal static class SurfaceShaping
{
    private const float IceLift = 0.01f;

    // coplanar with the native ice/dirt it covers: raise the ice and what lies on it, easing to zero where it meets the block edge so neighbours line up
    public static void LiftSurface(CPlugStaticObjectModel model, float sizeX, float sizeZ)
    {
        var mesh = model.Mesh!;
        var streams = IceStreams(mesh);
        if (streams.Count == 0) return;
        var all = streams.SelectMany(p => p).ToList();
        const float touch = 0.05f;
        var sides = new (bool On, Func<Vec3, float> Dist)[]
        {
            (all.Min(p => p.X) < touch, p => p.X), (all.Max(p => p.X) > sizeX - touch, p => sizeX - p.X),
            (all.Min(p => p.Z) < touch, p => p.Z), (all.Max(p => p.Z) > sizeZ - touch, p => sizeZ - p.Z),
        };
        var surface = ThemeSurface.Of(model);
        foreach (var positions in AllStreams(mesh))
            for (int i = 0; i < positions.Length; i++)
                if (streams.Contains(positions) || surface?.Rests(positions[i]) == true)
                {
                    var d = sides.Where(s => s.On).Select(s => s.Dist(positions[i])).DefaultIfEmpty(16).Min();
                    positions[i] = positions[i] with { Y = positions[i].Y + IceLift * MathF.Sin(MathF.PI / 2 * Math.Clamp(d / 16, 0, 1)) };
                }
    }

    private static HashSet<Vec3[]> IceStreams(CPlugSolid2Model mesh) => (mesh.ShadedGeoms ?? [])
        .Where(g => mesh.CustomMaterials?[g.MaterialIndex].MaterialUserInst?.Link?.Contains("PlatformIce") == true)
        .Select(g => mesh.Visuals?[g.VisualIndex]).OfType<CPlugVisualIndexedTriangles>()
        .Select(v => v.VertexStreams?.FirstOrDefault()?.Positions).OfType<Vec3[]>().ToHashSet();

    private static List<Vec3[]> AllStreams(CPlugSolid2Model mesh) => (mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>()
        .Select(v => v.VertexStreams?.FirstOrDefault()?.Positions).OfType<Vec3[]>().Distinct().ToList();

    private const float RampLength = 16;

    // a skinned neighbour sits higher: ice, its collision and whatever lies on it (pads, decals) rise to meet it at those sides, easing to zero inward
    public static void RampSurface(CPlugStaticObjectModel model, int sides, float height, float sizeX, float sizeZ)
    {
        var dist = new Func<float, float, float>[] { (x, _) => x, (x, _) => sizeX - x, (_, z) => z, (_, z) => sizeZ - z };
        float Lift(float x, float z)
        {
            var d = Enumerable.Range(0, 4).Where(i => (sides >> i & 1) != 0).Min(i => dist[i](x, z));
            return height * MathF.Cos(MathF.PI / 2 * Math.Clamp(d / RampLength, 0, 1));
        }
        var mesh = model.Mesh!;
        var surface = ThemeSurface.Of(model);
        var ice = IceStreams(mesh);
        foreach (var positions in AllStreams(mesh))
            for (int i = 0; i < positions.Length; i++)
                if (Lift(positions[i].X, positions[i].Z) is var lift and > 0 && (ice.Contains(positions) || surface?.Rests(positions[i]) == true))
                    positions[i] = positions[i] with { Y = positions[i].Y + lift };
        if (surface is null) return;
        foreach (var v in surface.Used) surface.Verts[v] = surface.Verts[v] with { Y = surface.Verts[v].Y + Lift(surface.Verts[v].X, surface.Verts[v].Z) };
    }

    private const float RestsWithin = 0.05f;

    // ice collision looked up from above, before the ramp
    private sealed class ThemeSurface
    {
        public readonly Vec3[] Verts;
        public readonly HashSet<int> Used;
        private readonly Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C)>> cells = [];
        private const float Cell = 4;
        // points on a cell border find triangles from both sides
        private const float Margin = 0.01f;

        public static ThemeSurface? Of(CPlugStaticObjectModel model) => model.Shape is CPlugSurface { Surf: CPlugSurface.Mesh { Vertices: { } verts, Triangles: { } tris } }
            ? new ThemeSurface(verts, tris.Where(t => ((CPlugSurface.MaterialId)t.U02).ToString().Contains("Ice")).Select(t => t.Indices).ToList()) : null;

        public ThemeSurface(Vec3[] verts, List<Int3> tris)
        {
            Verts = verts;
            Used = tris.SelectMany(t => new[] { t.X, t.Y, t.Z }).ToHashSet();
            foreach (var t in tris)
            {
                var (a, b, c) = (V(verts[t.X]), V(verts[t.Y]), V(verts[t.Z]));
                // walls have no area seen from above
                if (MathF.Abs(Cross(b - a, c - a)) < 1e-4f) continue;
                for (var x = (int)MathF.Floor((Min(a.X, b.X, c.X) - Margin) / Cell); x <= (int)MathF.Floor((Max(a.X, b.X, c.X) + Margin) / Cell); x++)
                    for (var z = (int)MathF.Floor((Min(a.Z, b.Z, c.Z) - Margin) / Cell); z <= (int)MathF.Floor((Max(a.Z, b.Z, c.Z) + Margin) / Cell); z++)
                        (cells.TryGetValue((x, z), out var l) ? l : cells[(x, z)] = []).Add((a, b, c));
            }
        }

        // on the surface or just above it
        public bool Rests(Vec3 p)
        {
            if (!cells.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)), out var list)) return false;
            foreach (var (a, b, c) in list)
            {
                var area = Cross(b - a, c - a);
                var (u, v) = (Cross(new Vector3(p.X, 0, p.Z) - a, c - a) / area, Cross(b - a, new Vector3(p.X, 0, p.Z) - a) / area);
                if (u < -1e-3f || v < -1e-3f || u + v > 1 + 1e-3f) continue;
                var above = p.Y - (a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y));
                if (above is > -0.005f and < RestsWithin) return true;
            }
            return false;
        }

        private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
        private static float Cross(Vector3 a, Vector3 b) => a.X * b.Z - a.Z * b.X;
        private static float Min(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));
        private static float Max(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));
    }
}
