using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

internal delegate bool BlockRule(CGameCtnBlock block, ConversionRun run);

// each rule either deals with the block (true) or passes it on; the order is the logic
internal static partial class BlockRules
{
    private static readonly BlockRule[] Rules =
    [
        Picked,
        Gates.EngineOff,
        AlreadyIce,
        OnKeepList,
        Pillar,
        SnowRules.TerrainCell,
        TerrainStays,
        DecoSceneryOff,
        SwapTable,
        IgnoredPrefix,
        NativeIceTwin,
        Water,
        Concrete,
        NoIceWanted,
        Gates.EffectOnGate,
        WallsOnly,
        SnowRules.DecoInTerrain,
        SnowOverIceTwin,
        SkinRules.AskedFor,
        CustomBlock,
        SkinRules.OnGround,
        AsItem,
    ];

    public static void Decide(CGameCtnBlock block, ConversionRun run)
    {
        foreach (var rule in Rules)
            if (rule(block, run))
            {
                run.DecidedBy[block] = rule.Method.Name;
                return;
            }
    }

    // chosen per piece (maps.jsonc "pieces", the GUI); snow goes through the snowTerrain areas, plain renames and lets the rules go on
    private static bool Picked(CGameCtnBlock block, ConversionRun run)
    {
        switch (run.Rules.PieceChoice(block))
        {
            case "keep":
                Bump(run.Report.KeptByList, block.Name);
                return true;
            case "custom":
                return BuildCustom(block, run) || AsItem(block, run);
            case "item":
                return AsItem(block, run);
            case "skin":
                SkinRules.Skin(block, run, block.IsGround ? ItemKind.None : ItemKind.Skin);
                return true;
            case "tops":
                TopsOnly(block, run);
                return true;
            case "plain" when Gates.PlainOf(block.Name, run) is { } plain:
                block.Name = plain;
                return false;
            default:
                return false;
        }
    }

    private static bool AlreadyIce(CGameCtnBlock block, ConversionRun run)
    {
        if (!block.Name.Contains("Ice") && !block.Name.Contains('\\')) return false;
        // flipped ice shows concrete: ice top
        if (!block.Name.Contains('\\')) IceTops.Add(run, block, run.Variant(block), run.Clips(block), ItemKind.TopOfIce, inBlock: true);
        return true;
    }

