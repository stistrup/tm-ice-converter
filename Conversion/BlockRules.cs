using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.BlockFlags;
using static TmSurfaceEditor.Counts;

namespace TmSurfaceEditor;

internal delegate bool BlockRule(CGameCtnBlock block, ConversionRun run);

// each rule either deals with the block (true) or passes it on; the order is the logic
internal static partial class BlockRules
{
    private static readonly BlockRule[] Rules =
    [
        CoverRules.UnderCoveredGameplay,
        // only sets a pillar in a snowed column into the air, picked or not
        CoverRules.PillarOnCover,
        Picked,
        Gates.EngineOff,
        AlreadyThemed,
        OnKeepList,
        Pillar,
        CoverRules.TerrainCell,
        TerrainStays,
        DecoSceneryOff,
        SwapTable,
        IgnoredPrefix,
        NativeTwin,
        Water,
        Concrete,
        NoItemWanted,
        Gates.EffectOnGate,
        WallsOnly,
        CoverRules.DecoInTerrain,
        CoverOverTwin,
        SkinRules.AskedFor,
        CustomBlock,
        SkinRules.OnGround,
        AsItem,
    ];

    // rules that leave a block as scenery (the preview groups those with deco)
    public static readonly HashSet<string> SceneryRules =
        [nameof(Pillar), nameof(Concrete), nameof(WallsOnly), nameof(NoItemWanted), nameof(DecoSceneryOff), nameof(CoverRules.DecoInTerrain), nameof(CoverOverTwin)];

    // for PresetApply
    public static readonly HashSet<string> TopsRules = [nameof(NoItemWanted), nameof(Concrete), nameof(WallsOnly), nameof(Pillar), nameof(AlreadyThemed)];
    public static readonly HashSet<string> CoverColumnRules = [nameof(CoverRules.TerrainCell), nameof(CoverRules.DecoInTerrain)];
    public const string CoverOverTwinRule = nameof(CoverOverTwin);
    public const string SwapTableRule = nameof(SwapTable);
    public const string CustomBlockRule = nameof(CustomBlock);

    public static void Decide(CGameCtnBlock block, ConversionRun run)
    {
        foreach (var rule in Rules)
            if (rule(block, run))
            {
                run.DecidedBy[block] = rule.Method.Name;
                return;
            }
    }

    // each pick goes the way its rule would; snow goes through the snow columns
    private static bool Picked(CGameCtnBlock block, ConversionRun run)
    {
        var choice = run.Rules.PieceChoice(block);
        // picks only: engine-off renames first, as the rules do
        if (!run.Rules.RulesOn && !block.Name.StartsWith("Gate")) Gates.EngineOff(block, run);
        switch (choice)
        {
            case "keep":
                Bump(run.Report.KeptByList, block.Name);
                return true;
            case "custom":
                return BuildCustom(block, run) || SkinRules.OnGround(block, run) || AsItem(block, run);
            case "item":
                return AsItem(block, run);
            case "skin":
                SkinRules.Skin(block, run, run.InTerrain(block) ? ItemKind.InTerrain : block.IsGround ? ItemKind.None : ItemKind.Skin);
                return true;
            case "tops":
                if (!AlreadyThemed(block, run)) TopsOnly(block, run);
                return true;
            case "twin":
                return NativeTwin(block, run) || Kept(block, run);
            case "table":
                return SwapTable(block, run) || Kept(block, run);
            case "twincover":
                return CoverOverTwin(block, run, always: true) || Kept(block, run);
            case "gate":
                return Gates.EffectOnGate(block, run, always: true) || Kept(block, run);
            case "remove" when block.Name.StartsWith("Gate"):
                run.EngineOffGates.Add(block);
                Bump(run.Report.EngineOff, (block.Name, ""));
                return true;
            case "snow" when CoverRules.PickedCover(block, run):
                return true;
            // then the road way, as the rules go: native twin first
            case "plain" when Gates.PlainOf(block.Name, run) is { } plain:
                block.Name = plain;
                return NativeTwin(block, run) || BuildCustom(block, run) || SkinRules.OnGround(block, run) || AsItem(block, run);
            // Flips.Run turned it
            case "flip":
                return NativeTwin(block, run) || Kept(block, run);
            // no target, or one with nothing to do here: only a snowed column changes it
            case var _ when !run.Rules.RulesOn:
                return CoverRules.TerrainCell(block, run) || CoverRules.DecoInTerrain(block, run) || Kept(block, run);
            default:
                return false;
        }
    }

    private static bool Kept(CGameCtnBlock block, ConversionRun run)
    {
        Bump(run.Report.KeptByList, block.Name);
        return true;
    }

