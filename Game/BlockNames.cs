using System.Text.RegularExpressions;

namespace TmIceConverter;

internal static partial class BlockNames
{
    // effect blocks; longest names first, they name the Modifier folder
    [GeneratedRegex("TurboRoulette|Turbo2|Turbo|Boost2|Boost|NoEngine|NoSteering|NoBrake|SlowMotion|Fragile|Reset|Cruise|Reactor")]
    private static partial Regex NoItem();

    public static bool IsGameplayBlock(string name) => NoItem().IsMatch(name);

    // direction picks the effect: entered from the other side it's the other reactor
    public static bool IsReactor(string name) => Regex.IsMatch(name, "Boost|Reactor");

    public static string? Effect(string name) => NoItem().Match(name) is { Success: true } m && m.Value != "Reactor" ? m.Value : null;

    // grass would poke through a skin
    public static bool IsWholeOnGround(string block, string? environment)
        => environment is "GreenCoast" or "BlueBay" && block.StartsWith("DecoPlatform");

    // cliff/hill variants: their Deco is the terrain itself, not a zone side
    public static bool OnTerrainShape(string block) => Regex.IsMatch(block, "On[A-Z][a-z]+(Cliff|Hill)");

    public static bool IsWallMaterial(string material) => material is "TrackWall" or "TrackWallClips" or "Structure";
}
