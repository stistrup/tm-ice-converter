using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.BlockFlags;
using static TmSurfaceEditor.Counts;

namespace TmSurfaceEditor;

internal static partial class Trees
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
        var snowed = run.Rules.Options.ConvertTrees && run.CoverBodies.Contains(block);
        // the terrain it stands on doesn't count, the road over it does
        var solid = run.Shapes.CollisionTriangles(name, block.IsGround, VariantOf(block), MobilOf(block))?
            .Where(t => TerrainFaces.ShaderOf(t.Material, run.Collection) is null).ToList() ?? [];
        var all = run.Shapes.CollisionTriangles(name, block.IsGround, VariantOf(block), MobilOf(block)) ?? [];
        foreach (var (model, local) in trees)
        {
            // prefab grass reaches over the bank and the water; a stand-in there floats
            if (GroundUnder(all, local.Translation) is { } ground && WaterMaterial().IsMatch(ground)) { Bump(run.Report.TreesInWater, model); continue; }
            var w = local * place;
            if (Vegetation.Value.For(run.Collection, model, w.Translation, RoomAbove(solid, local.Translation)) is not { } stand) { Bump(run.Report.TreesMissing, model); continue; }
            var frozen = snowed ? new[] { model, Vegetation.Value.ModelOf(run.Collection, stand), stand.Item }
                .Select(id => id is null ? null : ItemSwaps.CoverSwap(run, id)).FirstOrDefault(t => t is not null) : null;
            // yaw, pitch, roll as CreateFromYawPitchRoll builds them
            var (pitch, yaw, roll) = (MathF.Asin(Math.Clamp(-w.M32, -1, 1)), MathF.Atan2(w.M31, w.M33), MathF.Atan2(w.M12, w.M22));
            var ident = frozen is null ? new Ident(stand.Item, run.Map.Collection!.Value, "Nadeo") : new Ident(frozen, new Id(26), "Nadeo"); // Stadium2020
            var tree = run.Map.PlaceAnchoredObject(ident, new Vec3(w.M41, w.M42, w.M43), new Vec3(yaw, pitch, roll), default);
            tree.Flags = (short)(frozen is null ? stand.Variant << 8 : 0);
            tree.Scale = 1;
            tree.BlockUnitCoord = new Byte3(255, 255, 255);
            Bump(run.Report.Trees, (model, frozen ?? stand.Item));
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("Water|Lake|River|Sea")]
    private static partial System.Text.RegularExpressions.Regex WaterMaterial();

    private static float RoomAbove(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)> tris, Vector3 o)
        => Hit(tris, o, Vector3.UnitY).Distance;

    // top surface over the foot (water lies over the lake bed)
    private static string? GroundUnder(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)> tris, Vector3 o)
        => Hit(tris, o + new Vector3(0, 50, 0), -Vector3.UnitY).Material;

    private static (float Distance, string? Material) Hit(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)> tris, Vector3 o, Vector3 dir)
    {
        var (room, material) = (float.PositiveInfinity, (string?)null);
        foreach (var (a, b, c, m) in tris)
        {
            // Möller-Trumbore
            var (e1, e2) = (b - a, c - a);
            var p = Vector3.Cross(dir, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-6f) continue;
            var s = o - a;
            var u = Vector3.Dot(s, p) / det;
            if (u is < 0 or > 1) continue;
            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(dir, q) / det;
            if (v < 0 || u + v > 1) continue;
            var t = Vector3.Dot(e2, q) / det;
            if (t > 0 && t < room) (room, material) = (t, m);
        }
        return (room, material);
    }
}