    private static bool OnKeepList(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.OnKeepList(block)) return false;
        Bump(run.Report.KeptByList, block.Name);
        return true;
    }

    // scenery = what only gets dressed in snow: deco blocks, pillars, concrete and walls with iced tops
    private static bool KeepScenery(CGameCtnBlock block, ConversionRun run)
    {
        if (run.Rules.Options.ConvertScenery) return false;
        run.SceneryKept.Add(block);
        Bump(run.Report.SceneryKept, block.Name);
        return true;
    }

    private static void TopsOnly(CGameCtnBlock block, ConversionRun run)
        => IceTops.Add(run, block, run.Variant(block), run.Clips(block), inBlock: true);

    private static bool Pillar(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.Shapes.IsPillarBlock(block.Name)) return false;
        if (!KeepScenery(block, run)) TopsOnly(block, run);
        return true;
    }

    private static bool TerrainStays(CGameCtnBlock block, ConversionRun run) => run.Shapes.IsTerrain(block.Name);

    private static bool DecoSceneryOff(CGameCtnBlock block, ConversionRun run) => block.Name.StartsWith("Deco") && KeepScenery(block, run);

    private static bool SwapTable(CGameCtnBlock block, ConversionRun run)
    {
        var name = block.Name;
        if (!run.Rules.Blocks.TryGetValue(name, out var t)) return false;
        if (t.To is null) { Bump(run.Report.Kept, name); return true; }
        Apply(block, t, run.Report);
        run.Renamed.Add(block);
        Bump(run.Report.Swapped, (name, t.To, Source.Table));
        return true;
    }

    private static bool IgnoredPrefix(CGameCtnBlock block, ConversionRun run) => run.Rules.IgnorePrefixes.Any(block.Name.StartsWith);

    private static bool Available(string name, ConversionRun run)
        => run.KnownBlocks.Contains(name) && run.Translations.IsAvailable(name, run.Collection) && run.Shapes.EnvironmentHas(name) != false;

    private static bool NativeIceTwin(CGameCtnBlock block, ConversionRun run)
    {
        var name = block.Name;
        // air open roads off Stadium become items
        if (!block.IsGround && name.StartsWith("Open") && !run.Collection.StartsWith("Stadium")) return false;
        if (Candidates(name).FirstOrDefault(c => Available(c, run)) is not { } twin) return false;
        block.Name = twin;
        run.Renamed.Add(block);
        Bump(run.Report.Swapped, (name, twin, Source.Rule));
        IceTops.Add(run, block, run.Variant(block), run.Clips(block), ItemKind.TopOfIce);
        return true;
    }

    // water stays water: an ice top on a water block is a solid slab; banks with an ice version swapped above
    private static bool Water(CGameCtnBlock block, ConversionRun run)
    {
        if (!block.Name.Contains("Water")) return false;
        Bump(run.Report.Water, block.Name);
        return true;
    }

    // white deco = concrete, keep
    private static bool Concrete(CGameCtnBlock block, ConversionRun run)
    {
        if (!BlockKinds.IsConcrete(run, block, run.Variant(block), run.Clips(block))) return false;
        if (KeepScenery(block, run)) return true;
        Bump(run.Report.Concrete, block.Name);
        TopsOnly(block, run);
        return true;
    }

    private static bool NoIceWanted(CGameCtnBlock block, ConversionRun run)
    {
        if (WantsItem(block.Name)) return false;
        if (!KeepScenery(block, run)) TopsOnly(block, run);
        return true;
    }

    private static bool WallsOnly(CGameCtnBlock block, ConversionRun run)
    {
        if (!BlockKinds.IsWallOnly(run, block, run.Variant(block), run.Clips(block))) return false;
        if (KeepScenery(block, run)) return true;
        Bump(run.Report.Walls, block.Name);
        TopsOnly(block, run);
        return true;
    }

    // only a block keeps the ground away; grass grows through a skin on deco, not on ice
    private static bool SnowOverIceTwin(CGameCtnBlock block, ConversionRun run)
    {
        var name = block.Name;
        if (!(block.IsGround || run.InTerrain(block)) || !BlockNames.IsWholeOnGround(name, run.Collection) || !run.Rules.Options.SnowOnGroundDeco) return false;
        var twin = "PlatformIce" + name["DecoPlatform".Length..];
        if (!Available(twin, run) || !ItemPlacer.Place(run, block, run.Variant(block), run.Clips(block), ItemKind.OverTwin)) return false;
        block.Name = twin;
        run.Renamed.Add(block);
        Bump(run.Report.Swapped, (name, twin, Source.Rule));
        return true;
    }

    // waypoints as custom blocks need customWaypoints; free blocks too, the game turns and tilts them; ground blocks keep their ground plate.
    // the game draws the placed variant's body, not the resolver's no-pillar-below one
    private static bool CustomBlock(CGameCtnBlock block, ConversionRun run)
    {
        var options = run.Rules.Options;
        if (!options.CustomBlocks || run.InTerrain(block) || !options.CustomWaypoints && run.Shapes.Waypoint(block.Name) is not null) return false;
        return BuildCustom(block, run);
    }

    private static bool BuildCustom(CGameCtnBlock block, ConversionRun run)
    {
        var (name, shapes, items) = (block.Name, run.Shapes, run.Items);
        var (variant, mobil, collection, noClips) = (VariantOf(block), MobilOf(block), run.Map.Collection!.Value, ConversionRun.NoClips);
        var stack = NeedsNativeUnder(block, run);
        var built = items.BlockFor(name, name, block.IsGround, variant, mobil, collection, noClips, lift: stack) is { } own
            ? (own, name, noClips)
            : run.SurfaceParents.Of(name, variant, run.Clips(block)) is { } parent
                && items.BlockFor(name, parent.Name, block.IsGround, variant, mobil, collection, parent.Baked, lift: stack) is { } onParent
                ? (onParent, parent.Name, parent.Baked) : default((IceBlock, string, IReadOnlySet<int>)?);
        if (built is not var (iceBlock, archetype, baked)) return false;
        run.Customs[block] = iceBlock;
        run.BuiltAs[block] = (archetype, baked, stack);
        if (stack) run.Stacked.Add(block);
        // only the body's own terrain: the game still draws the clips, terrain ones included
        else if (block.IsGround && BlockNames.OnTerrainShape(name) && items.For(name, true, variant, mobil, baked, ItemKind.TerrainFaces) is { } terrain && terrain != IceItem.Nothing
            && ItemPlacer.Place(run, block, variant, baked, ItemKind.TerrainFaces))
            Bump(run.Report.TerrainItems, name);
        Bump(run.Report.ReplacedByBlock, name);
        return true;
    }

    // vegetation and blended hill/cliff terrain only come from the native block: keep it under the iced body
    // (not gates: two would trigger; their terrain goes into an item beside the body)
    // terrain only from clips: the game keeps drawing those, so a plain body will do
    private static bool NeedsNativeUnder(CGameCtnBlock block, ConversionRun run)
    {
        var (name, variant, mobil) = (block.Name, VariantOf(block), MobilOf(block));
        return !block.IsFree && run.Shapes.Waypoint(name) is null
            && (block.IsGround && BlockNames.OnTerrainShape(name) && run.Items.For(name, true, variant, mobil, ConversionRun.NoClips, ItemKind.TerrainFaces) is { } ownFaces && ownFaces != IceItem.Nothing
                || run.Shapes.Trees(name, block.IsGround, variant, mobil, run.Clips(block)).Count > 0);
    }

    private static bool AsItem(CGameCtnBlock block, ConversionRun run)
    {
        var inTerrain = run.InTerrain(block);
        if (!ItemPlacer.Place(run, block, run.Variant(block), run.Clips(block), inTerrain ? ItemKind.InTerrain : ItemKind.None))
        {
            Bump(run.Report.Unmatched, block.Name);
            return true;
        }
        run.Replaced.Add(block);
        Bump(run.Report.ReplacedByItem, block.Name);
        Bump(run.Report.ItemReasons, (block.Name, ItemReason(run, block, inTerrain, run.Rules.Options)));
        return true;
    }

    // why a grid block became an item, not a custom block
    private static string ItemReason(ConversionRun run, CGameCtnBlock block, bool inTerrain, ConvertOptions options)
        => !options.CustomBlocks ? "customBlocks off"
            : block.IsGround ? "ground"
            : inTerrain ? "in terrain"
            : !options.CustomWaypoints && run.Shapes.Waypoint(block.Name) is not null ? "waypoint (customWaypoints off)"
            : CustomBlockWriter.VariantId(false, 0, MobilOf(block)) is null ? "mobil row"
            : "no ice surface";

    [GeneratedRegex("Tech(?!nics)|Dirt|Grass|Plastic|Bump")]
    private static partial Regex Surface();

    private static readonly string[] IcePrefixes = ["DecoPlatform", "DecoHill", "DecoCliff10", "DecoCliff8", "DecoCliff"];

    private static bool WantsItem(string name)
        => Surface().IsMatch(name) || IcePrefixes.Any(name.StartsWith) || name.StartsWith("SnowRoad") || name.StartsWith("DecoWall");

    // ice name guesses, most specific first
    private static IEnumerable<string> Candidates(string name)
        => name.Contains("Penalty") ? [] : RawCandidates(name).Where(c => !c.Contains("RoadIce"));

    // never RoadIce
    private static IEnumerable<string> RawCandidates(string name)
    {
        foreach (Match m in Surface().Matches(name))
            yield return name[..m.Index] + "Ice" + name[(m.Index + m.Length)..];
        foreach (var p in IcePrefixes)
            if (name.StartsWith(p)) yield return p + "Ice" + name[p.Length..];
        if (name.StartsWith("DecoWall") && !Surface().IsMatch(name)) yield return name + "Ice";
    }

    private static void Apply(CGameCtnBlock block, Translation t, ConversionReport report)
    {
        block.Name = t.To!;
        if (t.Variant is { } v) block.Variant = v;
        if (t.SubVariant is { } sv) block.SubVariant = sv;

        if (block.IsFree)
        {
            if (t.Rotate != 0 && block.YawPitchRoll is { } ypr)
                block.YawPitchRoll = ypr with { X = ypr.X - t.Rotate * MathF.PI / 2 };
            if (t.Offset is not null) report.FreeBlockOffsetsSkipped++;
            return;
        }

        var facing = (int)block.Direction;
        if (t.Offset is [var x, var y, var z])
        {
            var (wx, wz) = RotateQuarterTurns(x, z, facing);
            block.Coord += new Int3(wx, y, wz);
        }
        if (t.Rotate != 0)
            block.Direction = (Direction)(((facing + t.Rotate) % 4 + 4) % 4);
    }

    private static (int X, int Z) RotateQuarterTurns(int x, int z, int turns) => (((turns % 4) + 4) % 4) switch
    {
        0 => (x, z),
        1 => (-z, x),
        2 => (-x, -z),
        _ => (z, -x),
    };
}
