using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;

namespace TmIceConverter;

// what a block is, for the rules
internal static class BlockKinds
{
    public static HashSet<int> FreeClips(BlockCatalog shapes, CGameCtnBlock block)
        => shapes.ClipSides(block.Name, false, VariantOf(block))
            .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti).Select(c => ClipKey.Of(s.Bit, c.Index))).ToHashSet();

    // what the game draws on a free block, vertical (wall sides) included
    public static HashSet<int> DrawnFreeClips(BlockCatalog shapes, CGameCtnBlock block)
        => shapes.ClipSides(block.Name, false, VariantOf(block))
            .SelectMany(s => s.Clips.Where(c => !c.Anti).Select(c => ClipKey.Of(s.Bit, c.Index))).ToHashSet();

    // white deco with no ice version stays (concrete)
    public static bool IsConcrete(ConversionRun run, CGameCtnBlock block, int variant, IReadOnlySet<int> clips)
        => block.Name.StartsWith("Deco") && block.Color == DifficultyColor.White
            && !(run.Shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is { } materials
                && materials.Any(m => m.StartsWith("Deco") && !IsGroundFill(block, m)));

    public static bool IsWallOnly(ConversionRun run, CGameCtnBlock block, int variant, IReadOnlySet<int> clips)
        => run.Shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is { Count: > 0 } materials
            && materials.Any(BlockNames.IsWallMaterial)
            && !materials.Any(m => m != "Underwater" && !IsGroundFill(block, m) && ItemBuilder.SkinCovers(block.Name, m, run.Items.SnowOnZoneSides));

    public static bool IsGroundFill(CGameCtnBlock block, string material)
        => block.IsGround && block.Name.StartsWith("DecoWall") && material.StartsWith("Deco");
}
