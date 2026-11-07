using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.Counts;

namespace TmIceConverter;

public sealed class MapConverter(HashSet<string> knownBlocks, Translations translations, BlockCatalog shapes, ItemBuilder items)
{
    // kept across maps: slow to work out
    private readonly SurfaceParents surfaceParents = new(knownBlocks, shapes);
    private readonly Dictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> flatTops = [];

    // own: the map's settings (MapSettings.Read); null: the defaults
    public ConversionReport Convert(CGameCtnChallenge map, Translations? own = null) => Run(map, own).Report;

    internal ConversionRun Run(CGameCtnChallenge map, Translations? own = null)
    {
        var collection = map.Collection?.ToString() ?? "Stadium2020";
        var rules = translations.For(map.MapName, own);
        shapes.Environment = items.Environment = collection;
        items.Style = new ItemStyle(rules.Options.SnowOnZoneSides, rules.Options.SnowOnGroundDeco, rules.SkinLift ?? ItemStyle.DefaultSkinLift, rules.SkinEdge ?? ItemStyle.DefaultSkinEdge);
        var run = new ConversionRun(map, collection, rules, translations, knownBlocks, shapes, items, surfaceParents, flatTops);

        foreach (var block in map.GetBlocks()) BlockRules.Decide(block, run);
        Neighbours.Keep(run);
        if (rules.Presets && rules.Options.RampToSkins) RampPass.Run(run);

        // these read the native names
        var snow = new SnowSurfaces(map, run.Replaced.Concat(run.Customs.Keys), run.Renamed, run.Skinned, shapes, rules.Options.SnowOnZoneSides);
        var trees = Trees.LostToBodies(run);
        PlaceNativesUnderBodies(run);
        RenameCustomBlocks(run);

        foreach (var block in run.Replaced.Concat(run.EngineOffGates)) map.RemoveBlock(block);
        if (rules.Presets) Gates.RemoveEngineOffItems(run);
        ItemSwaps.Run(run, snow);
        // after the swaps: the stand-ins stand on terrain, not ice
        foreach (var (block, name, models) in trees) Trees.Plant(run, block, name, models);
        if (!rules.Presets) return run;
        MatchPlatformColour(run);
        if (rules.Options.RemoveForcedCameras) RemoveForcedCameras(run);
        return run;
    }

    private static void PlaceNativesUnderBodies(ConversionRun run)
    {
        var map = run.Map;
        foreach (var block in run.Stacked)
        {
            var native = map.PlaceBlock(block.Name, block.Coord, block.Direction, block.IsGround, 0, 0);
            (native.Flags, native.Color, native.LightmapQuality) = (block.Flags, block.Color, block.LightmapQuality);
            // before the body, as the editor orders a cell
            map.Blocks!.Remove(native);
            map.Blocks.Insert(map.Blocks.IndexOf(block), native);
            Bump(run.Report.KeptUnder, block.Name);
        }
    }

    private static void RenameCustomBlocks(ConversionRun run)
    {
        foreach (var (block, iceBlock) in run.Customs)
        {
            if (run.Stacked.Contains(block)) block.Flags |= BlockFlags.Ghost;
            block.Name = iceBlock.Ident.Id + "_CustomBlock";
            block.Flags |= CustomBlockWriter.CustomFlag;
            block.Author = iceBlock.Ident.Author;
            run.Report.UsedBlocks.Add(iceBlock);
        }
    }

    // lone Default-colour platform gets the others' colour
    private static void MatchPlatformColour(ConversionRun run)
    {
        var platforms = run.Map.GetBlocks().Where(b => b.Name.StartsWith("Platform")).ToList();
        var colours = platforms.Where(b => b.Color != DifficultyColor.Default).Select(b => b.Color).Distinct().ToList();
        var strays = platforms.Where(b => b.Color == DifficultyColor.Default).ToList();
        if (colours is not [var colour] || strays.Count == 0 || strays.Count * 2 >= platforms.Count) return;
        foreach (var b in strays) { b.Color = colour; Bump(run.Report.Recoloured, b.Name); }
    }

    private static void RemoveForcedCameras(ConversionRun run)
    {
        if (run.Map.ClipGroupInGame?.Clips is not { } clips) return;
        foreach (var c in clips) c.Clip.Tracks.RemoveAll(t => t.Blocks.Any(b => b is CGameCtnMediaBlockCameraGame));
        foreach (var c in clips.Where(c => c.Clip.Tracks.Count == 0)) run.Report.CameraClipsRemoved.Add(c.Clip.Name ?? "(unnamed)");
        clips.RemoveAll(c => c.Clip.Tracks.Count == 0);
    }
}
