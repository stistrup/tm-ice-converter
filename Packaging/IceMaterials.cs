namespace TmIceConverter;

// data/ice-materials.jsonc
internal sealed class IceMaterials(string dataDir)
{
    private static readonly HashSet<string> IceSurfaces = ["RoadIce", "Ice", "Snow"];

    // "item:" entries only on item copies, "top:" only on top skins
    public Dictionary<string, string> Map(string? prefix)
    {
        var map = Jsonc.Load<Dictionary<string, string>>(Path.Combine(dataDir, "ice-materials.jsonc"));
        if (prefix is not null)
            foreach (var (key, to) in map.Where(kv => kv.Key.StartsWith(prefix)).ToList()) map[key[prefix.Length..]] = to;
        return map;
    }

    public static bool IsIce(string material) => MaterialLib.Surface(material) is { } s && IceSurfaces.Contains(s);

    public bool SwappedToIce(IReadOnlyDictionary<string, string> map, string material)
        => map.TryGetValue(material, out var to) && to != material && IsIce(to);
}
