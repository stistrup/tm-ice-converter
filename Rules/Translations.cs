using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TmIceConverter;

public sealed record Translation(
    string? To, // null = keep
    int Rotate = 0, // quarter turns
    int[]? Offset = null,
    byte? Variant = null,
    byte? SubVariant = null);

public sealed class Translations
{
    public Dictionary<string, Translation> Blocks { get; } = [];
    public List<string> IgnorePrefixes { get; } = [];
    public Dictionary<string, List<string>> UnavailableIn { get; } = [];
    public Dictionary<string, string> Items { get; } = [];

    public Dictionary<string, string> ItemsOnSnow { get; } = [];

    // global only, no pick or map setting changes them
    public List<string> UntouchedItems { get; } = [];

    public bool Untouched(string itemId) => UntouchedItems.Any(p => Matches(itemId, p));

    public Dictionary<string, Translations> Maps { get; } = [];

    public string? Name { get; internal set; }

    public List<(string Name, bool Value)> Settings { get; } = [];

    // per piece: "Name x,y,z" (grid cell) or "Name x, y, z" (free block, item) -> keep | custom | item | skin | snow | tops | plain | remove | frozen
    public Dictionary<string, string> Pieces { get; } = [];

    // snowbottom: older saves, read as snow
    public static readonly string[] PieceChoices = ["keep", "custom", "item", "skin", "snow", "snowbottom", "tops", "plain", "remove", "frozen", "flip", "snowflip"];

    // off: only the pieces picked by hand convert; options, areas and map-wide passes stay out
    public bool Presets { get; internal set; } = true;

    public string? PieceChoice(GBX.NET.Engines.Game.CGameCtnBlock block)
        => block.IsFree
            ? block.AbsolutePositionInMap is { } p ? ChoiceAt(block.Name, new System.Numerics.Vector3(p.X, p.Y, p.Z)) : null
            : Pieces.GetValueOrDefault(KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z));

    public string? ChoiceAt(string id, System.Numerics.Vector3 at) => Pieces.FirstOrDefault(kv => IsAt(kv.Key, id, at) == true).Value;

    public ConvertOptions Options { get; private set; } = new();

    public HashSet<string> Keep { get; } = [];

    public static string KeepKey(string name, int x, int y, int z) => $"{name} {x},{y},{z}";

    public HashSet<string> KeepItems { get; } = [];

    public bool KeepsItem(string id) => KeepItems.Any(p => Matches(id, p));

    public HashSet<string> RemoveItems { get; } = [];

    // terrain cell areas (x0, z0, x1, z1) that become snow
    public List<(int X0, int Z0, int X1, int Z1)> SnowTerrain { get; } = [];

    // columns snowed by the terrain options (CoverFill)
    public HashSet<(int X, int Z)> SnowCells { get; } = [];
    public bool SnowsTerrainAt(int x, int z) => SnowCells.Contains((x, z)) || SnowTerrain.Any(a => x >= a.X0 && x <= a.X1 && z >= a.Z0 && z <= a.Z1);

    // skin height over the native surface, metres; null = the code default
    public float? SkinLift { get; internal set; }

    // skin skirt angle in degrees (90 = straight down); null = the code default
    public float? SkinEdge { get; internal set; }

    // cell areas whose blocks stay native under an ice skin (as skinsOnly does for the whole map)
    public List<(int X0, int Z0, int X1, int Z1)> Skinned { get; } = [];

    public bool SkinsAt(int x, int z) => Options.SkinsOnly || Skinned.Any(a => x >= a.X0 && x <= a.X1 && z >= a.Z0 && z <= a.Z1);

    public bool RemovesItem(string id, System.Numerics.Vector3 at) => RemoveItems.Any(entry => IsAt(entry, id, at) ?? Matches(id, entry));

    // "Name x, y, z": that name within a metre of the point; null when the entry has no point
    public static bool? IsAt(string entry, string id, System.Numerics.Vector3 at)
    {
        if (Regex.Match(entry, @"^(\S+)\s+(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)$") is not { Success: true } m) return null;
        float Number(int group) => float.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
        return Matches(id, m.Groups[1].Value) && System.Numerics.Vector3.Distance(at, new(Number(2), Number(3), Number(4))) <= 1;
    }

    private static bool Matches(string id, string pattern) => Regex.IsMatch(id, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    internal static string NormalizeKeep(string entry) => Regex.Replace(entry.Trim(), @"\s*,\s*", ",");

    // the global rules with one map's own (MapSettings.Read)
    public Translations For(string mapName, Translations? own = null)
    {
        own ??= new Translations();
        var result = new Translations { Name = own.Name, Presets = own.Presets };
        result.Options = Settings.Concat(own.Settings).Aggregate(new ConvertOptions(), (o, s) => o.With(s.Name, s.Value));
        result.IgnorePrefixes.AddRange(IgnorePrefixes.Concat(own.IgnorePrefixes));
        foreach (var (k, v) in Blocks.Concat(own.Blocks)) result.Blocks[k] = v;
        result.Keep.UnionWith(Keep.Concat(own.Keep));
        result.KeepItems.UnionWith(KeepItems.Concat(own.KeepItems));
        result.RemoveItems.UnionWith(RemoveItems.Concat(own.RemoveItems));
        if (own.Presets)
        {
            result.SnowTerrain.AddRange(own.SnowTerrain);
            result.Skinned.AddRange(own.Skinned);
        }
        result.SkinLift = own.SkinLift;
        result.SkinEdge = own.SkinEdge;
        foreach (var (k, v) in own.Pieces) result.Pieces[NormalizeKeep(k)] = v;
        // snow picked on a grid piece: its column is a snowTerrain area
        foreach (var (k, v) in result.Pieces)
            if (v is "snow" or "snowflip" && Regex.Match(k, @"^\S+ (-?\d+),-?\d+,(-?\d+)$") is { Success: true } m)
            {
                var (x, z) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                result.SnowTerrain.Add((x, z, x, z));
            }
        foreach (var (k, v) in UnavailableIn) result.UnavailableIn[k] = v;
        foreach (var (k, v) in Items) result.Items[k] = v;
        foreach (var (k, v) in ItemsOnSnow) result.ItemsOnSnow[k] = v;
        return result;
    }

    public bool IsAvailable(string block, string collection)
        => !(UnavailableIn.TryGetValue(collection, out var prefixes) && prefixes.Any(block.StartsWith)
            || collection != "Stadium2020" && UnavailableIn.TryGetValue("*", out var everywhere) && everywhere.Any(block.StartsWith));

    // "From": "To" | null | { "to", "rotate", "offset", "variant" }
    public static Translations Load(string dataDir) => TranslationsReader.FromFiles(Path.Combine(dataDir, "swaps.jsonc"));

    // one map's own part, field by field: what doesn't read goes to problems and is left out
    public static Translations ReadMap(JsonElement root, List<string> problems) => TranslationsReader.Parse(root, problems);
}
