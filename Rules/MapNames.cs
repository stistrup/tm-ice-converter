using System.Text.RegularExpressions;

namespace TmIceConverter;

public static partial class MapNames
{
    // without the game's $ codes (colours, styles, links)
    public static string Plain(string name) => FormatCodes().Replace(name, "");

    // the converter's default: the map's name (or its maps.jsonc name), its old ice tag dropped, " (ICE)"
    public static string DefaultOutput(string mapName, Translations translations, string? own = null)
        => (own ?? translations.NameFor(IceTag().Replace(mapName, ""))) + " (ICE)";

    // a file name for a map name; null when Windows wouldn't take it (the game runs there or under Wine)
    public static string? FileName(string mapName)
    {
        var plain = Plain(mapName).Trim();
        return plain.Length == 0 || plain.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || plain.Any(char.IsControl) || plain.EndsWith('.')
            ? null : plain + ".Map.Gbx";
    }

    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[lLhHpP](\[[^\]]*\])?|.)")]
    private static partial Regex FormatCodes();

    [GeneratedRegex(@"\s*\(ice[^)]*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex IceTag();
}
