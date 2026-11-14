namespace TmSurfaceEditor;

// data/picks.jsonc: a pick = material + way (+ option)
internal sealed record PickCatalog(
    Dictionary<string, PickCatalog.Named> Materials,
    Dictionary<string, PickCatalog.Named> Ways,
    Dictionary<string, PickCatalog.Named> Options,
    Dictionary<string, PickCatalog.Pick> Picks,
    Dictionary<string, PickCatalog.Kind> Kinds)
{
    public sealed record Named(string Label, string Description);

    public sealed record Pick(string Label, string Material, string? Way, string? Option);

    public sealed record Kind(string Label, List<string> Picks);

    public sealed record Choice(string Value, string Label, string Material, string? Way, string? Option);

    // labels in their order: ways in theirs is the default order
    public sealed record Words(Dictionary<string, Named> Materials, Dictionary<string, Named> Ways, Dictionary<string, Named> Options);

    public static PickCatalog Load(string dataDir)
    {
        var catalog = Jsonc.Load<PickCatalog>(Path.Combine(dataDir, "picks.jsonc"));
        var unknown = catalog.Picks.Keys.Where(p => p != "" && !ConversionRules.PieceChoices.Contains(p))
            .Concat(catalog.Kinds.Values.SelectMany(k => k.Picks).Where(p => !catalog.Picks.ContainsKey(p)))
            .Concat(catalog.Picks.Values.SelectMany(p => new[] { p.Material }.Where(m => !catalog.Materials.ContainsKey(m))
                .Concat(new[] { p.Way }.Where(w => w is not null && !catalog.Ways.ContainsKey(w)))
                .Concat(new[] { p.Option }.Where(o => o is not null && !catalog.Options.ContainsKey(o)))!))
            .ToList();
        if (unknown.Count > 0) throw new FormatException($"picks.jsonc: unknown pick(s), material(s), way(s) or option(s) {string.Join(", ", unknown.Distinct())}");
        return catalog;
    }

    public Dictionary<string, List<Choice>> Choices
        => Kinds.ToDictionary(k => k.Key, k => k.Value.Picks.Select(p => Picks[p] is var c ? new Choice(p, c.Label, c.Material, c.Way, c.Option) : null!).ToList());

    public Dictionary<string, string> KindLabels => Kinds.ToDictionary(k => k.Key, k => k.Value.Label);

    public Words Labels => new(Materials, Ways, Options);
}
