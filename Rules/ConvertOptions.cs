using System.ComponentModel;
using System.Reflection;

namespace TmSurfaceEditor;

// the switches; these defaults are what a map without settings of its own converts with.
// Category: map (a map-wide job), piece (how pieces convert: what presets set), look (the material's look). Description: the GUI's info text
public sealed record ConvertOptions(
    [property: Category("map"), Description("Forced camera changes are taken out of the in-game clips")]
    bool RemoveForcedCameras = false,
    [property: Category("map"), Description("Engine-off blocks become reset blocks, engine-off gates are removed")]
    bool EngineOffToReset = false,
    [property: Category("piece"), Description("Slow-motion and reset pads become ice with a gate that keeps the effect")]
    bool EffectsToGate = false,
    [property: Category("map"), Description("Engine-off blocks become plain blocks, engine-off gates are removed; wins over engine-off to reset")]
    bool EngineOffToPlain = false,
    [property: Category("look"), Description("Open-zone sides convert too, inside custom blocks and skins only; roads swapped to the game's own ice roads keep their grass")]
    bool SnowOnZoneSides = false,
    [property: Category("piece"), Description("Deco platforms on or in the ground: native ice with a snow skin")]
    bool SnowOnGroundDeco = false,
    [property: Category("piece"), Description("Blocks that would become whole items become custom blocks instead")]
    bool CustomBlocks = true,
    [property: Category("piece"), Description("Starts, checkpoints and finishes as custom blocks too")]
    bool CustomWaypoints = true,
    [property: Category("piece"), Description("Trees and plants become frozen or snowy ones")]
    bool ConvertTrees = false,
    [property: Category("piece"), Description("Deco blocks, pillars, concrete and walls get snow")]
    bool ConvertScenery = false,
    [property: Category("map"), Description("Empty cells, the game's flat ground, get converted too")]
    bool FillEmptyWithSnow = false,
    [property: Category("map"), Description("Shores (water hills) at ground level get converted above the water line; raised lakes stay")]
    bool SnowOnWaterHills = false,
    [property: Category("map"), Description("Cliffs standing on the ground get converted at their foot; cliffs on terraces stay")]
    bool SnowOnCliffs = false,
    [property: Category("piece"), Description("Native blocks keep their place under an ice skin instead of becoming custom blocks: smaller maps, one ice look")]
    bool SkinsOnly = false,
    [property: Category("piece"), Description("Starts, checkpoints and finishes stay native under an ice skin; they are the largest custom blocks")]
    bool SkinWaypoints = false,
    [property: Category("piece"), Description("Experimental: custom blocks next to a skinned block lift their ice toward it, so the surfaces meet")]
    bool RampToSkins = false,
    [property: Category("piece"), Description("Sides the game draws from clips (dirt, grass edges) convert too, built into the block")]
    bool ClipSidesInBlock = true)
{
    public ConvertOptions With(string name, bool value) => name switch
    {
        "removeForcedCameras" => this with { RemoveForcedCameras = value },
        "engineOffToReset" => this with { EngineOffToReset = value },
        "effectsToGate" => this with { EffectsToGate = value },
        "engineOffToPlain" => this with { EngineOffToPlain = value },
        "snowOnZoneSides" => this with { SnowOnZoneSides = value },
        "snowOnGroundDeco" => this with { SnowOnGroundDeco = value },
        "customBlocks" => this with { CustomBlocks = value },
        "customWaypoints" => this with { CustomWaypoints = value },
        "convertTrees" => this with { ConvertTrees = value },
        "convertScenery" => this with { ConvertScenery = value },
        "fillEmptyWithSnow" => this with { FillEmptyWithSnow = value },
        "snowOnWaterHills" => this with { SnowOnWaterHills = value },
        "snowOnCliffs" => this with { SnowOnCliffs = value },
        "skinsOnly" => this with { SkinsOnly = value },
        "skinWaypoints" => this with { SkinWaypoints = value },
        "rampToSkins" => this with { RampToSkins = value },
        "clipSidesInBlock" => this with { ClipSidesInBlock = value },
        _ => throw new FormatException($"unknown option '{name}' (known: {string.Join(", ", All().Select(o => o.Name))})"),
    };

    // every option by its settings name, with its description
    public IEnumerable<(string Name, bool Value)> All() => Properties.Select(p => (NameOf(p), (bool)p.GetValue(this)!));

    public static IEnumerable<(string Name, string Category, string Description)> Described()
        => Properties.Select(p => (NameOf(p), p.GetCustomAttribute<CategoryAttribute>()?.Category ?? "", p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""));

    private static IEnumerable<PropertyInfo> Properties
        => typeof(ConvertOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(bool));

    private static string NameOf(PropertyInfo p) => char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
}
