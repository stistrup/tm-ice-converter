using System.ComponentModel;
using System.Reflection;

namespace TmIceConverter;

// the map-wide switches; these defaults are what a map without settings of its own converts with.
// Description: the GUI's info text
public sealed record ConvertOptions(
    [property: Description("Forced camera changes are taken out of the in-game clips")]
    bool RemoveForcedCameras = false,
    [property: Description("Engine-off blocks become reset blocks, engine-off gates are removed")]
    bool EngineOffToReset = false,
    [property: Description("Slow-motion and reset pads become ice with a gate that keeps the effect")]
    bool EffectsToGate = false,
    [property: Description("Engine-off blocks become plain blocks, engine-off gates are removed; wins over engine-off to reset")]
    bool EngineOffToPlain = false,
    [property: Description("Snow on open-zone sides, inside custom blocks and skins only; roads swapped to the game's own ice roads keep their grass")]
    bool SnowOnZoneSides = false,
    [property: Description("Deco platforms on or in the ground: native ice with a snow skin")]
    bool SnowOnGroundDeco = false,
    [property: Description("Blocks that would become whole items become custom blocks instead")]
    bool CustomBlocks = true,
    [property: Description("Starts, checkpoints and finishes as custom blocks too")]
    bool CustomWaypoints = true,
    [property: Description("Trees and plants become frozen or snowy ones")]
    bool ConvertTrees = false,
    [property: Description("Deco blocks, pillars, concrete and walls get snow")]
    bool ConvertScenery = false,
    [property: Description("Empty cells, the game's flat ground, become snow too")]
    bool FillEmptyWithSnow = false,
    [property: Description("Shores (water hills) get snow above the water line")]
    bool SnowOnWaterHills = false,
    [property: Description("Cliffs get snow at their foot")]
    bool SnowOnCliffs = false,
    [property: Description("Native blocks keep their place under an ice skin instead of becoming custom blocks: smaller maps, one ice look")]
    bool SkinsOnly = false,
    [property: Description("Starts, checkpoints and finishes stay native under an ice skin; they are the largest custom blocks")]
    bool SkinWaypoints = false,
    [property: Description("Experimental: custom blocks next to a skinned block lift their ice toward it, so the surfaces meet")]
    bool RampToSkins = false)
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
        _ => throw new FormatException($"unknown option '{name}' (known: {string.Join(", ", All().Select(o => o.Name))})"),
    };

    // every option by its settings name, with its description
    public IEnumerable<(string Name, bool Value)> All() => Properties.Select(p => (NameOf(p), (bool)p.GetValue(this)!));

    public static IEnumerable<(string Name, string Description)> Described()
        => Properties.Select(p => (NameOf(p), p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""));

    private static IEnumerable<PropertyInfo> Properties
        => typeof(ConvertOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(bool));

    private static string NameOf(PropertyInfo p) => char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
}
