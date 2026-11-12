namespace TmSurfaceEditor;

// data/picks.jsonc
internal sealed record PickCatalog(Dictionary<string, PickCatalog.Pick> Picks, Dictionary<string, PickCatalog.Kind> Kinds)
{
    public sealed record Pick(string Label, string Description);

    public sealed record Kind(string Label, List<string> Picks);

    public sealed record Choice(string Value, string Label, string Description);

    public static PickCatalog Load(string dataDir)
    {
        var catalog = Jsonc.Load<PickCatalog>(Path.Combine(dataDir, "picks.jsonc"));
        var unknown = catalog.Picks.Keys.Where(p => p != "" && !ConversionRules.PieceChoices.Contains(p))
            .Concat(catalog.Kinds.Values.SelectMany(k => k.Picks).Where(p => !catalog.Picks.ContainsKey(p))).ToList();
        if (unknown.Count > 0) throw new FormatException($"picks.jsonc: unknown pick(s) {string.Join(", ", unknown.Distinct())}");
        return catalog;
    }

    public Dictionary<string, List<Choice>> Choices
        => Kinds.ToDictionary(k => k.Key, k => k.Value.Picks.Select(p => new Choice(p, Picks[p].Label, Picks[p].Description)).ToList());

    public Dictionary<string, string> KindLabels => Kinds.ToDictionary(k => k.Key, k => k.Value.Label);
}
