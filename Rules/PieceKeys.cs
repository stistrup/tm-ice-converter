using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// a pick's key: "Name x,y,z" for a grid block; where two share it, direction and flags too, then their order
public static class PieceKeys
{
    public static Dictionary<CGameCtnBlock, string> Ambiguous(CGameCtnChallenge map)
    {
        var keys = new Dictionary<CGameCtnBlock, string>(ReferenceEqualityComparer.Instance);
        foreach (var shared in map.GetBlocks().Where(b => !b.IsFree).GroupBy(Plain).Where(g => g.Count() > 1))
            foreach (var same in shared.GroupBy(b => $"{Plain(b)} {b.Direction} {b.Flags:X}"))
            {
                var n = 0;
                foreach (var block in same) keys[block] = same.Count() > 1 ? $"{same.Key} #{++n}" : same.Key;
            }
        return keys;
    }

    public static string Plain(CGameCtnBlock block) => ConversionRules.KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z);
}