    private static bool AlreadyThemed(CGameCtnBlock block, ConversionRun run)
    {
        var word = run.Items.Theme.BlockWord;
        if (!(word is not null && block.Name.Contains(word)) && !block.Name.Contains('\\')) return false;
        // flipped ice shows concrete: ice top
        if (!block.Name.Contains('\\')) SurfaceTops.Add(run, block, run.Variant(block), run.Clips(block), ItemKind.TopOfTwin, inBlock: true);
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
        => SurfaceTops.Add(run, block, run.Variant(block), run.Clips(block), inBlock: true);

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
        => run.KnownBlocks.Contains(name) && run.DataRules.IsAvailable(name, run.Collection) && run.Shapes.EnvironmentHas(name) != false;

    private static bool NativeTwin(CGameCtnBlock block, ConversionRun run)
    {
        var name = block.Name;
        // air open roads off Stadium become items
        if (!block.IsGround && name.StartsWith("Open") && !run.Collection.StartsWith("Stadium")) return false;
        if (run.Items.Theme.BlockWord is not { } word || Candidates(name, word).FirstOrDefault(c => Available(c, run)) is not { } twin) return false;
        block.Name = twin;
        run.Renamed.Add(block);
        Bump(run.Report.Swapped, (name, twin, Source.Rule));
        SurfaceTops.Add(run, block, run.Variant(block), run.Clips(block), ItemKind.TopOfTwin);
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

    private static bool NoItemWanted(CGameCtnBlock block, ConversionRun run)
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
    private static bool CoverOverTwin(CGameCtnBlock block, ConversionRun run) => CoverOverTwin(block, run, always: false);

    // always: picked, whatever the option says
    private static bool CoverOverTwin(CGameCtnBlock block, ConversionRun run, bool always)
    {
        var name = block.Name;
        if (!(block.IsGround || run.InTerrain(block)) || !BlockNames.IsWholeOnGround(name, run.Collection) || !(always || run.Rules.Options.SnowOnGroundDeco)) return false;
        if (run.Items.Theme.BlockWord is not { } word) return false;
        var twin = "Platform" + word + name["DecoPlatform".Length..];
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
        var (self, selfBaked) = !stack && ExposedThemedClips(block, run) is { } exposed ? (SurfaceTops.Clipless, exposed) : (name, noClips);
        var built = items.BlockFor(name, self, block.IsGround, variant, mobil, collection, selfBaked, lift: stack) is { } own
            ? (own, self, selfBaked)
            : run.SurfaceParents.Of(name, variant, run.Clips(block)) is { } parent
                && items.BlockFor(name, parent.Name, block.IsGround, variant, mobil, collection, parent.Baked, lift: stack) is { } onParent
                ? (onParent, parent.Name, parent.Baked) : default((BuiltBlock, string, IReadOnlySet<int>)?);
        if (built is not var (iceBlock, archetype, baked)) return false;
        run.Customs[block] = iceBlock;
        run.BuiltAs[block] = (archetype, baked, stack);
        if (stack) run.Stacked.Add(block);
        // only the body's own terrain: the game still draws the clips, terrain ones included
        else if (block.IsGround && BlockNames.OnTerrainShape(name) && items.For(name, true, variant, mobil, baked, ItemKind.TerrainFaces) is { } terrain && terrain != BuiltItem.Nothing
            && ItemPlacer.Place(run, block, variant, baked, ItemKind.TerrainFaces))
            Bump(run.Report.TerrainItems, name);
        Bump(run.Report.ReplacedByBlock, name);
        return true;
    }

    // the game draws the archetype's clips over a custom body: shown clips with a surface the theme converts go into the body,
    // on a clipless stand-in (air only, as it has no ground variant)
    private static IReadOnlySet<int>? ExposedThemedClips(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.Rules.Options.ClipSidesInBlock || block.IsGround || block.IsFree || run.Shapes.EnvironmentHas(SurfaceTops.Clipless) == false) return null;
        var shown = run.Clips(block);
        var theme = run.Items.Theme;
        var map = theme.Map(null);
        return run.Shapes.ClipMaterials(block.Name, false, VariantOf(block), MobilOf(block), shown).Any(m => theme.SwappedIn(map, m)) ? shown : null;
    }

    // vegetation and blended hill/cliff terrain only come from the native block: keep it under the iced body
    // (not gates: two would trigger; their terrain goes into an item beside the body)
    // terrain only from clips: the game keeps drawing those, so a plain body will do
    private static bool NeedsNativeUnder(CGameCtnBlock block, ConversionRun run)
    {
        var (name, variant, mobil) = (block.Name, VariantOf(block), MobilOf(block));
        if (block.IsFree || run.Shapes.Waypoint(name) is not null) return false;
        return HasTerrainFaces(block, run) || run.Shapes.Trees(name, block.IsGround, variant, mobil, run.Clips(block)).Count > 0;
    }

    private static bool HasTerrainFaces(CGameCtnBlock block, ConversionRun run)
    {
        if (!block.IsGround || !BlockNames.OnTerrainShape(block.Name)) return false;
        return run.Items.For(block.Name, true, VariantOf(block), MobilOf(block), ConversionRun.NoClips, ItemKind.TerrainFaces) is { } faces && faces != BuiltItem.Nothing;
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

    private static readonly string[] TwinPrefixes = ["DecoPlatform", "DecoHill", "DecoCliff10", "DecoCliff8", "DecoCliff"];

    private static bool WantsItem(string name)
        => Surface().IsMatch(name) || TwinPrefixes.Any(name.StartsWith) || name.StartsWith("SnowRoad") || name.StartsWith("DecoWall");

    // twin name guesses, most specific first; never a Road<word> block
    private static IEnumerable<string> Candidates(string name, string word)
        => name.Contains("Penalty") ? [] : RawCandidates(name, word).Where(c => !c.Contains("Road" + word));

    private static IEnumerable<string> RawCandidates(string name, string word)
    {
        foreach (Match m in Surface().Matches(name))
            yield return name[..m.Index] + word + name[(m.Index + m.Length)..];
        foreach (var p in TwinPrefixes)
            if (name.StartsWith(p)) yield return p + word + name[p.Length..];
        if (name.StartsWith("DecoWall") && !Surface().IsMatch(name)) yield return name + word;
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
