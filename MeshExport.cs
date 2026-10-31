using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Meta;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// Trackmania space: Y up, metres
public sealed record MeshPiece(
    string Name,
    string Material,
    bool Visible,
    bool Collidable,
    float[] Positions,
    float[]? Normals,
    float[]? Uv0,
    float[]? Uv1,
    int[] Indices,
    float[]? Colors = null); // decals render black without

// Bit = unit * 6 + side; Probe lands in the facing cell
public sealed record ClipSide(int Bit, Vector3 Probe)
{
    public Vector3 Center { get; init; }
    public IReadOnlyList<ClipInfo> Clips { get; init; } = [];
    public bool ClipOnly { get; init; }
}

public sealed record ClipInfo(int Index, string Name, string Type, bool Vertical, string? Group, string? SymmetricGroup,
    string? VerticalGroup, string? Pair, bool Full, bool Deletable, bool Anti, string MultiDir = "SameDir");

// block prefabs from the extraction -> mesh pieces
public sealed class MeshExport(string stadiumDir, IReadOnlyDictionary<string, string> materialMap) : IBlockShapes
{
    private readonly Dictionary<string, CPlugPrefab?> prefabCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (CGameCtnBlockInfo Info, string Path, CGameCtnBlockInfoVariant Variant)?> blockCache = [];
    public List<string> Warnings { get; } = [];
    private string? environment;

    public Func<string, bool>? KeepMaterial { get; init; }

    public float Lift { get; init; }

    public Vector3 Up { get; init; } = Vector3.UnitY;

    public Func<Vector3, bool>? KeepFace { get; init; }
    public bool PlatformLayout { get; init; }

    // material for visual faces that touch a border (the road lip under it): null = as usual, "" = left out
    public Func<string, string?>? LipAs { get; init; }

    private const float LipTouch = 0.02f;
    // lips run from below the road up to the border: 0.8 m (tech, bump), 1 m (dirt); flush borders touch flat faces
    private const float LipMinHeight = 0.5f, LipMaxHeight = 1.5f;
    // steeper than 60°
    private const float LipMaxNormalY = 0.5f;
    private List<(Vector3 A, Vector3 B, Vector3 C)>? borders;
    private bool collectingBorders;

    public string? Environment
    {
        get => environment;
        set { if (value != environment) { environment = value; blockCache.Clear(); } }
    }

    public bool IsTerrain(string blockName)
        => TryLoadBlock(blockName, out var block) && Regex.IsMatch(block.Path, @"\.ED(Flat|Frontier|Transition)\.Gbx$", RegexOptions.IgnoreCase);

    public string? DefinedIn(string blockName)
        => TryLoadBlock(blockName, out var block) && Paths.EnvironmentBlockInfos(stadiumDir, environment) is { } dir
            && block.Path.StartsWith(dir) ? environment : null;

    public static readonly Vector3 SpawnPoint = new(16, 2, 16);

    // air and ground can differ
    public (int X, int Z) BlockSize(string blockName, bool ground, int variantIndex)
    {
        var units = Units(blockName, ground, variantIndex) ?? [];
        return units.Count == 0 ? (1, 1) : (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1);
    }

    private (CGameCtnBlockInfo Info, string Path, CGameCtnBlockInfoVariant Variant) LoadBlock(string blockName)
        => TryLoadBlock(blockName, out var block) ? block
            : throw new FileNotFoundException($"No usable block definition for {blockName} in the extraction");

    // null = unknown
    public bool? EnvironmentHas(string blockName)
    {
        if (Paths.EnvironmentBlockInfos(stadiumDir, environment) is not { } dir) return null;
        if (!environmentBlocks.TryGetValue(dir, out var names))
        {
            names = Directory.EnumerateFiles(dir, "*.ED*.Gbx", SearchOption.AllDirectories)
                .Select(f => Path.GetFileName(f).Split('.')[0]).ToHashSet();
            var inventory = Path.Combine(dir, "BlockInfoInventory.gbx.json");
            if (File.Exists(inventory))
                names.UnionWith(Regex.Matches(File.ReadAllText(inventory), @"""Name""\s*:\s*""([^""]+)""").Select(m => m.Groups[1].Value));
            environmentBlocks[dir] = names;
        }
        return names.Contains(blockName);
    }

    private readonly Dictionary<string, HashSet<string>> environmentBlocks = [];

