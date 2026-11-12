using System.Text.Json.Nodes;

namespace TmSurfaceEditor;

// data/presets.jsonc
internal sealed record Preset(string Name, List<string> Kinds, Dictionary<string, bool>? Options, string? Pick)
{
    public static List<Preset> Load(string dataDir) => Jsonc.Load<List<Preset>>(Path.Combine(dataDir, "presets.jsonc"));
}

internal static class PresetRun
{
    // the map's own rules become picks first (once), then the preset's work replaces its pieces' picks
    public static async Task<JsonObject> Apply(ConversionPreview preview, string mapPath, MapSettings working, Preset preset, IReadOnlySet<string>? only)
    {
        var settings = working;
        if (working.Json["rules"]?.GetValue<bool>() != false)
            settings = new MapSettings(PresetApply.Settings(await preview.Settled(mapPath, working), working));
        var rules = settings.Clone();
        rules.Json["rules"] = true;
        rules.Json["pieces"] = new JsonObject();
        var options = rules.Json["options"] as JsonObject ?? [];
        foreach (var (name, value) in preset.Options ?? []) options[name] = value;
        rules.Json["options"] = options;
        var result = await preview.Settled(mapPath, rules);
        bool Takes(ConversionPreview.Decision d) => preset.Kinds.Contains(d.Kind) && (only is null || only.Contains(PresetApply.Normal(d.Key)));
        var scope = result.Blocks.Values.Concat(result.Items.Values).Where(Takes).Select(d => PresetApply.Normal(d.Key)).ToHashSet();
        return preset.Pick is { } pick
            ? PresetApply.Fixed(settings, scope, pick)
            : PresetApply.Settings(result, settings, scope);
    }
}
