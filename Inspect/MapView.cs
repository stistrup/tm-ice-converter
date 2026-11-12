using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.Plug;

namespace TmSurfaceEditor;

// map -> scene for tools/view_map.py
public sealed class MapView(BlockCatalog shapes)
{
    private static readonly string GameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;

    private readonly MeshExporter exporter = new(shapes.Files, new Dictionary<string, string>()) { DynaObjects = true, VegetTrees = true };
    private readonly Dictionary<string, List<ScenePiece>?> meshes = [];
    private readonly SceneMaterials materials = new(shapes);
    private readonly List<SceneInstance> instances = [];
    private readonly List<SceneInstance> placeholders = [];
    private readonly List<SceneInstance> boxes = [];
    private readonly List<SceneWaypoint> waypoints = [];
    private readonly Dictionary<string, int> skipped = [];
    // not drawn because the game doesn't draw them either
    private readonly Dictionary<string, int> hidden = [];
    private readonly Dictionary<string, string> skipReasons = [];
    private readonly Dictionary<CGameCtnBlock, int> blockIndex = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<string> Missing => materials.Missing.Concat(shapes.Missing).ToHashSet();

    // an importer material as our items use it (no vista restyle)
    public SceneMaterial OwnMaterial(string name) => materials.Own(name);

    public IReadOnlyDictionary<string, int> Skipped => skipped;
    public IReadOnlyDictionary<string, int> Hidden => hidden;

