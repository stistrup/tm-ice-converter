using System.Numerics;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

public enum Cover
{
    None,
    Top,
    InTerrain,
    // only if enough concrete faces up
    TopOfIce,
    // effect comes from a gate
    Gated,
    // snow over the native ice twin
    OverTwin,
    // custom ground block: the whole block, its ground plate as the skin would leave it
    GroundBody,
    // ground body over its kept native block: no terrain/Deco faces, the native draws them
    StackedBody,
    // custom block of a kept block: wall faces up as placed iced (as Top / TopOfIce would), the rest as a full item
    TopsBody,
    TopOfIceBody,
    // skin over a native air block (skinsOnly)
    Skin,
    // the visible terrain of a hill/cliff block, beside its custom block (terrain light in the block's own lightmap darkens it)
    TerrainFaces,
}

public interface IItemSource
{
    string? Environment { get; set; }
    bool SnowOnZoneSides { get; set; }
    bool SnowOnGroundDeco { get; set; }
    float SkinLift { get; set; }
    float SkinEdge { get; set; }
    IceItem? For(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, Cover cover = Cover.None, Vector2 tilt = default);
    IceItem? ForItem(string itemId);
    IceBlock? BlockFor(string block, string archetype, bool ground, int variant, int mobil, Id collection, IReadOnlySet<int> baked, Cover? cover = null, Vector2 tilt = default, bool lift = false, int ramp = 0);
    IceBlock? SnowTerrainFor(string block, int variant, int mobil, Id collection) => null;
    IReadOnlyDictionary<string, string> Failures { get; }
}

// block meshes -> FBX (Blender) -> NadeoImporter -> fix-ups
public sealed class ItemBuilder(string dataDir) : IItemSource
{
    // bump to rebuild every item
    private const int PipelineVersion = 15;

    private readonly MeshExport shapes = new(Paths.ExtractedStadium, new Dictionary<string, string>());
    private readonly Dictionary<string, string> failures = [];
    private readonly HashSet<string> failedItems = [];
    private readonly HashSet<string> emptyItems = [];

    private const string NothingToCover = "nothing to cover";
    private string? tools;

    public IReadOnlyDictionary<string, string> Failures => failures;

    public HashSet<string> Missing { get; } = [];

    public string? Environment { get => shapes.Environment; set => shapes.Environment = value; }

    // planning: For() only notes what's needed, BuildPlanned() builds it
    public bool Planning { get; set; }

    private sealed record Request(string Block, bool Ground, int Variant, int Mobil, IReadOnlySet<int> Clips, Cover Cover, Vector2 Tilt, string? Environment, bool ZoneSides, bool TerrainSnow, float SkinLift, float SkinEdge);

    public bool SnowOnZoneSides { get; set; }

    public bool SnowOnGroundDeco { get; set; } = true;

    public float SkinLift { get; set; } = DefaultSkinLift;

    public float SkinEdge { get; set; } = DefaultSkinEdge;

    private readonly Dictionary<string, Request> planned = [];
    // incomplete items never turn current: build once per run
    private readonly HashSet<string> built = [];

    public IceItem? For(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, Cover cover = Cover.None, Vector2 tilt = default)
    {
        var name = ItemName(shapes, block, ground, variant, mobil, clips, cover, tilt, SnowOnZoneSides, SnowOnGroundDeco, SkinLift, SkinEdge);
        if (failedItems.Contains(name)) return null;
        lock (emptyItems) if (emptyItems.Contains(name) || IsKnownEmpty(name) && emptyItems.Add(name)) return IceItem.Nothing;
        if (shapes.Units(block, ground, variant) is not null && shapes.FindPlacedBlockParts(block, ground, variant, mobil, clips) is null)
            return IceItem.Nothing;
        var path = Path.Combine(Paths.Items, name + ".Item.gbx");
        bool builtThisRun;
        lock (built) builtThisRun = built.Contains(name);
        if (!builtThisRun && !IsCurrent(name, path, out var builtFrom))
        {
            if (Planning)
            {
                planned.TryAdd(name, new Request(block, ground, variant, mobil, clips, cover, tilt, Environment, SnowOnZoneSides, SnowOnGroundDeco, SkinLift, SkinEdge));
                return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", new Id(26), "IceConverter"), path, shapes.BlockSize(block, ground, variant));
            }
            var error = Build(block, ground, variant, mobil, clips, builtFrom, cover: cover, tilt: tilt, zoneSides: SnowOnZoneSides, terrainSnow: SnowOnGroundDeco, skinLift: SkinLift, skinEdge: SkinEdge);
            if (error is NothingToCover)
            {
                lock (emptyItems) emptyItems.Add(name);
                WriteEmptyStamp(name);
                return IceItem.Nothing;
            }
            if (error is not null)
            {
                failures[block] = error;
                failedItems.Add(name);
                Console.WriteLine($"  no item for {block}: {error}");
                return null;
            }
        }
        var header = Gbx.ParseHeaderNode<CGameItemModel>(path).Ident;
        return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", header.Collection, header.Author), path, shapes.BlockSize(block, ground, variant));
    }

