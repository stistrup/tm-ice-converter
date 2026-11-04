using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GBX.NET;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// block meshes -> FBX (Blender) -> NadeoImporter -> fix-ups, with a cache
public sealed class ItemBuilder(string dataDir)
{
    // bump to rebuild every item
    private const int PipelineVersion = 15;

    private readonly BlockCatalog shapes = new(Paths.ExtractedStadium);
    private readonly IceMaterials iceMaterials = new(dataDir);
    private readonly Dictionary<string, string> failures = [];
    private readonly HashSet<string> failedItems = [];
    private readonly HashSet<string> emptyItems = [];

    private string? tools;

    public IReadOnlyDictionary<string, string> Failures => failures;

    public HashSet<string> Missing { get; } = [];

    public string? Environment { get => shapes.Environment; set => shapes.Environment = value; }

    // planning: For() only notes what's needed, BuildPlanned() builds it
    public bool Planning { get; set; }

    public ItemStyle Style { get; set; } = new();

    // once per run: the stale check only sees the item, not the block writer
    public bool RewriteBlocks { get; init; }

    private readonly HashSet<string> rewritten = [];

    private readonly Dictionary<string, ItemRequest> planned = [];
    // incomplete items never turn current: build once per run
    private readonly HashSet<string> built = [];

    public IceItem? For(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, ItemKind kind = ItemKind.None, Vector2 tilt = default)
    {
        var request = new ItemRequest(block, ground, variant, mobil, clips, kind, tilt, Environment, Style);
        var name = ItemRecipe.ItemName(request, shapes);
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
                planned.TryAdd(name, request);
                return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", new Id(26), "IceConverter"), path, shapes.BlockSize(block, ground, variant));
            }
            var error = Build(request, builtFrom, Console.Out);
            if (error is ItemMesh.NothingToCover)
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
    public IceBlock? BlockFor(string block, string archetype, bool ground, int variant, int mobil, Id collection, IReadOnlySet<int> baked, ItemKind? kind = null, Vector2 tilt = default, bool lift = false, int ramp = 0)
    {
        if (CustomBlockWriter.VariantId(ground, variant, mobil) is not { } variantId
            || For(block, ground, variant, mobil, baked, kind ?? (ground ? lift ? ItemKind.StackedBody : ItemKind.GroundBody : ItemKind.None), tilt) is not { } item || item == IceItem.Nothing) return null;
        // no ice in it: clips draw the surface (penalty slopes), the game would keep them native
        if (File.Exists(item.FilePath) && !HasIceSurface(item.FilePath)) return null;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + (archetype == block ? "" : "_on_" + archetype) + (lift ? "_lift1" : "") + (ramp == 0 ? "" : $"_r{ramp}" + (Style.SkinLift != ItemStyle.DefaultSkinLift ? $"l{MathF.Round(Style.SkinLift * 1000)}" : ""));
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Work, "Blocks", name + ".Block.Gbx");
        WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, archetype, variantId, shapes.Waypoint(block) is not null ? shapes.Spawn(block, ground, variant, mobil) : null,
                lift ? shapes.BlockSize(block, ground, variant) : null, ramp: ramp == 0 ? null : (ramp, Style.SkinLift, shapes.BlockSize(block, ground, variant))));
        return new IceBlock(ident, path);
    }

    internal void WriteBlock(string path, string itemPath, Action<string> write)
    {
        if (Planning) return;
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(itemPath)) write(path);
        else if (RewriteBlocks && rewritten.Add(path))
        {
            // an unchanged block keeps its date: the map's embedded zip stores it
            var fresh = path + ".new";
            write(fresh);
            if (File.ReadAllBytes(fresh).SequenceEqual(File.ReadAllBytes(path))) File.Delete(fresh);
            else File.Move(fresh, path, true);
        }
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

    private void BuildRequest(ItemRequest r, TextWriter log)
    {
        var name = ItemRecipe.ItemName(r, new BlockCatalog(Paths.ExtractedStadium) { Environment = r.Environment });
        lock (built) built.Add(name);
        if (IsCurrent(name, Path.Combine(Paths.Items, name + ".Item.gbx"), out var builtFrom)
            || Build(r, builtFrom, log) is not { } error) return;
        if (error is ItemMesh.NothingToCover)
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

    // nothing to kind leaves no item file: remember it, or every run exports it again
    private void WriteEmptyStamp(string name) => File.WriteAllLines(StampPath(name), ["empty", Tools()]);

    private bool IsKnownEmpty(string name)
        => File.Exists(StampPath(name)) && File.ReadAllLines(StampPath(name)) is ["empty", var tool] && tool == Tools();

    private static string PiecesPath(string name) => Path.Combine(Paths.Work, "Mesh", name + ".pieces.json");

    private static string StampPath(string name) => Path.Combine(Paths.Work, "Mesh", name + ".pipeline");

    private string BuildScript => Path.Combine(dataDir, "..", "tools", "build_fbx.py");

    // one item now, with its free clips
    public string? BuildOne(string block)
    {
        var clips = new BlockCatalog(Paths.ExtractedStadium).ClipSides(block, false, 0)
            .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti && c.Type.StartsWith("FreeClip")).Select(c => ClipKey.Of(s.Bit, c.Index)))
            .ToHashSet();
        return Build(new ItemRequest(block, false, 0, 0, clips, ItemKind.None, default, null, new ItemStyle()), null, Console.Out);
    }

    private string? Build(ItemRequest r, string? builtFrom, TextWriter log)
    {
        try
        {
            var made = new ItemMesh(iceMaterials, Missing).Make(r);
            if (made.Error is not null) return made.Error;
            var name = ItemRecipe.ItemName(r, made.Catalog);
            var result = Finish(name, made.Pieces, made.Waypoint, made.Catalog, builtFrom, log);
            if (result is null && r.Kind == ItemKind.TerrainFaces) Lightmaps.BackToProjection(name);
            return result;
        }
        catch (Exception e)
        {
            return Reason(e);
        }
    }

    private static string Reason(Exception e) => e is FileNotFoundException or InvalidDataException ? e.Message : $"{e.GetType().Name}: {e.Message}";

    private readonly Dictionary<string, IceItem?> itemCopies = [];

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
        var look = new BlockCatalog(Paths.ExtractedStadium) { Environment = environment };
        if (look.ItemFile(itemId) is not { } file) return null;
        try
        {
            var materialMap = iceMaterials.Map("item:");
            List<MeshPiece>? plain;
            try { plain = look.Plain.ExportItem(file, out _); }
            catch (InvalidCastException) { return null; }
            lock (Missing) Missing.UnionWith(look.Missing);
            bool Ices(string m) => materialMap.TryGetValue(m, out var to)
                ? to != m && IceMaterials.IsIce(to)
                : MaterialLib.Surface(m) is { } own && MaterialLib.NotIce.Contains(own);
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
                var catalog = new BlockCatalog(Paths.ExtractedStadium) { Environment = environment };
                var pieces = new MeshExporter(catalog.Files, materialMap).ExportItem(file, out var reason) ?? throw new InvalidDataException(reason);
                lock (Missing) Missing.UnionWith(catalog.Missing);
                if (Finish(name, pieces, null, catalog, builtFrom, log) is { } error) throw new InvalidDataException(error);
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

    private string? Finish(string name, List<MeshPiece> pieces, string? waypoint, BlockCatalog export, string? builtFrom, TextWriter log)
    {
        var meshDir = Path.Combine(Paths.Work, "Mesh");
        Directory.CreateDirectory(meshDir);
        var json = PiecesPath(name);
        var piecesJson = MeshPiece.Serialize(pieces);
        var input = InputHash(piecesJson);
        if (input == builtFrom && export.Missing.Count == 0) { WriteStamp(name, input); return null; }
        log.WriteLine($"  building {name}");
        File.WriteAllText(json, piecesJson);
        // a failure below must not leave the pieces looking built
        File.WriteAllText(StampPath(name), "incomplete");

        if (Importer.Import(name, pieces, waypoint, export.Environment, meshDir, json, BuildScript, out var surfaces) is { } error) return error;

        // rebuilt once extracted
        if (export.Missing.Count == 0) WriteStamp(name, input);
        else File.WriteAllText(StampPath(name), "incomplete");
        foreach (var w in export.Files.Warnings.Distinct()) log.WriteLine($"    warning: {w}");
        log.WriteLine($"    {pieces.Count} pieces; surfaces {string.Join(", ", surfaces.Select(kv => $"{kv.Key} x{kv.Value}"))}{(waypoint is null ? "" : $"; {waypoint}")}");
        return null;
    }





















}
