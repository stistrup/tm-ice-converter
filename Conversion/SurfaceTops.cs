using GBX.NET.Engines.Game;
using static TmSurfaceEditor.BlockFlags;
using static TmSurfaceEditor.Counts;

namespace TmSurfaceEditor;

internal static class SurfaceTops
{
    // no clips in any environment
    public const string Clipless = "StageTechnicsLight";

    // walls-only blocks stay native, ice on their upward faces (not gates or stands)
    // with customs: a custom block with its tops iced in, the skin only if that fails
    public static void Add(ConversionRun run, CGameCtnBlock block, int variant, IReadOnlySet<int> clips, ItemKind kind = ItemKind.Top, bool inBlock = false)
    {
        if (block.Name.StartsWith("Gate") || block.Name.StartsWith("Stand") || BlockNames.IsGameplayBlock(block.Name)) return;
        // the minimum skips platform rims; a wall's top is all wall
        if (kind == ItemKind.TopOfTwin && BlockKinds.IsWallOnly(run, block, variant, clips)) kind = ItemKind.Top;
        // odd angles: rotation order unknown
        if (!Placement.QuarterTurns(Placement.Tilt(block))) return;
        if (run.Shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is not { } materials || !materials.Any(BlockNames.IsWallMaterial)) return;
        // free blocks: on a clipless stand-in, their free clips iced into the body (the game would draw them over it)
        var (archetype, baked) = ArchetypeOf(run, block);
        // the game draws the placed variant's body
        if (inBlock && run.Rules.Options.CustomBlocks && run.Shapes.Waypoint(block.Name) is null
            && run.Items.BlockFor(block.Name, archetype, block.IsGround, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, baked,
                kind == ItemKind.TopOfTwin ? ItemKind.TopOfTwinBody : ItemKind.TopsBody, Placement.Tilt(block)) is { } iceBlock)
        {
            run.Customs[block] = iceBlock;
            run.Topped.Add(block);
            Bump(run.Report.TopsInBlock, block.Name);
            return;
        }
        if (ItemPlacer.Place(run, block, variant, clips, kind) && run.Topped.Add(block))
            Bump(run.Report.TopsThemed, block.Name);
    }

    // tops that only show after conversion
    public static void NowShown(ConversionRun run, List<(CGameCtnBlock Block, int Index)> remaining, List<HashSet<int>> now)
    {
        for (int k = 0; k < remaining.Count; k++)
        {
            var (block, i) = remaining[k];
            if (AlreadyTreated(run, block) || !TopAppears(now[k], run.Target[i]) || run.Shapes.IsTerrain(block.Name) || run.OnKeepList(block)) continue;
            Add(run, block, run.Placed[i].Variant, now[k], run.Renamed.Contains(block) ? ItemKind.TopOfTwin : ItemKind.Top);
        }
    }

    private static bool AlreadyTreated(ConversionRun run, CGameCtnBlock block)
        => run.Topped.Contains(block) || run.SceneryKept.Contains(block) || run.Replaced.Contains(block) || run.Skinned.Contains(block) || run.Customs.ContainsKey(block);

    private static (string Archetype, IReadOnlySet<int> Baked) ArchetypeOf(ConversionRun run, CGameCtnBlock block)
    {
        if (block.IsFree && run.Shapes.EnvironmentHas(Clipless) != false) return (Clipless, BlockKinds.DrawnFreeClips(run.Shapes, block));
        return (block.Name, ConversionRun.NoClips);
    }

    // new tops get ice from NowShown
    public static bool OnlyTopsAppear(IReadOnlySet<int> now, IReadOnlySet<int> original)
        => original.IsSubsetOf(now) && now.Except(original).All(key => (key & 0xFFFF) / 32 % 6 == 5);

    public static bool TopAppears(IReadOnlySet<int> now, IReadOnlySet<int> original)
        => now.Except(original).Any(key => (key & 0xFFFF) / 32 % 6 == 5);
}
