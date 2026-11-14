using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// which clips the game shows: matching sides connect (no free clips), vertical clips need a pillar below,
// full free clips replace deletable ones
public sealed class ClipResolver(BlockCatalog shapes)
{
    private BlockCatalog Shapes => shapes;

    public sealed record Placed(string Name, bool Ground, int Variant, Matrix4x4 Place);

    public Placed Of(CGameCtnBlock block)
    {
        var variant = BlockFlags.VariantOf(block);
        var units = shapes.Units(block.Name, block.IsGround, variant) ?? [new Int3(0, 0, 0)];
        return new Placed(block.Name, block.IsGround, variant,
            Placement.Of(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)));
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

    public List<HashSet<int>> Resolve(IReadOnlyList<Placed> blocks) => new Pass(this, blocks).Shown();

    // one resolve: every clip side by cell and direction, then what each shows
    private sealed class Pass
    {
        private readonly ClipResolver resolver;
        private readonly IReadOnlyList<Placed> blocks;
        private readonly bool[] pillar;
        private readonly Dictionary<(Int3 Cell, Int3 Dir), List<SideAt>> sides = [];
        private readonly Dictionary<Int3, List<int>> occupants = [];
        private readonly Dictionary<(Int3, Int3, string), bool> columns = [];

        public Pass(ClipResolver resolver, IReadOnlyList<Placed> blocks)
        {
            (this.resolver, this.blocks) = (resolver, blocks);
            var shapes = resolver.Shapes;
            pillar = blocks.Select(b => shapes.IsPillarBlock(b.Name)).ToArray();
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
            for (int i = 0; i < blocks.Count; i++)
                foreach (var c in resolver.UnitCells(blocks[i]))
                    (occupants.TryGetValue(c, out var l) ? l : occupants[c] = []).Add(i);
        }

        public List<HashSet<int>> Shown()
        {
            var shown = blocks.Select(_ => new HashSet<int>()).ToList();
            foreach (var ((cell, dir), list) in sides)
            {
                var facingAt = sides.GetValueOrDefault((cell + dir, Neg(dir))) ?? [];
                foreach (var at in list)
                    foreach (var c in Picked(cell, dir, list, at, facingAt))
                        shown[at.Block].Add(ClipKey.Of(at.Side.Bit, c.Index, c.Vertical ? ColumnAt(cell, dir, c.VerticalGroup, blocks[at.Block].Ground) : 0));
            }
            return shown;
        }

        // the free clips this side shows
        private List<ClipInfo> Picked(Int3 cell, Int3 dir, List<SideAt> list, SideAt at, List<SideAt> facingAt)
        {
            var facing = facingAt.SelectMany(s => s.Side.Clips).ToList();
            var connected = at.Side.Clips.Any(a => !a.Anti && facingAt.Any(f => f.Side.Clips.Any(b => Compatible(a, b)
                && (dir.Y == 0 || Grouped(a, b) || Shaped(dir, list, at, a, f, b)))));
            bool Shows(ClipInfo c)
            {
                // anti-clips show where they connect
                if (c.Anti) return facing.Any(f => Compatible(c, f));
                // horizontal clips go by their own group: another group connecting the side (water edge) doesn't hide a ramp
                if (c.HorizontalGroup is not null) return !facing.Any(f => f.HorizontalGroup == c.HorizontalGroup || Compatible(c, f));
                // poles (measured on the baked clips, no pillar needed): not facing their own group, nor a full clip when deletable,
                // nor other poles on a connected side
                if (c.Vertical && c.VerticalGroup is not null)
                    return !facing.Any(f => f.Vertical && f.VerticalGroup == c.VerticalGroup)
                        && !(c.Deletable && facing.Any(f => f.Full))
                        && !(connected && facing.Any(f => f.Vertical));
                // a full clip on the facing side removes deletable ones too
                return !connected && !(dir.Y > 0 && PillarAbove(cell, at.Block)) && !(c.Deletable && facing.Any(f => f.Full));
            }
            var picked = at.Side.Clips.Where(c => c.Type.StartsWith("FreeClip")).Where(Shows).ToList();
            if (picked.Any(c => c.Full)) picked = picked.Where(c => c.Full || !c.Deletable).ToList();
            return picked;
        }

        // shaped tops/bottoms connect facing the same or the opposite way; when several in a cell match
        // the same clip, only those facing its way connect
        private bool Shaped(Int3 dir, List<SideAt> list, SideAt at, ClipInfo a, SideAt f, ClipInfo b)
        {
            var apart = Apart(at, a, f, b);
            return a.MultiDir == "AllDir" || apart == 0
                || apart == 2 && !(dir.Y > 0 && list.Any(o => o.Block != at.Block && o.Side.Clips.Any(c => Compatible(c, b) && Apart(o, c, f, b) == 0)));
        }

        private int Apart(SideAt x, ClipInfo a, SideAt y, ClipInfo b) => ((Dir(x, a) - Dir(y, b)) % 4 + 4) % 4;

        private int Dir(SideAt s, ClipInfo c)
        {
            var block = blocks[s.Block];
            return ClipTurns.DirOf(block.Place) + resolver.Shapes.ShapedClipTurn(block.Name, block.Ground, block.Variant, s.Side.Bit, c);
        }

        // stacked pillars: only the top one shows its top
        private bool PillarAbove(Int3 cell, int block)
            => occupants.TryGetValue(cell + Up, out var l) && l.Any(o => o != block && pillar[o] && blocks[o].Name == blocks[block].Name);

        private bool HasVertical((Int3, Int3) at, string group, out bool onPillar)
        {
            onPillar = false;
            if (!sides.TryGetValue(at, out var list)) return false;
            var matching = list.Where(s => s.Side.Clips.Any(c => c.Vertical && c.VerticalGroup == group)).ToList();
            onPillar = matching.Any(s => pillar[s.Block]);
            return matching.Count > 0;
        }

        // a column of vertical clips that reaches a pillar
        private bool Column(Int3 cell, Int3 dir, string group)
        {
            if (columns.TryGetValue((cell, dir, group), out var known)) return known;
            var found = false;
            foreach (var step in new[] { Down, Up })
                for (var c = cell; HasVertical((c, dir), group, out var onPillar) && !found; c += step)
                    found |= onPillar;
            return columns[(cell, dir, group)] = found;
        }

        private int ColumnAt(Int3 cell, Int3 dir, string? group, bool ground)
        {
            if (group is null) return 0;
            var top = !HasVertical((cell + Up, dir), group, out _);
            var bottom = !HasVertical((cell + Down, dir), group, out _);
            return (top ? ClipKey.ColumnTop : 0) | (bottom ? ClipKey.ColumnBottom : 0) | (bottom && ground ? ClipKey.ColumnOnGround : 0);
        }
    }

    private static bool Grouped(ClipInfo a, ClipInfo b)
        => a.Group is not null && b.Group is not null && (a.Group == b.Group || a.Group == b.SymmetricGroup || a.SymmetricGroup == b.Group);

    private static bool Compatible(ClipInfo a, ClipInfo b)
    {
        if ((a.Pair is not null && a.Pair == b.Name) || (b.Pair is not null && b.Pair == a.Name)) return true;
        if (a.Group is null || b.Group is null) return false;
        return a.Group == b.Group || a.Group == b.SymmetricGroup || a.SymmetricGroup == b.Group;
    }

    private static Int3 Cell(Vector3 point, Matrix4x4 place) => Placement.CellAt(Vector3.Transform(point, place));

    private static Int3 Neg(Int3 v) => new(-v.X, -v.Y, -v.Z);
}