    public void Write(CGameCtnChallenge map, string scenePath)
        => File.WriteAllText(scenePath, JsonSerializer.Serialize(Build(map), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    public SceneData Build(CGameCtnChallenge map)
    {
        this.map = map;
        materials.ForCollection(map.Collection?.ToString());
        AddBlocks(map);
        AddItems(map);
        return new SceneData(map.MapName, map.Collection?.ToString(),
            meshes.Where(kv => kv.Value is { Count: > 0 }).ToDictionary(kv => kv.Key, kv => kv.Value!),
            instances, placeholders, boxes, waypoints, materials.All, skipped);
    }

    private sealed record GridBlock(CGameCtnBlock Block, ClipResolver.Placed Placed, int Mobil, Int3[] Cells);

    private void AddBlocks(CGameCtnChallenge map)
    {
        // the map's own order, for the conversion preview
        blockIndex.Clear();
        foreach (var (i, b) in map.GetBlocks().Index()) blockIndex[b] = i;
        var grid = new List<GridBlock>();
        foreach (var block in map.GetBlocks())
        {
            if (block.IsFree) AddFree(block);
            else grid.Add(OnGrid(block));
        }
        var resolver = new ClipResolver(shapes);
        var asShown = resolver.OfAll(grid.Select(g => g.Block).ToList());
        for (int i = 0; i < grid.Count; i++)
            if (grid[i].Placed.Name == asShown[i].Name)
                grid[i] = grid[i] with { Placed = grid[i].Placed with { Variant = asShown[i].Variant } };
        var shown = resolver.Resolve(grid.Select(g => g.Placed).ToList());
        var drop = Matrix4x4.CreateTranslation(0, -Placement.EnvironmentDrop(map.Collection?.ToString()), 0);
        // a placed native ground block draws its own ground and replaces the terrain in its cells (the game hides it);
        // our custom blocks carry no ground, so the terrain shows, except on hills and cliffs, whose bodies keep the terrain's faces
        var built = grid.Where(g => g.Block.IsGround && !shapes.IsTerrain(g.Block.Name) && OwnsGround(g.Block.Name)).SelectMany(g => g.Cells).ToHashSet();
        for (int i = 0; i < grid.Count; i++)
        {
            var (block, placed, mobil, cells) = grid[i];
            if (onlyBlocks?.Contains(block) == false) continue;
            if (shapes.IsTerrain(block.Name) && cells.Any(built.Contains))
            {
                Bump(hidden, $"block {block.Name}: terrain under a placed block");
                continue;
            }
            var info = Info(block, null);
            if (placed.Name != block.Name) info["drawnAs"] = placed.Name;
            if (placed.Variant != BlockFlags.VariantOf(block)) info["shownVariant"] = placed.Variant;
            Add(block, placed.Name, placed.Ground, placed.Variant, mobil, placed.Place * drop, shown[i], info);
        }
    }

    private void AddFree(CGameCtnBlock block)
    {
        var (p, r) = (block.AbsolutePositionInMap ?? default, block.YawPitchRoll ?? default);
        var free = Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
        var all = BlockKinds.DrawnFreeClips(shapes, block);
        if (onlyBlocks?.Contains(block) != false)
            Add(block, block.Name, block.IsGround, BlockFlags.VariantOf(block), BlockFlags.MobilOf(block), free, all, Info(block, p));
    }

    // as the clip resolver sees it: a body-less pillar draws from the block above, a custom block clips as its archetype
    private GridBlock OnGrid(CGameCtnBlock block)
    {
        var name = block.Name;
        var (meshName, ground, variant, mobil) = (name, block.IsGround, BlockFlags.VariantOf(block), BlockFlags.MobilOf(block));
        if (name.EndsWith("Pillar") && shapes.FindPlacedBlockParts(name, ground, variant, mobil, new HashSet<int>()) is null
            && shapes.ClipSides(name, ground, variant).Count == 0)
            (meshName, ground, variant, mobil) = (name[..^"Pillar".Length], false, 0, 0);
        // the game draws that block's clips around our body
        if (CustomBlockMesh.Parse(meshName) is { } custom) meshName = custom.Archetype;
        var units = shapes.Units(meshName, ground, variant) ?? [new Int3(0, 0, 0)];
        var place = Placement.Of(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1));
        var cells = units.Select(u => Placement.CellAt(Vector3.Transform(new Vector3(u.X * 32 + 16, u.Y * 8 + 4, u.Z * 32 + 16), place))).ToArray();
        return new GridBlock(block, new ClipResolver.Placed(meshName, ground, variant, place), mobil, cells);
    }

    // the shown clips of a custom block's archetype: the game still draws them (terrain ones included)
    private void AddClipsOf(string source, bool ground, int variant, int mobil, Matrix4x4 place, HashSet<int> clips, Dictionary<string, object> info)
    {
        if (clips.Count == 0) return;
        var key = $"clipsof:{source}|{(ground ? "g" : "a")}{variant}.{mobil}|{ClipKey.OfSet(clips)}";
        if (!meshes.ContainsKey(key))
        {
            var body = shapes.FindPlacedBlockParts(source, ground, variant, mobil, new HashSet<int>())?.ToHashSet() ?? [];
            var all = shapes.FindPlacedBlockParts(source, ground, variant, mobil, clips);
            meshes[key] = all is null ? null : Pieces(exporter.Export(all.Where(p => !body.Contains(p)).ToList()), shapes.MaterialModifierOf(source));
        }
        if (meshes[key] is not { Count: > 0 }) return;
        instances.Add(new SceneInstance(key, Flatten(place), new Dictionary<string, object>(info) { ["kind"] = "block", ["name"] = source, ["drawnAs"] = "clips" }));
    }

    private static bool OwnsGround(string block) => CustomBlockMesh.Parse(block) is not { } custom || BlockNames.OnTerrainShape(custom.Source);

    private Dictionary<string, object> Info(CGameCtnBlock block, Vec3? freePosition)
    {
        var info = new Dictionary<string, object>
        {
            ["kind"] = "block",
            ["block"] = blockIndex[block],
            ["name"] = block.Name,
            ["direction"] = block.Direction.ToString(),
            ["variant"] = BlockFlags.VariantOf(block),
            ["mobil"] = BlockFlags.MobilOf(block),
            ["flags"] = $"0x{block.Flags:X8}",
            ["color"] = (int)block.Color,
        };
        if (freePosition is { } p) info["position"] = new[] { p.X, p.Y, p.Z };
        else info["cell"] = new[] { block.Coord.X, block.Coord.Y, block.Coord.Z };
        var tags = new List<string>();
        if (block.IsFree) tags.Add("free");
        if (block.IsGround) tags.Add("ground");
        if (block.IsPillar) tags.Add("pillar");
        if (block.IsClip) tags.Add("clip");
        if ((block.Flags & 0x10000000) != 0) tags.Add("ghost");
        if (block.WaypointSpecialProperty?.Tag is { } tag) tags.Add(tag);
        if (tags.Count > 0) info["tags"] = string.Join(", ", tags);
        return info;
    }

    private void Add(CGameCtnBlock block, string meshName, bool ground, int variant, int mobil, Matrix4x4 place, HashSet<int> clips,
        Dictionary<string, object> info)
    {
        if (AddCustomBlock(block.Name, place, info))
        {
            if (CustomBlockMesh.Parse(block.Name) is not { Cover: true }) AddClipsOf(meshName, ground, variant, mobil, place, clips, info);
            return;
        }
        if (shapes.IsTerrain(meshName)) info["tags"] = info.TryGetValue("tags", out var t) ? $"{t}, terrain" : "terrain";
        var key = $"{meshName}|{(ground ? "g" : "a")}{variant}.{mobil}|{ClipKey.OfSet(clips)}";
        if (!meshes.ContainsKey(key))
        {
            var parts = shapes.FindPlacedBlockParts(meshName, ground, variant, mobil, clips);
            meshes[key] = parts is null ? null : Pieces(exporter.Export(parts), shapes.MaterialModifierOf(meshName));
        }
        // a body-less block (pillar) is only its clips: with all of them hidden the game draws nothing there either
        if (meshes[key] is null && clips.Count == 0 && shapes.ClipSides(meshName, ground, variant).Count > 0)
        {
            Bump(hidden, $"block {meshName}: only clips, all hidden");
            return;
        }
        if (meshes[key] is null)
        {
            // not extracted: box
            Bump(skipped, $"block {meshName}: no mesh, drawn as a box");
            var units = shapes.Units(meshName, ground, variant) ?? [new Int3(0, 0, 0)];
            info["units"] = new[] { units.Max(u => u.X) + 1, units.Max(u => u.Y) + 1, units.Max(u => u.Z) + 1 };
            // why there's no mesh: a body-less block draws only the clips the resolver shows
            info["clipSides"] = shapes.ClipSides(meshName, ground, variant).Sum(c => c.Clips.Count);
            info["shownClips"] = clips.Count;
            boxes.Add(new SceneInstance(meshName, Flatten(place), info));
            return;
        }
        if (meshes[key]!.Count == 0) return;
        instances.Add(new SceneInstance(key, Flatten(place), info));
        if (shapes.Waypoint(block.Name) is { } w) waypoints.Add(new SceneWaypoint(w, key, Flatten(place)));
    }

    private bool AddCustomBlock(string blockName, Matrix4x4 place, Dictionary<string, object> info)
    {
        if (CustomBlockMesh.Parse(blockName) is not { } custom) return false;
        var key = "item:" + custom.Item;
        if (!meshes.ContainsKey(key))
        {
            var json = Path.Combine(Paths.Pieces, custom.Item + ".pieces.json");
            meshes[key] = File.Exists(json) ? Pieces(Jsonc.Load<List<MeshPiece>>(json), null, ours: true) : null;
        }
        // not built here (someone else's map, a fresh install): the block the map carries
        if (meshes[key] is not { Count: > 0 })
        {
            key = "embedded:" + blockName;
            if (!meshes.ContainsKey(key)) meshes[key] = EmbeddedPieces(blockName[..^"_CustomBlock".Length]);
        }
        if (meshes[key] is not { Count: > 0 }) return false;
        info["kind"] = "custom block";
        info["sourceBlock"] = custom.Source;
        if (custom.Stacked) info["stacked"] = true;
        if (shapes.IsTerrain(custom.Source)) info["tags"] = info.TryGetValue("tags", out var t) ? $"{t}, terrain" : "terrain";
        instances.Add(new SceneInstance(key, Flatten(place), info));
        if (shapes.Waypoint(custom.Source) is { } w) waypoints.Add(new SceneWaypoint(w, key, Flatten(place)));
        return true;
    }

    // only some of a map's pieces (the ones a conversion changes), for the preview; only what has a real mesh
    public SceneData BuildPart(CGameCtnChallenge map, IReadOnlySet<CGameCtnBlock> blocks, IReadOnlySet<CGameCtnAnchoredObject> items)
    {
        this.map = map;
        materials.ForCollection(map.Collection?.ToString());
        onlyBlocks = blocks;
        AddBlocks(map);
        AddItems(map, items);
        return new SceneData(map.MapName, map.Collection?.ToString(),
            meshes.Where(kv => kv.Value is { Count: > 0 }).ToDictionary(kv => kv.Key, kv => kv.Value!),
            instances, [], [], waypoints, materials.All, skipped);
    }

    private IReadOnlySet<CGameCtnBlock>? onlyBlocks;

    private CGameCtnChallenge? map;
    // the map's embedded items and blocks by name (SurfaceEditor\X.Item.gbx), unpacked once into the cache
    private Dictionary<string, string>? embedded;

    private List<ScenePiece>? EmbeddedPieces(string name)
    {
        if (EmbeddedFile(name) is not { } path) return null;
        // importer materials, drawn as our cached pieces are
        try { return exporter.ExportItem(path, out _) is { } pieces ? Pieces(pieces, null, ours: true) : null; }
        catch (Exception) { return null; }
    }

    private string? EmbeddedFile(string name)
    {
        if (embedded is null)
        {
            embedded = new(StringComparer.OrdinalIgnoreCase);
            if (map?.EmbeddedZipData is { Length: > 0 } data)
            {
                var dir = Path.GetFullPath(Path.Combine(Paths.Cache, "Embedded", Convert.ToHexString(SHA256.HashData(data))[..16]));
                using var zip = new ZipArchive(new MemoryStream(data));
                foreach (var entry in zip.Entries.Where(e => e.Length > 0))
                {
                    var file = Path.GetFullPath(Path.Combine(dir, entry.FullName.Replace('\\', '/')));
                    if (!file.StartsWith(dir + Path.DirectorySeparatorChar)) continue;
                    if (!File.Exists(file))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        entry.ExtractToFile(file);
                    }
                    // under Items/, blocks too
                    var key = entry.FullName.Replace('/', '\\');
                    embedded[key.StartsWith("Items\\", StringComparison.OrdinalIgnoreCase) ? key["Items\\".Length..] : key] = file;
                }
            }
        }
        return embedded.GetValueOrDefault(name);
    }