    // the item's main body plus the baked clips in a custom block of the archetype; the game adds the other clips
    // ramp: local footprint sides (bits -X, +X, -Z, +Z) whose ice rises by the skin lift to meet a skinned neighbour
    public IceBlock? BlockFor(string block, string archetype, bool ground, int variant, int mobil, Id collection, IReadOnlySet<int> baked, Cover? cover = null, Vector2 tilt = default, bool lift = false, int ramp = 0)
    {
        if (CustomBlocks.VariantId(ground, variant, mobil) is not { } variantId
            || For(block, ground, variant, mobil, baked, cover ?? (ground ? lift ? Cover.StackedBody : Cover.GroundBody : Cover.None), tilt) is not { } item || item == IceItem.Nothing) return null;
        // no ice in it: clips draw the surface (penalty slopes), the game would keep them native
        if (File.Exists(item.FilePath) && !HasIceSurface(item.FilePath)) return null;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + (archetype == block ? "" : "_on_" + archetype) + (lift ? "_lift1" : "") + (ramp == 0 ? "" : $"_r{ramp}" + (SkinLift != DefaultSkinLift ? $"l{MathF.Round(SkinLift * 1000)}" : ""));
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Work, "Blocks", name + ".Block.Gbx");
        if (!Planning && (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(item.FilePath)))
            CustomBlocks.Write(item.FilePath, path, ident, archetype, variantId, shapes.Waypoint(block) is not null ? shapes.Spawn(block, ground, variant, mobil) : null,
                lift ? shapes.BlockSize(block, ground, variant) : null, ramp: ramp == 0 ? null : (ramp, SkinLift, shapes.BlockSize(block, ground, variant)));
        return new IceBlock(ident, path);
    }

    // snow terrain: the terrain block's own shape on a ground block that clears the native grass (a terrain archetype crashes the game)
    private const string SnowTerrainArchetype = "DecoPlatformBase";
    private const float SnowTerrainRaise = 0f;
    // WhiteShore's snow ground, with its frosted grass decals; loads in other environments too
    private const string SnowTerrainMaterial = @"WhiteShore\Media\Material\Land";

    public IceBlock? SnowTerrainFor(string block, int variant, int mobil, Id collection)
    {
        if (For(block, true, variant, mobil, new HashSet<int>(), Cover.GroundBody) is not { } item || item == IceItem.Nothing) return null;
        var itemName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath));
        var name = itemName + "_snowland";
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Work, "Blocks", name + ".Block.Gbx");
        if (!Planning && (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(item.FilePath)))
            CustomBlocks.Write(item.FilePath, path, ident, SnowTerrainArchetype, 0, raise: SnowTerrainRaise, snowAs: SnowTerrainMaterial, lightmapFromAbove: true);
        return new IceBlock(ident, path);
    }

    // terrain-only item: lightmap UVs back to the exported projection, as terrain shaders read them
    private static void ProjectedTerrainLight(string itemName)
    {
        var path = Path.Combine(Paths.Items, itemName + ".Item.gbx");
        var gbx = Gbx.Parse<CGameItemModel>(path);
        var mesh = ((CGameCommonItemEntityModel)gbx.Node.EntityModel!).StaticObject!.Mesh!;
        CustomBlocks.ProjectedLightmap(mesh, LoadJsonc<List<MeshPiece>>(Path.Combine(Paths.Work, "Mesh", itemName + ".pieces.json")), itemName);
        gbx.Save(path);
    }

    private static bool HasIceSurface(string itemPath)
        => (Gbx.ParseNode<CGameItemModel>(itemPath).EntityModel as CGameCommonItemEntityModel)?.StaticObject?.Mesh?.CustomMaterials is { } materials
            && materials.Any(m => m.MaterialUserInst is { } u && ((u.MaterialName ?? "") + (u.Link ?? "")).Contains("PlatformIce"));

    // false when nothing was planned
    public bool BuildPlanned()
    {
        var jobs = plannedCopies.Values.Select(c => (Action<TextWriter>)(log =>
        {
            var copy = BuildItemCopy(c.ItemId, c.Environment, log, plan: false);
            lock (itemCopies) itemCopies[$"{c.Environment}:{c.ItemId}"] = copy;
        })).Concat(planned.Values.Select(r => (Action<TextWriter>)(log => BuildRequest(r, log)))).ToList();
        plannedCopies.Clear();
        planned.Clear();
        if (jobs.Count == 0) return false;
        // jobs mostly wait on Blender and Wine start-up; two cores left for the desktop
        var parallel = Math.Max(1, System.Environment.ProcessorCount - 2);
        Console.WriteLine($"Building {jobs.Count} items, {parallel} at a time");
        var done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel }, job =>
        {
            var log = new StringWriter();
            job(log);
            var n = Interlocked.Increment(ref done);
            if (log.ToString() is { Length: > 0 } text) lock (Console.Out) Console.Write($"[{n}/{jobs.Count}] " + text.TrimStart());
        });
        return true;
    }

    private void BuildRequest(Request r, TextWriter log)
    {
        var shapesHere = new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>()) { Environment = r.Environment };
        var name = ItemName(shapesHere, r.Block, r.Ground, r.Variant, r.Mobil, r.Clips, r.Cover, r.Tilt, r.ZoneSides, r.TerrainSnow, r.SkinLift, r.SkinEdge);
        lock (built) built.Add(name);
        if (IsCurrent(name, Path.Combine(Paths.Items, name + ".Item.gbx"), out var builtFrom)
            || Build(r.Block, r.Ground, r.Variant, r.Mobil, r.Clips, builtFrom, r.Environment, log, r.Cover, r.Tilt, r.ZoneSides, r.TerrainSnow, r.SkinLift, r.SkinEdge) is not { } error) return;
        if (error is NothingToCover)
        {
            lock (emptyItems) emptyItems.Add(name);
            WriteEmptyStamp(name);
        }
        else
        {
            lock (failures) { failures[r.Block] = error; failedItems.Add(name); }
            log.WriteLine($"  no item for {r.Block}: {error}");
        }
    }

    // rebuild only when the tools and the exported input both changed
    private bool IsCurrent(string name, string itemPath, out string? builtFrom)
    {
        builtFrom = null;
        if (!File.Exists(itemPath)) return false;
        var stampPath = StampPath(name);
        var lines = File.Exists(stampPath) ? File.ReadAllLines(stampPath) : [];
        if (lines is [var tool, var input]) { builtFrom = input; return tool == Tools(); }
        if (lines is not ["incomplete"] && File.Exists(PiecesPath(name))) builtFrom = InputHash(File.ReadAllText(PiecesPath(name)));
        return false;
    }

    private string Tools() => tools ??= Hash(File.ReadAllBytes(typeof(ItemBuilder).Assembly.Location)
        .Concat(File.ReadAllBytes(BuildScript)).Concat(File.ReadAllBytes(Path.Combine(dataDir, "ice-materials.jsonc"))).ToArray());

    private string InputHash(string piecesJson) => Hash(Encoding.UTF8.GetBytes(PipelineVersion + "\n" + File.ReadAllText(BuildScript) + "\n" + piecesJson));

    private static string Hash(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data))[..16];

    private void WriteStamp(string name, string input) => File.WriteAllLines(StampPath(name), [Tools(), input]);

    // nothing to cover leaves no item file: remember it, or every run exports it again
    private void WriteEmptyStamp(string name) => File.WriteAllLines(StampPath(name), ["empty", Tools()]);

    private bool IsKnownEmpty(string name)
        => File.Exists(StampPath(name)) && File.ReadAllLines(StampPath(name)) is ["empty", var tool] && tool == Tools();

    private static string PiecesPath(string name) => Path.Combine(Paths.Work, "Mesh", name + ".pieces.json");

    private static string StampPath(string name) => Path.Combine(Paths.Work, "Mesh", name + ".pipeline");

    private string BuildScript => Path.Combine(dataDir, "..", "tools", "build_fbx.py");

    // no dots: the importer cuts the name there
    public string ItemName(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, Cover cover = Cover.None, Vector2 tilt = default)
        => ItemName(shapes, block, ground, variant, mobil, clips, cover, tilt, SnowOnZoneSides, SnowOnGroundDeco, SkinLift, SkinEdge);

    private static bool IsSkin(MeshExport shapes, string block, bool ground)
        => ground && !shapes.IsPillarBlock(block) && !IsWholeOnGround(block, shapes.Environment);

    // grass would poke through a skin
    internal static bool IsWholeOnGround(string block, string? environment)
        => environment is "GreenCoast" or "BlueBay" && block.StartsWith("DecoPlatform");

    private static bool IsPadLook(string material)
        => material.StartsWith("SpecialFX") || material.StartsWith("DecalSpecial") || material.StartsWith("SpecialSign") || material.StartsWith("TriggerFX");

    // leave the pad uncovered but keep it icy: its own collision, lifted, keeps its gameplay
    private static List<MeshPiece> AroundPad(List<MeshPiece> skin, List<MeshPiece> all, string? gameplay, float skinLift)
    {
        var pads = all.Where(p => !p.Visible && PadGameplay(p.Name) is not null).ToList();
        var glow = all.Where(p => p.Material.StartsWith("SpecialFX")).SelectMany(p => p.Indices.Select(i => (X: p.Positions[3 * i], Z: p.Positions[3 * i + 2])));
        var area = glow.Concat(pads.SelectMany(p => p.Indices.Select(i => (X: p.Positions[3 * i], Z: p.Positions[3 * i + 2])))).ToList();
        if (area.Count == 0) return skin;
        var (x0, x1, z0, z1) = (area.Min(v => v.X), area.Max(v => v.X), area.Min(v => v.Z), area.Max(v => v.Z));
        bool UnderPad(MeshPiece p, int t)
        {
            var xs = Enumerable.Range(0, 3).Select(k => p.Positions[3 * p.Indices[t + k]]).ToArray();
            var zs = Enumerable.Range(0, 3).Select(k => p.Positions[3 * p.Indices[t + k] + 2]).ToArray();
            return xs.Max() > x0 + 0.01f && xs.Min() < x1 - 0.01f && zs.Max() > z0 + 0.01f && zs.Min() < z1 - 0.01f;
        }
        var around = skin.Select(p => p with { Indices = Enumerable.Range(0, p.Indices.Length / 3).Select(i => 3 * i).Where(t => !UnderPad(p, t)).SelectMany(t => p.Indices[t..(t + 3)]).ToArray() })
            .Where(p => p.Indices.Length > 0 && (!p.Visible || HasFloor(p))).ToList();
        around.AddRange(pads.Select((p, i) => p with
        {
            Name = $"_notvisible_pad{i}_g{gameplay ?? PadGameplay(p.Name)}", Material = PadMaterial,
            Positions = p.Positions.Select((v, k) => k % 3 == 1 ? v + skinLift : v).ToArray(),
        }));
        return around;
    }

    // skirts alone have no lightmap area, which the importer rejects
    private static bool HasFloor(MeshPiece p)
    {
        if (p.Uv1 is not { } uv) return true;
        float Area(int t)
        {
            var (a, b, c) = (p.Indices[t], p.Indices[t + 1], p.Indices[t + 2]);
            return MathF.Abs((uv[2 * b] - uv[2 * a]) * (uv[2 * c + 1] - uv[2 * a + 1]) - (uv[2 * c] - uv[2 * a]) * (uv[2 * b + 1] - uv[2 * a + 1]));
        }
        return Enumerable.Range(0, p.Indices.Length / 3).Any(i => Area(3 * i) > 1e-7f);
    }

    // full item: the pad's collision as ice with the block's effect
    private static List<MeshPiece> IcyPads(List<MeshPiece> pieces, string? gameplay)
        => pieces.Select((p, i) => p.Visible || PadGameplay(p.Name) is not { } own ? p
            : p with { Name = $"_notvisible_pad{i}_g{gameplay ?? own}", Material = PadMaterial }).ToList();

    // special blocks share the turbo look; the effect's modifier swaps it
    private static Dictionary<string, string> EffectLook(string effect)
    {
        string Lib(string name, string fallback) => Regex.IsMatch(MaterialLib.Value, $@"DMaterial\({Regex.Escape(name)}\)") ? name : fallback;
        return new()
        {
            ["SpecialFXTurbo"] = Lib($"{effect}_SpecialFX", "SpecialFXTurbo"),
            ["DecalSpecialTurbo"] = Lib($"{effect}_Decal", "DecalSpecialTurbo"),
            ["SpecialSignTurbo"] = Lib($"{effect}_Sign", "SpecialSignTurbo"),
            ["SpecialSignOff"] = Lib($"{effect}_SignOff", "SpecialSignOff"),
            ["TriggerFXTurbo"] = Lib($"{effect}_TriggerFX", ""),
        };
    }

    // the effect's ice collision material says which gameplay; Turbo has none (the mesh's own)
    private static string? PadGameplayOf(string block)
    {
        if (Converter.Effect(block) is not { } effect) return null;
        var path = Path.Combine(Paths.ExtractedStadium, "Media", "Modifier", effect, "CollisionRoadIce.Material.Gbx");
        return File.Exists(path) && Gbx.ParseNode<GBX.NET.Engines.Plug.CPlugMaterial>(path) is { GameplayId: { } g } ? g.ToString() : null;
    }

    private static string? PadGameplay(string piece) => Regex.Match(piece, @"_g([A-Za-z0-9]+)$") is { Success: true } m ? m.Groups[1].Value : null;

    // own surface so the fix-up can tell it apart
    private const string PadMaterial = "CustomIce";

    // cliff/hill variants: their Deco is the terrain itself, not a zone side
    internal static bool OnTerrainShape(string block) => Regex.IsMatch(block, "On[A-Z][a-z]+(Cliff|Hill)");

    private static bool OnTerrain(string block, bool ground, Cover cover) => block.StartsWith("Deco") && (cover == Cover.InTerrain || cover == Cover.None && ground);

    // drop the foot at terrain level (flickers)
    private static List<MeshPiece> WithoutFoot(List<MeshPiece> skin)
    {
        static bool IsSnow(MeshPiece p) => p.Material.StartsWith("PlatformIce_Deco") || p.Material == "CustomSnow";
        Vector3 At(MeshPiece p, int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
        bool Flat(MeshPiece p, int t) => Vector3.Cross(At(p, p.Indices[t + 1]) - At(p, p.Indices[t]), At(p, p.Indices[t + 2]) - At(p, p.Indices[t])) is var n
            && n.LengthSquared() > 0 && Vector3.Normalize(n).Y > 0.99f;
        float Top(MeshPiece p, int t) => Math.Max(At(p, p.Indices[t]).Y, Math.Max(At(p, p.Indices[t + 1]).Y, At(p, p.Indices[t + 2]).Y));
        var flat = skin.Where(IsSnow).SelectMany(p => Enumerable.Range(0, p.Indices.Length / 3).Select(i => 3 * i).Where(t => Flat(p, t)).Select(t => Top(p, t))).ToList();
        if (flat.Count == 0) return skin;
        var foot = flat.Min() + 0.05f;
        return skin.Select(p => !IsSnow(p) ? p : p with { Indices = Enumerable.Range(0, p.Indices.Length / 3).Select(i => 3 * i).Where(t => !(Flat(p, t) && Top(p, t) <= foot)).SelectMany(t => p.Indices[t..(t + 3)]).ToArray() })
            .Where(p => p.Indices.Length > 0).ToList();
    }

    // skins at another lift (mm) or edge angle (degrees) than the default carry it in the name
    private static string ItemName(MeshExport shapes, string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, Cover cover = Cover.None, Vector2 tilt = default, bool zoneSides = false, bool terrainSnow = true, float skinLift = DefaultSkinLift, float skinEdge = DefaultSkinEdge)
        => BaseItemName(shapes, block, ground, variant, mobil, clips, cover, tilt, zoneSides, terrainSnow) is var name && Regex.IsMatch(name, "_a?skin")
            ? name + (skinLift != DefaultSkinLift ? $"_l{MathF.Round(skinLift * 1000)}" : "") + (skinEdge != DefaultSkinEdge ? $"_e{MathF.Round(skinEdge)}" : "") : name;

    private static string BaseItemName(MeshExport shapes, string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, Cover cover, Vector2 tilt, bool zoneSides, bool terrainSnow)
        => block + "_Ice" + (shapes.DefinedIn(block) is { } env ? "_" + env : "") + (cover is Cover.Top or Cover.TopOfIce ? "_top" : cover == Cover.Gated ? "_gated" : cover == Cover.GroundBody ? (zoneSides && !OnTerrainShape(block) ? "_gbodyzs" : "_gbody") : cover == Cover.StackedBody ? "_gbodys" : cover == Cover.TopsBody ? "_topsbody" : cover == Cover.TopOfIceBody ? "_topsbodyi" : cover == Cover.InTerrain && IsWholeOnGround(block, shapes.Environment) ? "_interrain" : cover == Cover.Skin ? "_askin" : cover == Cover.TerrainFaces ? "_terrain" : cover is Cover.InTerrain or Cover.OverTwin || IsSkin(shapes, block, ground) ? (zoneSides && !block.StartsWith("Deco") ? "_skinzs" : "_skin") : ground ? "_g" : "") + (!terrainSnow && OnTerrain(block, ground, cover) ? "_bare" : "") + TiltKey(tilt) + (variant == 0 && mobil == 0 ? "" : $"_v{variant}m{mobil}") + "_" + MeshExport.ClipsKey(clips);

    public string? Build(string block, bool ground = false, int variant = 0, int mobil = 0, IReadOnlySet<int>? clips = null, string? builtFrom = null,
        string? environment = null, TextWriter? log = null, Cover cover = Cover.None, Vector2 tilt = default, bool zoneSides = false, bool terrainSnow = true, float skinLift = DefaultSkinLift, float skinEdge = DefaultSkinEdge)
    {
        log ??= Console.Out;
        try
        {
            var top = cover is Cover.Top or Cover.TopOfIce;
            var tiltMatrix = Matrix4x4.CreateFromYawPitchRoll(0, tilt.X, tilt.Y);
            var topsBody = cover is Cover.TopsBody or Cover.TopOfIceBody;
            var stacked = cover == Cover.StackedBody;
            var terrainFaces = cover == Cover.TerrainFaces;
            var body = cover == Cover.GroundBody || stacked || topsBody && ground || terrainFaces;
            if (body && OnTerrainShape(block)) zoneSides = false;
            var whole = (cover == Cover.InTerrain || cover == Cover.None && ground) && IsWholeOnGround(block, environment ?? Environment);
            var skin = !whole && !body && !topsBody && cover != Cover.Gated && (cover != Cover.None || IsSkin(new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>()) { Environment = environment ?? Environment }, block, ground));
            var materialMap = MaterialMap(top ? "top:" : skin ? "skin:" : null);
            // "<block pattern>|<material>": only for those blocks
            foreach (var (key, to) in materialMap.Where(kv => kv.Key.Contains('|')).ToList())
                if (key.Split('|') is [var pattern, var material] && Regex.IsMatch(block, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$"))
                    materialMap[material] = to;
            if (body) foreach (var key in materialMap.Keys.Where(m => m.StartsWith("Deco") && !SkinCovers(block, m, zoneSides)).ToList()) materialMap.Remove(key);
            if (body && OnTerrainShape(block)) foreach (var key in materialMap.Keys.Where(m => m.StartsWith(TerrainShader) || ShaderOf(m, environment ?? Environment) is not null).ToList()) materialMap.Remove(key);
            // "native" in ice-materials.jsonc: the game's own material, linked after import
            var native = materialMap.Where(kv => kv.Value == "native").Select(kv => kv.Key).ToHashSet();
            foreach (var key in native) materialMap.Remove(key);
            if (skin || top) native.Clear();
            if (Converter.Effect(block) is { } effect) foreach (var (k, v) in EffectLook(effect)) materialMap[k] = v;
            var export = new MeshExport(Paths.ExtractedStadium, materialMap)
            {
                Environment = environment ?? Environment,
                KeepMaterial = top ? Converter.IsWallMaterial : topsBody ? m => !Converter.IsWallMaterial(m) : cover == Cover.Gated ? m => !IsPadLook(m) : skin ? m => SkinCovers(block, m, zoneSides) : ground && !body && !IsWholeOnGround(block, environment ?? Environment) ? m => !m.StartsWith("Deco") : null,
                Lift = skin ? skinLift : 0,
                SkirtAngle = skinEdge,
                Up = Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.Transpose(tiltMatrix)),
                KeepFace = top ? n => Vector3.TransformNormal(n, tiltMatrix).Y > TopMinY : null,
                // the lip keeps the road's own look (its texture's dark edge), linked to the game material
                LipAs = top ? null : m => SwappedToIce(materialMap, m) ? (skin ? "" : NativeLink(m, environment ?? Environment) is not null ? NativePrefix + m : "CustomConcrete") : null,
                PlatformLayout = top,
            };
            clips ??= export.ClipSides(block, ground, variant)
                .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti && c.Type.StartsWith("FreeClip")).Select(c => MeshExport.ClipKey(s.Bit, c.Index)))
                .ToHashSet();
            var parts = export.FindPlacedBlockParts(block, ground, variant, mobil, clips)
                ?? throw new InvalidDataException($"{block} has nothing to draw");
            var waypoint = skin ? null : export.Waypoint(block);
            var pieces = export.Export(parts);
            if (topsBody)
            {
                bool Up(Vector3 n) => Vector3.TransformNormal(n, tiltMatrix).Y > TopMinY;
                var tops = new MeshExport(Paths.ExtractedStadium, MaterialMap("top:"))
                    { Environment = export.Environment, KeepMaterial = Converter.IsWallMaterial, Up = export.Up, KeepFace = Up, PlatformLayout = true };
                var sides = new MeshExport(Paths.ExtractedStadium, materialMap)
                    { Environment = export.Environment, KeepMaterial = Converter.IsWallMaterial, Up = export.Up, KeepFace = n => !Up(n), LipAs = export.LipAs };
                var iced = tops.Export(parts);
                var area = iced.Where(p => p.Visible).Sum(Area);
                if (area == 0 || cover == Cover.TopOfIceBody && area < TopOfIceMinArea) return NothingToCover;
                lock (Missing) Missing.UnionWith(tops.Missing);
                pieces = [.. pieces, .. sides.Export(parts).Select(p => p with { Name = p.Name + "_side" }), .. iced.Select(p => p with { Name = p.Name + "_top" })];
            }
            if (!terrainSnow && OnTerrain(block, ground, cover)) pieces = WithoutFoot(pieces);
            // terrain layer masks: another environment's terrain material reads them as its own layers
            if (body && export.IsTerrain(block)) pieces = pieces.Select(p => p with { Colors = null }).ToList();
            if (skin && Converter.IsGameplayBlock(block)) pieces = AroundPad(pieces, new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>()) { Environment = environment ?? Environment }.Export(parts), PadGameplayOf(block), skinLift);
            else if (Converter.IsGameplayBlock(block)) pieces = IcyPads(pieces, PadGameplayOf(block));
            if (stacked) pieces = pieces.Where(p => !p.Material.StartsWith("Deco") && !p.Material.StartsWith(TerrainShader) && ShaderOf(p.Material, export.Environment) is null).ToList();
            else if (body && OnTerrainShape(block))
            {
                // terrain shaders light by position over the whole lightmap, the road's part too: visible terrain goes to its own item
                bool IsTerrainFace(MeshPiece p) => p.Material.StartsWith(TerrainShader) || ShaderOf(p.Material, export.Environment) is not null;
                pieces = pieces.Where(p => terrainFaces ? !p.Visible || IsTerrainFace(p) : !p.Visible || !IsTerrainFace(p)).ToList();
                pieces = AsTerrainBelow(pieces, export.Environment);
                if (terrainFaces) pieces = pieces.Where(p => p.Visible).ToList();
                if (terrainFaces && pieces.Count == 0) return NothingToCover;
            }
            pieces = pieces.Select(p => (native.Contains(p.Material) || body && !materialMap.ContainsKey(p.Material) && !(MaterialLibSurface(p.Material) is { } own && !NotIce.Contains(own)))
                && NativeLink(p.Material, export.Environment) is not null ? p with { Material = NativePrefix + p.Material } : p).ToList();
            lock (Missing) Missing.UnionWith(export.Missing);
            if (pieces.Count == 0) return export.Missing.Count > 0 ? "its prefab isn't extracted yet" : top || skin && Converter.IsGameplayBlock(block) ? NothingToCover : "no geometry found";
            if (cover == Cover.TopOfIce && pieces.Where(p => p.Visible).Sum(Area) < TopOfIceMinArea) return NothingToCover;
            if (waypoint is not null) pieces.AddRange(MeshExport.WaypointPieces(waypoint, pieces).ToList());
            // tilt baked into the mesh; custom blocks are tilted by the game
            if (tilt != default && !topsBody) pieces = pieces.Select(p => Turned(p, tiltMatrix)).ToList();

            var itemName = ItemName(export, block, ground, variant, mobil, clips, cover, tilt, zoneSides, terrainSnow, skinLift, skinEdge);
            var result = Finish(itemName, pieces, waypoint, export, builtFrom, log);
            if (result is null && terrainFaces) ProjectedTerrainLight(itemName);
            return result;
        }
        catch (Exception e)
        {
            return Reason(e);
        }
    }

    private static string Reason(Exception e) => e is FileNotFoundException or InvalidDataException ? e.Message : $"{e.GetType().Name}: {e.Message}";

    private readonly Dictionary<string, IceItem?> itemCopies = [];

    private static readonly HashSet<string> IceSurfaces = ["RoadIce", "Ice", "Snow"];

    private static bool SwappedToIce(IReadOnlyDictionary<string, string> materialMap, string material)
        => materialMap.TryGetValue(material, out var to) && to != material && MaterialLibSurface(to) is { } s && IceSurfaces.Contains(s);

    // ice copy of a placed item that drives as non-ice (mats, tubes, ramps)
    public IceItem? ForItem(string itemId)
    {
        var key = $"{Environment}:{itemId}";
        lock (itemCopies) if (itemCopies.TryGetValue(key, out var known)) return known;
        var copy = BuildItemCopy(itemId, Environment, Console.Out, Planning);
        if (!plannedCopies.ContainsKey(key)) lock (itemCopies) itemCopies[key] = copy;
        return copy;
    }

    private readonly Dictionary<string, (string ItemId, string? Environment)> plannedCopies = [];

    private IceItem? BuildItemCopy(string itemId, string? environment, TextWriter log, bool plan)
    {
        var look = new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>()) { Environment = environment };
        if (look.ItemFile(itemId) is not { } file) return null;
        try
        {
            var materialMap = MaterialMap("item:");
            List<MeshPiece>? plain;
            try { plain = look.ExportItem(file, out _); }
            catch (InvalidCastException) { return null; }
            lock (Missing) Missing.UnionWith(look.Missing);
            bool Ices(string m) => materialMap.TryGetValue(m, out var to)
                ? to != m && MaterialLibSurface(to) is { } s && IceSurfaces.Contains(s)
                : MaterialLibSurface(m) is { } own && NotIce.Contains(own);
            if (plain is null || !plain.Any(p => Ices(p.Material))) return null;

            var env = Path.GetRelativePath(Path.GetDirectoryName(Paths.ExtractedStadium)!, file).Split('/')[0];
            var name = itemId + "_Ice" + (env == "Stadium" ? "" : "_" + env);
            var path = Path.Combine(Paths.Items, name + ".Item.gbx");
            var ident = new Ident($"IceConverter\\{name}.Item.gbx", new Id(26), "IceConverter");
            if (!IsCurrent(name, path, out var builtFrom))
            {
                if (plan)
                {
                    lock (plannedCopies) plannedCopies.TryAdd($"{environment}:{itemId}", (itemId, environment));
                    return new IceItem(ident, path, (1, 1));
                }
                var export = new MeshExport(Paths.ExtractedStadium, materialMap) { Environment = environment };
                var pieces = export.ExportItem(file, out var reason) ?? throw new InvalidDataException(reason);
                lock (Missing) Missing.UnionWith(export.Missing);
                if (Finish(name, pieces, null, export, builtFrom, log) is { } error) throw new InvalidDataException(error);
            }
            var header = Gbx.ParseHeaderNode<CGameItemModel>(path).Ident;
            return new IceItem(ident with { Collection = header.Collection, Author = header.Author }, path, (1, 1));
        }
        catch (Exception e)
        {
            lock (failures) failures[itemId] = Reason(e);
            log.WriteLine($"  no ice copy of item {itemId}: {Reason(e)}");
            return null;
        }
    }

    // "item:" entries only on item copies, "top:" only on top skins
    private Dictionary<string, string> MaterialMap(string? prefix)
    {
        var map = LoadJsonc<Dictionary<string, string>>(Path.Combine(dataDir, "ice-materials.jsonc"));
        if (prefix is not null)
            foreach (var (key, to) in map.Where(kv => kv.Key.StartsWith(prefix)).ToList()) map[key[prefix.Length..]] = to;
        return map;
    }

    private const float TopMinY = 0.02f;

    private const float TopOfIceMinArea = 256;

    private static float Area(MeshPiece p)
    {
        Vector3 At(int i) => new(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]);
        return Enumerable.Range(0, p.Indices.Length / 3).Sum(t => Vector3.Cross(At(p.Indices[3 * t + 1]) - At(p.Indices[3 * t]), At(p.Indices[3 * t + 2]) - At(p.Indices[3 * t])).Length() / 2);
    }

    private static MeshPiece Turned(MeshPiece p, Matrix4x4 m)
    {
        static float[] Map(float[] a, Func<Vector3, Vector3> f)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i += 3) (r[i], r[i + 1], r[i + 2]) = f(new Vector3(a[i], a[i + 1], a[i + 2])) is var v ? (v.X, v.Y, v.Z) : default;
            return r;
        }
        return p with { Positions = Map(p.Positions, v => Vector3.Transform(v, m)), Normals = p.Normals is { } n ? Map(n, v => Vector3.TransformNormal(v, m)) : null };
    }

    private static string TiltKey(Vector2 tilt)
    {
        static string Deg(float r) => ((int)MathF.Round(r * 180 / MathF.PI)).ToString().Replace('-', 'm');
        return tilt == default ? "" : $"_t{Deg(tilt.X)}_{Deg(tilt.Y)}";
    }

    private string? Finish(string name, List<MeshPiece> pieces, string? waypoint, MeshExport export, string? builtFrom, TextWriter log)
    {
        var meshDir = Path.Combine(Paths.Work, "Mesh");
        Directory.CreateDirectory(meshDir);
        var json = PiecesPath(name);
        var piecesJson = MeshExport.Serialize(pieces);
        var input = InputHash(piecesJson);
        if (input == builtFrom && export.Missing.Count == 0) { WriteStamp(name, input); return null; }
        log.WriteLine($"  building {name}");
        File.WriteAllText(json, piecesJson);
        // a failure below must not leave the pieces looking built
        File.WriteAllText(StampPath(name), "incomplete");

        var materials = pieces.Select(p => p.Material).Distinct().Order().ToList();
        // the importer ignores PhysicsId unless set explicitly
        var physics = MaterialLibSurfaces(materials.Where(m => !m.StartsWith(NativePrefix)));
        foreach (var m in materials.Where(m => m.StartsWith(NativePrefix))) physics[m] = NativeSurface(m, export.Environment);
        if (materials.FirstOrDefault(m => !m.StartsWith(NativePrefix) && NotIce.Contains(physics[m])) is { } dry)
            return $"material {dry} drives as {physics[dry]}; map it to an ice material in data/ice-materials.jsonc";
        var standIn = NativeStandIns(materials, physics);
        var lightmapOnly = string.Join(",", materials.Where(MaterialLibLightmapOnly));
        if (!RunQuiet("blender", ["--background", "--factory-startup", "--python", BuildScript, "--", json, Path.Combine(meshDir, name + ".fbx"), lightmapOnly], out var blenderLog))
            return "Blender failed:\n" + blenderLog;

        File.WriteAllText(Path.Combine(meshDir, name + ".MeshParams.xml"), $"""
            <MeshParams MeshType="Static" Collection="Stadium" Scale="1" FbxFile="{name}.fbx">
              <Materials>
            {string.Join("\n", materials.Select(m => $"    <Material Name=\"{m}\" Link=\"{(m.StartsWith(NativePrefix) ? standIn[physics[m]] : m)}\" PhysicsId=\"{physics[m]}\"/>"))}
              </Materials>
            </MeshParams>
            """);
        File.WriteAllText(Path.Combine(Paths.Work, name + ".Item.xml"), $"""
            <Item Type="StaticObject" Collection="Stadium" AuthorName="IceConverter">
              <MeshParamsLink File="Mesh/{name}.MeshParams.xml"/>{(waypoint is null ? "" : $"\n  <Waypoint Type=\"{waypoint}\"/>")}
              <GridSnap HStep="32" VStep="8"/>
              <Levitation VStep="8" GhostMode="false"/>
              <Options OneAxisRotation="true"/>
            </Item>
            """);

        if (RunImporter($"Items\\IceConverter\\{name}.Item.xml") is { } importError) return importError;

        var itemPath = Path.Combine(Paths.Items, name + ".Item.gbx");
        var collision = LoadJsonc<List<FbxObject>>(Path.Combine(meshDir, name + ".objects.json"))
            .Where(o => !o.Name.StartsWith("_notcollidable_") && !o.Name.StartsWith("_trigger_"))
            .Select(o => (o.Name, o.Name.StartsWith("_notvisible_pad") ? $"RoadIce:{PadGameplay(o.Name)}" : physics[o.Material], o.Triangles)).ToList();
        var surfaces = ItemPhysics.Fix(itemPath, collision);
        if (materials.Any(m => m.StartsWith(NativePrefix))) PointNative(itemPath, export.Environment);
        if (waypoint is not null) SetWaypoint(itemPath, waypoint);
        AddVertexColours(itemPath);

        // rebuilt once extracted
        if (export.Missing.Count == 0) WriteStamp(name, input);
        else File.WriteAllText(StampPath(name), "incomplete");
        foreach (var w in export.Warnings.Distinct()) log.WriteLine($"    warning: {w}");
        log.WriteLine($"    {pieces.Count} pieces; surfaces {string.Join(", ", surfaces.Select(kv => $"{kv.Key} x{kv.Value}"))}{(waypoint is null ? "" : $"; {waypoint}")}");
        return null;
    }

    // the importer ignores <Waypoint> and _socket_start: patch both in
    private static void SetWaypoint(string itemPath, string waypoint)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        gbx.Node.WaypointType = Enum.Parse<CGameItemModel.EWaypointType>(waypoint);
        var entity = (CGameCommonItemEntityModel)gbx.Node.EntityModel!;
        var chunk = entity.Chunks.Get<CGameCommonItemEntityModel.Chunk2E027000>()
            ?? throw new InvalidDataException("item entity has no chunk 0x2E027000");
        var s = MeshExport.SpawnPoint;
        chunk.U03 = new Iso4(1, 0, 0, 0, 1, 0, 0, 0, 1, s.X, s.Y, s.Z);
        gbx.Save(itemPath);
    }

    // the importer keeps colours only for DColor0 materials; without them DecoHill reads as grass. Same set the editor fills on resave.
    private static readonly string[] ColouredMaterials = ["Custom", "PlatformIce_Deco", "PlatformIce_Decal", "Decal", "Chrono", "GlassWaterWall"];

    private static void AddVertexColours(string itemPath)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        if ((gbx.Node.EntityModel as CGameCommonItemEntityModel)?.StaticObject?.Mesh is not { } mesh) return;
        var added = false;
        foreach (var geom in mesh.ShadedGeoms ?? [])
        {
            var name = mesh.CustomMaterials is { } mats && geom.MaterialIndex < mats.Length ? mats[geom.MaterialIndex].MaterialUserInst?.MaterialName : null;
            if (name is null || !ColouredMaterials.Any(name.StartsWith)) continue;
            if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles { VertexStreams: [{ Positions: { } pos, Colors: { } colors } stream, ..] }) continue;
            if (colors.ContainsKey(0)) continue;
            InsertColourDecl(stream);
            colors[0] = Enumerable.Repeat(-1, pos.Length).ToArray();
            added = true;
        }
        if (added) gbx.Save(itemPath);
    }

    // GBX.NET writes streams from their declarations: Flags1 = stride/4 << 20 | type << 8 | attribute, Flags2 = offset << 2
    private static readonly FieldInfo DataDecls = typeof(CPlugVertexStream).GetField("dataDecls", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(CPlugVertexStream), "dataDecls");

    private static void InsertColourDecl(CPlugVertexStream stream)
    {
        const uint color0 = 0x08, colorType = 0x08 << 8, space = 0x2u << 28, strideStep = 1u << 20;
        var decls = (CPlugVertexStream.DataDecl[])DataDecls.GetValue(stream)!;
        var at = Array.FindIndex(decls, d => (d.Flags1 & 0xFF) > color0);
        if (at < 0) at = decls.Length;
        var offset = at < decls.Length ? decls[at].Offset : (int)((decls[0].Flags1 >> 20) & 0xFF) * 4;
        foreach (var (d, i) in decls.Select((d, i) => (d, i)))
        {
            d.Flags1 += strideStep;
            if (i >= at) { d.Flags2 += 4 << 2; d.Offset += 4; }
        }
        var colour = new CPlugVertexStream.DataDecl
        {
            Flags1 = (decls[0].Flags1 & 0x0FF00000) | space | colorType | color0,
            Flags2 = (uint)offset << 2,
            Offset = (ushort)offset,
        };
        DataDecls.SetValue(stream, decls[..at].Append(colour).Concat(decls[at..]).ToArray());
    }

    private static string? RunImporter(string itemXml)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Paths.Game, "NadeoImporter.exe"), ["Item", itemXml])
            : new ProcessStartInfo("wine", ["NadeoImporter.exe", "Item", itemXml]);
        psi.WorkingDirectory = Paths.Game;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (!OperatingSystem.IsWindows())
        {
            psi.Environment["WINEPREFIX"] = Paths.ImporterWinePrefix;
            psi.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
            psi.Environment["WINEDEBUG"] = "-all";
        }
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        return output.Contains("Created :") ? null : "NadeoImporter failed:\n" + output.Trim();
    }

    internal static bool RunQuiet(string file, string[] args, out string log)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        log = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }

    private static bool MaterialLibLightmapOnly(string name)
    {
        var lib = File.ReadAllText(Path.Combine(Paths.Game, "NadeoImporterMaterialLib.txt"));
        var m = Regex.Match(lib, $@"DMaterial\({Regex.Escape(name)}\)(.*?)(?=DMaterial\(|$)", RegexOptions.Singleline);
        return m.Success && Regex.IsMatch(m.Groups[1].Value, @"DUvLayer\s*\(\s*Lightmap\s*,\s*0\s*\)");
    }

    public const float DefaultSkinLift = 0.02f;

    // skirt angle from the surface; 90 = straight down
    public const float DefaultSkinEdge = 90;

    private static string? MaterialLibSurface(string name)
        => Regex.Match(MaterialLib.Value, $@"DMaterial\({Regex.Escape(name)}\)[^D]*(?:D(?!Material)[^D]*)*?DSurfaceId\s*\((\w+)\)") is { Success: true } m
            ? m.Groups[1].Value : null;

    private static readonly Lazy<string> MaterialLib = new(() => File.ReadAllText(Path.Combine(Paths.Game, "NadeoImporterMaterialLib.txt")));

    private static readonly HashSet<string> NotIce = ["Asphalt", "Dirt", "Grass", "Green", "Plastic", "Sand"];

    private static readonly HashSet<string> SkinSurfaces = [.. NotIce, "RoadSynthetic"];

    // Deco only on deco blocks (or with snowOnZoneSides)
    internal static bool SkinCovers(string block, string material, bool zoneSides = false)
        => material.StartsWith("Deco") && !(zoneSides && MaterialLibSurface(material) is { } deco && SkinSurfaces.Contains(deco))
            ? block.StartsWith("Deco")
            : MaterialLibSurface(material) is { } surface && SkinSurfaces.Contains(surface);

    // kept terrain the importer can't take: built on a library stand-in with the same surface, then linked to the game's own material
    private const string NativePrefix = "Native_";

    // a library material with texture and lightmap UVs, as the game's materials expect (Custom* are lightmap-only: the game crashed),
    // one per surface and none the item uses itself, or the importer merges their collision
    private static Dictionary<string, string> NativeStandIns(List<string> materials, Dictionary<string, string> physics)
    {
        var candidates = Regex.Matches(MaterialLib.Value, @"DMaterial\((\w+)\)((?:(?!DMaterial\().)*)", RegexOptions.Singleline)
            .Where(m => Regex.IsMatch(m.Groups[2].Value, @"DUvLayer\s*\(\s*BaseMaterial\s*,\s*0\s*\)") && Regex.IsMatch(m.Groups[2].Value, @"DUvLayer\s*\(\s*Lightmap\s*,\s*1\s*\)"))
            .Select(m => (Name: m.Groups[1].Value, Surface: Regex.Match(m.Groups[2].Value, @"DSurfaceId\s*\(\s*(\w+)").Groups[1].Value))
            // water stand-ins turned the block's terrain white
            .Where(c => !materials.Contains(c.Name)).OrderBy(c => c.Name.Contains("Water")).ToList();
        var result = new Dictionary<string, string>();
        foreach (var surface in materials.Where(m => m.StartsWith(NativePrefix)).Select(m => physics[m]).Distinct().Order())
        {
            var pick = candidates.FirstOrDefault(c => c.Surface == surface && !result.ContainsValue(c.Name)).Name
                ?? candidates.First(c => !result.ContainsValue(c.Name)).Name;
            result[surface] = pick;
        }
        return result;
    }

    // embedded terrain materials are named after their shader; without collision below, the environment's first material on that shader
    private const string TerrainShader = "Tech3 Block Py";

    private static readonly Dictionary<(string, string?), string?> terrainMaterials = [];

    // each terrain face takes the material of the collision under it (rock walls, dirt floor); the layer ids it was drawn with crash custom blocks
    private static List<MeshPiece> AsTerrainBelow(List<MeshPiece> pieces, string? environment)
    {
        static Vector3 At(MeshPiece p, int v) => new(p.Positions[3 * v], p.Positions[3 * v + 1], p.Positions[3 * v + 2]);
        var below = pieces.Where(p => p.Collidable && ShaderOf(p.Material, environment) is not null)
            .SelectMany(p => Enumerable.Range(0, p.Indices.Length / 3).Select(t => (p.Material, A: At(p, p.Indices[3 * t]), B: At(p, p.Indices[3 * t + 1]), C: At(p, p.Indices[3 * t + 2])))).ToList();
        return pieces.SelectMany(p =>
        {
            if (!p.Visible || !p.Material.StartsWith(TerrainShader)) return [p];
            var own = below.Where(c => ShaderOf(c.Material, environment) == p.Material).ToList();
            if (own.Count == 0) return TerrainMaterial(p.Material, environment) is { } m ? [p with { Material = m }] : [p];
            return Enumerable.Range(0, p.Indices.Length / 3)
                .GroupBy(t => { var mid = (At(p, p.Indices[3 * t]) + At(p, p.Indices[3 * t + 1]) + At(p, p.Indices[3 * t + 2])) / 3; return own.MinBy(c => MeshExport.DistanceToTriangle(mid, c.A, c.B, c.C)).Material; })
                .Select(g => p with { Name = $"{p.Name}_{g.Key}", Material = g.Key, Indices = g.SelectMany(t => p.Indices[(3 * t)..(3 * t + 3)]).ToArray() });
        }).ToList();
    }

    internal static string? ShaderOf(string material, string? environment)
    {
        var path = Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, environment ?? "Stadium", "Media", "Material", material + ".Material.Gbx");
        lock (terrainMaterials)
        {
            if (shaders.TryGetValue(path, out var known)) return known;
            return shaders[path] = File.Exists(path) && Gbx.ParseNode<CPlugMaterial>(path).ShaderFile?.FilePath is { } shader && Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(shader)) is var name && name.StartsWith(TerrainShader) ? name : null;
        }
    }

    private static readonly Dictionary<string, string?> shaders = [];

    private static string? TerrainMaterial(string shader, string? environment)
    {
        if (!shader.StartsWith(TerrainShader)) return null;
        lock (terrainMaterials)
        {
            if (terrainMaterials.TryGetValue((shader, environment), out var known)) return known;
            var dir = Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, environment ?? "Stadium", "Media", "Material");
            var found = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.Material.Gbx").Order()
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(Gbx.ParseNode<CPlugMaterial>(f).ShaderFile?.FilePath ?? "")) == shader) : null;
            return terrainMaterials[(shader, environment)] = found is null ? null : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(found));
        }
    }

    // the environment's own version first (vistas restyle Stadium materials)
    private static string? NativeLink(string material, string? environment)
    {
        var root = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var env = environment is null || environment.StartsWith("Stadium") ? "Stadium" : environment;
        // vistas swap Stadium's walls for "…InWorld" versions
        return new[] { $"{env}\\Media\\Modifier\\StadiumOnTerrain\\{material}", $"{env}\\Media\\Modifier\\StadiumOnTerrain\\{material}InWorld", $"{env}\\Media\\Material\\{material}", $"Stadium\\Media\\Material\\{material}" }
            .FirstOrDefault(l => File.Exists(Path.Combine(root, l.Replace('\\', '/') + ".Material.Gbx")));
    }

    private static string NativeSurface(string name, string? environment)
        => Gbx.ParseNode<CPlugMaterial>(Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!,
            NativeLink(name[NativePrefix.Length..], environment)!.Replace('\\', '/') + ".Material.Gbx")).SurfaceId.ToString();

    private static void PointNative(string itemPath, string? environment)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        var mesh = ((CGameCommonItemEntityModel)gbx.Node.EntityModel!).StaticObject!.Mesh!;
        foreach (var inst in (mesh.CustomMaterials ?? []).Select(m => m.MaterialUserInst).OfType<CPlugMaterialUserInst>().Where(u => u.MaterialName?.StartsWith(NativePrefix) == true))
        {
            inst.Link = NativeLink(inst.MaterialName![NativePrefix.Length..], environment);
            inst.IsUsingGameMaterial = true;
            inst.SurfacePhysicId = Enum.Parse<CPlugSurface.MaterialId>(NativeSurface(inst.MaterialName!, environment));
        }
        gbx.Save(itemPath);
    }

    private static Dictionary<string, string> MaterialLibSurfaces(IEnumerable<string> names)
    {
        var lib = File.ReadAllText(Path.Combine(Paths.Game, "NadeoImporterMaterialLib.txt"));
        return names.ToDictionary(n => n, n =>
        {
            var m = Regex.Match(lib, $@"DMaterial\({Regex.Escape(n)}\)[^D]*(?:D(?!Material)[^D]*)*?DSurfaceId\s*\((\w+)\)");
            return m.Success ? m.Groups[1].Value
                : throw new InvalidDataException($"material {n} isn't in NadeoImporterMaterialLib.txt; map it in data/ice-materials.jsonc");
        });
    }

    public static T LoadJsonc<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path),
        new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true })!;

    private sealed record FbxObject(string Name, string Material, int Triangles);
}
