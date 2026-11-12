using GBX.NET.Engines.Game;
using static TmSurfaceEditor.Counts;

namespace TmSurfaceEditor;

// native blocks under ice skins: smaller, and one look throughout
internal static class SkinRules
{
    public static bool AskedFor(CGameCtnBlock block, ConversionRun run)
    {
        var options = run.Rules.Options;
        if (!(run.Rules.SkinsAt(block.Coord.X, block.Coord.Z) || options.SkinWaypoints && run.Shapes.Waypoint(block.Name) is not null) || block.IsFree || run.InTerrain(block)) return false;
        Skin(block, run, block.IsGround ? ItemKind.None : ItemKind.Skin);
        return true;
    }

    // no custom block: ground blocks stay native under a skin too
    public static bool OnGround(CGameCtnBlock block, ConversionRun run)
    {
        var inTerrain = run.InTerrain(block);
        if (!(block.IsGround || inTerrain) || BlockNames.IsWholeOnGround(block.Name, run.Collection)) return false;
        Skin(block, run, inTerrain ? ItemKind.InTerrain : ItemKind.None);
        return true;
    }

    public static void Skin(CGameCtnBlock block, ConversionRun run, ItemKind kind)
    {
        if (ItemPlacer.Place(run, block, run.Variant(block), run.Clips(block), kind))
        {
            run.Skinned.Add(block);
            Bump(run.Report.Skinned, block.Name);
        }
        else Bump(run.Report.Unmatched, block.Name);
    }
}
