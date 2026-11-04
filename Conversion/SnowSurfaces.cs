using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;

namespace TmIceConverter;

// converted surfaces, to tell if an item is on ice
internal sealed class SnowSurfaces
{
    private const float Reach = 6;
    private const float ReachBelow = 8;
    private readonly Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C)>> columns = [];

    public SnowSurfaces(CGameCtnChallenge map, IEnumerable<CGameCtnBlock> items, IReadOnlySet<CGameCtnBlock> swapped,
        IReadOnlySet<CGameCtnBlock> skinned, IBlockShapes shapes, bool zoneSides)
    {
        var drop = Placement.EnvironmentDrop(map.Collection?.ToString());
        foreach (var block in items.Concat(swapped).Concat(skinned))
        {
            if (block.IsPillar || shapes.IsPillarBlock(block.Name)
                || shapes.CollisionTriangles(block.Name, block.IsGround, VariantOf(block), MobilOf(block)) is not { } tris) continue;
            Matrix4x4 place;
            if (block.IsFree)
            {
                var (a, r) = (block.AbsolutePositionInMap ?? default, block.YawPitchRoll ?? default);
                place = Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(a.X, a.Y, a.Z);
            }
            else
            {
                var units = shapes.Units(block.Name, block.IsGround, VariantOf(block)) ?? [new Int3(0, 0, 0)];
                place = Placement.Of(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)) * Matrix4x4.CreateTranslation(0, -drop, 0);
            }
            // swapped road's Deco is still env ground
            foreach (var (a, b, c, material) in tris)
            {
                if (material.StartsWith("Deco") && !block.Name.StartsWith("Deco") && swapped.Contains(block)) continue;
                if (skinned.Contains(block) && !ItemBuilder.SkinCovers(block.Name, material, zoneSides)) continue;
                var t = (A: Vector3.Transform(a, place), B: Vector3.Transform(b, place), C: Vector3.Transform(c, place));
                if (Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A)) is { } n && MathF.Abs(n.Y) < 0.3f) continue;
                var (x0, x1) = ((int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) / 32), (int)MathF.Floor(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) / 32));
                var (z0, z1) = ((int)MathF.Floor(MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)) / 32), (int)MathF.Floor(MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)) / 32));
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                    (columns.TryGetValue((x, z), out var list) ? list : columns[(x, z)] = []).Add(t);
            }
        }
    }

    // stands on ice, and the frozen tree wouldn't reach through ice above it
    public bool Touches(Vector3 root, float height)
    {
        var hs = Heights(root).ToList();
        return hs.Any(h => h >= root.Y - ReachBelow && h <= root.Y + OnIce) && !hs.Any(h => h > root.Y + OnIce && h <= root.Y + height);
    }

    private const float OnIce = 0.5f;

    public bool Flush(Vector3 p) => Heights(p).Any(h => MathF.Abs(h - p.Y) <= 0.02f);

    private IEnumerable<float> Heights(Vector3 p)
    {
        if (!columns.TryGetValue(((int)MathF.Floor(p.X / 32), (int)MathF.Floor(p.Z / 32)), out var tris)) yield break;
        foreach (var (a, b, c) in tris)
        {
            var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (MathF.Abs(d) < 1e-6f) continue;
            var u = ((p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z)) / d;
            var v = ((b.X - a.X) * (p.Z - a.Z) - (p.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            yield return a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
        }
    }

    public bool Under(Vector3 p)
    {
        if (!columns.TryGetValue(((int)MathF.Floor(p.X / 32), (int)MathF.Floor(p.Z / 32)), out var tris)) return false;
        foreach (var (a, b, c) in tris)
        {
            var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (MathF.Abs(d) < 1e-6f) continue;
            var u = ((p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z)) / d;
            var v = ((b.X - a.X) * (p.Z - a.Z) - (p.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var below = p.Y - (a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y));
            if (below <= ReachBelow && below >= -Reach) return true;
        }
        return false;
    }
}
