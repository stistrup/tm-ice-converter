using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TmSurfaceEditor;

public sealed record Translation(
    string? To, // null = keep
    int Rotate = 0, // quarter turns
    int[]? Offset = null,
    byte? Variant = null,
    byte? SubVariant = null);

public sealed class ConversionRules
{
    public Dictionary<string, Translation> Blocks { get; } = [];
    public List<string> IgnorePrefixes { get; } = [];
    public Dictionary<string, List<string>> UnavailableIn { get; } = [];
    public Dictionary<string, string> Items { get; } = [];

    public Dictionary<string, string> ItemsOnCover { get; } = [];

    // global only, no pick or map setting changes them
    public List<string> UntouchedItems { get; } = [];

    public bool Untouched(string itemId) => UntouchedItems.Any(p => Matches(itemId, p));

    public Dictionary<string, ConversionRules> Maps { get; } = [];

    public string? Name { get; internal set; }

    public List<(string Name, bool Value)> Settings { get; } = [];

    // per piece: "Name x,y,z" (grid cell) or "Name x, y, z" (free block, item) -> keep | custom | item | skin | snow | tops | plain | remove | frozen
    public Dictionary<string, string> Pieces { get; } = [];

    // copy, sink: items, as their rules do
    public static readonly string[] PieceChoices = ["keep", "custom", "item", "skin", "snow", "tops", "twin", "twincover", "table", "gate", "copy", "sink", "plain", "remove", "frozen", "flip", "snowflip"];

    // "rules": true, maps from before conversion targets: the rules decide every piece without a pick, the snow and skin areas count.
    // Off (the default): only pieces with a pick convert; map-wide options still run
    public bool RulesOn { get; internal set; }

    // grid blocks sharing a cell and name, by their own keys (PieceKeys); set per map before anything moves them
    public Dictionary<GBX.NET.Engines.Game.CGameCtnBlock, string> AmbiguousKeys { get; set; } = new(ReferenceEqualityComparer.Instance);

    // a shared block's own key first, then the cell's
    public string? PieceChoice(GBX.NET.Engines.Game.CGameCtnBlock block)
        => block.IsFree
            ? block.AbsolutePositionInMap is { } p ? ChoiceAt(block.Name, new System.Numerics.Vector3(p.X, p.Y, p.Z)) : null
            : AmbiguousKeys.TryGetValue(block, out var own) && Pieces.TryGetValue(NormalizeKeep(own), out var picked) ? picked
            : Pieces.GetValueOrDefault(KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z));

    public string? ChoiceAt(string id, System.Numerics.Vector3 at) => Pieces.FirstOrDefault(kv => IsAt(kv.Key, id, at) == true).Value;

    public ConvertOptions Options { get; private set; } = new();

    public HashSet<string> Keep { get; } = [];

    public static string KeepKey(string name, int x, int y, int z) => $"{name} {x},{y},{z}";

    public HashSet<string> KeepItems { get; } = [];

    public bool KeepsItem(string id) => KeepItems.Any(p => Matches(id, p));

    public HashSet<string> RemoveItems { get; } = [];

    // terrain cell areas (x0, z0, x1, z1) that become snow
    public List<(int X0, int Z0, int X1, int Z1)> CoverAreas { get; } = [];

    // columns snowed by the terrain options (CoverFill)
    public HashSet<(int X, int Z)> CoverCells { get; } = [];
    public bool CoversTerrainAt(int x, int z) => CoverCells.Contains((x, z)) || CoverAreas.Any(a => x >= a.X0 && x <= a.X1 && z >= a.Z0 && z <= a.Z1);

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
    public ConversionRules For(string mapName, ConversionRules? own = null)
    {
        own ??= new ConversionRules();
        var result = new ConversionRules { Name = own.Name, RulesOn = own.RulesOn };
        result.Options = Settings.Concat(own.Settings).Aggregate(new ConvertOptions(), (o, s) => o.With(s.Name, s.Value));
        result.IgnorePrefixes.AddRange(IgnorePrefixes.Concat(own.IgnorePrefixes));
        foreach (var (k, v) in Blocks.Concat(own.Blocks)) result.Blocks[k] = v;
        result.Keep.UnionWith(Keep.Concat(own.Keep));
        result.KeepItems.UnionWith(KeepItems.Concat(own.KeepItems));
        result.RemoveItems.UnionWith(RemoveItems.Concat(own.RemoveItems));
        if (own.RulesOn)
        {
            result.CoverAreas.AddRange(own.CoverAreas);
            result.Skinned.AddRange(own.Skinned);
        }
        result.SkinLift = own.SkinLift;
        result.SkinEdge = own.SkinEdge;
        foreach (var (k, v) in own.Pieces) result.Pieces[NormalizeKeep(k)] = v;
        // snow picked on a grid piece: its column is a snowTerrain area
        foreach (var (k, v) in result.Pieces)
            if (v is "snow" or "snowflip" && Regex.Match(k, @"^\S+ (-?\d+),-?\d+,(-?\d+)(\s|$)") is { Success: true } m)
            {
                var (x, z) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                result.CoverAreas.Add((x, z, x, z));
            }
        foreach (var (k, v) in UnavailableIn) result.UnavailableIn[k] = v;
        foreach (var (k, v) in Items) result.Items[k] = v;
        foreach (var (k, v) in ItemsOnCover) result.ItemsOnCover[k] = v;
        return result;
    }

    public bool IsAvailable(string block, string collection)
        => !(UnavailableIn.TryGetValue(collection, out var prefixes) && prefixes.Any(block.StartsWith)
            || collection != "Stadium2020" && UnavailableIn.TryGetValue("*", out var everywhere) && everywhere.Any(block.StartsWith));

    // "From": "To" | null | { "to", "rotate", "offset", "variant" }
    public static ConversionRules Load(string dataDir) => ConversionRulesReader.FromFiles(Path.Combine(dataDir, "swaps.jsonc"));

    // one map's own part, field by field: what doesn't read goes to problems and is left out
    public static ConversionRules ReadMap(JsonElement root, List<string> problems) => ConversionRulesReader.Parse(root, problems);
}