    private void AddItems(CGameCtnChallenge map, IReadOnlySet<CGameCtnAnchoredObject>? only = null)
    {
        foreach (var (itemIndex, item) in (map.AnchoredObjects ?? []).Index())
        {
            if (only is not null && !only.Contains(item)) continue;
            var id = item.ItemModel.Id;
            var p = item.AbsolutePositionInMap;
            var place = Placement.Of(item);
            var variant = (item.Flags & 0x1F00) >> 8;
            var key = "item:" + id + (variant > 0 && !Paths.IsOurs(id) ? $"#{variant}" : "");
            string? waypoint = null;
            var info = new Dictionary<string, object> { ["kind"] = "item", ["item"] = itemIndex, ["name"] = id, ["position"] = new[] { p.X, p.Y, p.Z }, ["color"] = (int)item.Color };
            if (Paths.IsOurs(id))
            {
                var name = id[(id.LastIndexOf('\\') + 1)..^".Item.gbx".Length];
                if (!meshes.ContainsKey(key))
                {
                    var json = Path.Combine(Paths.Pieces, name + ".pieces.json");
                    meshes[key] = File.Exists(json) ? Pieces(Jsonc.Load<List<MeshPiece>>(json), null, ours: true) : EmbeddedPieces(id);
                }
                waypoint = shapes.Waypoint(ItemRecipe.SourceBlock(name, Theme.Default));
                info["kind"] = "ice item";
                info["sourceBlock"] = ItemRecipe.SourceBlock(name, Theme.Default);
            }
            else if (!meshes.ContainsKey(key))
            {
                if (shapes.ItemFile(id) is not { } path) { meshes[key] = null; skipReasons[key] = "not extracted"; }
                else
                {
                    List<MeshPiece>? pieces;
                    string? reason;
                    try { pieces = exporter.ExportItem(path, out reason, variant); }
                    catch (Exception e) { pieces = null; reason = $"can't read ({e.GetType().Name})"; }
                    meshes[key] = pieces is null ? null : Pieces(pieces, null);
                    if (reason is not null) skipReasons[key] = reason;
                }
            }
            if (meshes[key] is not { Count: > 0 })
            {
                info["missing"] = skipReasons.GetValueOrDefault(key, "no mesh");
                placeholders.Add(new SceneInstance(id, Flatten(place), info));
                Bump(skipped, $"item {id}: {info["missing"]}");
                continue;
            }
            instances.Add(new SceneInstance(key, Flatten(place), info));
            if (waypoint is not null) waypoints.Add(new SceneWaypoint(waypoint, key, Flatten(place)));
        }
    }

    // ours: our blocks and items; their materials are the importer's, the vista doesn't restyle them
    private List<ScenePiece> Pieces(IEnumerable<MeshPiece> pieces, MaterialModifier? modifier, bool ours = false)
        => pieces.Where(p => p.Visible && !p.Name.StartsWith("_socket_") && !p.Name.StartsWith("_trigger_"))
            .Select(p => new ScenePiece(materials.Key(p.Material, modifier, ours), p.Positions, p.Normals, p.Uv0, p.Indices, p.Colors))
            .ToList();

    private static float[] Flatten(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static void Bump(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}
