using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// terrain options: empty cells (the game's flat ground, not in the map) get the flat terrain block; shores and cliffs snow by kind
internal static class SnowFill
{
    public static int Run(CGameCtnChallenge map, string collection, BlockCatalog shapes, Translations rules)
    {
        if (FlatBlock(collection) is not { } flat) return 0;
        var size = map.Size;
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var resolver = new ClipResolver(shapes);
        var placed = resolver.OfAll(grid);
        // pillars stand on the ground, SnowRules.PillarOnSnow lifts them
        var owned = placed.Where((_, i) => grid[i].IsGround && !grid[i].IsPillar && !shapes.IsPillarBlock(grid[i].Name))
            .SelectMany(resolver.UnitCells).Select(c => (c.X, c.Z)).ToHashSet();
        var y = GroundY(collection);
        var filled = 0;
        for (int x = 0; x < size.X; x++)
            for (int z = 0; z < size.Z; z++)
            {
                if (owned.Contains((x, z))) continue;
                map.PlaceBlock(flat, new Int3(x, y, z), Direction.North, true, 0, 0);
                rules.SnowCells.Add((x, z));
                filled++;
            }
        return filled;
    }

    // snowOnWaterHills / snowOnCliffs, ground level only: those terrain columns snow as if picked
    public static void ByKind(CGameCtnChallenge map, string collection, BlockCatalog shapes, Translations rules)
    {
        var (shores, cliffs) = (rules.Options.SnowOnWaterHills, rules.Options.SnowOnCliffs);
        if (!shores && !cliffs) return;
        var ground = GroundY(collection);
        foreach (var b in map.GetBlocks())
            if (!b.IsFree && shapes.IsTerrain(b.Name) && (shores && SnowRules.IsShore(b.Name) && b.Coord.Y < ground || cliffs && b.Name.Contains("Cliff") && b.Coord.Y <= ground))
                rules.SnowCells.Add((b.Coord.X, b.Coord.Z));
    }

    // grid Y of the flat ground; ground-level water lies one below
    private static int GroundY(string collection) => 8 + (int)(Placement.EnvironmentDrop(collection) / 8);

    // the environment's one flat land block (Dirt, Grass, Land); Stadium has no terrain blocks
    private static string? FlatBlock(string collection)
        => Paths.EnvironmentBlockInfos(Paths.ExtractedStadium, collection) is { } dir && Path.Combine(dir, "GameCtnBlockInfoFlat") is var flats && Directory.Exists(flats)
            ? Directory.EnumerateFiles(flats, "*.EDFlat.Gbx").Select(f => Path.GetFileName(f)[..^".EDFlat.Gbx".Length])
                .FirstOrDefault(n => !Regex.IsMatch(n, "Lake|Sea|Water|River"))
            : null;
}
