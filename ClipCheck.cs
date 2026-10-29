using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// compares the resolver's free clips with the clips the game baked into the map (name + facing cell)
public static class ClipCheck
{
    public sealed record Clip(string Name, Int3 Cell, string Kind, string Block, Int3 BlockCoord);

    public sealed record Result(List<Clip> GameOnly, List<Clip> OursOnly, Dictionary<string, int> Baked);

    public static Result Compare(CGameCtnChallenge map, IBlockShapes shapes)
    {
        shapes.Environment = map.Collection?.ToString() ?? "Stadium2020";
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var resolver = new ClipResolver(shapes);
        var placed = resolver.OfAll(grid);
        var shown = resolver.Resolve(placed);
        var possible = new Dictionary<(string, Int3), Clip>();
        var ours = new HashSet<(string, Int3)>();
        for (int i = 0; i < grid.Count; i++)
            foreach (var side in shapes.ClipSides(placed[i].Name, placed[i].Ground, placed[i].Variant))
            {
                var cell = Converter.CellAt(Vector3.Transform(side.Probe, placed[i].Place));
                foreach (var c in side.Clips.Where(c => c.Type.StartsWith("FreeClip")))
                {
                    var key = (c.Name, cell);
                    possible.TryAdd(key, new Clip(c.Name, cell, KindOf(side.Bit, c), grid[i].Name, new Int3(grid[i].Coord.X, grid[i].Coord.Y, grid[i].Coord.Z)));
                    if (shown[i].Any(k => (k & 0xFFFF) == MeshExport.ClipKey(side.Bit, c.Index))) ours.Add(key);
                }
            }
        // baked names no block side offers (terrain fill) are left out
        var baked = (map.BakedBlocks ?? []).Select(b => (b.Name, new Int3(b.Coord.X, b.Coord.Y, b.Coord.Z))).Where(possible.ContainsKey).ToHashSet();
        return new Result(
            baked.Except(ours).Select(k => possible[k]).ToList(),
            ours.Except(baked).Select(k => possible[k]).ToList(),
            baked.GroupBy(k => possible[k].Kind).ToDictionary(g => g.Key, g => g.Count()));
    }

    public static readonly string[] Kinds = ["top", "side", "bottom", "vertical"];

    private static string KindOf(int bit, ClipInfo clip) => bit % 6 == 5 ? "top" : bit % 6 == 4 ? "bottom" : clip.Vertical ? "vertical" : "side";
}
