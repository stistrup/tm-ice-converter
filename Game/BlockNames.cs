using System.Text.RegularExpressions;

namespace TmIceConverter;

internal static partial class BlockNames
{
    // effect blocks; longest names first, they name the Modifier folder
    [GeneratedRegex("TurboRoulette|Turbo2|Turbo|Boost2|Boost|NoEngine|NoSteering|NoBrake|SlowMotion|Fragile|Reset|Cruise|Reactor")]
    private static partial Regex NoItem();

    public static bool IsGameplayBlock(string name) => NoItem().IsMatch(name);

    public static string? Effect(string name) => NoItem().Match(name) is { Success: true } m && m.Value != "Reactor" ? m.Value : null;

    public static bool IsWallMaterial(string material) => material is "TrackWall" or "TrackWallClips" or "Structure";
}
