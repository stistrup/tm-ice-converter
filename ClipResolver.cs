using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// which clips the game shows: matching sides connect (no free clips), vertical clips need a pillar below,
// full free clips replace deletable ones
public sealed class ClipResolver(IBlockShapes shapes)
{
    public sealed record Placed(string Name, bool Ground, int Variant, Matrix4x4 Place);

    public Placed Of(CGameCtnBlock block)
    {
        var variant = Converter.VariantOf(block);
        var units = shapes.Units(block.Name, block.IsGround, variant) ?? [new Int3(0, 0, 0)];
        return new Placed(block.Name, block.IsGround, variant,
            Converter.Placement(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)));
    }

    // nothing under it: no-pillar-below variant
    public List<Placed> OfAll(IReadOnlyList<CGameCtnBlock> blocks)
    {
        var placed = blocks.Select(Of).ToList();
        var pillarCells = new HashSet<Int3>();
        for (int i = 0; i < blocks.Count; i++)
            if (shapes.IsPillarBlock(blocks[i].Name))
                foreach (var cell in UnitCells(placed[i])) pillarCells.Add(cell);
        // standing on a matching top clip counts as supported
        var tops = new Dictionary<Int3, List<ClipInfo>>();
        for (int i = 0; i < blocks.Count; i++)
            foreach (var side in shapes.ClipSides(placed[i].Name, placed[i].Ground, placed[i].Variant).Where(s => s.Bit % 6 == 5))
            {
                var above = Cell(side.Probe, placed[i].Place);
                (tops.TryGetValue(above, out var l) ? l : tops[above] = []).AddRange(side.Clips);
            }
        for (int i = 0; i < blocks.Count; i++)
        {
            if (shapes.NoPillarBelowVariant(placed[i].Name, placed[i].Ground, placed[i].Variant) is not { } npb) continue;
            var cells = UnitCells(placed[i]).ToList();
            var bottom = cells.Min(c => c.Y);
            var bottoms = shapes.ClipSides(placed[i].Name, placed[i].Ground, placed[i].Variant)
                .Where(s => s.Bit % 6 == 4).SelectMany(s => s.Clips).ToList();
            bool Supported(Int3 c) => pillarCells.Contains(c + Down)
                || tops.TryGetValue(c, out var offered) && bottoms.Any(a => offered.Any(t => Compatible(a, t)));
            if (!cells.Where(c => c.Y == bottom).Any(Supported))
                placed[i] = placed[i] with { Variant = npb };
        }
        return placed;
    }

    internal IEnumerable<Int3> UnitCells(Placed block)
        => (shapes.Units(block.Name, block.Ground, block.Variant) ?? [new Int3(0, 0, 0)])
            .Select(u => Cell(new Vector3(u.X * 32 + 16, u.Y * 8 + 4, u.Z * 32 + 16), block.Place));

    private sealed record SideAt(int Block, ClipSide Side);

    private static readonly Int3 Down = new(0, -1, 0), Up = new(0, 1, 0);

    public List<HashSet<int>> Resolve(IReadOnlyList<Placed> blocks)
    {
        var sides = new Dictionary<(Int3 Cell, Int3 Dir), List<SideAt>>();
        var pillar = blocks.Select(b => shapes.IsPillarBlock(b.Name)).ToArray();
        var terrain = blocks.Select(b => shapes.IsTerrain(b.Name)).ToArray();
        for (int i = 0; i < blocks.Count; i++)
        {
            foreach (var side in shapes.ClipSides(blocks[i].Name, blocks[i].Ground, blocks[i].Variant))
            {
                var cell = Cell(side.Center, blocks[i].Place);
                var key = (cell, Cell(side.Probe, blocks[i].Place) - cell);
                if (!sides.TryGetValue(key, out var list)) sides[key] = list = [];
                list.Add(new SideAt(i, side));
            }
        }

        var columns = new Dictionary<(Int3, Int3, string), bool>();
        bool HasVertical((Int3, Int3) at, string group, out bool onPillar)
        {
            onPillar = false;
            if (!sides.TryGetValue(at, out var list)) return false;
            var matching = list.Where(s => s.Side.Clips.Any(c => c.Vertical && c.VerticalGroup == group)).ToList();
            onPillar = matching.Any(s => pillar[s.Block]);
            return matching.Count > 0;
        }
        bool Column(Int3 cell, Int3 dir, string group)
        {
            if (columns.TryGetValue((cell, dir, group), out var known)) return known;
            var found = false;
            foreach (var step in new[] { Down, Up })
                for (var c = cell; HasVertical((c, dir), group, out var onPillar) && !found; c += step)
                    found |= onPillar;
            return columns[(cell, dir, group)] = found;
        }

        int ColumnAt(Int3 cell, Int3 dir, string? group, bool ground)
        {
            if (group is null) return 0;
            var top = !HasVertical((cell + Up, dir), group, out _);
            var bottom = !HasVertical((cell + Down, dir), group, out _);
            return (top ? MeshExport.ColumnTop : 0) | (bottom ? MeshExport.ColumnBottom : 0) | (bottom && ground ? MeshExport.ColumnOnGround : 0);
        }

        // top hidden if another block shares the cell
        var occupants = new Dictionary<Int3, List<int>>();
        for (int i = 0; i < blocks.Count; i++)
            foreach (var c in UnitCells(blocks[i]))
                (occupants.TryGetValue(c, out var l) ? l : occupants[c] = []).Add(i);
        // unless it has its own top there
        var topsAt = sides.Where(kv => kv.Key.Dir == Up).SelectMany(kv => kv.Value.Where(s => s.Side.Clips.Count > 0).Select(s => (kv.Key.Cell, s.Block))).ToHashSet();
        bool Covered(Int3 cell, int block)
            => occupants.TryGetValue(cell, out var l) && l.Any(o => o != block && !pillar[o] && !terrain[o] && !topsAt.Contains((cell, o)));
        // stacked pillars: only the top one shows its top
        bool PillarAbove(Int3 cell, int block)
            => occupants.TryGetValue(cell + Up, out var l) && l.Any(o => pillar[o] && blocks[o].Name == blocks[block].Name);

        var shown = blocks.Select(_ => new HashSet<int>()).ToList();
        foreach (var ((cell, dir), list) in sides)
        {
            var facingAt = sides.GetValueOrDefault((cell + dir, Neg(dir))) ?? [];
            var facing = facingAt.SelectMany(s => s.Side.Clips).ToList();
            var sharedPoles = dir.X < 0 || dir.Z < 0;
            foreach (var at in list)
            {
                // anti-clips show where they connect
                // shaped tops/bottoms only connect when turned the same way
                var connected = at.Side.Clips.Any(a => !a.Anti && facingAt.Any(f => f.Side.Clips.Any(b => Compatible(a, b)
                    && (dir.Y == 0 || Grouped(a, b) || SameTurn(blocks[at.Block].Place, blocks[f.Block].Place)))));
                var picked = at.Side.Clips
                    .Where(c => c.Type.StartsWith("FreeClip"))
                    .Where(c => c.Anti ? facing.Any(f => Compatible(c, f))
                        : c.Vertical && c.VerticalGroup is not null
                        ? Column(cell, dir, c.VerticalGroup)
                            && !(sharedPoles && facing.Any(f => f.Vertical && f.VerticalGroup == c.VerticalGroup) && Column(cell + dir, Neg(dir), c.VerticalGroup))
                        : !connected && !(dir.Y > 0 && (Covered(cell, at.Block) || PillarAbove(cell, at.Block))))
                    .ToList();
                if (picked.Any(c => c.Full)) picked = picked.Where(c => c.Full || !c.Deletable).ToList();
                foreach (var c in picked)
                    shown[at.Block].Add(MeshExport.ClipKey(at.Side.Bit, c.Index, c.Vertical ? ColumnAt(cell, dir, c.VerticalGroup, blocks[at.Block].Ground) : 0));
            }
        }
        return shown;
    }

    private static bool Grouped(ClipInfo a, ClipInfo b)
        => a.Group is not null && b.Group is not null && (a.Group == b.Group || a.Group == b.SymmetricGroup || a.SymmetricGroup == b.Group);

    private static bool SameTurn(Matrix4x4 a, Matrix4x4 b)
        => Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < 0.01f;

    private static bool Compatible(ClipInfo a, ClipInfo b)
    {
        if ((a.Pair is not null && a.Pair == b.Name) || (b.Pair is not null && b.Pair == a.Name)) return true;
        if (a.Group is null || b.Group is null) return false;
        return a.Group == b.Group || a.Group == b.SymmetricGroup || a.SymmetricGroup == b.Group;
    }

    private static Int3 Cell(Vector3 point, Matrix4x4 place) => Converter.CellAt(Vector3.Transform(point, place));

    private static Int3 Neg(Int3 v) => new(-v.X, -v.Y, -v.Z);
}
