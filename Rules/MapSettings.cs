using System.Text.Json;
using System.Text.Json.Nodes;

namespace TmSurfaceEditor;

// one map's choices (settings/maps/<uid>.json); kept as written, so fields this version can't read survive a save
public sealed class MapSettings(JsonObject json)
{
    public JsonObject Json { get; } = json;

    // nothing of its own: data/options.jsonc as it is
    public static MapSettings Empty => new([]);

    public static MapSettings Parse(string text)
        => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
            ? new(Upgrade(o, 0)) : throw new FormatException("settings must be a JSON object");

    // stored settings from an older version, in place: the only place old forms are known
    public static JsonObject Upgrade(JsonObject json, int version)
    {
        // 1: "snowbottom" picks, now plain snow
        if (version < 2 && json["pieces"] is JsonObject pieces)
            foreach (var (key, value) in pieces.ToList())
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s == "snowbottom") pieces[key] = "snow";
        // 2: the global rules' switch was "presets", a word presets now have
        if (version < 3 && json["presets"] is { } on)
        {
            json.Remove("presets");
            json["rules"] = on;
        }
        return json;
    }

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
    public ConversionRules Read(List<string> problems)
    {
        try { return ConversionRules.ReadMap(JsonSerializer.SerializeToElement(Json), problems); }
        catch (FormatException e)
        {
            problems.Add(e.Message);
            return new ConversionRules();
        }
    }

    public override string ToString() => Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
