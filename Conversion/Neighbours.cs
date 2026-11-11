using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

// neighbours whose clips would change become items too
internal static class Neighbours
{
    public static void Keep(ConversionRun run)
    {
        var (grid, placed, target, replaced) = (run.Grid, run.Placed, run.Target, run.Replaced);
        var resolver = new ClipResolver(run.Shapes);
        for (int round = 0; round < 50; round++)
        {
            var remaining = grid.Select((b, i) => (Block: b, Index: i)).Where(x => !replaced.Contains(x.Block)).ToList();
            var now = resolver.Resolve(remaining.Select(x => resolver.Of(x.Block) is var p && p.Name == placed[x.Index].Name
                ? p with { Variant = placed[x.Index].Variant } : p).ToList());
            var added = 0;
            for (int k = 0; k < remaining.Count; k++)
            {
                var (block, i) = remaining[k];
                if (StaysBlock(run, block, placed[i].Variant, now[k], target[i])) continue;
                if (!ItemPlacer.Place(run, block, placed[i].Variant, target[i])) continue;
                replaced.Add(block);
                Bump(run.Report.KeptByItem, block.Name);
                added++;
            }
            if (added == 0) break;
        }
        var final = grid.Select((b, i) => (Block: b, Index: i)).Where(x => !replaced.Contains(x.Block)).ToList();
        SurfaceTops.NowShown(run, final, resolver.Resolve(final.Select(x => resolver.Of(x.Block) is var p && p.Name == placed[x.Index].Name
            ? p with { Variant = placed[x.Index].Variant } : p).ToList()));
    }

    // fine as it is, or can't become an item copy
    private static bool StaysBlock(ConversionRun run, CGameCtnBlock block, int variant, IReadOnlySet<int> now, IReadOnlySet<int> target)
    {
        bool WallOnly() => BlockKinds.IsWallOnly(run, block, variant, target);
        if (SurfaceTops.OnlyTopsAppear(now, target) && !(WallOnly() && AdapterAbove(run, block))) return true;
        // stands: an end cap is fine
        if (block.Name.StartsWith("Stand")) return true;
        if (run.SceneryKept.Contains(block) || run.Renamed.Contains(block) || run.Customs.ContainsKey(block) || run.Skinned.Contains(block)) return true;
        // an item copy has no trigger
        if (block.IsGround || run.Shapes.IsPillarBlock(block.Name) || BlockNames.IsGameplayBlock(block.Name)) return true;
        if (run.OnKeepList(block) || BlockKinds.IsConcrete(run, block, variant, target)) return true;
        return WallOnly() && !(SurfaceTops.TopAppears(now, target) && AdapterAbove(run, block));
    }

    private static bool AdapterAbove(ConversionRun run, CGameCtnBlock block)
        => !block.IsFree && run.Replaced.Any(r => !r.IsFree && r.Coord == block.Coord + new Int3(0, 1, 0)
            && run.Shapes.ClipSides(r.Name, r.IsGround, VariantOf(r)).Any(s => s.Bit % 6 == 4 && s.Clips.Any(c => c.Anti)));
}
