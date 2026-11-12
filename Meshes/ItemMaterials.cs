using System.Text.RegularExpressions;

namespace TmSurfaceEditor;

// Map: block material -> library material; Native: left as the game's own
internal sealed record ItemMaterials(Dictionary<string, string> Map, HashSet<string> Native)
{
    public static ItemMaterials For(ItemRequest r, ItemRecipe recipe, Theme theme, string? environment)
    {
        var map = theme.Map(recipe.Top ? "top:" : recipe.Skin ? "skin:" : null);
        ApplyBlockPatterns(map, r.Block);
        if (recipe.Body && r.Kind != ItemKind.CoveredGround) RemoveUncoveredDeco(map, theme, r.Block, recipe.ZoneSides);
        // terrain snow: the cut decides, not the map
        if (recipe.OnTerrainBody || r.Kind == ItemKind.CoveredTerrain) Remove(map, m => TerrainFaces.IsTerrain(m, environment));
        // snow bodies keep their water surface (shores, lakes); elsewhere water under ice is left out
        if (r.Kind is ItemKind.CoveredTerrain or ItemKind.CoveredGround) map["Water"] = "native";
        var native = TakeNative(map);
        if (recipe.Skin || recipe.Top) native.Clear();
        if (BlockNames.Effect(r.Block) is { } effect) foreach (var (k, v) in Pads.EffectLook(effect)) map[k] = v;
        return new(map, native);
    }

    // "<block pattern>|<material>": only for those blocks
    private static void ApplyBlockPatterns(Dictionary<string, string> map, string block)
    {
        foreach (var (key, to) in map.Where(kv => kv.Key.Contains('|')).ToList())
            if (key.Split('|') is [var pattern, var material] && Matches(block, pattern))
                map[material] = to;
    }

    private static bool Matches(string block, string pattern)
        => Regex.IsMatch(block, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    private static void RemoveUncoveredDeco(Dictionary<string, string> map, Theme theme, string block, bool zoneSides)
        => Remove(map, m => m.StartsWith("Deco") && !theme.SkinCovers(block, m, zoneSides));

    // "native" in materials.jsonc: the game's own material, linked after import
    private static HashSet<string> TakeNative(Dictionary<string, string> map)
    {
        var native = map.Where(kv => kv.Value == "native").Select(kv => kv.Key).ToHashSet();
        foreach (var key in native) map.Remove(key);
        return native;
    }

    private static void Remove(Dictionary<string, string> map, Func<string, bool> which)
    {
        foreach (var key in map.Keys.Where(which).ToList()) map.Remove(key);
    }
}
