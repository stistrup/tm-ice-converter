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


    public string? Name { get; private set; }


    public List<(string Name, bool Value)> Settings { get; } = [];

    // per piece: "Name x,y,z" (grid cell) or "Name x, y, z" (free block, item) -> keep | custom | item | skin | snow | tops | plain | remove | frozen
    public Dictionary<string, string> Pieces { get; } = [];

    // snowbottom: older saves, read as snow
    public static readonly string[] PieceChoices = ["keep", "custom", "item", "skin", "snow", "snowbottom", "tops", "plain", "remove", "frozen"];

    // off: only the pieces picked by hand convert; options, areas and map-wide passes stay out
    public bool Presets { get; private set; } = true;

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

    public bool SnowsTerrainAt(int x, int z) => SnowTerrain.Any(a => x >= a.X0 && x <= a.X1 && z >= a.Z0 && z <= a.Z1);

    // skin height over the native surface, metres; null = the code default
    public float? SkinLift { get; private set; }

    // skin skirt angle in degrees (90 = straight down); null = the code default
    public float? SkinEdge { get; private set; }

    // cell areas whose blocks stay native under an ice skin (as skinsOnly does for the whole map)
    public List<(int X0, int Z0, int X1, int Z1)> Skinned { get; } = [];

    public bool SkinsAt(int x, int z) => Options.SkinsOnly || Skinned.Any(a => x >= a.X0 && x <= a.X1 && z >= a.Z0 && z <= a.Z1);

    public bool RemovesItem(string id, System.Numerics.Vector3 at) => RemoveItems.Any(entry => IsAt(entry, id, at) ?? Matches(id, entry));

    // "Name x, y, z": that name within a metre of the point; null when the entry has no point
    public static bool? IsAt(string entry, string id, System.Numerics.Vector3 at)
        => Regex.Match(entry, @"^(\S+)\s+(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)$") is { Success: true } m
            ? Matches(id, m.Groups[1].Value) && System.Numerics.Vector3.Distance(at, new(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture))) <= 1
            : null;

    private static bool Matches(string id, string pattern) => Regex.IsMatch(id, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    private static string NormalizeKeep(string entry) => Regex.Replace(entry.Trim(), @"\s*,\s*", ",");

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
            if (v is "snow" && Regex.Match(k, @"^\S+ (-?\d+),-?\d+,(-?\d+)$") is { Success: true } m)
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
    public static Translations Load(string dataDir) => FromFiles(Path.Combine(dataDir, "swaps.jsonc"));

    private static Translations FromFiles(params string[] paths)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var result = new Translations();
        foreach (var path in paths)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
            var part = Parse(doc.RootElement);
            foreach (var (k, v) in part.Blocks) result.Blocks[k] = v;
            result.IgnorePrefixes.AddRange(part.IgnorePrefixes);
            foreach (var (k, v) in part.UnavailableIn) result.UnavailableIn[k] = v;
            foreach (var (k, v) in part.Items) result.Items[k] = v;
            foreach (var (k, v) in part.ItemsOnSnow) result.ItemsOnSnow[k] = v;
            result.UntouchedItems.AddRange(part.UntouchedItems);
            result.Settings.AddRange(part.Settings);
            result.Keep.UnionWith(part.Keep);
            result.KeepItems.UnionWith(part.KeepItems);
            result.RemoveItems.UnionWith(part.RemoveItems);
        }
        return result;
    }

    // one map's own part, field by field: what doesn't read goes to problems and is left out
    public static Translations ReadMap(JsonElement root, List<string> problems) => Parse(root, problems);

    // problems null: the first bad field throws (data files); else each bad field or entry is skipped and noted
    private static Translations Parse(JsonElement root, List<string>? problems = null)
    {
        var result = new Translations();
        void Read(string what, Action read)
        {
            if (problems is null) { read(); return; }
            try { read(); }
            catch (FormatException e) { problems.Add($"{what}: {e.Message}"); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
            {
                problems.Add($"{what}: wrong type");
            }
        }
        IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new FormatException("not a list");
        IEnumerable<JsonProperty> Object(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : throw new FormatException("not an object");
        string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FormatException($"{e} is not text");

        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("settings must be an object");

        if (root.TryGetProperty("name", out var newName)) Read("name", () => result.Name = Text(newName));
        if (root.TryGetProperty("presets", out var presets)) Read("presets", () => result.Presets = presets.GetBoolean());

        if (root.TryGetProperty("options", out var options))
            Read("options", () =>
            {
                foreach (var option in Object(options))
                    Read($"option {option.Name}", () =>
                    {
                        if (!new ConvertOptions().All().Any(o => o.Name == option.Name)) throw new FormatException("unknown option");
                        if (option.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"{option.Value} is not true/false");
                        result.Settings.Add((option.Name, option.Value.GetBoolean()));
                    });
            });

        foreach (var (key, set, normal) in new[] { ("keep", result.Keep, true), ("keepItems", result.KeepItems, false), ("removeItems", result.RemoveItems, false) })
            if (root.TryGetProperty(key, out var list))
                Read(key, () =>
                {
                    foreach (var entry in Array(list))
                        Read($"{key} entry", () => set.Add(normal ? NormalizeKeep(Text(entry)) : Text(entry).Trim()));
                });
        if (root.TryGetProperty("pieces", out var pieces))
            Read("pieces", () =>
            {
                foreach (var piece in Object(pieces))
                    Read($"piece {piece.Name}", () => result.Pieces[NormalizeKeep(piece.Name)] = piece.Value.ValueKind == JsonValueKind.String && PieceChoices.Contains(piece.Value.GetString())
                        ? piece.Value.GetString() is "snowbottom" ? "snow" : piece.Value.GetString()! : throw new FormatException($"unknown choice {piece.Value}"));
            });
        if (root.TryGetProperty("skinLiftCm", out var lift)) Read("skinLiftCm", () => result.SkinLift = lift.GetSingle() / 100);
        if (root.TryGetProperty("skinEdgeDeg", out var edge)) Read("skinEdgeDeg", () => result.SkinEdge = edge.GetSingle());
        foreach (var (key, areas) in new[] { ("snowTerrain", result.SnowTerrain), ("skinned", result.Skinned) })
            if (root.TryGetProperty(key, out var list))
                Read(key, () =>
                {
                    foreach (var area in Array(list))
                        Read($"{key} area", () =>
                        {
                            if (Array(area).Select(e => e.GetInt32()).ToArray() is not [var x0, var z0, var x1, var z1])
                                throw new FormatException($"{area} is not [x0, z0, x1, z1]");
                            areas.Add((Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1)));
                        });
                });

        if (root.TryGetProperty("ignorePrefixes", out var ignore))
            Read("ignorePrefixes", () => result.IgnorePrefixes.AddRange(Array(ignore).Select(Text)));

        if (root.TryGetProperty("untouchedItems", out var untouched))
            Read("untouchedItems", () => result.UntouchedItems.AddRange(Array(untouched).Select(Text)));

        if (root.TryGetProperty("unavailableIn", out var unavailable))
            Read("unavailableIn", () =>
            {
                foreach (var env in Object(unavailable))
                    result.UnavailableIn[env.Name] = Array(env.Value).Select(Text).ToList();
            });

        foreach (var (name, table) in new[] { ("items", result.Items), ("itemsOnSnow", result.ItemsOnSnow) })
            if (root.TryGetProperty(name, out var itemTable))
                Read(name, () =>
                {
                    foreach (var entry in Object(itemTable))
                        Read($"{name} {entry.Name}", () => table[entry.Name] = entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } to
                            ? to : throw new FormatException($"item '{entry.Name}' needs a target item id"));
                });

        if (root.TryGetProperty("blocks", out var blocks))
            Read("blocks", () =>
            {
                foreach (var entry in Object(blocks))
                    Read($"block {entry.Name}", () => result.Blocks[entry.Name] = ParseEntry(entry.Name, entry.Value));
            });
        return result;
    }

    private static Translation ParseEntry(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new Translation(null),
        JsonValueKind.String when value.GetString() is { Length: > 0 } to => new Translation(to),
        JsonValueKind.String => throw new FormatException($"'{name}' has an empty target; use null to keep the block"),
        JsonValueKind.Object => new Translation(
            value.TryGetProperty("to", out var to) ? to.GetString() : name,
            value.TryGetProperty("rotate", out var rot) ? rot.GetInt32() : 0,
            value.TryGetProperty("offset", out var off) ? off.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null,
            value.TryGetProperty("variant", out var v) ? v.GetByte() : null,
            value.TryGetProperty("subVariant", out var sv) ? sv.GetByte() : null),
        _ => throw new FormatException($"'{name}' must be a string, null or an object"),
    };
}
