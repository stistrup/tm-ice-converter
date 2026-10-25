using System.Numerics;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// map -> scene for tools/view_map.py
public sealed class MapView(MeshExport shapes)
{
    private static readonly string GameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;

    private readonly Dictionary<string, List<ScenePiece>?> meshes = [];
    private readonly Dictionary<string, SceneMaterial> materials = [];
    private readonly List<SceneInstance> instances = [];
    private readonly List<SceneInstance> placeholders = [];
    private readonly List<SceneInstance> boxes = [];
    private readonly List<SceneWaypoint> waypoints = [];
    private readonly Dictionary<string, int> skipped = [];
    private readonly Dictionary<string, string> skipReasons = [];
    private readonly HashSet<string> missing = [];

    private readonly ILookup<string, string> materialFiles = Directory
        .EnumerateFiles(GameData, "*.Material.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
        .ToLookup(f => Stem(f), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Missing => missing.Concat(shapes.Missing).ToHashSet();

    public IReadOnlyDictionary<string, int> Skipped => skipped;

    public void Write(CGameCtnChallenge map, string scenePath)
    {
        AddBlocks(map);
        AddItems(map);
        var scene = new
        {
            Map = map.MapName,
            Environment = map.Collection?.ToString(),
            Meshes = meshes.Where(kv => kv.Value is { Count: > 0 }).ToDictionary(kv => kv.Key, kv => kv.Value),
            Instances = instances,
            Placeholders = placeholders,
            Boxes = boxes,
            Waypoints = waypoints,
            Materials = materials,
            Skipped = skipped,
        };
        File.WriteAllText(scenePath, JsonSerializer.Serialize(scene, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private void AddBlocks(CGameCtnChallenge map)
    {
        var grid = new List<(CGameCtnBlock Block, ClipResolver.Placed Placed, int Mobil)>();
        foreach (var block in map.GetBlocks())
        {
            var name = block.Name;
            if (block.IsFree)
            {
                var (p, r) = (block.AbsolutePositionInMap ?? default, block.YawPitchRoll ?? default);
                var free = Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                var all = shapes.ClipSides(name, block.IsGround, Converter.VariantOf(block))
                    .SelectMany(s => s.Clips.Where(c => !c.Vertical).Select(c => MeshExport.ClipKey(s.Bit, c.Index))).ToHashSet();
                Add(block, name, block.IsGround, Converter.VariantOf(block), Converter.MobilOf(block), free, all, Info(block, p));
                continue;
            }

            // draw from the block above
            var (meshName, ground, variant, mobil) = (name, block.IsGround, Converter.VariantOf(block), Converter.MobilOf(block));
            if (name.EndsWith("Pillar") && shapes.FindPlacedBlockParts(name, ground, variant, mobil, new HashSet<int>()) is null
                && shapes.ClipSides(name, ground, variant).Count == 0)
                (meshName, ground, variant, mobil) = (name[..^"Pillar".Length], false, 0, 0);
            var units = shapes.Units(meshName, ground, variant) ?? [new Int3(0, 0, 0)];
            var place = Converter.Placement(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1));
            grid.Add((block, new ClipResolver.Placed(meshName, ground, variant, place), mobil));
        }

        var resolver = new ClipResolver(shapes);
        var asShown = resolver.OfAll(grid.Select(g => g.Block).ToList());
        for (int i = 0; i < grid.Count; i++)
            if (grid[i].Placed.Name == asShown[i].Name)
                grid[i] = grid[i] with { Placed = grid[i].Placed with { Variant = asShown[i].Variant } };

        var shown = resolver.Resolve(grid.Select(g => g.Placed).ToList());
        var drop = Matrix4x4.CreateTranslation(0, -Converter.EnvironmentDrop(map.Collection?.ToString()), 0);
        for (int i = 0; i < grid.Count; i++)
        {
            var (block, placed, mobil) = grid[i];
            var info = Info(block, null);
            if (placed.Name != block.Name) info["drawnAs"] = placed.Name;
            if (placed.Variant != Converter.VariantOf(block)) info["shownVariant"] = placed.Variant;
            Add(block, placed.Name, placed.Ground, placed.Variant, mobil, placed.Place * drop, shown[i], info);
        }
    }

    private static Dictionary<string, object> Info(CGameCtnBlock block, Vec3? freePosition)
    {
        var info = new Dictionary<string, object>
        {
            ["kind"] = "block",
            ["name"] = block.Name,
            ["direction"] = block.Direction.ToString(),
            ["variant"] = Converter.VariantOf(block),
            ["mobil"] = Converter.MobilOf(block),
            ["flags"] = $"0x{block.Flags:X8}",
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
        if (shapes.IsTerrain(meshName)) info["tags"] = info.TryGetValue("tags", out var t) ? $"{t}, terrain" : "terrain";
        var key = $"{meshName}|{(ground ? "g" : "a")}{variant}.{mobil}|{MeshExport.ClipsKey(clips)}";
        if (!meshes.ContainsKey(key))
        {
            var parts = shapes.FindPlacedBlockParts(meshName, ground, variant, mobil, clips);
            meshes[key] = parts is null ? null : Pieces(shapes.Export(parts), shapes.MaterialModifierDir(meshName));
        }
        if (meshes[key] is null)
        {
            // not extracted: box
            Bump(skipped, $"block {meshName}: no mesh, drawn as a box");
            var units = shapes.Units(meshName, ground, variant) ?? [new Int3(0, 0, 0)];
            info["units"] = new[] { units.Max(u => u.X) + 1, units.Max(u => u.Y) + 1, units.Max(u => u.Z) + 1 };
            boxes.Add(new SceneInstance(meshName, Flatten(place), info));
            return;
        }
        if (meshes[key]!.Count == 0) return;
        instances.Add(new SceneInstance(key, Flatten(place), info));
        if (shapes.Waypoint(block.Name) is { } w) waypoints.Add(new SceneWaypoint(w, key, Flatten(place)));
    }

    private void AddItems(CGameCtnChallenge map)
    {
        foreach (var item in map.AnchoredObjects ?? [])
        {
            var id = item.ItemModel.Id;
            var (p, r, pivot) = (item.AbsolutePositionInMap, item.YawPitchRoll, item.PivotPosition);
            var place = Matrix4x4.CreateTranslation(-pivot.X, -pivot.Y, -pivot.Z)
                * Matrix4x4.CreateScale(item.Scale is > 0 and var s ? s : 1)
                * Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z)
                * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
            var key = "item:" + id;
            string? waypoint = null;
            var info = new Dictionary<string, object> { ["kind"] = "item", ["name"] = id, ["position"] = new[] { p.X, p.Y, p.Z } };
            if (id.StartsWith("IceConverter\\"))
            {
                var name = id[(id.LastIndexOf('\\') + 1)..^".Item.gbx".Length];
                if (!meshes.ContainsKey(key))
                {
                    var json = Path.Combine(Paths.Work, "Mesh", name + ".pieces.json");
                    meshes[key] = File.Exists(json) ? Pieces(ItemBuilder.LoadJsonc<List<MeshPiece>>(json), null) : null;
                }
                waypoint = shapes.Waypoint(name[..name.IndexOf("_Ice")]);
                info["kind"] = "ice item";
                info["sourceBlock"] = name[..name.IndexOf("_Ice")];
            }
            else if (!meshes.ContainsKey(key))
            {
                if (shapes.ItemFile(id) is not { } path) { meshes[key] = null; skipReasons[key] = "not extracted"; }
                else
                {
                    List<MeshPiece>? pieces;
                    string? reason;
                    try { pieces = shapes.ExportItem(path, out reason); }
                    catch (Exception e) { pieces = null; reason = $"can't read ({e.GetType().Name})"; }
                    meshes[key] = pieces is null ? null : Pieces(pieces, null);
                    if (reason is not null) skipReasons[key] = reason;
                }
            }
            if (meshes[key] is not { Count: > 0 })
            {
                placeholders.Add(new SceneInstance(id, Flatten(place), info));
                Bump(skipped, $"item {id}: {skipReasons.GetValueOrDefault(key, "no mesh")}");
                continue;
            }
            instances.Add(new SceneInstance(key, Flatten(place), info));
            if (waypoint is not null) waypoints.Add(new SceneWaypoint(waypoint, key, Flatten(place)));
        }
    }

    private List<ScenePiece> Pieces(IEnumerable<MeshPiece> pieces, string? modifierDir)
        => pieces.Where(p => p.Visible && !p.Name.StartsWith("_socket_") && !p.Name.StartsWith("_trigger_"))
            .Select(p => new ScenePiece(MaterialKey(p.Material, modifierDir), p.Positions, p.Normals, p.Uv0, p.Indices, p.Colors))
            .ToList();

    private string MaterialKey(string name, string? modifierDir)
    {
        string? path = null;
        if (modifierDir is not null && FindFile(modifierDir, name + ".Material.Gbx") is { } m) path = m;
        else if (name.IndexOf('_') is > 0 and var u
            && FindFile(Path.Combine(Paths.ExtractedStadium, "Media", "Modifier", name[..u]), name[(u + 1)..] + ".Material.Gbx") is { } i) path = i;
        else path = materialFiles[name].OrderBy(f => f.Contains("/Media/Material/") ? 0 : 1).FirstOrDefault();

        var key = path is null ? name : shapes.RelativeToGameData(path);
        if (!materials.ContainsKey(key)) materials[key] = path is null ? new SceneMaterial(name, null, []) : Material(name, path);
        return key;
    }

    private SceneMaterial Material(string name, string path)
    {
        var header = Gbx.ParseHeader(path);
        var ancestor = header.RefTable?.AncestorLevel ?? 0;
        var refs = (header.RefTable?.Files ?? []).Select(f => f.FilePath).ToList();
        var shader = refs.FirstOrDefault(f => f.EndsWith(".Material.gbx", StringComparison.OrdinalIgnoreCase)) is { } sh ? Stem(sh) : null;
        var textures = new List<SceneTexture>();
        foreach (var tex in refs.Where(f => f.EndsWith(".Texture.gbx", StringComparison.OrdinalIgnoreCase)))
        {
            var texPath = Resolve(path, ancestor, tex);
            var stem = Stem(texPath);
            var role = stem[(stem.LastIndexOf('_') + 1)..];
            string? image = null;
            if (File.Exists(texPath))
            {
                var texHeader = Gbx.ParseHeader(texPath);
                if (texHeader.RefTable?.Files.FirstOrDefault()?.FilePath is { } img)
                    image = Resolve(texPath, texHeader.RefTable.AncestorLevel, img);
            }
            else missing.Add(shapes.RelativeToGameData(texPath));
            image ??= Path.Combine(Path.GetDirectoryName(texPath)!, "Image", stem + ".dds");
            if (!File.Exists(image)) { missing.Add(shapes.RelativeToGameData(image)); image = null; }
            textures.Add(new SceneTexture(role, stem, image));
        }
        return new SceneMaterial(name, shader, textures);
    }

    private static string? FindFile(string dir, string fileName)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, fileName, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault()
            : null;

    private static string Resolve(string fromFile, int ancestorLevel, string refPath)
    {
        var dir = Path.GetDirectoryName(fromFile)!;
        for (int i = 0; i < ancestorLevel; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/')));
    }

    private static string Stem(string path) => Path.GetFileName(path).Split('.')[0];

    private static float[] Flatten(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static void Bump(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}

public sealed record ScenePiece(string Material, float[] Positions, float[]? Normals, float[]? Uv0, int[] Indices, float[]? Colors);

// M is row-major (world = local * M)
public sealed record SceneInstance(string Mesh, float[] M, Dictionary<string, object> Info);

public sealed record SceneWaypoint(string Kind, string Mesh, float[] M);

public sealed record SceneMaterial(string Name, string? Shader, List<SceneTexture> Textures);

public sealed record SceneTexture(string Role, string Name, string? Image);
