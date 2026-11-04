using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

internal static class Trees
{
    private static readonly Lazy<Vegetation> Vegetation = new(() => new Vegetation(Path.GetDirectoryName(Paths.ExtractedStadium)!));

    // before the blocks are renamed
    public static List<(CGameCtnBlock Block, string Name, IReadOnlyList<(string Model, Matrix4x4 Place)> Trees)> LostToBodies(ConversionRun run)
        => run.Customs.Keys.Where(b => !run.Stacked.Contains(b))
            .Select(b => (Block: b, Name: b.Name, Trees: run.Shapes.Trees(b.Name, b.IsGround, VariantOf(b), MobilOf(b), run.Clips(b))))
            .Where(t => t.Trees.Count > 0).ToList();

    // a custom body drops the trees its prefab placed; the environment's vegetation items go where they stood
    public static void Plant(ConversionRun run, CGameCtnBlock block, string name, IReadOnlyList<(string Model, Matrix4x4 Place)> trees)
    {
        var place = block.IsFree
            ? Matrix4x4.CreateFromYawPitchRoll(block.YawPitchRoll!.Value.X, block.YawPitchRoll.Value.Y, block.YawPitchRoll.Value.Z)
                * Matrix4x4.CreateTranslation(block.AbsolutePositionInMap!.Value.X, block.AbsolutePositionInMap.Value.Y, block.AbsolutePositionInMap.Value.Z)
            : Placement.Of(block, run.Shapes.Units(name, block.IsGround, VariantOf(block)) is { Count: > 0 } units ? (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1) : (1, 1));
        place *= Matrix4x4.CreateTranslation(0, -Placement.EnvironmentDrop(run.Collection), 0);
        // the terrain it stands on doesn't count, the road over it does
        var solid = run.Shapes.CollisionTriangles(name, block.IsGround, VariantOf(block), MobilOf(block))?
            .Where(t => ItemBuilder.ShaderOf(t.Material, run.Collection) is null).ToList() ?? [];
        foreach (var (model, local) in trees)
        {
            var w = local * place;
            if (Vegetation.Value.For(run.Collection, model, w.Translation, RoomAbove(solid, local.Translation)) is not { } stand) { Bump(run.Report.TreesMissing, model); continue; }
            // yaw, pitch, roll as CreateFromYawPitchRoll builds them
            var (pitch, yaw, roll) = (MathF.Asin(Math.Clamp(-w.M32, -1, 1)), MathF.Atan2(w.M31, w.M33), MathF.Atan2(w.M12, w.M22));
            var tree = run.Map.PlaceAnchoredObject(new Ident(stand.Item, run.Map.Collection!.Value, "Nadeo"), new Vec3(w.M41, w.M42, w.M43), new Vec3(yaw, pitch, roll), default);
            tree.Flags = (short)(stand.Variant << 8);
            tree.Scale = 1;
            tree.BlockUnitCoord = new Byte3(255, 255, 255);
            Bump(run.Report.Trees, (model, stand.Item));
        }
    }

    private static float RoomAbove(List<(Vector3 A, Vector3 B, Vector3 C, string Material)> tris, Vector3 o)
    {
        var room = float.PositiveInfinity;
        foreach (var (a, b, c, _) in tris)
        {
            // Möller-Trumbore, ray straight up
            var (e1, e2) = (b - a, c - a);
            var p = Vector3.Cross(Vector3.UnitY, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-6f) continue;
            var s = o - a;
            var u = Vector3.Dot(s, p) / det;
            if (u is < 0 or > 1) continue;
            var q = Vector3.Cross(s, e1);
            var v = q.Y / det;
            if (v < 0 || u + v > 1) continue;
            var t = Vector3.Dot(e2, q) / det;
            if (t > 0 && t < room) room = t;
        }
        return room;
    }
}
