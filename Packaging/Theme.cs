using System.Text.Json.Serialization;

namespace TmIceConverter;

// what roads turn into and what covers the ground: data/themes/<name>/
public sealed class Theme
{
    public const string DefaultName = "ice";

    public string Tag { get; init; } = "";
    public HashSet<string> Surfaces { get; init; } = [];
    public HashSet<string> Replaces { get; init; } = [];
    public string Road { get; init; } = "";
    public string Cover { get; init; } = "";
    public string Family { get; init; } = "";
    public string PadSurface { get; init; } = "";
    public string? BlockWord { get; init; }

    [JsonIgnore]
    public string Folder { get; private set; } = "";

    public string MaterialsFile => Path.Combine(Folder, "materials.jsonc");

    public IEnumerable<string> Files => [Path.Combine(Folder, "theme.jsonc"), MaterialsFile];

    // file names start with it: recognisable in the editor and the items folder
    public string ItemPrefix => "g" + Tag + "_";

    public string NameTag => "_" + Tag;

    private static readonly Dictionary<string, Theme> loaded = [];

    public static Theme Default => Load(Paths.Data);

    public static Theme Load(string dataDir, string name = DefaultName)
    {
        var folder = Path.GetFullPath(Path.Combine(dataDir, "themes", name));
        lock (loaded)
        {
            if (loaded.TryGetValue(folder, out var known)) return known;
            var theme = Jsonc.Load<Theme>(Path.Combine(folder, "theme.jsonc"));
            theme.Folder = folder;
            return loaded[folder] = theme;
        }
    }

    // a fresh copy each time; "item:" entries only on item copies, "top:" only on top skins
    public Dictionary<string, string> Map(string? prefix)
    {
        var map = Jsonc.Load<Dictionary<string, string>>(MaterialsFile);
        if (prefix is not null)
            foreach (var (key, to) in map.Where(kv => kv.Key.StartsWith(prefix)).ToList()) map[key[prefix.Length..]] = to;
        return map;
    }

    public bool DrivesAsTheme(string material) => MaterialLib.Surface(material) is { } s && Surfaces.Contains(s);

    public bool SwappedIn(IReadOnlyDictionary<string, string> map, string material)
        => map.TryGetValue(material, out var to) && to != material && DrivesAsTheme(to);

    // a library material on a surface the theme leaves alone
    public bool KeepsOwnSurface(string material) => MaterialLib.Surface(material) is { } s && !Replaces.Contains(s);

    // Deco only on deco blocks (or with snowOnZoneSides)
    public bool SkinCovers(string block, string material, bool zoneSides = false)
        => material.StartsWith("Deco") && !(zoneSides && MaterialLib.Surface(material) is { } deco && SkinsOver(deco))
            ? block.StartsWith("Deco")
            : MaterialLib.Surface(material) is { } surface && SkinsOver(surface);

    private bool SkinsOver(string surface) => Replaces.Contains(surface) || surface == "RoadSynthetic";
}
