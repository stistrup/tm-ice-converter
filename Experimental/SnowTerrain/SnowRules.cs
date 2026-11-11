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
    // open water; shores (WaterHill, LakeShore) are ground at the water's edge
    private static bool IsWater(string terrain) => System.Text.RegularExpressions.Regex.IsMatch(terrain, "Lake|Sea|Water|River") && !System.Text.RegularExpressions.Regex.IsMatch(terrain, "Hill|Shore");

    public static bool IsShore(string terrain) => System.Text.RegularExpressions.Regex.IsMatch(terrain, "Lake|Sea|Water|River") && System.Text.RegularExpressions.Regex.IsMatch(terrain, "Hill|Shore");

    // snow terrain there: in a snow area, land, not built on
    public static bool SnowsCell(ConversionRun run, int x, int z)
        => run.Rules.SnowsTerrainAt(x, z) && run.Grid.Any(t => t.Coord.X == x && t.Coord.Z == z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name)
            && !run.Snow.BuiltOn.Any(b => b.X == x && b.Z == z && b.Y <= t.Coord.Y + 1));

    // the native block goes, the game still draws the ground; placed as variant 0 of the archetype
    public static bool TerrainCell(CGameCtnBlock block, ConversionRun run)
    {
        var (name, coord) = (block.Name, block.Coord);
        if (!run.Shapes.IsTerrain(name) || !run.Rules.SnowsTerrainAt(coord.X, coord.Z) || IsWater(name)
            || run.Snow.BuiltOn.Any(c => c.X == coord.X && c.Z == coord.Z && c.Y <= coord.Y + 1)
            || run.Items.SnowTerrainFor(name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, run.Snow.UnderDeco.Contains((coord.X, coord.Z))) is not { } snowBlock) return false;
        run.Snowed.Add((block, VariantOf(block), MobilOf(block)));
        Snow(run, block, snowBlock);
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

    // snow picked on a non-terrain block: its deco surface in the snow ground
    public static bool PickedSnow(CGameCtnBlock block, ConversionRun run)
    {
        // a custom block loses the native trigger: the block goes to the air, its ground onto the terrain cell
        if (block.IsGround && !block.IsFree && BlockNames.IsGameplayBlock(block.Name))
        {
            if (!SnowedGameplay(run).ContainsValue(block)) return false;
            // its terrain-bringing ground variant would still lay its dirt: the plain air variant
            block.Flags &= ~BlockFlags.Ground & ~BlockFlags.Replacement & ~(0x3F << 21);
            Bump(run.Report.SnowTerrain, block.Name);
            return true;
        }
        if (run.Shapes.IsTerrain(block.Name) || block.IsPillar || run.Shapes.IsPillarBlock(block.Name) || block.IsFree
            || run.Items.SnowDecoFor(block.Name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, block.IsGround) is not { } snowDeco) return false;
        Snow(run, block, snowDeco);
        Bump(run.Report.SnowTerrain, block.Name);
        return true;
    }

    // the terrain cell under a gameplay block picked to snow carries that block's ground
    public static bool UnderSnowedGameplay(CGameCtnBlock block, ConversionRun run)
    {
        var coord = block.Coord;
        if (!run.Shapes.IsTerrain(block.Name) || IsWater(block.Name) || !SnowedGameplay(run).TryGetValue((coord.X, coord.Z), out var above)
            || run.Items.GameplayGroundFor(above.Name, VariantOf(above), MobilOf(above), run.Map.Collection!.Value) is not { } ground) return false;
        Snow(run, block, ground);
        (block.Coord, block.Direction) = (above.Coord, above.Direction);
        block.Flags &= ~0xFFF & ~(0x3F << 21);
        return true;
    }

    private static void Snow(ConversionRun run, CGameCtnBlock block, IceBlock body)
    {
        run.Customs[block] = body;
        run.SnowBodies.Add(block);
    }

    // ground gameplay blocks picked to snow, by cell, with terrain under them; read before any of them goes to the air
    private static Dictionary<(int X, int Z), CGameCtnBlock> SnowedGameplay(ConversionRun run)
        => run.SnowedGameplay ??= run.Grid.Where(b => b.IsGround && !b.IsFree && BlockNames.IsGameplayBlock(b.Name) && run.Rules.PieceChoice(b) == "snow"
                && run.Grid.Any(t => t.Coord.X == b.Coord.X && t.Coord.Z == b.Coord.Z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name)))
            .GroupBy(b => (b.Coord.X, b.Coord.Z)).ToDictionary(g => g.Key, g => g.First());

    public static bool DecoInTerrain(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.InTerrain(block) || !run.Rules.SnowsTerrainAt(block.Coord.X, block.Coord.Z)
            || run.Items.SnowDecoFor(block.Name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value) is not { } snowDeco) return false;
        Snow(run, block, snowDeco);
        Bump(run.Report.SnowTerrain, block.Name);
        return true;
    }
}
