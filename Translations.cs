using System.Text.Json;
using System.Text.RegularExpressions;

namespace TmIceConverter;

public sealed record Translation(
    string? To, // null = keep
    int Rotate = 0, // quarter turns
    int[]? Offset = null,
    byte? Variant = null,
    byte? SubVariant = null);

public sealed record ConvertOptions(bool RemoveForcedCameras = false, bool KeepPlasticWalls = false, bool EngineOffToReset = false, bool EffectsToGate = false, bool EngineOffToPlain = false, bool SnowOnZoneSides = false)
{
    public ConvertOptions With(string name, bool value) => name switch
    {
        "removeForcedCameras" => this with { RemoveForcedCameras = value },
        "keepPlasticWalls" => this with { KeepPlasticWalls = value },
        "engineOffToReset" => this with { EngineOffToReset = value },
        "effectsToGate" => this with { EffectsToGate = value },
        "engineOffToPlain" => this with { EngineOffToPlain = value },
        "snowOnZoneSides" => this with { SnowOnZoneSides = value },
        _ => throw new FormatException($"unknown option '{name}' (known: removeForcedCameras, keepPlasticWalls, engineOffToReset, effectsToGate, engineOffToPlain, snowOnZoneSides)"),
    };
}

public sealed class Translations
{
    public Dictionary<string, Translation> Blocks { get; } = [];
    public List<string> IgnorePrefixes { get; } = [];
    public Dictionary<string, List<string>> UnavailableIn { get; } = [];
    public Dictionary<string, string> Items { get; } = [];

    public Dictionary<string, string> ItemsOnSnow { get; } = [];

    public Dictionary<string, Translations> Maps { get; } = [];

    public string? Name { get; private set; }

    public string NameFor(string mapName) => Maps.GetValueOrDefault(mapName)?.Name ?? mapName;

    public List<(string Name, bool Value)> Settings { get; } = [];

    public List<(string Name, bool Value)> RunSettings { get; } = [];

    public ConvertOptions Options { get; private set; } = new();

    public HashSet<string> Keep { get; } = [];

    public static string KeepKey(string name, int x, int y, int z) => $"{name} {x},{y},{z}";

    public HashSet<string> KeepItems { get; } = [];

    public bool KeepsItem(string id) => KeepItems.Any(p => Matches(id, p));

    public HashSet<string> RemoveItems { get; } = [];

    public bool RemovesItem(string id, System.Numerics.Vector3 at) => RemoveItems.Any(entry =>
        Regex.Match(entry, @"^(\S+)\s+(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)$") is { Success: true } m
            ? Matches(id, m.Groups[1].Value) && System.Numerics.Vector3.Distance(at, new(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture))) <= 1
            : Matches(id, entry));

    private static bool Matches(string id, string pattern) => Regex.IsMatch(id, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    private static string NormalizeKeep(string entry) => Regex.Replace(entry.Trim(), @"\s*,\s*", ",");

    public Translations For(string mapName)
    {
        var own = Maps.GetValueOrDefault(mapName) ?? new Translations();
        var result = new Translations();
        result.Options = Settings.Concat(own.Settings).Concat(RunSettings).Aggregate(new ConvertOptions(), (o, s) => o.With(s.Name, s.Value));
        result.IgnorePrefixes.AddRange(IgnorePrefixes.Concat(own.IgnorePrefixes));
        if (result.Options.KeepPlasticWalls) result.IgnorePrefixes.Add("PlatformPlasticWall");
        foreach (var (k, v) in Blocks.Concat(own.Blocks)) result.Blocks[k] = v;
        result.Keep.UnionWith(Keep.Concat(own.Keep));
        result.KeepItems.UnionWith(KeepItems.Concat(own.KeepItems));
        result.RemoveItems.UnionWith(RemoveItems.Concat(own.RemoveItems));
        foreach (var (k, v) in UnavailableIn) result.UnavailableIn[k] = v;
        foreach (var (k, v) in Items) result.Items[k] = v;
        foreach (var (k, v) in ItemsOnSnow) result.ItemsOnSnow[k] = v;
        return result;
    }

    public bool IsAvailable(string block, string collection)
        => !(UnavailableIn.TryGetValue(collection, out var prefixes) && prefixes.Any(block.StartsWith)
            || collection != "Stadium2020" && UnavailableIn.TryGetValue("*", out var everywhere) && everywhere.Any(block.StartsWith));

    // "From": "To" | null | { "to", "rotate", "offset", "variant" }
    public static Translations Load(string path)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
        var result = Parse(doc.RootElement);
        if (doc.RootElement.TryGetProperty("maps", out var maps))
            foreach (var map in maps.EnumerateObject())
                result.Maps[map.Name] = Parse(map.Value);
        return result;
    }

    private static Translations Parse(JsonElement root)
    {
        var result = new Translations();

        if (root.TryGetProperty("name", out var newName)) result.Name = newName.GetString();

        if (root.TryGetProperty("options", out var options))
            foreach (var option in options.EnumerateObject())
            {
                result.Settings.Add((option.Name, option.Value.GetBoolean()));
                new ConvertOptions().With(option.Name, false);
            }

        if (root.TryGetProperty("keep", out var keep))
            result.Keep.UnionWith(keep.EnumerateArray().Select(e => NormalizeKeep(e.GetString()!)));
        if (root.TryGetProperty("keepItems", out var keepItems))
            result.KeepItems.UnionWith(keepItems.EnumerateArray().Select(e => e.GetString()!.Trim()));
        if (root.TryGetProperty("removeItems", out var removeItems))
            result.RemoveItems.UnionWith(removeItems.EnumerateArray().Select(e => e.GetString()!.Trim()));

        if (root.TryGetProperty("ignorePrefixes", out var ignore))
            result.IgnorePrefixes.AddRange(ignore.EnumerateArray().Select(e => e.GetString()!));

        if (root.TryGetProperty("unavailableIn", out var unavailable))
            foreach (var env in unavailable.EnumerateObject())
                result.UnavailableIn[env.Name] = env.Value.EnumerateArray().Select(e => e.GetString()!).ToList();

        foreach (var (name, table) in new[] { ("items", result.Items), ("itemsOnSnow", result.ItemsOnSnow) })
            if (root.TryGetProperty(name, out var itemTable))
                foreach (var entry in itemTable.EnumerateObject())
                    table[entry.Name] = entry.Value.GetString() is { Length: > 0 } to
                        ? to : throw new FormatException($"item '{entry.Name}' needs a target item id");

        if (root.TryGetProperty("blocks", out var blocks))
        {
            foreach (var entry in blocks.EnumerateObject())
                result.Blocks[entry.Name] = ParseEntry(entry.Name, entry.Value);
        }
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
