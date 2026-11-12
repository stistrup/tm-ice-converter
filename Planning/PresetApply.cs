using System.Text.Json.Nodes;

namespace TmSurfaceEditor;

// a rules run, turned into one pick per piece
internal static class PresetApply
{
    // only: the piece keys to take (null: all); other pieces keep their picks
    public static JsonObject Settings(ConversionPreview.Result result, MapSettings into, IReadOnlySet<string>? only = null)
    {
        var picks = result.Blocks.Values.Concat(result.Items.Values)
            .Where(d => d.Kind != "" && (only is null || only.Contains(Normal(d.Key))))
            .Select(d => (d.Key, PickOf(d)));
        return With(into, picks);
    }

    // "keep" drops their picks
    public static JsonObject Fixed(MapSettings into, IEnumerable<string> keys, string pick)
        => With(into, keys.Select(k => (k, pick == "keep" ? null : (string?)pick)));

    private static JsonObject With(MapSettings into, IEnumerable<(string Key, string? Pick)> picks)
    {
        var json = into.Clone().Json;
        var pieces = json["pieces"] as JsonObject ?? [];
        var byNormal = pieces.Select(p => p.Key).GroupBy(Normal).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, pick) in picks)
        {
            var stored = byNormal.GetValueOrDefault(Normal(key)) ?? key;
            if (pick is not null) pieces[stored] = pick;
            else pieces.Remove(stored);
        }
        json["pieces"] = pieces;
        json["rules"] = false;
        // now in the picks
        json.Remove("snowTerrain");
        json.Remove("skinned");
        return json;
    }

    // null: stays as it is
    private static string? PickOf(ConversionPreview.Decision d)
    {
        if (d.Picked is { } own) return own;
        if (d.Kind == "items")
            return d.Outcome switch { "removed" => "remove", "swapped" => "frozen", "ice" => "copy", "sunk" => "sink", _ => null };
        var rule = d.Rule ?? "";
        return d.Outcome switch
        {
            "kept" or "unchanged" => null,
            // a snowed column carries what stands in it; the gameplay block's own snow pick brings its ground
            _ when BlockRules.CoverColumnRules.Contains(rule) => "snow",
            _ when rule == nameof(CoverRules.UnderCoveredGameplay) => null,
            "twin" when rule == BlockRules.CoverOverTwinRule => "twincover",
            "twin" when rule == BlockRules.SwapTableRule => "table",
            "twin" => "twin",
            "removed" => "remove",
            _ when BlockRules.TopsRules.Contains(rule) => "tops",
            "item" when rule == nameof(Gates.EffectOnGate) => "gate",
            // a custom block the neighbour pass turned into an item: picked custom, the same pass does it again
            _ when rule == BlockRules.CustomBlockRule => "custom",
            "custom" or "item" or "skin" or "tops" => d.Outcome,
            _ => null,
        };
    }

    public static string Normal(string key) => System.Text.RegularExpressions.Regex.Replace(key.Trim(), @"\s*,\s*", ",");
}
