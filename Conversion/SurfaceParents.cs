using System.Text.RegularExpressions;

namespace TmIceConverter;

internal sealed partial class SurfaceParents(HashSet<string> knownBlocks, IBlockShapes shapes)
{
    private readonly Dictionary<(string, int, string?), (string Name, HashSet<int> Missing)?> surfaceParents = [];

    // same family, same cells, its clips minus some that don't connect (penalty sides): those get baked in, the rest stay native
    public (string Name, HashSet<int> Baked)? Of(string name, int variant, IReadOnlySet<int> shown)
    {
        var key = (name, variant, shapes.Environment);
        if (!surfaceParents.TryGetValue(key, out var found))
        {
            var own = shapes.ClipSides(name, false, variant);
            var units = shapes.Units(name, false, variant);
            var family = Family().Match(name).Value;
            found = null;
            shapes.RecordMissing = false;
            // closest name first: RoadTechSlopePenaltyDirt -> RoadTechSlopeStraight before RoadTechNarrowSlope…
            foreach (var other in knownBlocks.Where(o => o != name && family.Length > 0 && o.StartsWith(family) && !o.Contains('\\'))
                .OrderByDescending(o => CommonPrefix(o, name)).ThenBy(o => o.Length).ThenBy(o => o, StringComparer.Ordinal))
            {
                if (units is null || shapes.EnvironmentHas(other) == false || shapes.Units(other, false, variant) is not { } u || !u.SequenceEqual(units)) continue;
                var theirs = shapes.ClipSides(other, false, variant).ToDictionary(s => s.Bit, s => s.Clips.Select(c => c.Name).ToHashSet());
                if (theirs.Any(kv => own.FirstOrDefault(s => s.Bit == kv.Key) is not { } side || !kv.Value.IsSubsetOf(side.Clips.Select(c => c.Name)))) continue;
                var missing = own.SelectMany(s => s.Clips.Where(c => !(theirs.TryGetValue(s.Bit, out var n) && n.Contains(c.Name))).Select(c => (Side: s, Clip: c))).ToList();
                if (missing.Count == 0 || missing.Any(m => m.Clip.Group is not null || m.Clip.SymmetricGroup is not null || m.Clip.Vertical)) continue;
                found = (other, missing.Select(m => MeshExport.ClipKey(m.Side.Bit, m.Clip.Index)).ToHashSet());
                break;
            }
            shapes.RecordMissing = true;
            surfaceParents[key] = found;
        }
        return found is { } f ? (f.Name, f.Missing.Where(shown.Contains).ToHashSet()) : null;
    }

    // leading two words: RoadTech, DecoPlatform
    [GeneratedRegex("^[A-Z][a-z]+[A-Z][a-z]+")]
    private static partial Regex Family();

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }
}
