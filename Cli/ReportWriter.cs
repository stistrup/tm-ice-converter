using System.Text;

namespace TmSurfaceEditor;

// .report.md, kept with its run
internal static class ReportWriter
{
    public static string Format(string source, ConversionReport report, IReadOnlyDictionary<string, string> itemFailures)
    {
        var sb = new StringBuilder($"# {Path.GetFileName(source)}\n\n");
        sb.AppendLine("After loading: compute shadows (the map has none; Normal keeps it smaller, rooms take up to 7.1 MB), validate, save.\n");
        Changed(sb, report, itemFailures);
        Swaps(sb, report);
        Extras(sb, report, itemFailures);
        Kept(sb, report);
        return sb.ToString();
    }

    // what became ice, and what couldn't
    private static void Changed(StringBuilder sb, ConversionReport report, IReadOnlyDictionary<string, string> itemFailures)
    {
        Top(sb, "Custom ice blocks", report.ReplacedByBlock, Rows(report.ReplacedByBlock, sorted: true),
            "The native block with an ice body; the game adds its borders, caps and pillars and joins it to its neighbours.");
        Top(sb, "Replaced by ice items", report.ReplacedByItem,
            report.ItemReasons.OrderBy(kv => kv.Key.Reason).ThenByDescending(kv => kv.Value).Select(kv => $"- {kv.Key.Block} x{kv.Value} ({kv.Key.Reason})")
                .Concat(report.KeptByItem.Select(kv => $"- {kv.Key} x{kv.Value}: native neighbour (pillar, road…) that would change next to the items, rebuilt as it was")));
        Top(sb, "Kept under an ice skin", report.Skinned, Rows(report.Skinned, sorted: true),
            "Ground blocks and deco blocks set into the terrain stay native; their drivable surface gets an ice item on top (not the flat foot at terrain level).");
        Top(sb, "Ice ramped up to a skinned neighbour", report.Ramped, Rows(report.Ramped, sorted: true));
        if (report.Left > 0)
        {
            sb.AppendLine($"## Left as they were ({report.Left})\n");
            foreach (var (name, n) in report.Unmatched.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- {name} x{n}: {(itemFailures.TryGetValue(name, out var why) ? FirstLine(why) : "no ice version")}");
            sb.AppendLine("\nA block with a known ice equivalent can go in `blocks` in data/swaps.jsonc.\n");
        }
        if (report.FreeBlockOffsetsSkipped > 0)
            sb.AppendLine($"Note: {report.FreeBlockOffsetsSkipped} free-placed blocks had an `offset` that was not applied.\n");
    }

    private static void Swaps(StringBuilder sb, ConversionReport report)
    {
        sb.AppendLine($"## Swapped ({report.Swapped.Values.Sum()})\n");
        sb.AppendLine("| Count | From | To | Source |\n|---:|---|---|---|");
        foreach (var ((from, to, src), n) in report.Swapped.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"| {n} | {from} | {to} | {src} |");
        if (report.SwappedItems.Count == 0) return;
        sb.AppendLine($"\n## Items swapped ({report.SwappedItems.Values.Sum()})\n");
        sb.AppendLine("| Count | From | To |\n|---:|---|---|");
        foreach (var ((from, to), n) in report.SwappedItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {from} | {to} |");
    }

    private static void Extras(StringBuilder sb, ConversionReport report, IReadOnlyDictionary<string, string> itemFailures)
    {
        Below(sb, "Hill/cliff terrain as items", report.TerrainItems, Rows(report.TerrainItems, sorted: true),
            "The block's visible terrain sits in an item beside its custom block: in the block, its light darkened the road after shadows.");
        Below(sb, "Scenery kept native", report.SceneryKept, Rows(report.SceneryKept, sorted: true),
            "convertScenery is off: deco blocks, pillars, concrete and walls stay as they are.");
        if (report.CoveredTerrain.Count > 0)
        {
            sb.AppendLine($"\n## Terrain in snow ({report.CoveredTerrain.Values.Sum()})\n");
            sb.AppendLine("From the map's snowTerrain areas; cells under a ground block stay, water stays.\n");
            if (report.FilledCells > 0) sb.AppendLine($"fillEmptyWithSnow: {report.FilledCells} empty cells got the flat terrain block, counted below.\n");
            foreach (var row in Rows(report.CoveredTerrain, sorted: true)) sb.AppendLine(row);
        }
        Below(sb, "Native kept under the custom block, for its vegetation/terrain", report.KeptUnder, Rows(report.KeptUnder, sorted: true),
            "The custom block is a ghost over the native one; its ice is drawn up to 1 cm higher (collision unchanged).");
        Trees(sb, report);
        Below(sb, "Engine-off blocks", report.EngineOff, report.EngineOff.Select(kv => $"- {kv.Key.From} x{kv.Value}: {(kv.Key.To == "" ? "removed" : kv.Key.To)}"),
            "Options engineOffToReset / engineOffToPlain: the ones you drive on became the block shown (reset or plain), the gates were removed.");
        Below(sb, "Items iced", report.ThemedItems, Rows(report.ThemedItems, sorted: true),
            "Placed items with a driving surface (inflatable tubes and mats, ramps), now ice copies built from their own meshes.");
        Below(sb, "Items left as they were", report.ItemsNotConverted, report.ItemsNotConverted.Select(kv => $"- {kv.Key} x{kv.Value}: {FirstLine(itemFailures[kv.Key])}"));
        Below(sb, "Never converted", report.Untouched, Rows(report.Untouched));
        if (report.CameraClipsRemoved.Count > 0)
        {
            sb.AppendLine($"\n## Forced cameras removed ({report.CameraClipsRemoved.Count})\n");
            sb.AppendLine("In-game MediaTracker clips that forced the camera (or only switched it back), taken out.\n");
            foreach (var name in report.CameraClipsRemoved) sb.AppendLine($"- {name}");
        }
    }

    private static void Trees(StringBuilder sb, ConversionReport report)
    {
        if (report.Trees.Count + report.TreesMissing.Count + report.TreesInWater.Count == 0) return;
        sb.AppendLine($"\n## Trees of custom blocks, as vegetation items ({report.Trees.Values.Sum()})\n");
        sb.AppendLine("| Count | Tree model | Item |\n|---:|---|---|");
        foreach (var ((model, item), n) in report.Trees.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | {item} |");
        foreach (var (model, n) in report.TreesMissing.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | (no item) |");
        foreach (var (model, n) in report.TreesInWater.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | (over water, left out) |");
    }

    // what stayed native and why
    private static void Kept(StringBuilder sb, ConversionReport report)
    {
        Below(sb, "Walls, kept native", report.Walls, Rows(report.Walls),
            "Only walls, nothing to ice: the native wall stone stays (an item would be plain concrete).");
        Below(sb, "Walls and concrete, tops iced in a custom block", report.TopsInBlock, Rows(report.TopsInBlock),
            "A custom block of the native one: its wall material is ice where it faces up as placed, the rest as before.");
        Below(sb, "Walls and concrete, tops iced", report.TopsThemed, Rows(report.TopsThemed),
            "Kept native (walls, concrete, structure decks, pillar tops, flipped platforms); an ice skin covers their wall material where it faces up (tops, slopes, curves).");
        Below(sb, "Concrete, kept", report.Concrete, Rows(report.Concrete),
            "White deco blocks show concrete; with no native ice version in this environment they stay as they are.");
        Below(sb, "Effect roads made ice under a gate", report.Gated, Rows(report.Gated),
            "Option effectsToGate: an ice road item without the pad, Nadeo's 32 m gate for the effect (slow motion, reset) across its middle.");
        Below(sb, "Items sunk 2 cm under the ice", report.Sunk, Rows(report.Sunk), "Their flat top lay in a converted surface and flickered with it.");
        Below(sb, "Platforms given the map's platform colour", report.Recoloured, Rows(report.Recoloured),
            "Left at Default (yellow borders) among platforms that all share one colour.");
        Below(sb, "Removed by the map's removeItems list", report.RemovedByList, Rows(report.RemovedByList));
        Below(sb, "Kept by the map's keep list", report.KeptByList, Rows(report.KeptByList));
        Below(sb, "Water, kept native", report.Water, Rows(report.Water, sorted: true));
        Below(sb, "Kept by table", report.Kept, Rows(report.Kept));
    }

    // sections above the swap table end with a blank line, the ones below start with one
    private static void Top<T>(StringBuilder sb, string title, IReadOnlyDictionary<T, int> counts, IEnumerable<string> rows, string? note = null)
    {
        if (counts.Count == 0) return;
        sb.AppendLine($"## {title} ({counts.Values.Sum()})\n");
        if (note is not null) sb.AppendLine(note + "\n");
        foreach (var row in rows) sb.AppendLine(row);
        sb.AppendLine();
    }

    private static void Below<T>(StringBuilder sb, string title, IReadOnlyDictionary<T, int> counts, IEnumerable<string> rows, string? note = null)
    {
        if (counts.Count == 0) return;
        sb.AppendLine($"\n## {title} ({counts.Values.Sum()})\n");
        if (note is not null) sb.AppendLine(note + "\n");
        foreach (var row in rows) sb.AppendLine(row);
    }

    private static IEnumerable<string> Rows(IReadOnlyDictionary<string, int> counts, bool sorted = false)
        => (sorted ? counts.OrderByDescending(kv => kv.Value) : counts.AsEnumerable()).Select(kv => $"- {kv.Key} x{kv.Value}");

    private static string FirstLine(string text) => text.Split('\n', 2)[0];
}
