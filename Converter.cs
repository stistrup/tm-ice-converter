using System.Numerics;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

public enum Source { Table, Rule }

public sealed record IceItem(Ident Ident, string FilePath, (int X, int Z) Size)
{
    public static readonly IceItem Nothing = new(new Ident("", new Id(26), ""), "", (0, 0));
}

public interface IBlockShapes
{
    string? Environment { get; set; }
    IReadOnlyList<Int3>? Units(string blockName, bool ground, int variantIndex);
    IReadOnlyList<ClipSide> ClipSides(string blockName, bool ground, int variantIndex);
    bool IsPillarBlock(string blockName);
    bool IsTerrain(string blockName);
    // null = unknown
    bool? EnvironmentHas(string blockName);
    int? NoPillarBelowVariant(string blockName, bool ground, int variantIndex);
    // null = not extracted
    IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)>? CollisionTriangles(string blockName, bool ground, int variantIndex, int mobil);
    // null = not extracted
    IReadOnlySet<string>? Materials(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips);
}

public sealed class ConversionReport
{
    public Dictionary<(string From, string To, Source Source), int> Swapped { get; } = [];
    public Dictionary<string, int> Kept { get; } = [];
    public Dictionary<string, int> Unmatched { get; } = [];
    public Dictionary<string, int> ReplacedByItem { get; } = [];
    public Dictionary<string, int> Skinned { get; } = [];
    public Dictionary<string, int> SpecialKept { get; } = [];
    public Dictionary<string, int> SpecialSkinned { get; } = [];
    public Dictionary<string, int> KeptByList { get; } = [];
    public Dictionary<string, int> RemovedByList { get; } = [];
    public Dictionary<string, int> Recoloured { get; } = [];
    public Dictionary<string, int> Sunk { get; } = [];
    public Dictionary<string, int> Gated { get; } = [];
    public HashSet<IceItem> UsedItems { get; } = [];
    public Dictionary<string, int> KeptByItem { get; } = [];
    public Dictionary<string, int> Concrete { get; } = [];
    public Dictionary<string, int> Walls { get; } = [];
    public Dictionary<string, int> TopsIced { get; } = [];
    public List<string> CameraClipsRemoved { get; } = [];
    public Dictionary<(string From, string To), int> EngineOff { get; } = []; // "" = removed
    public Dictionary<(string From, string To), int> SwappedItems { get; } = [];
    public Dictionary<string, int> IcedItems { get; } = [];
    public Dictionary<string, int> ItemsNotIced { get; } = [];
    public int FreeBlockOffsetsSkipped { get; set; }

    public int Left => Unmatched.Values.Sum() + SpecialKept.Values.Sum();
}

public sealed partial class Converter(HashSet<string> knownBlocks, Translations translations, IBlockShapes shapes, IItemSource items)
{
    [GeneratedRegex("Tech(?!nics)|Dirt|Grass|Plastic|Bump")]
    private static partial Regex Surface();

    // effect blocks; longest names first, they name the Modifier folder
    [GeneratedRegex("TurboRoulette|Turbo2|Turbo|Boost2|Boost|NoEngine|NoSteering|NoBrake|SlowMotion|Fragile|Reset|Cruise|Reactor")]
    private static partial Regex NoItem();

    internal static bool IsGameplayBlock(string name) => NoItem().IsMatch(name);

    internal static string? Effect(string name) => NoItem().Match(name) is { Success: true } m && m.Value != "Reactor" ? m.Value : null;

    private static readonly string[] IcePrefixes = ["DecoPlatform", "DecoHill", "DecoCliff10", "DecoCliff8", "DecoCliff"];

    public ConversionReport Convert(CGameCtnChallenge map)
    {
        var report = new ConversionReport();
        var collection = map.Collection?.ToString() ?? "Stadium2020";
        var rules = translations.For(map.MapName);
        keepList = rules.Keep;
        topped.Clear();
        keepItems = rules;
        shapes.Environment = items.Environment = collection;
        items.SnowOnZoneSides = rules.Options.SnowOnZoneSides;
        items.SnowOnGroundDeco = rules.Options.SnowOnGroundDeco;
        var resolver = new ClipResolver(shapes);
        // clips as in the original
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var placed = resolver.OfAll(grid);
        var target = resolver.Resolve(placed);
        var clipsOf = new Dictionary<CGameCtnBlock, HashSet<int>>(ReferenceEqualityComparer.Instance);
        var variantOf = new Dictionary<CGameCtnBlock, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < grid.Count; i++) (clipsOf[grid[i]], variantOf[grid[i]]) = (target[i], placed[i].Variant);

