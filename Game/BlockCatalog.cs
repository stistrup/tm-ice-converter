using System.Numerics;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Plug;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Meta;

namespace TmIceConverter;

// what the game's data says about blocks and items, per environment
public sealed class BlockCatalog
{
    private readonly string stadiumDir;
    private readonly ClipFit fit;
    private readonly Dictionary<string, (CGameCtnBlockInfo Info, string Path, CGameCtnBlockInfoVariant Variant)?> blockCache = [];
    private string? environment;

    public BlockCatalog(string stadiumDir)
    {
        this.stadiumDir = stadiumDir;
        fit = new ClipFit(stadiumDir);
        Files = new GameFiles(stadiumDir);
        Plain = new MeshExporter(Files, new Dictionary<string, string>());
    }

    public GameFiles Files { get; }

    // materials as the game has them
    public MeshExporter Plain { get; }

    public HashSet<string> Missing => Files.Missing;

    public bool RecordMissing { get => Files.RecordMissing; set => Files.RecordMissing = value; }

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

    // air and ground can differ
    public (int X, int Z) BlockSize(string blockName, bool ground, int variantIndex)
    {
        var units = Units(blockName, ground, variantIndex) ?? [];
        return units.Count == 0 ? (1, 1) : (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1);
    }

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
                    if (RecordMissing && GameFiles.ResolveRef(infoPath, refs.AncestorLevel, f.FilePath) is var target && !File.Exists(target))
                        Files.Missing.Add(Files.RelativeToGameData(target));
            var variant = (CGameCtnBlockInfoVariant?)info?.VariantBaseAir ?? info?.VariantBaseGround;
            blockCache[blockName] = cached = info is null || variant is null ? null : (info, infoPath!, variant);
        }
        block = cached ?? default;
        return cached is not null;
    }

    public List<(string Path, Matrix4x4 Transform)>? FindPlacedBlockParts(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> shownClips)
        => TryLoadBlock(blockName, out var block) && PickVariant(block.Info, ground, variantIndex) is { } variant
            ? BlockParts.Of(fit, block.Path, variant, mobil, block.Info.PillarShapeMultiDir.ToString(), (bit, index, _, _) =>
                Enumerable.Range(0, 8).Cast<int?>().FirstOrDefault(column => shownClips.Contains(ClipKey.Of(bit, index, column!.Value))))
            : null;

    public bool IsPillarBlock(string blockName) => TryLoadBlock(blockName, out var block) && block.Info.IsPillar;

    // NoPillarBelowIndex, air only
    public int? NoPillarBelowVariant(string blockName, bool ground, int variantIndex)
        => !ground && TryLoadBlock(blockName, out var block) && PickVariant(block.Info, false, variantIndex) is { NoPillarBelowIndex: var npb }
            && npb != 255 && npb != variantIndex && npb <= (block.Info.AdditionalVariantsAir?.Length ?? 0)
            ? npb : null;

    public IReadOnlyList<ClipSide> ClipSides(string blockName, bool ground, int variantIndex)
    {
        if (!TryLoadBlock(blockName, out var block) || PickVariant(block.Info, ground, variantIndex) is not { } variant) return [];
        var clipOnly = BlockParts.MobilPrefab(variant, block.Path) is null;
        return BlockParts.ClipSlots(variant).Select(s =>
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
                BlockParts.IsVertical(clips[i]), NonEmpty(node?.ClipGroupId), NonEmpty(node?.SymmetricalClipGroupId),
                NonEmpty((node as CGameCtnBlockInfoClipVertical)?.VerticalClipGroupId), NonEmpty(node?.ASymmetricalClipId),
                node?.IsFullFreeClip ?? false, node?.CanBeDeletedByFullFreeClip ?? false, IsAntiClip(node), node?.TopBottomMultiDir.ToString() ?? "SameDir");
        }
    }

    // GBX.NET's U03 is IsAntiClip
    private static bool IsAntiClip(CGameCtnBlockInfoClip? node)
        => node?.Chunks.Get<CGameCtnBlockInfoClip.Chunk03053006>() is { U03: > 0 };

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // a block's TerrainModifier: a material folder plus a game skin listing which materials it swaps
    // (TrackWallToDecoCliff: only TrackWall, from PlatformGrass). GBX.NET can't read the modifier's body; its paths are plain text
    public MaterialModifier? MaterialModifierOf(string blockName)
    {
        if (!TryLoadBlock(blockName, out var block) || block.Info.MaterialModifierFile?.FilePath is not { } file) return null;
        var ancestor = Gbx.ParseHeader(block.Path).RefTable?.AncestorLevel ?? 0;
        var modifierFile = GameFiles.ResolveRef(block.Path, ancestor, file);
        if (!File.Exists(modifierFile)) return null;
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(modifierFile));
        var gameData = Path.GetDirectoryName(stadiumDir)!;
        var folder = Regex.Match(text, @"[A-Za-z0-9_]+\\Media\\Modifier\\[A-Za-z0-9_]+\\") is { Success: true } f
            ? Path.Combine(gameData, f.Value.Replace('\\', '/').TrimEnd('/')) : null;
        var skin = Regex.Match(text, @"([A-Za-z0-9_]+)\.GameSkin\.gbx", RegexOptions.IgnoreCase) is { Success: true } k
            ? Path.Combine(stadiumDir, "GameSkin", k.Groups[1].Value + ".GameSkin.gbx") : null;
        if (folder is null || !Directory.Exists(folder) || skin is null || !File.Exists(skin)) return null;
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fid in Gbx.ParseHeaderNode<CPlugGameSkin>(skin).HeaderFids ?? [])
            if (fid.Directory is { } source)
                swaps.TryAdd(Path.GetFileName(source.Replace('\\', '/')).Split('.')[0], fid.Name.TrimStart('*'));
        return new MaterialModifier(folder, swaps);
    }

    private static CGameCtnBlockInfoVariant? PickVariant(CGameCtnBlockInfo info, bool ground, int index)
    {
        var extra = ground ? info.AdditionalVariantsGround?.Cast<CGameCtnBlockInfoVariant>() : info.AdditionalVariantsAir;
        CGameCtnBlockInfoVariant? baseVariant = ground ? info.VariantBaseGround : info.VariantBaseAir;
        var variant = index > 0 && extra?.ElementAtOrDefault(index - 1) is { } e ? e : baseVariant;
        return variant is { Mobils.Length: > 0 } ? variant : (CGameCtnBlockInfoVariant?)info.VariantBaseAir ?? info.VariantBaseGround;
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
            || BlockParts.MobilPrefab(variant, block.Path) is not { } main) return turns;
        var corners = fit.Corners([main]);
        var ancestor = Gbx.ParseHeader(block.Path).RefTable?.AncestorLevel ?? 0;
        foreach (var (unitOrigin, bit, clips, place, _) in BlockParts.ClipSlots(variant))
        {
            if (bit % 6 < 4) continue;
            for (int i = 0; i < (clips?.Length ?? 0); i++)
                if (clips![i] is { Node: { ASymmetricalClipId.Length: > 0 } node, File: { } file }
                    && ((CGameCtnBlockInfoVariant?)node.VariantBaseAir ?? node.VariantBaseGround) is { } clipVariant
                    && BlockParts.MobilPrefab(clipVariant, GameFiles.ResolveRef(block.Path, ancestor, file.FilePath)) is { } part)
                {
                    // sloped bottoms follow the road; corners can't tell a slope's ends apart
                    var sloped = bit % 6 == 4 ? fit.BottomClipTurn("AllDir", part, main) : Matrix4x4.Identity;
                    turns[(bit, i)] = !sloped.IsIdentity || fit.Slope(part, upOnly: false).Length() >= 0.1f && bit % 6 == 4
                        ? sloped : fit.FittedTopTurn(part, place * Matrix4x4.CreateTranslation(unitOrigin), corners);
                }
        }
        return turns;
    }

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
            var u = ((Waypoints.SpawnPoint.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (Waypoints.SpawnPoint.Z - a.Z)) / d;
            var v = ((b.X - a.X) * (Waypoints.SpawnPoint.Z - a.Z) - (Waypoints.SpawnPoint.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var y = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            if (road is not { } r || y < r.Y) road = (y, n);
        }
        if (road is not { } found) return (Waypoints.SpawnPoint, 0, 0);
        // up = (-sin roll, cos roll cos pitch, cos roll sin pitch), as Matrix4x4.CreateFromYawPitchRoll turns it
        return (Waypoints.SpawnPoint with { Y = found.Y }, MathF.Atan2(found.N.Z, found.N.Y), -MathF.Asin(found.N.X));
    }

    public IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)>? CollisionTriangles(string blockName, bool ground, int variantIndex, int mobil)
    {
        if (FindPlacedBlockParts(blockName, ground, variantIndex, mobil, new HashSet<int>()) is not { } parts) return null;
        var tris = new List<(Vector3, Vector3, Vector3, string)>();
        foreach (var p in Plain.Export(parts).Where(p => p.Collidable))
        {
            Vector3 At(int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
            for (int t = 0; t + 2 < p.Indices.Length; t += 3) tris.Add((At(p.Indices[t]), At(p.Indices[t + 1]), At(p.Indices[t + 2]), p.Material));
        }
        return tris;
    }

    public IReadOnlySet<string>? Materials(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips)
        => FindPlacedBlockParts(blockName, ground, variantIndex, mobil, clips) is { } parts
            ? Plain.Export(parts).Select(p => p.Material).ToHashSet() : null;

    public IReadOnlyList<(string Model, Matrix4x4 Place)> Trees(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips)
    {
        if (FindPlacedBlockParts(blockName, ground, variantIndex, mobil, clips) is not { } parts) return [];
        return Plain.Trees(parts);
    }

    public IReadOnlyList<Int3>? Units(string blockName, bool ground, int variantIndex)
    {
        if (!TryLoadBlock(blockName, out var block)) return null;
        var variant = PickVariant(block.Info, ground, variantIndex) ?? block.Variant;
        return variant.BlockUnitModels is { Length: > 0 } units ? units.Select(u => u.RelativeOffset).ToList() : [new Int3(0, 0, 0)];
    }
}

// swaps: game material name -> the name to load from Folder instead
public sealed record MaterialModifier(string Folder, IReadOnlyDictionary<string, string> Swaps);
