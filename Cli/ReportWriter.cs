using System.Text;

namespace TmIceConverter;

internal static class ReportWriter
{
    public static string Format(string source, ConversionReport report, IReadOnlyDictionary<string, string> itemFailures)
    {
        var sb = new StringBuilder($"# {Path.GetFileName(source)}\n\n");
        sb.AppendLine("After loading: compute shadows (the map has none; Normal keeps it smaller, rooms take up to 7.1 MB), validate, save.\n");

        if (report.ReplacedByBlock.Count > 0)
        {
            sb.AppendLine($"## Custom ice blocks ({report.ReplacedByBlock.Values.Sum()})\n");
            sb.AppendLine("The native block with an ice body; the game adds its borders, caps and pillars and joins it to its neighbours.\n");
            foreach (var (name, n) in report.ReplacedByBlock.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
            sb.AppendLine();
        }

        if (report.ReplacedByItem.Count > 0)
        {
            sb.AppendLine($"## Replaced by ice items ({report.ReplacedByItem.Values.Sum()})\n");
            foreach (var ((name, why), n) in report.ItemReasons.OrderBy(kv => kv.Key.Reason).ThenByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n} ({why})");
            foreach (var (name, n) in report.KeptByItem) sb.AppendLine($"- {name} x{n}: native neighbour (pillar, road…) that would change next to the items, rebuilt as it was");
            sb.AppendLine();
        }

        if (report.Skinned.Count > 0)
        {
            sb.AppendLine($"## Kept under an ice skin ({report.Skinned.Values.Sum()})\n");
            sb.AppendLine("Ground blocks and deco blocks set into the terrain stay native; their drivable surface gets an ice item on top (not the flat foot at terrain level).\n");
            foreach (var (name, n) in report.Skinned.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
            sb.AppendLine();
        }

        if (report.Ramped.Count > 0)
        {
            sb.AppendLine($"## Ice ramped up to a skinned neighbour ({report.Ramped.Values.Sum()})\n");
            foreach (var (name, n) in report.Ramped.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
            sb.AppendLine();
        }

        if (report.Left > 0)
        {
            sb.AppendLine($"## Left as they were ({report.Left})\n");
            foreach (var (name, n) in report.Unmatched.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- {name} x{n}: {(itemFailures.TryGetValue(name, out var why) ? FirstLine(why) : "no ice version")}");
                sb.AppendLine("\nA block with a known ice equivalent can go in `blocks` in data/swaps.jsonc.\n");
        }

        if (report.FreeBlockOffsetsSkipped > 0)
            sb.AppendLine($"Note: {report.FreeBlockOffsetsSkipped} free-placed blocks had an `offset` that was not applied.\n");

        sb.AppendLine($"## Swapped ({report.Swapped.Values.Sum()})\n");
        sb.AppendLine("| Count | From | To | Source |\n|---:|---|---|---|");
        foreach (var ((from, to, src), n) in report.Swapped.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"| {n} | {from} | {to} | {src} |");

        if (report.SwappedItems.Count > 0)
        {
            sb.AppendLine($"\n## Items swapped ({report.SwappedItems.Values.Sum()})\n");
            sb.AppendLine("| Count | From | To |\n|---:|---|---|");
            foreach (var ((from, to), n) in report.SwappedItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {from} | {to} |");
        }
        if (report.TerrainItems.Count > 0)
        {
            sb.AppendLine($"\n## Hill/cliff terrain as items ({report.TerrainItems.Values.Sum()})\n");
            sb.AppendLine("The block's visible terrain sits in an item beside its custom block: in the block, its light darkened the road after shadows.\n");
            foreach (var (name, n) in report.TerrainItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }
        if (report.SceneryKept.Count > 0)
        {
            sb.AppendLine($"\n## Scenery kept native ({report.SceneryKept.Values.Sum()})\n");
            sb.AppendLine("convertScenery is off: deco blocks, pillars, concrete and walls stay as they are.\n");
            foreach (var (name, n) in report.SceneryKept.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }
        if (report.SnowTerrain.Count > 0)
        {
            sb.AppendLine($"\n## Terrain in snow ({report.SnowTerrain.Values.Sum()})\n");
            sb.AppendLine("From the map's snowTerrain areas; cells under a ground block stay, water stays.\n");
            if (report.FilledCells > 0) sb.AppendLine($"fillEmptyWithSnow: {report.FilledCells} empty cells got the flat terrain block, counted below.\n");
            foreach (var (name, n) in report.SnowTerrain.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }
        if (report.KeptUnder.Count > 0)
        {
            sb.AppendLine($"\n## Native kept under the custom block, for its vegetation/terrain ({report.KeptUnder.Values.Sum()})\n");
            sb.AppendLine("The custom block is a ghost over the native one; its ice is drawn up to 1 cm higher (collision unchanged).\n");
            foreach (var (name, n) in report.KeptUnder.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Trees.Count + report.TreesMissing.Count + report.TreesInWater.Count > 0)
        {
            sb.AppendLine($"\n## Trees of custom blocks, as vegetation items ({report.Trees.Values.Sum()})\n");
            sb.AppendLine("| Count | Tree model | Item |\n|---:|---|---|");
            foreach (var ((model, item), n) in report.Trees.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | {item} |");
            foreach (var (model, n) in report.TreesMissing.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | (no item) |");
            foreach (var (model, n) in report.TreesInWater.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {model} | (over water, left out) |");
        }

        if (report.EngineOff.Count > 0)
        {
            sb.AppendLine($"\n## Engine-off blocks ({report.EngineOff.Values.Sum()})\n");
            sb.AppendLine("Options engineOffToReset / engineOffToPlain: the ones you drive on became the block shown (reset or plain), the gates were removed.\n");
            foreach (var ((from, to), n) in report.EngineOff) sb.AppendLine($"- {from} x{n}: {(to == "" ? "removed" : to)}");
        }
        if (report.IcedItems.Count > 0)
        {
            sb.AppendLine($"\n## Items iced ({report.IcedItems.Values.Sum()})\n");
            sb.AppendLine("Placed items with a driving surface (inflatable tubes and mats, ramps), now ice copies built from their own meshes.\n");
            foreach (var (name, n) in report.IcedItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }
        if (report.ItemsNotIced.Count > 0)
        {
            sb.AppendLine($"\n## Items left as they were ({report.ItemsNotIced.Values.Sum()})\n");
            foreach (var (name, n) in report.ItemsNotIced) sb.AppendLine($"- {name} x{n}: {FirstLine(itemFailures[name])}");
        }

        if (report.Untouched.Count > 0)
        {
            sb.AppendLine($"\n## Never converted ({report.Untouched.Values.Sum()})\n");
            foreach (var (name, n) in report.Untouched) sb.AppendLine($"- {name} x{n}");
        }

        if (report.CameraClipsRemoved.Count > 0)
        {
            sb.AppendLine($"\n## Forced cameras removed ({report.CameraClipsRemoved.Count})\n");
            sb.AppendLine("In-game MediaTracker clips that forced the camera (or only switched it back), taken out.\n");
            foreach (var name in report.CameraClipsRemoved) sb.AppendLine($"- {name}");
        }
        if (report.Walls.Count > 0)
        {
            sb.AppendLine($"\n## Walls, kept native ({report.Walls.Values.Sum()})\n");
            sb.AppendLine("Only walls, nothing to ice: the native wall stone stays (an item would be plain concrete).\n");
            foreach (var (name, n) in report.Walls) sb.AppendLine($"- {name} x{n}");
        }
        if (report.TopsInBlock.Count > 0)
        {
            sb.AppendLine($"\n## Walls and concrete, tops iced in a custom block ({report.TopsInBlock.Values.Sum()})\n");
            sb.AppendLine("A custom block of the native one: its wall material is ice where it faces up as placed, the rest as before.\n");
            foreach (var (name, n) in report.TopsInBlock) sb.AppendLine($"- {name} x{n}");
        }
        if (report.TopsIced.Count > 0)
        {
            sb.AppendLine($"\n## Walls and concrete, tops iced ({report.TopsIced.Values.Sum()})\n");
            sb.AppendLine("Kept native (walls, concrete, structure decks, pillar tops, flipped platforms); an ice skin covers their wall material where it faces up (tops, slopes, curves).\n");
            foreach (var (name, n) in report.TopsIced) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Concrete.Count > 0)
        {
            sb.AppendLine($"\n## Concrete, kept ({report.Concrete.Values.Sum()})\n");
            sb.AppendLine("White deco blocks show concrete; with no native ice version in this environment they stay as they are.\n");
            foreach (var (name, n) in report.Concrete) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Gated.Count > 0)
        {
            sb.AppendLine($"\n## Effect roads made ice under a gate ({report.Gated.Values.Sum()})\n");
            sb.AppendLine("Option effectsToGate: an ice road item without the pad, Nadeo's 32 m gate for the effect (slow motion, reset) across its middle.\n");
            foreach (var (name, n) in report.Gated) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Sunk.Count > 0)
        {
            sb.AppendLine($"\n## Items sunk 2 cm under the ice ({report.Sunk.Values.Sum()})\n");
            sb.AppendLine("Their flat top lay in a converted surface and flickered with it.\n");
            foreach (var (name, n) in report.Sunk) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Recoloured.Count > 0)
        {
            sb.AppendLine($"\n## Platforms given the map's platform colour ({report.Recoloured.Values.Sum()})\n");
            sb.AppendLine("Left at Default (yellow borders) among platforms that all share one colour.\n");
            foreach (var (name, n) in report.Recoloured) sb.AppendLine($"- {name} x{n}");
        }
        if (report.RemovedByList.Count > 0)
        {
            sb.AppendLine($"\n## Removed by the map's removeItems list ({report.RemovedByList.Values.Sum()})\n");
            foreach (var (name, n) in report.RemovedByList) sb.AppendLine($"- {name} x{n}");
        }
        if (report.KeptByList.Count > 0)
        {
            sb.AppendLine($"\n## Kept by the map's keep list ({report.KeptByList.Values.Sum()})\n");
            foreach (var (name, n) in report.KeptByList) sb.AppendLine($"- {name} x{n}");
        }
        if (report.Water.Count > 0)
        {
            sb.AppendLine($"\n## Water, kept native ({report.Water.Values.Sum()})\n");
            foreach (var (name, n) in report.Water.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        }

        if (report.Kept.Count > 0)
        {
            sb.AppendLine($"\n## Kept by table ({report.Kept.Values.Sum()})\n");
            foreach (var (name, n) in report.Kept) sb.AppendLine($"- {name} x{n}");
        }
        return sb.ToString();
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0];
}
