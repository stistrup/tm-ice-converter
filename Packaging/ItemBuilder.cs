using System.Collections.Concurrent;
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

    // per thread: a convert's workers each do one map at a time and set these first (MapConverter.Run)
    private readonly ThreadLocal<BlockCatalog> shapesOf = new(() => new BlockCatalog(Paths.ExtractedStadium));
    private readonly ThreadLocal<ItemStyle> styleOf = new(() => new ItemStyle());
    private BlockCatalog shapes => shapesOf.Value!;
    // one item or block file is planned, built or written by one map at a time
    private readonly ConcurrentDictionary<string, object> nameLocks = new();
    private object NameLock(string name) => nameLocks.GetOrAdd(name, _ => new object());
    private readonly IceMaterials iceMaterials = new(dataDir);
    private readonly Dictionary<string, string> failures = [];
    private readonly HashSet<string> failedItems = [];
    private readonly HashSet<string> emptyItems = [];

    private string? tools;

    public IReadOnlyDictionary<string, string> Failures { get { lock (failures) return new Dictionary<string, string>(failures); } }

    public HashSet<string> Missing { get; } = [];

    public string? Environment { get => shapes.Environment; set => shapes.Environment = value; }

    // planning: For() only notes what's needed, BuildPlanned() builds it
    public bool Planning { get; set; }

    public ItemStyle Style { get => styleOf.Value!; set => styleOf.Value = value; }

    // planning works out each new item's mesh at once (a convert: one plan, one build);
    // off for the preview, which answers first and draws the built pieces after
    public bool MeshWhilePlanning { get; init; }

    // once per run: the stale check only sees the item, not the block writer
    public bool RewriteBlocks { get; init; }

    private readonly HashSet<string> rewritten = [];

    // the mesh comes with it: worked out while planning, the build only runs Blender and the importer
    private readonly Dictionary<string, (ItemRequest Request, ItemMeshResult? Made)> planned = [];
    // whether a planned item has ice on it, known from its mesh before it's built
    private readonly Dictionary<string, bool> iceSurfaces = [];
    // planned items whose mesh changed: Blender and the importer run (the rest only get a new stamp)
    private readonly HashSet<string> plannedImports = [];

    // what building the planned items takes: (importer runs, all)
    public (int Imports, int All) PlannedWork
    {
        get { lock (planned) lock (plannedCopies) return (plannedImports.Count + plannedCopies.Count, planned.Count + plannedCopies.Count); }
    }

    // jobs mostly wait on Blender and Wine start-up; two cores left for the desktop
    public static int BuildsAtOnce => Math.Max(1, System.Environment.ProcessorCount - 2);
    // incomplete items never turn current: build once per run
    private readonly HashSet<string> built = [];

    public IceItem? For(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, ItemKind kind = ItemKind.None, Vector2 tilt = default, IReadOnlyList<Vector2>? snowKeep = null)
    {
        var request = new ItemRequest(block, ground, variant, mobil, clips, kind, tilt, Environment, Style, snowKeep);
        var name = ItemRecipe.ItemName(request, shapes);
        lock (failures) if (failedItems.Contains(name)) return null;
        lock (emptyItems) if (emptyItems.Contains(name) || IsKnownEmpty(name) && emptyItems.Add(name)) return IceItem.Nothing;
        if (shapes.Units(block, ground, variant) is not null && shapes.FindPlacedBlockParts(block, ground, variant, mobil, clips) is null)
            return IceItem.Nothing;
        var path = Path.Combine(Paths.Items, name + ".Item.gbx");
        lock (NameLock(name))
        {
            bool builtThisRun;
            lock (built) builtThisRun = built.Contains(name);
            if (!builtThisRun && !IsCurrent(name, path, out var builtFrom))
            {
                if (Planning)
                {
                    bool known;
                    lock (planned) known = planned.ContainsKey(name);
                    if (!MeshWhilePlanning) lock (planned) planned.TryAdd(name, (request, null));
                    // empty or not, ice or not: known now, so the plan needn't wait for the build to find out
                    else if (!known)
                    {
                        var made = new ItemMesh(iceMaterials, Missing).Make(request);
                        if (made.Error is ItemMesh.NothingToCover)
                        {
                            lock (emptyItems) emptyItems.Add(name);
                            WriteEmptyStamp(name);
                            return IceItem.Nothing;
                        }
                        if (made.Error is { } planError)
                        {
                            lock (failures) { failures[block] = planError; failedItems.Add(name); }
                            Console.WriteLine($"  no item for {block}: {planError}");
                            return null;
                        }
                        var import = builtFrom is null || InputHash(MeshPiece.Serialize(made.Pieces)) != builtFrom;
                        lock (planned)
                        {
                            planned[name] = (request, made);
                            if (import) plannedImports.Add(name);
                        }
                        lock (iceSurfaces) iceSurfaces[name] = made.Pieces.Any(p => p.Material.Contains("PlatformIce"));
                    }
                    return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", new Id(26), "IceConverter"), path, shapes.BlockSize(block, ground, variant));
                }
                var error = Build(request, builtFrom, Console.Out);
                lock (built) built.Add(name);
                if (error is ItemMesh.NothingToCover)
                {
                    lock (emptyItems) emptyItems.Add(name);
                    WriteEmptyStamp(name);
                    return IceItem.Nothing;
                }
                if (error is not null)
                {
                    lock (failures) { failures[block] = error; failedItems.Add(name); }
                    Console.WriteLine($"  no item for {block}: {error}");
                    return null;
                }
            }
        }
        // a background build has it but hasn't written it yet
        if (Planning && !File.Exists(path))
            return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", new Id(26), "IceConverter"), path, shapes.BlockSize(block, ground, variant));
        var header = Gbx.ParseHeaderNode<CGameItemModel>(path).Ident;
        return new IceItem(new Ident($"IceConverter\\{name}.Item.gbx", header.Collection, header.Author), path, shapes.BlockSize(block, ground, variant));
    }

    // an item's pieces as it would be built, to look at (nothing written); kept by item name
    public List<MeshPiece>? Pieces(string block, bool ground, int variant, int mobil, ItemKind kind)
    {
        var request = new ItemRequest(block, ground, variant, mobil, new HashSet<int>(), kind, default, Environment, Style);
        return looked.GetOrAdd(ItemRecipe.ItemName(request, shapes), _ => new ItemMesh(iceMaterials, Missing).Make(request) is { Error: null } made ? made.Pieces : null);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<MeshPiece>?> looked = new();

    // the item's main body plus the baked clips in a custom block of the archetype; the game adds the other clips
    // ramp: local footprint sides (bits -X, +X, -Z, +Z) whose ice rises by the skin lift to meet a skinned neighbour
    public IceBlock? BlockFor(string block, string archetype, bool ground, int variant, int mobil, Id collection, IReadOnlySet<int> baked, ItemKind? kind = null, Vector2 tilt = default, bool lift = false, int ramp = 0)
    {
        if (CustomBlockWriter.VariantId(ground, variant, mobil) is not { } variantId
            || For(block, ground, variant, mobil, baked, kind ?? (ground ? lift ? ItemKind.StackedBody : ItemKind.GroundBody : ItemKind.None), tilt) is not { } item || item == IceItem.Nothing) return null;
        // no ice in it: clips draw the surface (penalty slopes), the game would keep them native
        if (!IceSurface(item.FilePath)) return null;
        var rampKey = ramp == 0 ? "" : $"_r{ramp}" + (Style.SkinLift != ItemStyle.DefaultSkinLift ? $"l{MathF.Round(Style.SkinLift * 1000)}" : "");
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + (archetype == block ? "" : "_on_" + archetype) + (lift ? "_lift1" : "") + rampKey;
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Blocks, name + ".Block.Gbx");
        WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, block, archetype, variantId, shapes.Waypoint(block) is not null ? shapes.Spawn(block, ground, variant, mobil) : null,
                lift ? shapes.BlockSize(block, ground, variant) : null, ramp: ramp == 0 ? null : (ramp, Style.SkinLift, shapes.BlockSize(block, ground, variant))));
        return new IceBlock(ident, path);
    }

    internal void WriteBlock(string path, string itemPath, Action<string> write)
    {
        if (Planning) return;
        lock (NameLock(path))
        {
            bool first;
            lock (rewritten) first = rewritten.Add(path);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(itemPath)) write(path);
            else if (RewriteBlocks && first)
            {
                // an unchanged block keeps its date: the map's embedded zip stores it
                var fresh = path + ".new";
                write(fresh);
                if (File.ReadAllBytes(fresh).SequenceEqual(File.ReadAllBytes(path))) File.Delete(fresh);
                else File.Move(fresh, path, true);
            }
        }
    }

    private bool IceSurface(string itemPath)
    {
        lock (iceSurfaces)
            if (iceSurfaces.TryGetValue(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(itemPath)), out var known)) return known;
        return !File.Exists(itemPath) || HasIceSurface(itemPath);
    }

    private static bool HasIceSurface(string itemPath)
        => (Gbx.ParseNode<CGameItemModel>(itemPath).EntityModel as CGameCommonItemEntityModel)?.StaticObject?.Mesh?.CustomMaterials is { } materials
            && materials.Any(m => m.MaterialUserInst is { } u && ((u.MaterialName ?? "") + (u.Link ?? "")).Contains("PlatformIce"));

    // false when nothing was planned
    // progress: (built, of)
    public bool BuildPlanned(Action<int, int>? progress = null, CancellationToken cancel = default)
    {
        var jobs = TakePlanned();
        if (jobs.Count == 0) return false;
        progress?.Invoke(0, jobs.Count);
        RunBuilds(jobs, n => progress?.Invoke(n, jobs.Count), cancel);
        return true;
    }

    public int PlannedCount
    {
        get { lock (planned) lock (plannedCopies) return plannedCopies.Count + planned.Count; }
    }

    private List<(ItemRequest Request, ItemMeshResult? Made)> TakeRequests()
    {
        lock (planned)
        {
            var all = planned.Values.ToList();
            planned.Clear();
            plannedImports.Clear();
            return all;
        }
    }

    // what planning noted, taken out to build (the GUI builds while the next plans run)
    public List<Action<TextWriter>> TakePlanned()
    {
        List<(string ItemId, string? Environment)> copies;
        lock (plannedCopies)
        {
            copies = [.. plannedCopies.Values];
            plannedCopies.Clear();
        }
        return [.. copies.Select(c => (Action<TextWriter>)(log =>
        {
            var copy = BuildItemCopy(c.ItemId, c.Environment, log, plan: false);
            lock (itemCopies) itemCopies[$"{c.Environment}:{c.ItemId}"] = copy;
        })).Concat(TakeRequests().Select(p => (Action<TextWriter>)(log => BuildRequest(p.Request, log, p.Made))))];
    }

    // cancel: no new job starts, the running ones finish
    public static void RunBuilds(List<Action<TextWriter>> jobs, Action<int>? progress = null, CancellationToken cancel = default)
    {
        var parallel = BuildsAtOnce;
        Console.WriteLine($"Building {jobs.Count} items, {parallel} at a time");
        var done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancel }, job =>
        {
            var log = new StringWriter();
            job(log);
            var n = Interlocked.Increment(ref done);
            progress?.Invoke(n);
            if (log.ToString() is { Length: > 0 } text) lock (Console.Out) Console.Write($"[{n}/{jobs.Count}] " + text.TrimStart());
        });
    }

    private void BuildRequest(ItemRequest r, TextWriter log, ItemMeshResult? made = null)
    {
        var name = ItemRecipe.ItemName(r, new BlockCatalog(Paths.ExtractedStadium) { Environment = r.Environment });
        lock (NameLock(name))
        {
            lock (built) built.Add(name);
            if (IsCurrent(name, Path.Combine(Paths.Items, name + ".Item.gbx"), out var builtFrom)
                || Build(r, builtFrom, log, made) is not { } error) return;
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

    // the code: our dll, or in a single-file release the executable (the dll has no file of its own there)
    private static string CodeFile => typeof(ItemBuilder).Assembly.Location is { Length: > 0 } dll ? dll : System.Environment.ProcessPath!;

    private string Tools() => tools ??= Hash(File.ReadAllBytes(CodeFile)
        .Concat(File.ReadAllBytes(BuildScript)).Concat(File.ReadAllBytes(Path.Combine(dataDir, "ice-materials.jsonc"))).ToArray());

    private string InputHash(string piecesJson) => Hash(Encoding.UTF8.GetBytes(PipelineVersion + "\n" + File.ReadAllText(BuildScript) + "\n" + piecesJson));

    private static string Hash(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data))[..16];

    private void WriteStamp(string name, string input) => File.WriteAllLines(StampPath(name), [Tools(), input]);

    // nothing to kind leaves no item file: remember it, or every run exports it again
    private void WriteEmptyStamp(string name) => File.WriteAllLines(StampPath(name), ["empty", Tools()]);

    private bool IsKnownEmpty(string name)
        => File.Exists(StampPath(name)) && File.ReadAllLines(StampPath(name)) is ["empty", var tool] && tool == Tools();

    private static string PiecesPath(string name) => Path.Combine(Paths.Pieces, name + ".pieces.json");

    private static string StampPath(string name) => Path.Combine(Paths.Pieces, name + ".pipeline");

    private string BuildScript => Path.Combine(dataDir, "..", "tools", "build_fbx.py");

    // one item now, with its free clips
    public string? BuildOne(string block)
    {
        var clips = new BlockCatalog(Paths.ExtractedStadium).ClipSides(block, false, 0)
            .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti && c.Type.StartsWith("FreeClip")).Select(c => ClipKey.Of(s.Bit, c.Index)))
            .ToHashSet();
        return Build(new ItemRequest(block, false, 0, 0, clips, ItemKind.None, default, null, new ItemStyle()), null, Console.Out);
    }

    private string? Build(ItemRequest r, string? builtFrom, TextWriter log, ItemMeshResult? made = null)
    {
        try
        {
            made ??= new ItemMesh(iceMaterials, Missing).Make(r);
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
        lock (NameLock("copy:" + key))
        {
            lock (itemCopies) if (itemCopies.TryGetValue(key, out var known)) return known;
            var copy = BuildItemCopy(itemId, Environment, Console.Out, Planning);
            bool planning;
            lock (plannedCopies) planning = plannedCopies.ContainsKey(key);
            if (!planning) lock (itemCopies) itemCopies[key] = copy;
            return copy;
        }
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
            var name = ItemRecipe.Prefix + itemId + "_Ice" + (env == "Stadium" ? "" : "_" + env);
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
        Directory.CreateDirectory(Paths.Pieces);
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
