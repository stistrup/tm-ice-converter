using System.Text.Json;
using System.Text.Json.Nodes;

namespace TmIceConverter;

// one map's choices, shaped like its maps.jsonc entry; kept as written, so fields this version can't read survive a save
public sealed class MapSettings(JsonObject json)
{
    public JsonObject Json { get; } = json;

    // the map's maps.jsonc entry, or nothing
    public static MapSettings Seed(Translations data, string mapName)
        => new(data.MapEntries.TryGetValue(mapName, out var entry) ? entry.DeepClone().AsObject() : []);

    public static MapSettings Parse(string text)
        => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
            ? new(o) : throw new FormatException("settings must be a JSON object");

    public MapSettings Clone() => new(Json.DeepClone().AsObject());

    public MapSettings WithOptions(IEnumerable<(string Name, bool Value)> options)
    {
        var copy = Clone();
        var own = copy.Json["options"] as JsonObject ?? [];
        foreach (var (name, value) in options) own[name] = value;
        copy.Json["options"] = own;
        return copy;
    }

    // what the conversion goes by; problems: fields left out because they don't read
    public Translations Read(List<string> problems)
    {
        try { return Translations.ReadMap(JsonSerializer.SerializeToElement(Json), problems); }
        catch (FormatException e)
        {
            problems.Add(e.Message);
            return new Translations();
        }
    }

    public override string ToString() => Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
