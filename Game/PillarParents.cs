using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// the block a pillar holds up: the first non-pillar block above its cells (TrackWallFromParent takes its material folder)
public static class PillarParents
{
    private const int MaxClimb = 64;

    public static Dictionary<CGameCtnBlock, CGameCtnBlock> Find(BlockCatalog shapes, IEnumerable<(CGameCtnBlock Block, IReadOnlyList<Int3> Cells)> blocks)
    {
        var all = blocks.ToList();
        var pillar = all.Select(b => shapes.IsPillarBlock(b.Block.Name)).ToArray();
        var holders = new Dictionary<Int3, CGameCtnBlock>();
        foreach (var (i, (block, cells)) in all.Index())
            if (!pillar[i]) foreach (var cell in cells) holders.TryAdd(cell, block);
        var parents = new Dictionary<CGameCtnBlock, CGameCtnBlock>(ReferenceEqualityComparer.Instance);
        foreach (var (i, (block, cells)) in all.Index())
        {
            if (!pillar[i]) continue;
            var found = cells.Select(c => Above(c, holders)).FirstOrDefault(p => p is not null);
            if (found is not null) parents[block] = found;
        }
        return parents;
    }

    private static CGameCtnBlock? Above(Int3 cell, Dictionary<Int3, CGameCtnBlock> holders)
    {
        for (int up = 1; up <= MaxClimb; up++)
            if (holders.TryGetValue(new Int3(cell.X, cell.Y + up, cell.Z), out var block)) return block;
        return null;
    }
}
