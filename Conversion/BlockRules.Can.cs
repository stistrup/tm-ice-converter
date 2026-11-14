using GBX.NET.Engines.Game;
using static TmSurfaceEditor.BlockFlags;

namespace TmSurfaceEditor;

// which picks would change a block, by the same checks Picked's ways make; quick, no meshes.
// Optimistic where only a build can tell (custom, skin, tops, snow on deco)
internal static partial class BlockRules
{
    public static bool Can(string pick, CGameCtnBlock block, ConversionRun run) => pick switch
    {
        "twin" => TwinOf(block, run) is not null,
        "twincover" => CoverTwinOf(block, run) is not null,
        "table" => run.Rules.Blocks.ContainsKey(block.Name),
        "gate" => !block.IsFree && Gates.GateEffect(block.Name) is not null,
        "remove" => block.Name.StartsWith("Gate"),
        "plain" => Gates.PlainOf(block.Name, run) is not null,
        "snow" or "snowflip" => run.Shapes.IsTerrain(block.Name) || BlockNames.IsGameplayBlock(block.Name) && block.IsGround
            || !block.IsPillar && !run.Shapes.IsPillarBlock(block.Name) && !block.IsFree && CustomBlockWriter.VariantId(block.IsGround, VariantOf(block), MobilOf(block)) is not null,
        _ => true,
    };

    // per block, before any rule renames it
    public static void LearnPicks(ConversionRun run, IEnumerable<string> picks)
    {
        var all = picks.ToList();
        foreach (var block in run.Map.GetBlocks())
            run.CannotPick[block] = all.Where(p => !Can(p, block, run)).ToHashSet();
        foreach (var item in run.Map.AnchoredObjects ?? [])
            run.CannotPick[item] = all.Where(p => !ItemSwaps.Can(p, item.ItemModel, run)).ToHashSet();
    }
}
