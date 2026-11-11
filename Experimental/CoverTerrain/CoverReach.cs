using System.Numerics;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// snow terrain keeps the patches that join snow across cell edges, traced out from cells snowed all over; a stretch of cells joined nowhere keeps all
internal static class CoverReach
{
    private const float Overlap = 0.1f;

    private sealed record Patch(int Cell, List<(int Side, Vector2 A, Vector2 B)> Edges);

    private sealed record CoverCell(CGameCtnBlock Block, int Variant, int Mobil, Matrix4x4 ToLocal, (int X, int Z)[] Beyond);

    // cells snowed in part, their snow patches (edges in world space), patch ids per cell, and cells snowed all over
    private sealed class Found
    {
        public List<CoverCell> Cells { get; } = [];
        public List<Patch> Patches { get; } = [];
        public Dictionary<(int X, int Z), List<int>> ByCell { get; } = [];
        public HashSet<(int X, int Z)> Whole { get; } = [];
    }

    public static void Run(ConversionRun run)
    {
        if (run.Snowed.Count == 0) return;
        var found = Find(run);
        var joins = Joins(run, found);
        var root = Stretches(found.ByCell.Keys);
        var reached = joins.Keys.Select(p => found.Cells[found.Patches[p].Cell].Block.Coord).Select(c => root((c.X, c.Z))).ToHashSet();
        foreach (var (block, variant, mobil, toLocal, _) in found.Cells)
        {
            var at = (block.Coord.X, block.Coord.Z);
            var own = found.ByCell[at];
            var joined = own.Where(joins.ContainsKey).ToList();
            if (joined.Count == own.Count || !reached.Contains(root(at))) continue;
            var keep = joined.SelectMany(p => joins[p]).Select(w => Vector3.Transform(new Vector3(w.X, 0, w.Y), toLocal)).Select(l => new Vector2(l.X, l.Z)).ToList();
            if (run.Items.SnowTerrainFor(block.Name, variant, mobil, run.Map.Collection!.Value, run.Snow.UnderDeco.Contains(at), keep) is { } limited)
                run.Customs[block] = limited;
        }
    }

    private static Found Find(ConversionRun run)
    {
        var found = new Found();
        var cover = run.Items.Theme.Cover;
        foreach (var (block, variant, mobil) in run.Snowed)
        {
            var at = (block.Coord.X, block.Coord.Z);
            if (run.Shapes.Units(block.Name, true, variant) is not { } units
                || run.Items.Pieces(block.Name, true, variant, mobil, ItemKind.CoveredTerrain) is not { } pieces) continue;
            if (pieces.Where(p => p.Visible).All(p => p.Material == cover || p.Material.EndsWith("Water"))) { found.Whole.Add(at); continue; }
            var toWorld = Placement.Of(block, Placement.Footprint(units));
            Matrix4x4.Invert(toWorld, out var toLocal);
            Vector2 World(Vector2 v) => Vector3.Transform(new Vector3(v.X, 0, v.Y), toWorld) is var w ? new(w.X, w.Z) : default;
            var box = CoverPatches.BoxOf(pieces.Where(p => p.Visible && !p.Material.EndsWith("Water")));
            // the cell beyond each local side
            var beyond = new[] { new Vector2(box.X0 - 1, 16), new Vector2(box.X1 + 1, 16), new Vector2(16, box.Z0 - 1), new Vector2(16, box.Z1 + 1) }
                .Select(World).Select(w => ((int)MathF.Floor(w.X / 32), (int)MathF.Floor(w.Y / 32))).ToArray();
            var cell = found.Cells.Count;
            found.Cells.Add(new CoverCell(block, variant, mobil, toLocal, beyond));
            var tris = pieces.Where(p => p.Visible && p.Material == cover).SelectMany(CoverPatches.Triangles).ToList();
            var patchOf = CoverPatches.Of(tris);
            var own = new Dictionary<int, Patch>();
            for (int n = 0; n < tris.Count; n++)
            {
                if (!own.TryGetValue(patchOf[n], out var patch)) own[patchOf[n]] = patch = new Patch(cell, []);
                foreach (var e in CoverPatches.Edges(tris[n], box)) patch.Edges.Add((e.Side, World(e.A), World(e.B)));
            }
            var ids = found.ByCell[at] = [];
            foreach (var patch in own.Values.Where(p => p.Edges.Count > 0)) { ids.Add(found.Patches.Count); found.Patches.Add(patch); }
        }
        return found;
    }

    // patch -> the joining stretches' middles (world), traced out from the cells snowed all over
    private static Dictionary<int, List<Vector2>> Joins(ConversionRun run, Found found)
    {
        var (cells, patches) = (found.Cells, found.Patches);
        var snowed = run.Snowed.Select(s => (s.Block.Coord.X, s.Block.Coord.Z)).ToHashSet();
        bool Whole((int X, int Z) c) => found.Whole.Contains(c) || !snowed.Contains(c) && CoverRules.SnowsCell(run, c.X, c.Z);
        var joins = new Dictionary<int, List<Vector2>>();
        var queue = new Queue<int>();
        void Join(int patch, Vector2 at)
        {
            var first = !joins.ContainsKey(patch);
            (joins.TryGetValue(patch, out var list) ? list : joins[patch] = []).Add(at);
            if (first) queue.Enqueue(patch);
        }
        for (int i = 0; i < patches.Count; i++)
            foreach (var (side, a, b) in patches[i].Edges)
                if (Whole(cells[patches[i].Cell].Beyond[side])) Join(i, (a + b) / 2);
        while (queue.TryDequeue(out var i))
            foreach (var (side, a, b) in patches[i].Edges)
            {
                if (!found.ByCell.TryGetValue(cells[patches[i].Cell].Beyond[side], out var next)) continue;
                foreach (var j in next)
                {
                    if (joins.ContainsKey(j)) continue;
                    foreach (var (_, c, d) in patches[j].Edges)
                        if (Shared(a, b, c, d) is { } middle) { Join(j, middle); break; }
                }
            }
        return joins;
    }

    // neighbouring cells snowed in part as one stretch: joined nowhere, it keeps all its snow. Returns each cell's stretch
    private static Func<(int, int), (int, int)> Stretches(IEnumerable<(int X, int Z)> partCells)
    {
        var root = partCells.ToDictionary(c => ((int, int))c, c => ((int, int))c);
        (int, int) Root((int, int) c) { while (root[c] != c) c = root[c] = root[root[c]]; return c; }
        foreach (var (x, z) in root.Keys.ToList())
            foreach (var n in new[] { (x + 1, z), (x, z + 1) })
                if (root.ContainsKey(n)) root[Root(n)] = Root((x, z));
        return Root;
    }

    // two stretches of the same cell edge: the middle of what they share
    private static Vector2? Shared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var along = MathF.Abs(a.X - b.X) > MathF.Abs(a.Y - b.Y);
        float Along(Vector2 v) => along ? v.X : v.Y;
        float Across(Vector2 v) => along ? v.Y : v.X;
        if (MathF.Abs(Across(a) - Across(c)) > Overlap || MathF.Abs(Across(a) - Across(d)) > Overlap) return null;
        var (lo, hi) = (Math.Max(Math.Min(Along(a), Along(b)), Math.Min(Along(c), Along(d))), Math.Min(Math.Max(Along(a), Along(b)), Math.Max(Along(c), Along(d))));
        if (hi - lo < Overlap) return null;
        var m = (lo + hi) / 2;
        return along ? new Vector2(m, Across(a)) : new Vector2(Across(a), m);
    }
}
