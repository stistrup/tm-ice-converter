using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

// per-map snowTerrain areas
internal sealed class SnowArea
{
    // cells where a ground block hides the terrain; air blocks and pillars leave it showing
    public HashSet<Int3> BuiltOn { get; }
    public HashSet<(int X, int Z)> UnderDeco { get; }

    public SnowArea(ConversionRun run)
    {
        var (shapes, rules) = (run.Shapes, run.Rules);
        UnderDeco = run.Grid.Where(b => !b.IsGround && b.Name.StartsWith("Deco") && rules.SnowsTerrainAt(b.Coord.X, b.Coord.Z) && run.BelowGround(b)).SelectMany(b =>
        {
            var size = shapes.BlockSize(b.Name, false, VariantOf(b));
            var (sx, sz) = b.Direction is Direction.East or Direction.West ? (size.Z, size.X) : (size.X, size.Z);
            // one cell can't reach over another
            return sx * sz == 1 ? [] : Enumerable.Range(0, sx).SelectMany(dx => Enumerable.Range(0, sz).Select(dz => (b.Coord.X + dx, b.Coord.Z + dz)));
        }).ToHashSet();
        BuiltOn = run.Grid.Where(b => b.IsGround && !b.IsPillar && !shapes.IsTerrain(b.Name) && !shapes.IsPillarBlock(b.Name)).SelectMany(b => run.Cells[b]).ToHashSet();
    }
}

internal static class SnowRules
{
    private static bool IsWater(string terrain) => System.Text.RegularExpressions.Regex.IsMatch(terrain, "Lake|Sea|Water|River");

    // the native block goes, the game still draws the ground; placed as variant 0 of the archetype
    public static bool TerrainCell(CGameCtnBlock block, ConversionRun run)
    {
        var (name, coord) = (block.Name, block.Coord);
        if (!run.Shapes.IsTerrain(name) || !run.Rules.SnowsTerrainAt(coord.X, coord.Z) || IsWater(name)
            || run.Snow.BuiltOn.Any(c => c.X == coord.X && c.Z == coord.Z && c.Y <= coord.Y + 1)
            || run.Items.SnowTerrainFor(name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, run.Snow.UnderDeco.Contains((coord.X, coord.Z))) is not { } snowBlock) return false;
        run.Customs[block] = snowBlock;
        block.Flags &= ~0xFFF & ~(0x3F << 21);
        Bump(run.Report.SnowTerrain, name);
        return true;
    }

    // a ground pillar's bottom clip lays a full-cell Deco patch (with its vegetation) level with the snow; the air pillar has none
    public static bool PillarOnSnow(CGameCtnBlock block, ConversionRun run)
    {
        var coord = block.Coord;
        if (block.IsGround && (block.IsPillar || run.Shapes.IsPillarBlock(block.Name)) && run.Rules.SnowsTerrainAt(coord.X, coord.Z)
            && run.Grid.Any(t => t.Coord.X == coord.X && t.Coord.Z == coord.Z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name)))
            block.Flags &= ~BlockFlags.Ground;
        return false;
    }

    public static bool DecoInTerrain(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.InTerrain(block) || !run.Rules.SnowsTerrainAt(block.Coord.X, block.Coord.Z)
            || run.Items.SnowDecoFor(block.Name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value) is not { } snowDeco) return false;
        run.Customs[block] = snowDeco;
        Bump(run.Report.SnowTerrain, block.Name);
        return true;
    }
}
