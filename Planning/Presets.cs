using System.Text.Json.Nodes;

namespace TmSurfaceEditor;

// one group ("" = all, else a path and everything under it), one method from groups.jsonc; "original" takes the group's targets away
internal sealed record GroupStep(string Group, string Method);

// data/presets.jsonc
internal sealed record Preset(string Name, List<GroupStep> Steps)
{
    public static List<Preset> Load(string dataDir) => Jsonc.Load<List<Preset>>(Path.Combine(dataDir, "presets.jsonc"));
}

internal static class GroupRun
{
    public const string Original = "original";

    // only: piece keys to keep it to (null: the whole map)
    public static async Task<JsonObject> Apply(ConversionPreview preview, string mapPath, MapSettings working, IEnumerable<GroupStep> steps, GroupCatalog catalog, IReadOnlySet<string>? only)
    {
        var settings = working;
        // still on rules: those become targets first, once
        if (working.Json["rules"]?.GetValue<bool>() == true)
            settings = new MapSettings(PresetApply.Settings(await preview.Settled(mapPath, working), working));
        foreach (var step in steps)
        {
            var method = step.Method == Original ? null
                : catalog.Methods.GetValueOrDefault(step.Method) ?? throw new FormatException($"no method {step.Method}");
            var result = await preview.Settled(mapPath, RulesFor(settings, method?.Options));
            var scope = result.Blocks.Values.Concat(result.Items.Values)
                .Where(d => d.Group is { } g && (step.Group == "" || g == step.Group || g.StartsWith(step.Group + "/")))
                .Select(d => PresetApply.Normal(d.Key))
                .Where(k => only is null || only.Contains(k))
                .ToHashSet();
            settings = new MapSettings(method is null ? PresetApply.Fixed(settings, scope, "keep")
                : method.Pick is { } pick ? PresetApply.Fixed(settings, scope, pick)
                : PresetApply.Settings(result, settings, scope));
        }
        return settings.Json;
    }

    // the rules with the method's options; the snow targets stay, the columns they make change what stands in them
    private static MapSettings RulesFor(MapSettings settings, Dictionary<string, bool>? methodOptions)
    {
        var rules = settings.Clone();
        rules.Json["rules"] = true;
        var snow = new JsonObject();
        foreach (var (key, value) in settings.Json["pieces"] as JsonObject ?? [])
            if (value?.GetValue<string>() is "snow" or "snowflip") snow[key] = value.GetValue<string>();
        rules.Json["pieces"] = snow;
        var options = rules.Json["options"] as JsonObject ?? [];
        foreach (var (name, value) in methodOptions ?? []) options[name] = value;
        rules.Json["options"] = options;
        return rules;
    }
}