        // terrain height per column
        var cellsOf = new Dictionary<CGameCtnBlock, List<Int3>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < grid.Count; i++) cellsOf[grid[i]] = resolver.UnitCells(placed[i]).ToList();
        var terrainTop = grid.Where(b => b.IsGround).SelectMany(b => cellsOf[b]).GroupBy(c => (c.X, c.Z)).ToDictionary(g => g.Key, g => g.Max(c => c.Y));
        var replaced = new HashSet<CGameCtnBlock>(ReferenceEqualityComparer.Instance);
        var renamed = new HashSet<CGameCtnBlock>(ReferenceEqualityComparer.Instance);
        var skinned = new HashSet<CGameCtnBlock>(ReferenceEqualityComparer.Instance);
        var engineOffGates = new List<CGameCtnBlock>();
        foreach (var block in map.GetBlocks())
        {
            var name = block.Name;
            // engine-off -> reset or plain block; engine-off gates go
            if ((rules.Options.EngineOffToReset || rules.Options.EngineOffToPlain) && name.Contains("NoEngine"))
            {
                if (name.StartsWith("Gate")) { engineOffGates.Add(block); Bump(report.EngineOff, (name, "")); continue; }
                var stripped = name.Replace("SpecialNoEngine", "");
                if (rules.Options.EngineOffToPlain && new[] { stripped, stripped + "Base", stripped + "Straight" }.FirstOrDefault(knownBlocks.Contains) is { } plain)
                {
                    Bump(report.EngineOff, (name, plain));
                    block.Name = name = plain;
                }
                else if (name.Replace("NoEngine", "Reset") is var reset && knownBlocks.Contains(reset))
                {
                    Bump(report.EngineOff, (name, reset));
                    block.Name = name = reset;
                }
            }
            if (IsAlreadyIceOrCustom(name))
            {
                // flipped ice shows concrete: ice top
                if (!name.Contains('\\'))
                    IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report, Cover.TopOfIce);
                continue;
            }
            if (IsOnKeepList(block)) { Bump(report.KeptByList, name); continue; }
            if (shapes.IsPillarBlock(name))
            {
                IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report);
                continue;
            }
            if (shapes.IsTerrain(name)) continue;

            if (rules.Blocks.TryGetValue(name, out var t))
            {
                if (t.To is null) { Bump(report.Kept, name); continue; }
                Apply(block, t, report);
                renamed.Add(block);
                Bump(report.Swapped, (name, t.To, Source.Table));
                continue;
            }

            if (rules.IgnorePrefixes.Any(name.StartsWith)) continue;

            // air open roads off Stadium become items
            var auto = !block.IsGround && name.StartsWith("Open") && !collection.StartsWith("Stadium") ? null
                : Candidates(name).FirstOrDefault(c => knownBlocks.Contains(c) && translations.IsAvailable(c, collection) && shapes.EnvironmentHas(c) != false);
            if (auto is not null)
            {
                block.Name = auto;
                renamed.Add(block);
                Bump(report.Swapped, (name, auto, Source.Rule));
                IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report, Cover.TopOfIce);
                continue;
            }

            // white deco = concrete, keep
            if (IsConcrete(block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block]))
            { Bump(report.Concrete, name); IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report); continue; }
            if (!WantsItem(name))
            {
                IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report);
                continue;
            }
            // keep for the effect, ice around the pad
            if (rules.Options.EffectsToGate && GateEffect(name) is { } effect && !block.IsFree && GateOn(map, block, effect, variantOf[block], clipsOf[block], report))
            {
                replaced.Add(block);
                continue;
            }
            if (IsWallOnly(block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block]))
            { Bump(report.Walls, name); IceTops(map, block, block.IsFree ? VariantOf(block) : variantOf[block], block.IsFree ? FreeClips(block) : clipsOf[block], report); continue; }
            // ground blocks and deco set into the terrain stay native under a skin
            var inTerrain = !block.IsGround && !block.IsFree && name.StartsWith("Deco")
                && cellsOf[block].Any(c => terrainTop.TryGetValue((c.X, c.Z), out var groundY) && c.Y <= groundY);
            // only a block keeps the ground away; grass grows through a skin on deco, not on ice
            if ((block.IsGround || inTerrain) && ItemBuilder.IsWholeOnGround(name, collection) && rules.Options.SnowOnGroundDeco
                && "PlatformIce" + name["DecoPlatform".Length..] is var twin && knownBlocks.Contains(twin) && translations.IsAvailable(twin, collection) && shapes.EnvironmentHas(twin) != false
                && PlaceItem(map, block, variantOf[block], clipsOf[block], report, Cover.OverTwin))
            {
                block.Name = twin;
                renamed.Add(block);
                Bump(report.Swapped, (name, twin, Source.Rule));
                continue;
            }
            if ((block.IsGround || inTerrain) && !ItemBuilder.IsWholeOnGround(name, collection))
            {
                if (PlaceItem(map, block, variantOf[block], clipsOf[block], report, inTerrain ? Cover.InTerrain : Cover.None)) { skinned.Add(block); Bump(report.Skinned, name); }
                else Bump(report.Unmatched, name);
                continue;
            }
            var ok = block.IsFree
                ? PlaceItem(map, block, VariantOf(block), FreeClips(block), report)
                : PlaceItem(map, block, variantOf[block], clipsOf[block], report, inTerrain ? Cover.InTerrain : Cover.None);
            if (!ok) { Bump(report.Unmatched, name); continue; }
            replaced.Add(block);
            Bump(report.ReplacedByItem, name);
        }
        KeepNeighbours(map, grid, placed, target, replaced, renamed, skinned, report);
        var snow = new SnowSurfaces(map, replaced, renamed, skinned, shapes, rules.Options.SnowOnZoneSides);
        foreach (var block in replaced.Concat(engineOffGates)) map.RemoveBlock(block);
        if (rules.Options.EngineOffToReset || rules.Options.EngineOffToPlain)
            foreach (var gate in map.GetAnchoredObjects().Where(o => o.ItemModel?.Id is { } id && id.StartsWith("Gate") && id.Contains("NoEngine")).ToList())
            {
                map.RemoveAnchoredObject(gate);
                Bump(report.EngineOff, (gate.ItemModel!.Id, ""));
            }
        SwapItems(map, snow, report);
        MatchPlatformColour(map, report);
        if (rules.Options.RemoveForcedCameras) RemoveForcedCameras(map, report);
        return report;
    }

    private static void RemoveForcedCameras(CGameCtnChallenge map, ConversionReport report)
    {
        if (map.ClipGroupInGame?.Clips is not { } clips) return;
        foreach (var c in clips) c.Clip.Tracks.RemoveAll(t => t.Blocks.Any(b => b is CGameCtnMediaBlockCameraGame));
        foreach (var c in clips.Where(c => c.Clip.Tracks.Count == 0)) report.CameraClipsRemoved.Add(c.Clip.Name ?? "(unnamed)");
        clips.RemoveAll(c => c.Clip.Tracks.Count == 0);
    }

    private void SwapItems(CGameCtnChallenge map, SnowSurfaces snow, ConversionReport report)
    {
        var icedAt = new Dictionary<(float, float, float), int>();
        var removed = new List<CGameCtnAnchoredObject>();
        foreach (var item in map.GetAnchoredObjects())
        {
            if (item.ItemModel is not { Author: "Nadeo" } model) continue;
            if (keepItems.RemovesItem(model.Id, new Vector3(item.AbsolutePositionInMap.X, item.AbsolutePositionInMap.Y, item.AbsolutePositionInMap.Z)))
            { removed.Add(item); Bump(report.RemovedByList, model.Id); continue; }
            if (keepItems.KeepsItem(model.Id)) { Bump(report.KeptByList, model.Id); continue; }
            var p = item.AbsolutePositionInMap;
            // "<Env>:<Item>" beats the bare id
            bool Find(Dictionary<string, string> table, out string to)
                => table.TryGetValue($"{map.Collection}:{model.Id}", out to!) || table.TryGetValue(model.Id, out to!);
            if (Find(translations.Items, out var to)
                || Find(translations.ItemsOnSnow, out to) && snow.Touches(new Vector3(p.X, p.Y, p.Z), TreeHeight(to)))
            {
                item.ItemModel = model with { Id = to, Collection = new Id(26) }; // Stadium2020
                Bump(report.SwappedItems, (model.Id, to));
            }
            // stacked copies go up 1 cm each
            else if (items.ForItem(model.Id) is { } ice)
            {
                var spot = (MathF.Round(p.X, 2), MathF.Round(p.Y, 2), MathF.Round(p.Z, 2));
                var stacked = icedAt.GetValueOrDefault(spot);
                icedAt[spot] = stacked + 1;
                if (stacked > 0) item.AbsolutePositionInMap = p with { Y = p.Y + 0.01f * stacked };
                item.ItemModel = ice.Ident;
                report.UsedItems.Add(ice);
                Bump(report.IcedItems, model.Id);
            }
            else if (items.Failures.ContainsKey(model.Id)) Bump(report.ItemsNotIced, model.Id);
            else if (snow.Under(new Vector3(p.X, p.Y, p.Z)) && FlushWithIce(item, snow))
            {
                item.AbsolutePositionInMap = p with { Y = p.Y - FlushSink };
                Bump(report.Sunk, model.Id);
            }
        }
        foreach (var item in removed) map.AnchoredObjects!.Remove(item);
    }

    // converted surfaces, to tell if an item is on ice
    private sealed class SnowSurfaces
    {
        private const float Reach = 6;
        private const float ReachBelow = 8;
        private readonly Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C)>> columns = [];

        public SnowSurfaces(CGameCtnChallenge map, IEnumerable<CGameCtnBlock> items, IReadOnlySet<CGameCtnBlock> swapped,
            IReadOnlySet<CGameCtnBlock> skinned, IBlockShapes shapes, bool zoneSides)
        {
            var drop = EnvironmentDrop(map.Collection?.ToString());
            foreach (var block in items.Concat(swapped).Concat(skinned))
            {
                if (block.IsPillar || shapes.IsPillarBlock(block.Name)
                    || shapes.CollisionTriangles(block.Name, block.IsGround, VariantOf(block), MobilOf(block)) is not { } tris) continue;
                Matrix4x4 place;
                if (block.IsFree)
                {
                    var (a, r) = (block.AbsolutePositionInMap ?? default, block.YawPitchRoll ?? default);
                    place = Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(a.X, a.Y, a.Z);
                }
                else
                {
                    var units = shapes.Units(block.Name, block.IsGround, VariantOf(block)) ?? [new Int3(0, 0, 0)];
                    place = Placement(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)) * Matrix4x4.CreateTranslation(0, -drop, 0);
                }
                // swapped road's Deco is still env ground
                foreach (var (a, b, c, material) in tris)
                {
                    if (material.StartsWith("Deco") && !block.Name.StartsWith("Deco") && swapped.Contains(block)) continue;
                    if (skinned.Contains(block) && !ItemBuilder.SkinCovers(block.Name, material, zoneSides)) continue;
                    var t = (A: Vector3.Transform(a, place), B: Vector3.Transform(b, place), C: Vector3.Transform(c, place));
                    if (Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A)) is { } n && MathF.Abs(n.Y) < 0.3f) continue;
                    var (x0, x1) = ((int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) / 32), (int)MathF.Floor(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) / 32));
                    var (z0, z1) = ((int)MathF.Floor(MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)) / 32), (int)MathF.Floor(MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)) / 32));
                    for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                        (columns.TryGetValue((x, z), out var list) ? list : columns[(x, z)] = []).Add(t);
                }
            }
        }

        // ice anywhere in the tree's column?
        public bool Touches(Vector3 root, float height) => Heights(root).Any(h => h >= root.Y - ReachBelow && h <= root.Y + height);

        public bool Flush(Vector3 p) => Heights(p).Any(h => MathF.Abs(h - p.Y) <= 0.02f);

        private IEnumerable<float> Heights(Vector3 p)
        {
            if (!columns.TryGetValue(((int)MathF.Floor(p.X / 32), (int)MathF.Floor(p.Z / 32)), out var tris)) yield break;
            foreach (var (a, b, c) in tris)
            {
                var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                if (MathF.Abs(d) < 1e-6f) continue;
                var u = ((p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z)) / d;
                var v = ((b.X - a.X) * (p.Z - a.Z) - (p.X - a.X) * (b.Z - a.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                yield return a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            }
        }

        public bool Under(Vector3 p)
        {
            if (!columns.TryGetValue(((int)MathF.Floor(p.X / 32), (int)MathF.Floor(p.Z / 32)), out var tris)) return false;
            foreach (var (a, b, c) in tris)
            {
                var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                if (MathF.Abs(d) < 1e-6f) continue;
                var u = ((p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z)) / d;
                var v = ((b.X - a.X) * (p.Z - a.Z) - (p.X - a.X) * (b.Z - a.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                var below = p.Y - (a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y));
                if (below <= ReachBelow && below >= -Reach) return true;
            }
            return false;
        }
    }

    // neighbours whose clips would change become items too
    private IReadOnlySet<string> keepList = new HashSet<string>();
    // effectsToGate
    private static string? GateEffect(string name) => name.Contains("SlowMotion") ? "SlowMotion" : name.Contains("Reset") ? "Reset" : null;

    private bool GateOn(CGameCtnChallenge map, CGameCtnBlock block, string effect, int variant, IReadOnlySet<int> clips, ConversionReport report)
    {
        // flat road only
        var middle = new Vector2(16, 16);
        float? surface = null;
        foreach (var (a, b, c, _) in shapes.CollisionTriangles(block.Name, block.IsGround, variant, MobilOf(block)) ?? [])
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (MathF.Abs(n.Y) < 0.99f) continue;
            var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (MathF.Abs(d) < 1e-6f) continue;
            var u = ((middle.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (middle.Y - a.Z)) / d;
            var v = ((b.X - a.X) * (middle.Y - a.Z) - (middle.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var h = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            surface = surface is { } s ? MathF.Max(s, h) : h;
        }
        if (surface is not { } y) return false;
        if (!PlaceItem(map, block, variant, clips, report, Cover.Gated)) return false;
        var units = shapes.Units(block.Name, block.IsGround, variant) ?? [new Int3(0, 0, 0)];
        var at = Vector3.Transform(new Vector3(middle.X, y, middle.Y), Placement(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)))
            - new Vector3(0, EnvironmentDrop(map.Collection?.ToString()), 0);
        map.PlaceAnchoredObject(new Ident("GateSpecial32m" + effect, new Id(26), "Nadeo"), new Vec3(at.X, at.Y, at.Z), new Vec3(-(int)block.Direction * MathF.PI / 2, 0, 0), default);
        Bump(report.Gated, block.Name);
        return true;
    }

    private static float TreeHeight(string frozen) => frozen switch
    {
        "FrozenTreeVerySmall" => 3,
        "FrozenTreeSmall" => 6,
        "FrozenTreeMedium" => 10,
        "FrozenTreeBig" => 15,
        "FrozenTreeTall" => 20,
        "WinterFrozenTree" => 12,
        _ => 6,
    };

    // flat top flush with ice flickers: sink it
    private const float FlushSink = 0.02f;
    private readonly Dictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> flatTops = [];

    private bool FlushWithIce(CGameCtnAnchoredObject item, SnowSurfaces snow)
    {
        var id = item.ItemModel.Id;
        var key = $"{shapes.Environment}:{id}";
        if (!flatTops.TryGetValue(key, out var tops))
        {
            tops = [];
            if (shapes is MeshExport mesh && mesh.ItemFile(id) is { } file)
                try
                {
                    foreach (var piece in mesh.ExportItem(file, out _)?.Where(p => p.Visible) ?? [])
                        for (int t = 0; t + 2 < piece.Indices.Length; t += 3)
                        {
                            Vector3 V(int i) => new(piece.Positions[3 * i], piece.Positions[3 * i + 1], piece.Positions[3 * i + 2]);
                            var (a, b, c) = (V(piece.Indices[t]), V(piece.Indices[t + 1]), V(piece.Indices[t + 2]));
                            var n = Vector3.Cross(b - a, c - a);
                            if (n.Length() > 0.01f && n.Y / n.Length() > 0.99f) tops.Add((a, b, c));
                        }
                }
                catch (Exception) { }
            flatTops[key] = tops;
        }
        if (tops.Count == 0) return false;
        var (p, r, pivot) = (item.AbsolutePositionInMap, item.YawPitchRoll, item.PivotPosition);
        var place = Matrix4x4.CreateTranslation(-pivot.X, -pivot.Y, -pivot.Z) * Matrix4x4.CreateScale(item.Scale is > 0 and var sc ? sc : 1)
            * Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
        var area = 0f;
        foreach (var (a, b, c) in tops)
        {
            var (wa, wb, wc) = (Vector3.Transform(a, place), Vector3.Transform(b, place), Vector3.Transform(c, place));
            if (snow.Flush((wa + wb + wc) / 3)) area += Vector3.Cross(wb - wa, wc - wa).Length() / 2;
        }
        return area >= 1;
    }

    private Translations keepItems = new();
    private readonly HashSet<CGameCtnBlock> topped = new(ReferenceEqualityComparer.Instance);

    private bool IsOnKeepList(CGameCtnBlock block)
        => !block.IsFree && keepList.Contains(Translations.KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z));

    private void KeepNeighbours(CGameCtnChallenge map, List<CGameCtnBlock> grid, List<ClipResolver.Placed> placed,
        List<HashSet<int>> target, HashSet<CGameCtnBlock> replaced, HashSet<CGameCtnBlock> renamed, HashSet<CGameCtnBlock> skinned, ConversionReport report)
    {
        var resolver = new ClipResolver(shapes);
        for (int round = 0; round < 50; round++)
        {
            var remaining = grid.Select((b, i) => (Block: b, Index: i)).Where(x => !replaced.Contains(x.Block)).ToList();
            var now = resolver.Resolve(remaining.Select(x => resolver.Of(x.Block) is var p && p.Name == placed[x.Index].Name
                ? p with { Variant = placed[x.Index].Variant } : p).ToList());
            var added = 0;
            for (int k = 0; k < remaining.Count; k++)
            {
                var (block, i) = remaining[k];
                if (OnlyTopsAppear(now[k], target[i]) && !(IsWallOnly(block, placed[i].Variant, target[i]) && AdapterAbove(block, replaced))
                    // stands: an end cap is fine
                    || block.Name.StartsWith("Stand") || renamed.Contains(block) || skinned.Contains(block) || block.IsGround || shapes.IsPillarBlock(block.Name) || IsOnKeepList(block) || IsConcrete(block, placed[i].Variant, target[i])
                    || IsWallOnly(block, placed[i].Variant, target[i]) && !(TopAppears(now[k], target[i]) && AdapterAbove(block, replaced))) continue;
                if (!PlaceItem(map, block, placed[i].Variant, target[i], report)) continue;
                replaced.Add(block);
                Bump(report.KeptByItem, block.Name);
                added++;
            }
            if (added == 0) break;
        }
        var final = grid.Select((b, i) => (Block: b, Index: i)).Where(x => !replaced.Contains(x.Block)).ToList();
        TopsNowShown(map, placed, target, final, resolver.Resolve(final.Select(x => resolver.Of(x.Block) is var p && p.Name == placed[x.Index].Name
            ? p with { Variant = placed[x.Index].Variant } : p).ToList()), replaced, skinned, renamed, report);
    }

    // lone Default-colour platform gets the others' colour
    private static void MatchPlatformColour(CGameCtnChallenge map, ConversionReport report)
    {
        var platforms = map.GetBlocks().Where(b => b.Name.StartsWith("Platform")).ToList();
        var colours = platforms.Where(b => b.Color != DifficultyColor.Default).Select(b => b.Color).Distinct().ToList();
        var strays = platforms.Where(b => b.Color == DifficultyColor.Default).ToList();
        if (colours is not [var colour] || strays.Count == 0 || strays.Count * 2 >= platforms.Count) return;
        foreach (var b in strays) { b.Color = colour; Bump(report.Recoloured, b.Name); }
    }

    // tops that only show after conversion
    private void TopsNowShown(CGameCtnChallenge map, List<ClipResolver.Placed> placed, List<HashSet<int>> target,
        List<(CGameCtnBlock Block, int Index)> remaining, List<HashSet<int>> now, HashSet<CGameCtnBlock> replaced,
        HashSet<CGameCtnBlock> skinned, HashSet<CGameCtnBlock> renamed, ConversionReport report)
    {
        for (int k = 0; k < remaining.Count; k++)
        {
            var (block, i) = remaining[k];
            if (topped.Contains(block) || replaced.Contains(block) || skinned.Contains(block) || !TopAppears(now[k], target[i])
                || shapes.IsTerrain(block.Name) || IsOnKeepList(block)) continue;
            IceTops(map, block, placed[i].Variant, now[k], report, renamed.Contains(block) ? Cover.TopOfIce : Cover.Top);
        }
    }

    // new tops get ice from TopsNowShown
    private static bool OnlyTopsAppear(IReadOnlySet<int> now, IReadOnlySet<int> original)
        => original.IsSubsetOf(now) && now.Except(original).All(key => (key & 0xFFFF) / 32 % 6 == 5);

    private static bool TopAppears(IReadOnlySet<int> now, IReadOnlySet<int> original)
        => now.Except(original).Any(key => (key & 0xFFFF) / 32 % 6 == 5);

    private bool AdapterAbove(CGameCtnBlock block, IReadOnlySet<CGameCtnBlock> replaced)
        => !block.IsFree && replaced.Any(r => !r.IsFree && r.Coord == block.Coord + new Int3(0, 1, 0)
            && shapes.ClipSides(r.Name, r.IsGround, VariantOf(r)).Any(s => s.Bit % 6 == 4 && s.Clips.Any(c => c.Anti)));

    // item origin is the block corner; turning moves the footprint, shift it back
    internal static Matrix4x4 Placement(CGameCtnBlock block, (int X, int Z) size)
    {
        var corner = new Vector3(block.Coord.X * 32, (block.Coord.Y - 8) * 8, block.Coord.Z * 32);
        var turn = Matrix4x4.CreateRotationY(-(int)block.Direction * MathF.PI / 2);
        var (w, l) = (size.X * 32, size.Z * 32);
        var footprint = new[] { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(0, 0, l), new Vector3(w, 0, l) }
            .Select(c => Vector3.Transform(c, turn)).ToArray();
        return turn * Matrix4x4.CreateTranslation(corner - new Vector3(MathF.Round(footprint.Min(c => c.X)), 0, MathF.Round(footprint.Min(c => c.Z))));
    }

    // how much lower grid Y sits than on Stadium
    internal static float EnvironmentDrop(string? collection)
    {
        collection ??= "Stadium2020";
        lock (Drops)
        {
            if (Drops.TryGetValue(collection, out var known)) return known;
            var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
            var dir = Path.Combine(gameData, collection == "Stadium2020" ? "Stadium" : collection);
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.DecorationSize.Gbx", SearchOption.AllDirectories).FirstOrDefault() : null;
            var drop = 56f;
            if (file is not null && Gbx.ParseNode(file) is CGameCtnDecorationSize size)
                drop = 8 * ((size.OffsetBlockY ? size.BaseHeightBase + 1 : size.BaseHeightOffset) - 8);
            else Console.WriteLine($"  warning: no decoration size extracted for {collection}; assuming items sit as on RedIsland");
            return Drops[collection] = drop;
        }
    }

    private static readonly Dictionary<string, float> Drops = [];

    // ground blocks keep it in flag bits 21-26
    // block flags: bits 0-5 mobil row (GBX.NET Variant), 6-11 mobil entry (SubVariant), 21-26 block info variant
    internal static int VariantOf(CGameCtnBlock block) => (block.Flags >> 21) & 0x3F;

    internal static int MobilOf(CGameCtnBlock block) => block.Flags & 0xFFF;

    internal static Int3 CellAt(Vector3 world) => new((int)MathF.Floor(world.X / 32), (int)MathF.Floor(world.Y / 8) + 8, (int)MathF.Floor(world.Z / 32));

    private bool PlaceItem(CGameCtnChallenge map, CGameCtnBlock block, int variant, IReadOnlySet<int> clips, ConversionReport report, Cover cover = Cover.None)
    {
        if (items.For(block.Name, block.IsGround, variant, MobilOf(block), clips, cover, Tilt(block)) is not { } item) return false;
        if (item == IceItem.Nothing) return cover is not (Cover.Top or Cover.TopOfIce) && !IsGameplayBlock(block.Name);
        Vector3 pos;
        Vec3 rotation;
        if (block.IsFree)
        {
            var p = block.AbsolutePositionInMap ?? default;
            // tilt is baked into the mesh
            (pos, rotation) = (new Vector3(p.X, p.Y, p.Z), Tilt(block) == default ? block.YawPitchRoll ?? default : new Vec3(block.YawPitchRoll!.Value.X, 0, 0));
        }
        else
        {
            pos = Placement(block, item.Size).Translation - new Vector3(0, EnvironmentDrop(map.Collection?.ToString()), 0);
            rotation = new Vec3(-(int)block.Direction * MathF.PI / 2, 0, 0);
        }
        // ghost blocks: skip duplicates
        if (map.GetAnchoredObjects().Any(o => o.ItemModel?.Id == item.Ident.Id && o.YawPitchRoll == rotation
                && Vector3.Distance(new Vector3(o.AbsolutePositionInMap.X, o.AbsolutePositionInMap.Y, o.AbsolutePositionInMap.Z), pos) < 0.01f))
            return true;
        var placed = map.PlaceAnchoredObject(item.Ident, new Vec3(pos.X, pos.Y, pos.Z), rotation, default);
        if (!block.IsGround && cover == Cover.None) placed.WaypointSpecialProperty = block.WaypointSpecialProperty;
        placed.Color = block.Color;
        report.UsedItems.Add(item);
        return true;
    }

    private HashSet<int> FreeClips(CGameCtnBlock block)
        => shapes.ClipSides(block.Name, false, VariantOf(block))
            .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti).Select(c => MeshExport.ClipKey(s.Bit, c.Index))).ToHashSet();

    // white deco with no ice version stays (concrete)
    private bool IsConcrete(CGameCtnBlock block, int variant, IReadOnlySet<int> clips)
        => block.Name.StartsWith("Deco") && block.Color == DifficultyColor.White
            && !(shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is { } materials
                && materials.Any(m => m.StartsWith("Deco") && !IsGroundFill(block, m)));

    // walls-only blocks stay native, ice on their upward faces (not gates or stands)
    private void IceTops(CGameCtnChallenge map, CGameCtnBlock block, int variant, IReadOnlySet<int> clips, ConversionReport report, Cover cover = Cover.Top)
    {
        if (block.Name.StartsWith("Gate") || block.Name.StartsWith("Stand") || IsGameplayBlock(block.Name)) return;
        // odd angles: rotation order unknown
        if (!QuarterTurns(Tilt(block))) return;
        if (shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is { } materials && materials.Any(IsWallMaterial)
            && PlaceItem(map, block, variant, clips, report, cover)
            && topped.Add(block))
            Bump(report.TopsIced, block.Name);
    }

    private static Vector2 Tilt(CGameCtnBlock block)
        => block.IsFree && block.YawPitchRoll is { } r && (MathF.Abs(r.Y) > 0.01f || MathF.Abs(r.Z) > 0.01f) ? new Vector2(r.Y, r.Z) : default;

    private static bool QuarterTurns(Vector2 tilt)
    {
        static bool Square(float a) => MathF.Abs(a / (MathF.PI / 2) - MathF.Round(a / (MathF.PI / 2))) < 0.01f;
        return Square(tilt.X) && Square(tilt.Y);
    }

    internal static bool IsWallMaterial(string material) => material is "TrackWall" or "TrackWallClips" or "Structure";

    private bool IsWallOnly(CGameCtnBlock block, int variant, IReadOnlySet<int> clips)
        => shapes.Materials(block.Name, block.IsGround, variant, MobilOf(block), clips) is { Count: > 0 } materials
            && materials.Any(IsWallMaterial)
            && !materials.Any(m => m != "Underwater" && !IsGroundFill(block, m) && ItemBuilder.SkinCovers(block.Name, m, items.SnowOnZoneSides));

    private static bool IsGroundFill(CGameCtnBlock block, string material)
        => block.IsGround && block.Name.StartsWith("DecoWall") && material.StartsWith("Deco");

    private static bool IsAlreadyIceOrCustom(string name) => name.Contains("Ice") || name.Contains('\\');

    private static bool WantsItem(string name)
        => Surface().IsMatch(name) || IcePrefixes.Any(name.StartsWith) || name.StartsWith("SnowRoad") || name.StartsWith("DecoWall");

    // ice name guesses, most specific first
    private static IEnumerable<string> Candidates(string name)
        => name.Contains("Penalty") ? [] : RawCandidates(name).Where(c => !c.Contains("RoadIce"));

    // never RoadIce
    private static IEnumerable<string> RawCandidates(string name)
    {
        foreach (Match m in Surface().Matches(name))
            yield return name[..m.Index] + "Ice" + name[(m.Index + m.Length)..];
        foreach (var p in IcePrefixes)
            if (name.StartsWith(p)) yield return p + "Ice" + name[p.Length..];
        if (name.StartsWith("DecoWall") && !Surface().IsMatch(name)) yield return name + "Ice";
    }

    private static void Apply(CGameCtnBlock block, Translation t, ConversionReport report)
    {
        block.Name = t.To!;
        if (t.Variant is { } v) block.Variant = v;
        if (t.SubVariant is { } sv) block.SubVariant = sv;

        if (block.IsFree)
        {
            if (t.Rotate != 0 && block.YawPitchRoll is { } ypr)
                block.YawPitchRoll = ypr with { X = ypr.X - t.Rotate * MathF.PI / 2 };
            if (t.Offset is not null) report.FreeBlockOffsetsSkipped++;
            return;
        }

        var facing = (int)block.Direction;
        if (t.Offset is [var x, var y, var z])
        {
            var (wx, wz) = RotateQuarterTurns(x, z, facing);
            block.Coord += new Int3(wx, y, wz);
        }
        if (t.Rotate != 0)
            block.Direction = (Direction)(((facing + t.Rotate) % 4 + 4) % 4);
    }

    private static (int X, int Z) RotateQuarterTurns(int x, int z, int turns) => (((turns % 4) + 4) % 4) switch
    {
        0 => (x, z),
        1 => (-z, x),
        2 => (-x, -z),
        _ => (z, -x),
    };

    private static void Bump<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
        => counts[key] = counts.GetValueOrDefault(key) + 1;
}