    private bool TryLoadBlock(string blockName, out (CGameCtnBlockInfo Info, string Path, CGameCtnBlockInfoVariant Variant) block)
    {
        if (!blockCache.TryGetValue(blockName, out var cached))
        {
            var infoPath = blockName.Contains('\\') ? null
                : new[] { Paths.EnvironmentBlockInfos(stadiumDir, environment), Path.Combine(stadiumDir, "GameCtnBlockInfo") }
                    .OfType<string>().Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, blockName + ".ED*.Gbx", SearchOption.AllDirectories)).FirstOrDefault();
            var info = infoPath is null ? null : Gbx.ParseNode(infoPath) as CGameCtnBlockInfo;
            // GBX.NET leaves missing refs null
            if (infoPath is not null && Gbx.ParseHeader(infoPath).RefTable is { } refs)
                foreach (var f in refs.Files)
                    if (RecordMissing && ResolveRef(infoPath, refs.AncestorLevel, f.FilePath) is var target && !File.Exists(target))
                        Missing.Add(RelativeToGameData(target));
            var variant = (CGameCtnBlockInfoVariant?)info?.VariantBaseAir ?? info?.VariantBaseGround;
            blockCache[blockName] = cached = info is null || variant is null ? null : (info, infoPath!, variant);
        }
        block = cached ?? default;
        return cached is not null;
    }

    public List<(string Path, Matrix4x4 Transform)>? FindPlacedBlockParts(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> shownClips)
        => TryLoadBlock(blockName, out var block) && PickVariant(block.Info, ground, variantIndex) is { } variant
            ? Parts(block.Path, variant, mobil, block.Info.PillarShapeMultiDir.ToString(), (bit, index, _, _) =>
                Enumerable.Range(0, 8).Cast<int?>().FirstOrDefault(column => shownClips.Contains(ClipKey(bit, index, column!.Value))))
            : null;

    public static int ClipKey(int bit, int index, int column = 0) => bit * 32 + index + (column << 16);

    public const int ColumnTop = 1, ColumnBottom = 2, ColumnOnGround = 4;

    // vertical clip mobils: air 0 middle, 1 top, 2 bottom, 3 both; ground 0 bottom, 1 both
    private static (CGameCtnBlockInfoVariant? Variant, int Mobil) VerticalMobil(CGameCtnBlockInfo clip, int column)
        => (column & ColumnOnGround) != 0 && clip.VariantBaseGround is { Mobils.Length: > 0 } ground
            ? (ground, column & ColumnTop)
            : (clip.VariantBaseAir, column & (ColumnTop | ColumnBottom));

    public static string ClipsKey(IReadOnlySet<int> clips)
        => clips.Count == 0 ? "none" : System.Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.ASCII.GetBytes(string.Join(",", clips.Order()))))[..12];

    public bool IsPillarBlock(string blockName) => TryLoadBlock(blockName, out var block) && block.Info.IsPillar;

    // NoPillarBelowIndex, air only
    public int? NoPillarBelowVariant(string blockName, bool ground, int variantIndex)
        => !ground && TryLoadBlock(blockName, out var block) && PickVariant(block.Info, false, variantIndex) is { NoPillarBelowIndex: var npb }
            && npb != 255 && npb != variantIndex && npb <= (block.Info.AdditionalVariantsAir?.Length ?? 0)
            ? npb : null;

    public IReadOnlyList<ClipSide> ClipSides(string blockName, bool ground, int variantIndex)
    {
        if (!TryLoadBlock(blockName, out var block) || PickVariant(block.Info, ground, variantIndex) is not { } variant) return [];
        var clipOnly = MobilPrefab(variant, block.Path) is null;
        return ClipSlots(variant).Select(s =>
            {
                var clips = ClipInfos(s.Clips).ToList();
                return new ClipSide(s.Bit, s.Probe)
                {
                    Center = s.UnitOrigin + new Vector3(16, 4, 16),
                    Clips = clips,
                    ClipOnly = clipOnly,
                };
            })
            .Where(s => s.Clips.Count > 0).ToList();
    }

    private static IEnumerable<ClipInfo> ClipInfos(External<CGameCtnBlockInfoClip>[]? clips)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < (clips?.Length ?? 0); i++)
        {
            if (clips![i].File?.FilePath is not { } file || !seen.Add(file)) continue;
            var node = clips[i].Node;
            yield return new ClipInfo(i, node?.Ident.Id ?? Path.GetFileName(file).Split('.')[0], node?.ClipType.ToString() ?? "",
                IsVertical(clips[i]), NonEmpty(node?.ClipGroupId), NonEmpty(node?.SymmetricalClipGroupId),
                NonEmpty((node as CGameCtnBlockInfoClipVertical)?.VerticalClipGroupId), NonEmpty(node?.ASymmetricalClipId),
                node?.IsFullFreeClip ?? false, node?.CanBeDeletedByFullFreeClip ?? false, IsAntiClip(node), node?.TopBottomMultiDir.ToString() ?? "SameDir");
        }
    }

    // GBX.NET's U03 is IsAntiClip
    private static bool IsAntiClip(CGameCtnBlockInfoClip? node)
        => node?.Chunks.Get<CGameCtnBlockInfoClip.Chunk03053006>() is { U03: > 0 };

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    public string? MaterialModifierDir(string blockName)
    {
        if (!TryLoadBlock(blockName, out var block) || block.Info.MaterialModifierFile?.FilePath is not { } file) return null;
        var ancestor = Gbx.ParseHeader(block.Path).RefTable?.AncestorLevel ?? 0;
        var dir = ResolveRef(block.Path, ancestor, file);
        dir = dir[..dir.IndexOf('.', dir.LastIndexOf('/'))];
        return Directory.Exists(dir) ? dir : null;
    }

    private static CGameCtnBlockInfoVariant? PickVariant(CGameCtnBlockInfo info, bool ground, int index)
    {
        var extra = ground ? info.AdditionalVariantsGround?.Cast<CGameCtnBlockInfoVariant>() : info.AdditionalVariantsAir;
        CGameCtnBlockInfoVariant? baseVariant = ground ? info.VariantBaseGround : info.VariantBaseAir;
        var variant = index > 0 && extra?.ElementAtOrDefault(index - 1) is { } e ? e : baseVariant;
        return variant is { Mobils.Length: > 0 } ? variant : (CGameCtnBlockInfoVariant?)info.VariantBaseAir ?? info.VariantBaseGround;
    }

    private List<(string Path, Matrix4x4 Transform)>? Parts(string infoPath, CGameCtnBlockInfoVariant variant, int mobil, string pillarShapeDirs,
        Func<int, int, External<CGameCtnBlockInfoClip>, bool, int?> show)
    {
        var parts = new List<(string, Matrix4x4)>();
        var walls = new List<(string Path, Matrix4x4 Transform)>();
        var main = MobilPrefab(variant, infoPath, mobil);
        if (main is not null) { parts.Add((main.Value.Path, main.Value.Transform)); walls.Add(parts[^1]); }

        var ancestor = Gbx.ParseHeader(infoPath).RefTable?.AncestorLevel ?? 0;
        var blockName = Path.GetFileName(infoPath).Split('.')[0];
        // baked clips say how the game turns it
        Matrix4x4? Learned(int bit, CGameCtnBlockInfoClip node) => ClipTurns.Learned(blockName, bit, node.Ident.Id) is { } k ? AroundUnitCenter(-k * MathF.PI / 2) : null;
        var rise = Vector2.Zero;
        if (main is null)
            foreach (var (unitOrigin, bit, clips, _, probe) in ClipSlots(variant))
                if (bit % 6 < 4 && clips is { Length: > 0 })
                    rise += Vector2.Normalize(new Vector2(probe.X - unitOrigin.X - 16, probe.Z - unitOrigin.Z - 16));
        var tops = new List<((string Path, Matrix4x4 Transform) Part, Matrix4x4 Place, bool ClipOnly, Matrix4x4? Learned)>();
        foreach (var (unitOrigin, bit, clips, place, _) in ClipSlots(variant))
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < (clips?.Length ?? 0); i++)
            {
                var clip = clips![i];
                if (clip.File?.FilePath is not { } filePath || !seen.Add(filePath) || show(bit, i, clip, main is null) is not { } column) continue;
                if (clip.Node is not { } node || clip.File is not { } file) continue;
                var clipPath = ResolveRef(infoPath, ancestor, file.FilePath);
                var prefab = IsVertical(clip) && VerticalMobil(node, column) is ({ } vertical, var position)
                    ? MobilPrefab(vertical, clipPath, position, exact: true)
                    : ((CGameCtnBlockInfoVariant?)node.VariantBaseAir ?? node.VariantBaseGround) is { } clipVariant
                        ? MobilPrefab(clipVariant, clipPath) : null;
                if (prefab is { } part)
                {
                    if (bit % 6 == 5) { tops.Add((part, place * Matrix4x4.CreateTranslation(unitOrigin), main is null, Learned(bit, node))); continue; }
                    var turn = bit % 6 != 4 ? Matrix4x4.Identity
                        : Learned(bit, node) ?? (main is { } m ? BottomClipTurn(pillarShapeDirs, part, m) : Matrix4x4.Identity);
                    if (bit % 6 == 4 && turn.IsIdentity && Learned(bit, node) is null) turn = InFootprintTurn(part, place * Matrix4x4.CreateTranslation(unitOrigin), variant);
                    parts.Add((part.Path, part.Transform * turn * place * Matrix4x4.CreateTranslation(unitOrigin)));
                    if (bit % 6 < 4) walls.Add(parts[^1]);
                }
            }
        }
        if (tops.Count > 0)
        {
            var corners = walls.Count > 0 ? Corners(walls) : [];
            foreach (var (part, place, clipOnly, learned) in tops)
            {
                var sloped = Slope(part, upOnly: false).Length() >= 0.1f;
                // the fit is noise on clip-only blocks
                var turn = learned ?? (!sloped ? Matrix4x4.Identity : clipOnly ? TopClipTurn(part, rise) : corners.Count > 0 ? FittedTopTurn(part, place, corners) : Matrix4x4.Identity);
                parts.Add((part.Path, part.Transform * turn * place));
            }
        }
        return parts.Count > 0 ? parts : null;
    }

    // quarter turns of a shaped top/bottom clip on top of its block's direction: learned from maps, else the fitted mesh turn
    public int ShapedClipTurn(string blockName, bool ground, int variantIndex, int bit, ClipInfo clip)
        => ClipTurns.Learned(blockName, bit, clip.Name)
            ?? (ShapedClipTurns(blockName, ground, variantIndex).TryGetValue((bit, clip.Index), out var turn) ? ClipTurns.DirOf(turn) : 0);

    private readonly Dictionary<(string, bool, int), Dictionary<(int Bit, int Index), Matrix4x4>> shapedTurns = [];

    // turn of each shaped top/bottom clip inside its block: the clip mesh fitted to the block's corners
    public IReadOnlyDictionary<(int Bit, int Index), Matrix4x4> ShapedClipTurns(string blockName, bool ground, int variantIndex)
    {
        if (shapedTurns.TryGetValue((blockName, ground, variantIndex), out var known)) return known;
        var turns = shapedTurns[(blockName, ground, variantIndex)] = [];
        if (!TryLoadBlock(blockName, out var block) || PickVariant(block.Info, ground, variantIndex) is not { } variant
            || MobilPrefab(variant, block.Path) is not { } main) return turns;
        var corners = Corners([main]);
        var ancestor = Gbx.ParseHeader(block.Path).RefTable?.AncestorLevel ?? 0;
        foreach (var (unitOrigin, bit, clips, place, _) in ClipSlots(variant))
        {
            if (bit % 6 < 4) continue;
            for (int i = 0; i < (clips?.Length ?? 0); i++)
                if (clips![i] is { Node: { ASymmetricalClipId.Length: > 0 } node, File: { } file }
                    && ((CGameCtnBlockInfoVariant?)node.VariantBaseAir ?? node.VariantBaseGround) is { } clipVariant
                    && MobilPrefab(clipVariant, ResolveRef(block.Path, ancestor, file.FilePath)) is { } part)
                {
                    // sloped bottoms follow the road; corners can't tell a slope's ends apart
                    var sloped = bit % 6 == 4 ? BottomClipTurn("AllDir", part, main) : Matrix4x4.Identity;
                    turns[(bit, i)] = !sloped.IsIdentity || Slope(part, upOnly: false).Length() >= 0.1f && bit % 6 == 4
                        ? sloped : FittedTopTurn(part, place * Matrix4x4.CreateTranslation(unitOrigin), corners);
                }
        }
        return turns;
    }

    private List<Vector3> Corners(List<(string Path, Matrix4x4 Transform)> parts)
    {
        var plain = new MeshExport(stadiumDir, new Dictionary<string, string>());
        return plain.Export(parts).Where(p => p.Visible)
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
            .Distinct().ToList();
    }

    // the data doesn't say how the game turns it: pick the best fit
    private Matrix4x4 FittedTopTurn((string Path, Matrix4x4 Transform) clip, Matrix4x4 place, List<Vector3> corners)
    {
        var plain = new MeshExport(stadiumDir, new Dictionary<string, string>());
        int Matches(float yaw)
        {
            var turned = plain.Export([(clip.Path, clip.Transform * AroundUnitCenter(yaw) * place)]).Where(p => p.Visible);
            return turned.SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
                .Distinct().Count(v => corners.Any(c => Vector3.DistanceSquared(c, v) < 0.01f));
        }
        var turns = new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 };
        var best = turns.Select(t => (Turn: t, Matches: Matches(t))).ToList();
        return AroundUnitCenter(best.Max(b => b.Matches) > best[0].Matches ? best.MaxBy(b => b.Matches).Turn : 0);
    }

    // same, for bottom clips bigger than a unit
    private Matrix4x4 InFootprintTurn((string Path, Matrix4x4 Transform) clip, Matrix4x4 place, CGameCtnBlockInfoVariant variant)
    {
        var cells = (variant.BlockUnitModels ?? []).Select(u => (u.RelativeOffset.X, u.RelativeOffset.Z)).ToHashSet();
        bool Inside(Vector3 v) => cells.Any(c => v.X >= c.X * 32 - 0.5f && v.X <= c.X * 32 + 32.5f && v.Z >= c.Z * 32 - 0.5f && v.Z <= c.Z * 32 + 32.5f);
        var plain = new MeshExport(stadiumDir, new Dictionary<string, string>());
        int Outside(float yaw) => plain.Export([(clip.Path, clip.Transform * AroundUnitCenter(yaw) * place)]).Where(p => p.Visible)
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
            .Count(v => !Inside(v));
        var unturned = Outside(0);
        if (unturned == 0) return Matrix4x4.Identity;
        var best = new[] { MathF.PI / 2, MathF.PI, -MathF.PI / 2 }.Select(t => (Turn: t, Outside: Outside(t))).MinBy(b => b.Outside);
        return best.Outside < unturned ? AroundUnitCenter(best.Turn) : Matrix4x4.Identity;
    }

    // mirrored blocks share one clip: match the road's slope
    private Matrix4x4 BottomClipTurn(string pillarShapeDirs, (string Path, Matrix4x4 Transform) clip, (string Path, Matrix4x4 Transform) block)
    {
        var turns = pillarShapeDirs switch
        {
            "SymmetricalDirs" => new[] { 0f, MathF.PI },
            "AllDir" or "AllDirs" => [0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2],
            _ => [0f],
        };
        if (turns.Length == 1) return Matrix4x4.Identity;
        var road = Slope(block, upOnly: true);
        var clipSlope = Slope(clip, upOnly: false);
        if (road.Length() < 0.1f || clipSlope.Length() < 0.1f) return Matrix4x4.Identity;
        var best = turns.MaxBy(t => Vector2.Dot(Turned(clipSlope, t), road) - (t == 0 ? 0 : 0.01f));
        return AroundUnitCenter(best);
    }

    private Matrix4x4 TopClipTurn((string Path, Matrix4x4 Transform) clip, Vector2 rise)
    {
        var slope = Slope(clip, upOnly: false);
        if (slope.Length() < 0.1f || rise.Length() < 0.5f) return Matrix4x4.Identity;
        var turns = new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 };
        return AroundUnitCenter(turns.MaxBy(t => Vector2.Dot(Turned(slope, t), rise) - (t == 0 ? 0 : 0.01f)));
    }

    private static Vector2 Turned(Vector2 slope, float yaw)
        => new Vector2(Vector3.TransformNormal(new Vector3(slope.X, 0, slope.Y), Matrix4x4.CreateRotationY(yaw)).X,
            Vector3.TransformNormal(new Vector3(slope.X, 0, slope.Y), Matrix4x4.CreateRotationY(yaw)).Z);

    private Vector2 Slope((string Path, Matrix4x4 Transform) part, bool upOnly)
    {
        var key = (part.Path, part.Transform, upOnly);
        lock (slopes)
            if (slopes.TryGetValue(key, out var known)) return known;
        var plain = new MeshExport(stadiumDir, new Dictionary<string, string>());
        Vector2 sum = default;
        float area = 0;
        foreach (var piece in plain.Export([part]).Where(p => upOnly ? p.Collidable && !p.Visible : p.Visible))
            for (int t = 0; t + 2 < piece.Indices.Length; t += 3)
            {
                Vector3 V(int i) => new(piece.Positions[i * 3], piece.Positions[i * 3 + 1], piece.Positions[i * 3 + 2]);
                var n = Vector3.Cross(V(piece.Indices[t + 1]) - V(piece.Indices[t]), V(piece.Indices[t + 2]) - V(piece.Indices[t]));
                var a = n.Length();
                if (a < 1e-6f) continue;
                n /= a;
                if (upOnly ? n.Y < 0.5f : MathF.Abs(n.Y) < 0.5f) continue;
                sum += new Vector2(-n.X / n.Y, -n.Z / n.Y) * a;
                area += a;
            }
        var slope = area > 0 ? sum / area : default;
        lock (slopes) slopes[key] = slope;
        return slope;
    }

    private static readonly Dictionary<(string, Matrix4x4, bool), Vector2> slopes = [];

    private static readonly Dictionary<string, ILookup<string, string>> itemFolders = [];

    // env's own version first
    public string? ItemFile(string itemId)
    {
        var gameData = Path.GetDirectoryName(stadiumDir)!;
        var dirs = environment is null or "Stadium2020" ? [stadiumDir] : new[] { Path.Combine(gameData, environment), stadiumDir };
        foreach (var dir in dirs.Select(d => Path.Combine(d, "Items")))
        {
            ILookup<string, string> files;
            lock (itemFolders)
                if (!itemFolders.TryGetValue(dir, out files!))
                    itemFolders[dir] = files = (Directory.Exists(dir)
                        ? Directory.EnumerateFiles(dir, "*.Item.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                        : []).ToLookup(f => Path.GetFileName(f).Split('.')[0], StringComparer.OrdinalIgnoreCase);
            if (files[itemId].FirstOrDefault() is { } file) return file;
        }
        return null;
    }

    private static readonly Dictionary<string, List<string>?> itemVariants = [];

    // model names of a variant list item: FallTreeMedium, Fall.Prefab…
    public List<string>? ItemVariants(string itemId)
    {
        if (ItemFile(itemId) is not { } file) return null;
        lock (itemVariants)
            if (itemVariants.TryGetValue(file, out var known)) return known;
        var found = Gbx.ParseNode<CGameItemModel>(file).EntityModel is NPlugItem_SVariantList { Variants: { } variants }
            ? variants.Select(v => Path.GetFileName(v.EntityModelFile?.FilePath ?? "") is var f && f.EndsWith(".Prefab.Gbx") ? f[..^".Gbx".Length] : f.Split('.')[0]).ToList()
            : null;
        lock (itemVariants) itemVariants[file] = found;
        return found;
    }

    public List<MeshPiece>? ExportItem(string itemPath, out string? reason)
    {
        reason = null;
        var item = Gbx.ParseNode<CGameItemModel>(itemPath);
        var ancestor = Gbx.ParseHeader(itemPath).RefTable?.AncestorLevel ?? 0;
        var pieces = new List<MeshPiece>();
        switch (item.EntityModel)
        {
            case NPlugItem_SVariantList { Variants: [{ EntityModelFile: { } file }, ..] }:
                return PrefabOrStatic(ResolveRef(itemPath, ancestor, file.FilePath), pieces, out reason);
            case CPlugPrefab prefab:
                AddPrefab(prefab, itemPath, Matrix4x4.Identity, pieces, depth: 0);
                break;
            case CGameCommonItemEntityModel { StaticObject: { } som }:
                AddStaticObject(som, Matrix4x4.Identity, Path.GetFileNameWithoutExtension(itemPath), pieces);
                break;
            // entity in a separate file: take it from the ref table
            case null when Gbx.ParseHeader(itemPath).RefTable?.Files.FirstOrDefault(f => f.FilePath.EndsWith(".Prefab.Gbx", StringComparison.OrdinalIgnoreCase)) is { } prefabRef:
                return PrefabOrStatic(ResolveRef(itemPath, ancestor, prefabRef.FilePath), pieces, out reason);
            case var other:
                reason = other?.GetType().Name ?? "no entity model";
                return null;
        }
        return pieces;
    }

    private List<MeshPiece>? PrefabOrStatic(string target, List<MeshPiece> pieces, out string? reason)
    {
        reason = null;
        var kind = Path.GetExtension(Path.GetFileNameWithoutExtension(target)).TrimStart('.');
        if (kind is not ("Prefab" or "StaticObject")) { reason = kind; return null; }
        if (!File.Exists(target))
        {
            Missing.Add(RelativeToGameData(target));
            reason = "not extracted";
            return null;
        }
        if (kind == "Prefab") AddPrefab(target, Matrix4x4.Identity, pieces, depth: 0);
        else AddStaticObject(Gbx.ParseNode<CPlugStaticObjectModel>(target), Matrix4x4.Identity, Path.GetFileNameWithoutExtension(target), pieces);
        return pieces;
    }

    public string? Waypoint(string blockName)
        => TryLoadBlock(blockName, out var block) && block.Info.WayPointType.ToString() is var w && w != "None" ? w : null;

    // on the road at the block's middle, tilted with it: the lowest upward face there (the arch is above)
    public (Vector3 At, float Pitch, float Roll) Spawn(string blockName, bool ground, int variantIndex, int mobil)
    {
        (float Y, Vector3 N)? road = null;
        foreach (var (a, b, c, _) in CollisionTriangles(blockName, ground, variantIndex, mobil) ?? [])
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (n.Y < 0.5f || MathF.Abs(d) < 1e-6f) continue;
            var u = ((SpawnPoint.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (SpawnPoint.Z - a.Z)) / d;
            var v = ((b.X - a.X) * (SpawnPoint.Z - a.Z) - (SpawnPoint.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var y = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            if (road is not { } r || y < r.Y) road = (y, n);
        }
        if (road is not { } found) return (SpawnPoint, 0, 0);
        // up = (-sin roll, cos roll cos pitch, cos roll sin pitch), as Matrix4x4.CreateFromYawPitchRoll turns it
        return (SpawnPoint with { Y = found.Y }, MathF.Atan2(found.N.Z, found.N.Y), -MathF.Asin(found.N.X));
    }

    public HashSet<string> Missing { get; } = [];

    // off while only looking blocks up
    public bool RecordMissing { get; set; } = true;

    public string RelativeToGameData(string path) => Path.GetRelativePath(Path.GetDirectoryName(stadiumDir)!, path).Replace('\\', '/');

    private static bool IsVertical(External<CGameCtnBlockInfoClip> clip)
        => clip.File?.FilePath.EndsWith(".EDVerticalClip.Gbx", StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)>? CollisionTriangles(string blockName, bool ground, int variantIndex, int mobil)
    {
        if (FindPlacedBlockParts(blockName, ground, variantIndex, mobil, new HashSet<int>()) is not { } parts) return null;
        var tris = new List<(Vector3, Vector3, Vector3, string)>();
        foreach (var p in Export(parts).Where(p => p.Collidable))
        {
            Vector3 At(int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
            for (int t = 0; t + 2 < p.Indices.Length; t += 3) tris.Add((At(p.Indices[t]), At(p.Indices[t + 1]), At(p.Indices[t + 2]), p.Material));
        }
        return tris;
    }

    public IReadOnlySet<string>? Materials(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips)
        => FindPlacedBlockParts(blockName, ground, variantIndex, mobil, clips) is { } parts
            ? Export(parts).Select(p => p.Material).ToHashSet() : null;

    public IReadOnlyList<Int3>? Units(string blockName, bool ground, int variantIndex)
    {
        if (!TryLoadBlock(blockName, out var block)) return null;
        var variant = PickVariant(block.Info, ground, variantIndex) ?? block.Variant;
        return variant.BlockUnitModels is { Length: > 0 } units ? units.Select(u => u.RelativeOffset).ToList() : [new Int3(0, 0, 0)];
    }

    private static IEnumerable<(Vector3 UnitOrigin, int Bit, External<CGameCtnBlockInfoClip>[]? Clips, Matrix4x4 Place, Vector3 Probe)> ClipSlots(CGameCtnBlockInfoVariant variant)
    {
        var units = variant.BlockUnitModels ?? [];
        for (int ui = 0; ui < units.Length; ui++)
        {
            var unit = units[ui];
            var unitOrigin = new Vector3(unit.RelativeOffset.X * 32, unit.RelativeOffset.Y * 8, unit.RelativeOffset.Z * 32);
            // clips are authored in the neighbouring unit's space
            var side = Matrix4x4.CreateTranslation(0, 0, -32);
            var sideProbe = new Vector3(16, 4, -1);
            var slots = new (External<CGameCtnBlockInfoClip>[]?, Matrix4x4, Vector3)[]
            {
                (unit.ClipsNorth, side * AroundUnitCenter(MathF.PI), Vector3.Transform(sideProbe, AroundUnitCenter(MathF.PI))),
                (unit.ClipsEast, side * AroundUnitCenter(MathF.PI / 2), Vector3.Transform(sideProbe, AroundUnitCenter(MathF.PI / 2))),
                (unit.ClipsSouth, side, sideProbe),
                (unit.ClipsWest, side * AroundUnitCenter(-MathF.PI / 2), Vector3.Transform(sideProbe, AroundUnitCenter(-MathF.PI / 2))),
                (unit.ClipsBottom, Matrix4x4.CreateTranslation(0, -8, 0), new Vector3(16, -1, 16)),
                (unit.ClipsTop, Matrix4x4.CreateTranslation(0, 8, 0), new Vector3(16, 9, 16)),
            };
            for (int si = 0; si < slots.Length; si++)
                yield return (unitOrigin, ui * slots.Length + si, slots[si].Item1, slots[si].Item2, unitOrigin + slots[si].Item3);
        }
    }

    private static Matrix4x4 AroundUnitCenter(float yaw)
        => Matrix4x4.CreateTranslation(-16, 0, -16) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(16, 0, 16);

    // mobil as in block flags: row in bits 0-5, entry in bits 6-11
    private static (string Path, Matrix4x4 Transform)? MobilPrefab(CGameCtnBlockInfoVariant variant, string ownerPath, int mobil = 0, bool exact = false)
    {
        var mobils = variant.Mobils ?? [];
        var row = mobils.ElementAtOrDefault(mobil & 0x3F);
        var chosen = (row?.ElementAtOrDefault(mobil >> 6) is { PrefabFidFile: not null } entry ? entry : row?.FirstOrDefault(m => m.PrefabFidFile is not null))
            ?? (exact ? null : mobils.SelectMany(m => m).FirstOrDefault(m => m.PrefabFidFile is not null));
        if (chosen is not { PrefabFidFile: { } file } picked) return null;
        var ancestor = Gbx.ParseHeader(ownerPath).RefTable?.AncestorLevel ?? 0;
        // degrees
        const float deg = MathF.PI / 180;
        var transform = picked.HasGeomTransformation
            ? Matrix4x4.CreateFromYawPitchRoll(picked.GeomRotation.Y * deg, picked.GeomRotation.X * deg, picked.GeomRotation.Z * deg)
                * Matrix4x4.CreateTranslation(picked.GeomTranslation.X, picked.GeomTranslation.Y, picked.GeomTranslation.Z)
            : Matrix4x4.Identity;
        return (ResolveRef(ownerPath, ancestor, file.FilePath), transform);
    }

    // spawn socket + trigger box for waypoint items
    private const string HelperMaterial = "TrackBorders";
    private const string SocketMaterial = "PlatformIce_PlatformTech";

    public static IEnumerable<MeshPiece> WaypointPieces(string waypoint, IReadOnlyList<MeshPiece> block)
    {
        yield return new MeshPiece("_socket_start", SocketMaterial, Visible: false, Collidable: true,
            [SpawnPoint.X, SpawnPoint.Y, SpawnPoint.Z, SpawnPoint.X + 0.1f, SpawnPoint.Y, SpawnPoint.Z, SpawnPoint.X, SpawnPoint.Y, SpawnPoint.Z + 0.1f],
            null, null, null, [0, 1, 2]);
        if (waypoint != "Start")
            // tech roads at 2 m, dirt at 0.5 m
        {
            var gate = GateZ(block);
            yield return Box("_trigger_gate", new Vector3(2.7f, 0, gate - 0.5f), new Vector3(29.3f, 10, gate + 0.5f));
        }
    }

    private static float GateZ(IReadOnlyList<MeshPiece> block)
    {
        var z = block.Where(p => p.Visible && p.Name.Contains("Chrono"))
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => p.Positions[i * 3 + 2])).ToList();
        return z.Count == 0 ? 16 : (z.Min() + z.Max()) / 2;
    }

    private static MeshPiece Box(string name, Vector3 lo, Vector3 hi)
    {
        var p = new List<float>();
        for (int i = 0; i < 8; i++)
            p.AddRange([(i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z]);
        int[] faces = [0, 2, 3, 1, /* -Z */ 4, 5, 7, 6, /* +Z */ 0, 1, 5, 4, /* -Y */ 2, 6, 7, 3, /* +Y */ 0, 4, 6, 2, /* -X */ 1, 3, 7, 5 /* +X */];
        var idx = new List<int>();
        for (int f = 0; f < faces.Length; f += 4)
            idx.AddRange([faces[f], faces[f + 1], faces[f + 2], faces[f], faces[f + 2], faces[f + 3]]);
        return new MeshPiece(name, HelperMaterial, Visible: false, Collidable: false, p.ToArray(), null, null, null, idx.ToArray());
    }

    public List<MeshPiece> Export(IEnumerable<(string Path, Matrix4x4 Transform)> parts)
    {
        var pieces = new List<MeshPiece>();
        var list = parts.ToList();
        borders = null;
        if (LipAs is not null)
        {
            (borders, collectingBorders) = ([], true);
            foreach (var (path, transform) in list) AddPrefab(path, transform, pieces, depth: 0);
            collectingBorders = false;
        }
        foreach (var (path, transform) in list) AddPrefab(path, transform, pieces, depth: 0);
        return pieces;
    }

    private void AddPrefab(string path, Matrix4x4 transform, List<MeshPiece> pieces, int depth)
    {
        if (LoadPrefab(path) is { } prefab) AddPrefab(prefab, path, transform, pieces, depth);
    }

    private void AddPrefab(CPlugPrefab prefab, string path, Matrix4x4 transform, List<MeshPiece> pieces, int depth)
    {
        if (depth > 6) return;
        var ancestor = Gbx.ParseHeader(path).RefTable?.AncestorLevel ?? 0;
        var label = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));

        for (int i = 0; i < (prefab.Ents?.Length ?? 0); i++)
        {
            var ent = prefab.Ents![i];
            var rot = new Quaternion(ent.Rotation.X, ent.Rotation.Y, ent.Rotation.Z, ent.Rotation.W);
            var local = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(ent.Position.X, ent.Position.Y, ent.Position.Z);
            var world = local * transform;

            switch (ent.Model)
            {
                case CPlugStaticObjectModel som:
                    AddStaticObject(som, world, $"{label}_{i}", pieces);
                    break;
                case CPlugPrefab nested:
                    var nestedPath = ent.ModelFile is { } nf ? ResolveRef(path, ancestor, nf.FilePath) : path;
                    AddPrefab(nested, nestedPath, world, pieces, depth + 1);
                    break;
                case null when ent.ModelFile is { } file:
                    var target = ResolveRef(path, ancestor, file.FilePath);
                    if (target.EndsWith(".Prefab.Gbx", StringComparison.OrdinalIgnoreCase))
                        AddPrefab(target, world, pieces, depth + 1);
                    else
                        Warnings.Add($"skipped {file.FilePath} (not a prefab)");
                    break;
                case { } other:
                    Warnings.Add($"skipped {label} entity {i}: {other.GetType().Name}");
                    break;
            }
        }
    }

    private void AddStaticObject(CPlugStaticObjectModel som, Matrix4x4 world, string label, List<MeshPiece> pieces)
    {
        if (collectingBorders)
        {
            CollectBorders(som, world);
            return;
        }
        if (som.Mesh is { } mesh)
        {
            // best LOD only
            var geoms = mesh.ShadedGeoms ?? [];
            var hasLods = geoms.Any(g => g.LodMask != 0);
            var bestLod = geoms.Select(g => g.LodMask).Where(m => m != 0).Select(m => m & -m).DefaultIfEmpty(1).Min();
            foreach (var (geom, gi) in geoms.Select((g, gi) => (g, gi)).Where(x => x.g.LodMask == 0 ? !hasLods : (x.g.LodMask & bestLod) != 0))
            {
                if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles vis) continue;
                var stream = vis.VertexStreams?.FirstOrDefault();
                if (stream?.Positions is not { } pos || vis.IndexBuffer?.Indices is not { } idx) continue;

                var source = MaterialName(mesh, geom.MaterialIndex);
                if (MapMaterial(source) is "") continue;
                if (KeepMaterial is { } keep && !keep(source)) continue;
                var worldPos = Transform(pos, world);
                idx = KeptFaces(worldPos, idx);
                var lipAs = LipAs?.Invoke(source);
                int[] lip = [];
                if (lipAs is not null) (idx, lip) = SplitLip(worldPos, idx);
                if (lip.Length > 0 && lipAs is not "")
                {
                    // the game gives the hidden lip no lightmap space
                    pieces.Add(WithProjectedLightmap(new MeshPiece($"_notcollidable_{label}_v{gi}_lip", lipAs!, Visible: true, Collidable: false,
                        Lifted(worldPos, lip), stream.Normals is { } ln ? TransformNormals(ln, world) : null, null, null, lip)));
                }
                if (idx.Length == 0) continue;
                var positions = Lifted(worldPos, idx);
                var piece = new MeshPiece(
                    $"_notcollidable_{label}_v{gi}", MapMaterial(source), Visible: true, Collidable: false,
                    positions, stream.Normals is { } n ? TransformNormals(n, world) : null,
                    SnowLaidOut(source) ? PlainSnowUv(positions) : PlatformLayout || PlatformLaidOut.Contains(source) ? PlatformUv(positions) : Uv(stream, 0),
                    Uv(stream, 1), idx, Colors(stream));
                if (Lift != 0) piece = WithSkirt(piece, worldPos);
                pieces.Add(piece.Uv1 is null && !source.StartsWith("Decal") ? WithProjectedLightmap(piece) : piece);
            }
        }

        if (som.Shape is CPlugSurface { Surf: CPlugSurface.Mesh surf } shape && surf.Vertices is { } verts && surf.Triangles is { } tris)
        {
            var worldVerts = Transform(verts, world);
            var positions = Lifted(worldVerts, KeptFaces(worldVerts, tris.SelectMany(t => new[] { t.Indices.X, t.Indices.Y, t.Indices.Z }).ToArray()));
            foreach (var group in tris.GroupBy(t => t.SurfaceIndex))
            {
                var name = shape.Materials is { } mats && group.Key < mats.Length
                    ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mats[group.Key].MaterialFile?.FilePath ?? "Unknown"))
                    : "Unknown";
                if (MapMaterial(name) is "" || KeepMaterial is { } keep && !keep(name)) continue;
                var kept = KeptFaces(worldVerts, group.SelectMany(t => new[] { t.Indices.X, t.Indices.Y, t.Indices.Z }).ToArray());
                if (kept.Length == 0) continue;
                var gameplay = group.GroupBy(t => t.U03).MaxBy(g => g.Count())!.Key;
                pieces.Add(new MeshPiece(
                    $"_notvisible_{label}_c{group.Key}" + (gameplay == 0 ? "" : $"_g{(CPlugSurface.GameplayId)gameplay}"), MapMaterial(name), Visible: false, Collidable: true,
                    positions, null, null, null, kept));
            }
        }
    }

    // platform UVs for roads, plain-snow UVs for snow
    private bool SnowLaidOut(string source) => source != "DecoHill" && MapMaterial(source) == "PlatformIce_DecoHill";

    private static readonly HashSet<string> PlatformLaidOut = ["RoadTech", "RoadBump", "RoadDirt", "ThemeSnowRoad"];

    private static float[] PlatformUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 1 - positions[3 * i + 2] / 32);
        return uv;
    }

    // avoid the texture's blue band
    private static float[] PlainSnowUv(float[] positions)
    {
        var uv = new float[positions.Length / 3 * 2];
        float length = 32;
        for (int i = 2; i < positions.Length; i += 3) length = MathF.Max(length, MathF.Ceiling(positions[i] / 32) * 32);
        for (int i = 0; i < uv.Length / 2; i++) (uv[2 * i], uv[2 * i + 1]) = (positions[3 * i] / 32, 0.3f + 0.4f * positions[3 * i + 2] / length);
        return uv;
    }

    // lightmap UVs for surfaces without
    private static MeshPiece WithProjectedLightmap(MeshPiece piece)
    {
        var (pos, idx) = (piece.Positions, piece.Indices);
        Vector3 At(int i) => new(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
        var remap = new Dictionary<(int Vertex, int Patch), int>();
        var (positions, normals, uv0, uv1, colors) = (new List<float>(), new List<float>(), new List<float>(), new List<float>(), new List<float>());
        var indices = new int[idx.Length];
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var n = Vector3.Cross(At(idx[t + 1]) - At(idx[t]), At(idx[t + 2]) - At(idx[t]));
            var (ax, ay, az) = (MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));
            var patch = ay >= ax && ay >= az ? (n.Y >= 0 ? 0 : 1) : ax >= az ? (n.X >= 0 ? 2 : 3) : (n.Z >= 0 ? 4 : 5);
            for (int k = 0; k < 3; k++)
            {
                var v = idx[t + k];
                if (!remap.TryGetValue((v, patch), out var nv))
                {
                    nv = remap[(v, patch)] = positions.Count / 3;
                    positions.AddRange(pos.AsSpan(3 * v, 3));
                    if (piece.Normals is { } pn) normals.AddRange(pn.AsSpan(3 * v, 3));
                    if (piece.Uv0 is { } u0) uv0.AddRange(u0.AsSpan(2 * v, 2));
                    if (piece.Colors is { } c) colors.AddRange(c.AsSpan(4 * v, 4));
                    var p = At(v) / 32;
                    var (u, w) = patch switch { 0 or 1 => (p.X, p.Z), 2 or 3 => (p.Z, p.Y), _ => (p.X, p.Y) };
                    uv1.AddRange([u + patch * 8, w]);
                }
                indices[t + k] = nv;
            }
        }
        return piece with
        {
            Positions = [.. positions],
            Normals = piece.Normals is null ? null : [.. normals],
            Uv0 = piece.Uv0 is null ? null : [.. uv0],
            Uv1 = [.. uv1],
            Indices = indices,
            Colors = piece.Colors is null ? null : [.. colors],
        };
    }

    private void CollectBorders(CPlugStaticObjectModel som, Matrix4x4 world)
    {
        if (som.Mesh is not { } mesh) return;
        var geoms = mesh.ShadedGeoms ?? [];
        var hasLods = geoms.Any(g => g.LodMask != 0);
        var bestLod = geoms.Select(g => g.LodMask).Where(m => m != 0).Select(m => m & -m).DefaultIfEmpty(1).Min();
        foreach (var geom in geoms.Where(g => g.LodMask == 0 ? !hasLods : (g.LodMask & bestLod) != 0))
        {
            if (!MaterialName(mesh, geom.MaterialIndex).Contains("Border")) continue;
            if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles { VertexStreams: [{ Positions: { } pos }, ..], IndexBuffer.Indices: { } idx }) continue;
            var p = Transform(pos, world);
            Vector3 At(int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
            for (int t = 0; t + 2 < idx.Length; t += 3) borders!.Add((At(idx[t]), At(idx[t + 1]), At(idx[t + 2])));
        }
    }

    // faces with a corner on a border's surface, as tall as a lip
    private (int[] Others, int[] Lip) SplitLip(float[] positions, int[] indices)
    {
        if (borders is not { Count: > 0 }) return (indices, []);
        Vector3 At(int i) => new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        bool OnBorder(Vector3 q) => borders.Any(b => DistanceToTriangle(q, b.A, b.B, b.C) < LipTouch);
        var touching = new Dictionary<int, bool>();
        bool Touches(int v) => touching.TryGetValue(v, out var on) ? on : touching[v] = OnBorder(At(v));
        var (rest, lip) = (new List<int>(), new List<int>());
        bool LipHigh(int t)
        {
            var ys = indices[t..(t + 3)].Select(v => positions[3 * v + 1]).ToArray();
            // steep only: sloped road beside a border rises as much
            var n = Vector3.Normalize(Vector3.Cross(At(indices[t + 1]) - At(indices[t]), At(indices[t + 2]) - At(indices[t])));
            return ys.Max() - ys.Min() is >= LipMinHeight and <= LipMaxHeight && MathF.Abs(n.Y) < LipMaxNormalY;
        }
        for (int t = 0; t + 2 < indices.Length; t += 3)
            (LipHigh(t) && (Touches(indices[t]) || Touches(indices[t + 1]) || Touches(indices[t + 2])) ? lip : rest).AddRange(indices[t..(t + 3)]);
        return ([.. rest], [.. lip]);
    }

    // closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5)
    private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var (ab, ac, ap) = (b - a, c - a, p - a);
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)));
        var denom = 1 / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
    }

    private int[] KeptFaces(float[] positions, int[] indices)
    {
        if (KeepFace is null) return indices;
        Vector3 At(int i) => new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        return Enumerable.Range(0, indices.Length / 3).Select(t => 3 * t)
            .Where(t => Vector3.Cross(At(indices[t + 1]) - At(indices[t]), At(indices[t + 2]) - At(indices[t])) is var n
                && n.LengthSquared() > 0 && KeepFace(Vector3.Normalize(n)))
            .SelectMany(t => indices[t..(t + 3)]).ToArray();
    }

    // along the normal, not straight up (no ledge at borders)
    private float[] Lifted(float[] positions, int[] indices)
    {
        if (Lift == 0) return positions;
        var normals = new Vector3[positions.Length / 3];
        Vector3 At(int v) => new(positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            var n = Vector3.Cross(At(b) - At(a), At(c) - At(a));
            if (Vector3.Dot(n, Up) < 0) n = -n;
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        var lifted = (float[])positions.Clone();
        for (int v = 0; v < normals.Length; v++)
        {
            if (normals[v] == Vector3.Zero) continue;
            var d = Vector3.Normalize(normals[v]) * Lift;
            (lifted[3 * v], lifted[3 * v + 1], lifted[3 * v + 2]) = (lifted[3 * v] + d.X, lifted[3 * v + 1] + d.Y, lifted[3 * v + 2] + d.Z);
        }
        return lifted;
    }

    // closes the lift gap at open edges, facing out
    private static MeshPiece WithSkirt(MeshPiece piece, float[] unlifted)
    {
        var (pos, idx) = (piece.Positions, piece.Indices);
        Vector3 At(float[] p, int v) => new(p[3 * v], p[3 * v + 1], p[3 * v + 2]);
        (int, int, int) Key(int v) => ((int)MathF.Round(unlifted[3 * v] * 1000), (int)MathF.Round(unlifted[3 * v + 1] * 1000), (int)MathF.Round(unlifted[3 * v + 2] * 1000));
        var edges = new Dictionary<((int, int, int), (int, int, int)), (int A, int B, int C, int Count)>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var (a, b, c) = (idx[t + k], idx[t + (k + 1) % 3], idx[t + (k + 2) % 3]);
                var (ka, kb) = (Key(a), Key(b));
                if (ka == kb) continue;
                var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
                edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : (a, b, c, 1);
            }
        var open = edges.Values.Where(e => e.Count == 1).ToList();
        if (open.Count == 0) return piece;
        var (positions, normals, uv0, uv1, colors) = (new List<float>(pos), piece.Normals is { } n0 ? new List<float>(n0) : null,
            piece.Uv0 is { } u0 ? new List<float>(u0) : null, piece.Uv1 is { } u1 ? new List<float>(u1) : null, piece.Colors is { } c0 ? new List<float>(c0) : null);
        var indices = new List<int>(idx);
        foreach (var (a, b, c, _) in open)
        {
            var (ta, tb) = (At(pos, a), At(pos, b));
            var edge = tb - ta;
            var outward = At(pos, c) - ta;
            outward -= edge * Vector3.Dot(outward, edge) / edge.LengthSquared();
            outward = -Vector3.Normalize(outward);
            var first = positions.Count / 3;
            foreach (var (v, p) in new[] { (a, ta), (b, tb), (b, At(unlifted, b)), (a, At(unlifted, a)) })
            {
                positions.AddRange([p.X, p.Y, p.Z]);
                normals?.AddRange([outward.X, outward.Y, outward.Z]);
                uv0?.AddRange(piece.Uv0!.AsSpan(2 * v, 2));
                uv1?.AddRange(piece.Uv1!.AsSpan(2 * v, 2));
                colors?.AddRange(piece.Colors!.AsSpan(4 * v, 4));
            }
            var face = Vector3.Cross(tb - ta, At(unlifted, b) - ta);
            indices.AddRange(Vector3.Dot(face, outward) >= 0 ? [first, first + 1, first + 2, first, first + 2, first + 3] : [first, first + 2, first + 1, first, first + 3, first + 2]);
        }
        return piece with
        {
            Positions = [.. positions], Normals = normals?.ToArray(), Uv0 = uv0?.ToArray(), Uv1 = uv1?.ToArray(), Colors = colors?.ToArray(), Indices = [.. indices],
        };
    }

    private string MapMaterial(string name)
    {
        if (materialMap.TryGetValue(name, out var mapped)) return mapped;
        Warnings.Add($"material {name} has no mapping; using it as-is");
        return name;
    }

    private static string MaterialName(CPlugSolid2Model mesh, int index)
    {
        if (mesh.Materials is { } mats && index < mats.Length && mats[index] is { } ext && ext.File?.FilePath is { } fp)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fp));
        if (mesh.Materials is { } embedded && index < embedded.Length && embedded[index]?.Node?.ShaderFile?.FilePath is { } shader)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(shader));
        if (mesh.MaterialIds is { } ids && index < ids.Length) return ids[index];
        return "Unknown";
    }

    private static float[]? Colors(CPlugVertexStream stream)
        => stream.Colors is { } sets && sets.TryGetValue(0, out var c)
            ? c.SelectMany(argb => new[] { (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, (argb >> 24) & 0xFF }.Select(b => b / 255f)).ToArray()
            : null;

    private static float[]? Uv(CPlugVertexStream stream, int set)
        => stream.UVs is { } uvs && uvs.TryGetValue(set, out var uv) ? uv.SelectMany(v => new[] { v.X, v.Y }).ToArray() : null;

    private static float[] Transform(Vec3[] points, Matrix4x4 m)
        => points.SelectMany(p => { var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m); return new[] { v.X, v.Y, v.Z }; }).ToArray();

    private static float[] TransformNormals(Vec3[] normals, Matrix4x4 m)
        => normals.SelectMany(p => { var v = Vector3.Normalize(Vector3.TransformNormal(new Vector3(p.X, p.Y, p.Z), m)); return new[] { v.X, v.Y, v.Z }; }).ToArray();

    private CPlugPrefab? LoadPrefab(string path)
    {
        if (prefabCache.TryGetValue(path, out var cached)) return cached;
        CPlugPrefab? prefab = null;
        if (!File.Exists(path))
        {
            Warnings.Add($"missing {Path.GetRelativePath(stadiumDir, path)} (not extracted)");
            Missing.Add(RelativeToGameData(path));
        }
        else
        {
            try { prefab = Gbx.ParseNode<CPlugPrefab>(path); }
            catch (Exception e) { Warnings.Add($"cannot read {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }
        return prefabCache[path] = prefab;
    }

    private static string ResolveRef(string fromFile, int ancestorLevel, string refPath)
    {
        var dir = Path.GetDirectoryName(fromFile)!;
        for (int i = 0; i < ancestorLevel; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/')));
    }

    public static void Write(string path, IEnumerable<MeshPiece> pieces) => File.WriteAllText(path, Serialize(pieces));

    public static string Serialize(IEnumerable<MeshPiece> pieces)
        => JsonSerializer.Serialize(pieces, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
