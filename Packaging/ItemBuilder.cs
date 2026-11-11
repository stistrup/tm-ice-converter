using System.Collections.Concurrent;
using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// block meshes -> FBX (Blender) -> NadeoImporter -> fix-ups, with a cache; custom blocks and item copies in the other parts
public sealed partial class ItemBuilder
{
    private readonly ItemStamps stamps;

    public ItemBuilder(string dataDir)
    {
        Theme = Theme.Load(dataDir);
        stamps = new ItemStamps(dataDir, Theme);
    }

    public Theme Theme { get; }

    // per thread: a convert's workers each do one map at a time and set these first (MapConverter.Run)
    private readonly ThreadLocal<BlockCatalog> shapesOf = new(() => new BlockCatalog(Paths.ExtractedStadium));
    private readonly ThreadLocal<ItemStyle> styleOf = new(() => new ItemStyle());
    private BlockCatalog shapes => shapesOf.Value!;

    // one item or block file is planned, built or written by one map at a time
    private readonly ConcurrentDictionary<string, object> nameLocks = new();
    private object NameLock(string name) => nameLocks.GetOrAdd(name, _ => new object());

    private readonly Dictionary<string, string> failures = [];
    private readonly HashSet<string> failedItems = [];
    private readonly HashSet<string> emptyItems = [];

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

    // the mesh comes with it: worked out while planning, the build only runs Blender and the importer
    private readonly Dictionary<string, (ItemRequest Request, ItemMeshResult? Made)> planned = [];
    // planned items whose mesh changed: Blender and the importer run (the rest only get a new stamp)
    private readonly HashSet<string> plannedImports = [];
    // incomplete items never turn current: build once per run
    private readonly HashSet<string> built = [];
    private readonly ConcurrentDictionary<string, List<MeshPiece>?> looked = new();

    // jobs mostly wait on Blender and Wine start-up; two cores left for the desktop
    public static int BuildsAtOnce => Math.Max(1, System.Environment.ProcessorCount - 2);

    // what building the planned items takes: (importer runs, all)
    public (int Imports, int All) PlannedWork
    {
        get { lock (planned) lock (plannedCopies) return (plannedImports.Count + plannedCopies.Count, planned.Count + plannedCopies.Count); }
    }

    public int PlannedCount
    {
        get { lock (planned) lock (plannedCopies) return plannedCopies.Count + planned.Count; }
    }

    public BuiltItem? For(string block, bool ground, int variant, int mobil, IReadOnlySet<int> clips, ItemKind kind = ItemKind.None, Vector2 tilt = default, IReadOnlyList<Vector2>? snowKeep = null)
    {
        var request = new ItemRequest(block, ground, variant, mobil, clips, kind, tilt, Environment, Style, snowKeep);
        var name = ItemRecipe.ItemName(request, shapes, Theme);
        lock (failures) if (failedItems.Contains(name)) return null;
        lock (emptyItems) if (emptyItems.Contains(name) || stamps.IsKnownEmpty(name) && emptyItems.Add(name)) return BuiltItem.Nothing;
        if (shapes.Units(block, ground, variant) is not null && shapes.FindPlacedBlockParts(block, ground, variant, mobil, clips) is null)
            return BuiltItem.Nothing;
        var path = ItemPath(name);
        (int X, int Z) Size() => shapes.BlockSize(block, ground, variant);
        lock (NameLock(name))
        {
            bool builtThisRun;
            lock (built) builtThisRun = built.Contains(name);
            if (!builtThisRun && !stamps.IsCurrent(name, path, out var builtFrom))
            {
                if (Planning) return Plan(request, name, builtFrom) is { } planError ? NoItem(name, block, planError, Console.Out) : new BuiltItem(ItemIdent(name), path, Size());
                var error = Build(request, builtFrom, Console.Out);
                lock (built) built.Add(name);
                if (error is not null) return NoItem(name, block, error, Console.Out);
            }
        }
        // a background build has it but hasn't written it yet
        if (Planning && !File.Exists(path)) return new BuiltItem(ItemIdent(name), path, Size());
        var header = Gbx.ParseHeaderNode<CGameItemModel>(path).Ident;
        return new BuiltItem(ItemIdent(name) with { Collection = header.Collection, Author = header.Author }, path, Size());
    }

    // an item's pieces as it would be built, to look at (nothing written); kept by item name
    public List<MeshPiece>? Pieces(string block, bool ground, int variant, int mobil, ItemKind kind)
    {
        var request = new ItemRequest(block, ground, variant, mobil, new HashSet<int>(), kind, default, Environment, Style);
        return looked.GetOrAdd(ItemRecipe.ItemName(request, shapes, Theme), _ => new ItemMesh(Theme, Missing).Make(request) is { Error: null } made ? made.Pieces : null);
    }

    // one item now, with its free clips
    public string? BuildOne(string block)
    {
        var clips = new BlockCatalog(Paths.ExtractedStadium).ClipSides(block, false, 0)
            .SelectMany(s => s.Clips.Where(c => !c.Vertical && !c.Anti && c.Type.StartsWith("FreeClip")).Select(c => ClipKey.Of(s.Bit, c.Index)))
            .ToHashSet();
        return Build(new ItemRequest(block, false, 0, 0, clips, ItemKind.None, default, null, new ItemStyle()), null, Console.Out);
    }

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

    // what planning noted, taken out to build (the GUI builds while the next plans run)
    public List<Action<TextWriter>> TakePlanned()
        => [.. TakeCopies(), .. TakeRequests().Select(p => (Action<TextWriter>)(log => BuildRequest(p.Request, log, p.Made)))];

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

    private static string ItemPath(string name) => Path.Combine(Paths.Items, name + ".Item.gbx");

    private static Ident ItemIdent(string name) => new($"{Paths.ItemFolder}\\{name}.Item.gbx", new Id(26), "IceConverter");

    // null: noted; with MeshWhilePlanning the mesh is made now, so the plan knows at once if it's empty or fails
    private string? Plan(ItemRequest request, string name, string? builtFrom)
    {
        if (!MeshWhilePlanning)
        {
            lock (planned) planned.TryAdd(name, (request, null));
            return null;
        }
        lock (planned) if (planned.ContainsKey(name)) return null;
        var made = new ItemMesh(Theme, Missing).Make(request);
        if (made.Error is not null) return made.Error;
        var import = builtFrom is null || stamps.InputHash(MeshPiece.Serialize(made.Pieces)) != builtFrom;
        lock (planned)
        {
            planned[name] = (request, made);
            if (import) plannedImports.Add(name);
        }
        lock (themeSurfaces) themeSurfaces[name] = made.Pieces.Any(p => p.Material.Contains(Theme.Family));
        return null;
    }

    // nothing to cover: an empty item, remembered; else a failure, reported
    private BuiltItem? NoItem(string name, string block, string error, TextWriter log)
    {
        if (error is ItemMesh.NothingToCover)
        {
            lock (emptyItems) emptyItems.Add(name);
            stamps.WriteEmpty(name);
            return BuiltItem.Nothing;
        }
        lock (failures) { failures[block] = error; failedItems.Add(name); }
        log.WriteLine($"  no item for {block}: {error}");
        return null;
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

    private void BuildRequest(ItemRequest r, TextWriter log, ItemMeshResult? made = null)
    {
        var name = ItemRecipe.ItemName(r, new BlockCatalog(Paths.ExtractedStadium) { Environment = r.Environment }, Theme);
        lock (NameLock(name))
        {
            lock (built) built.Add(name);
            if (stamps.IsCurrent(name, ItemPath(name), out var builtFrom)) return;
            if (Build(r, builtFrom, log, made) is { } error) NoItem(name, r.Block, error, log);
        }
    }

    private string? Build(ItemRequest r, string? builtFrom, TextWriter log, ItemMeshResult? made = null)
    {
        try
        {
            made ??= new ItemMesh(Theme, Missing).Make(r);
            if (made.Error is not null) return made.Error;
            var name = ItemRecipe.ItemName(r, made.Catalog, Theme);
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

    // unchanged pieces only get a new stamp
    private string? Finish(string name, List<MeshPiece> pieces, string? waypoint, BlockCatalog export, string? builtFrom, TextWriter log)
    {
        var meshDir = Path.Combine(Paths.Work, "Mesh");
        Directory.CreateDirectory(meshDir);
        Directory.CreateDirectory(Paths.Pieces);
        var json = ItemStamps.PiecesPath(name);
        var piecesJson = MeshPiece.Serialize(pieces);
        var input = stamps.InputHash(piecesJson);
        if (input == builtFrom && export.Missing.Count == 0) { stamps.Write(name, input); return null; }
        log.WriteLine($"  building {name}");
        File.WriteAllText(json, piecesJson);
        stamps.WriteIncomplete(name);

        if (Importer.Import(Theme, name, pieces, waypoint, export.Environment, meshDir, json, stamps.BuildScript, out var surfaces) is { } error) return error;

        // rebuilt once extracted
        if (export.Missing.Count == 0) stamps.Write(name, input);
        else stamps.WriteIncomplete(name);
        foreach (var w in export.Files.Warnings.Distinct()) log.WriteLine($"    warning: {w}");
        log.WriteLine($"    {pieces.Count} pieces; surfaces {string.Join(", ", surfaces.Select(kv => $"{kv.Key} x{kv.Value}"))}{(waypoint is null ? "" : $"; {waypoint}")}");
        return null;
    }
}
