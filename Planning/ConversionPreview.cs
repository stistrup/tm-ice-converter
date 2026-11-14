using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

internal sealed partial class ConversionPreview(string dataDir)
{
    // Category: map, piece or look (ConvertOptions)
    public sealed record Option(string Name, bool Value, bool MapValue, string Category, string Description);

    // per block / item index in the map's own order
    public sealed record Decision(string Outcome, string? Rule, string? To)
    {
        // what the manual flow groups it under, and the key a pick is stored by
        public string Kind { get; init; } = "";
        public string Key { get; init; } = "";
        public string? Picked { get; init; }
        // its group for group conversions (data/groups.jsonc), e.g. roads/checkpoints; null: none
        public string? Group { get; init; }
    }

    private static string KindOf(ConversionRun run, string name, bool ground, bool pillarOrFree, string? rule)
    {
        if (run.Shapes.IsTerrain(name)) return System.Text.RegularExpressions.Regex.IsMatch(name, "Lake|Sea|Water|River") ? "shore" : "terrain";
        if (run.Shapes.Waypoint(name) is not null) return "waypoints";
        if (BlockNames.IsGameplayBlock(name)) return (BlockNames.IsReactor(name) ? "reactor" : "special") + (ground ? "ground" : "");
        var scenery = name.StartsWith("Deco") || rule is not null && BlockRules.SceneryRules.Contains(rule);
        if (!scenery) return "roads";
        return pillarOrFree || run.Shapes.IsPillarBlock(name) ? "decononsnow" : "deco";
    }

    private static string Inv(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string KeyOf(string name, CGameCtnBlock block, Dictionary<CGameCtnBlock, string> ambiguous)
        => ambiguous.GetValueOrDefault(block) is { } own ? own
            : block.IsFree && block.AbsolutePositionInMap is { } p ? $"{name} {Inv(p.X)}, {Inv(p.Y)}, {Inv(p.Z)}" : ConversionRules.KeepKey(name, block.Coord.X, block.Coord.Y, block.Coord.Z);

    public sealed record Result(List<Option> Options, Dictionary<int, Decision> Blocks, Dictionary<int, Decision> Items, int AddedItems, long Milliseconds,
        Dictionary<string, string> Swaps, Dictionary<string, Look> Looks, string AddedScene,
        // originals drawn for real in the added scene (or gone): hidden in the view
        List<int> HiddenBlocks, List<int> HiddenItems,
        // builds finished before this result; a later one means the missing pieces are there now
        int Builds,
        // settings fields that didn't read, and picks for pieces this map doesn't have
        List<string> Problems, List<string> StalePicks)
    {
        // per kind what a piece can be picked to become (data/picks.jsonc)
        public Dictionary<string, List<PickCatalog.Choice>> Choices { get; init; } = [];
        public Dictionary<string, string> KindLabels { get; init; } = [];
        // waiting for their items to be built, by index in the map's own order
        public List<int> PendingBlocks { get; init; } = [];
        public List<int> PendingItems { get; init; } = [];
    }

    // the pieces previews needed and nobody had built, built in the background
    public sealed record BuildStatus(bool Running, int Done, int Total, int Builds);

    // a material the swaps lead to, as the page's scene materials list it (images relative to GameData)
    public sealed record Look(string Name, string? Shader, List<LookTexture> Textures, List<string>? Tints);

    public sealed record LookTexture(string Role, string Name, string? Image);

    // the theme's materials.jsonc as is, and every material it names
    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) ThemeLooks(BlockCatalog shapes)
    {
        var swaps = Jsonc.Load<Dictionary<string, string>>(Theme.Load(dataDir).MaterialsFile);
        var view = new MapView(shapes);
        var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var looks = new Dictionary<string, Look>();
        foreach (var to in swaps.Values.Where(v => v is not ("" or "native")).Distinct())
        {
            var m = view.OwnMaterial(to);
            looks[to] = new Look(m.Name, m.Shader, m.Textures.Select(t => new LookTexture(t.Role, t.Name, t.Image is null ? null : Path.GetRelativePath(gameData, t.Image))).ToList(), m.Tints);
        }
        return (swaps, looks);
    }

    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) themeLooks = ([], []);
    private PickCatalog? picks;
    private GroupCatalog? groups;

    private readonly SemaphoreSlim one = new(1);
    private MapConverter? converter;
    private ConversionRules? dataRules;
    // newest data file the converter was set up from: an edited jsonc sets it up again
    private DateTime dataStamp;
    private readonly Dictionary<string, Result> recent = [];
    // the last runs' scene drawing, to redraw once their pieces are built; few, they hold the converted map
    private readonly List<(string Key, Func<(Result, (byte[] Manifest, byte[] Geometry))> Draw)> drawings = [];
    // the items a run adds, as a small scene of their own; by Result.AddedScene
    private readonly Dictionary<string, (byte[] Manifest, byte[] Geometry)> addedScenes = [];
    private BlockCatalog? catalog;
    private ItemBuilder? builder;
    private Task building = Task.CompletedTask;
    private int buildDone, buildTotal, builds;
    // the map the page asked for last: runs for others are dropped, their builds stop
    private volatile string? wanted;
    private string? buildsFor;
    private CancellationTokenSource buildCancel = new();
    private readonly Lock buildLock = new();

    public sealed class Superseded() : Exception("another map was asked for");

    private void Want(string mapPath)
    {
        wanted = mapPath;
        lock (buildLock) if (buildsFor is not null && buildsFor != mapPath) buildCancel.Cancel();
    }

    public BuildStatus Building() => new(!building.IsCompleted, buildDone, buildTotal, builds);

    // under `one`: whatever the runs so far planned and found unbuilt
    private void StartBuilds(string mapPath)
    {
        if (!building.IsCompleted || builder is not { PlannedCount: > 0 }) return;
        var jobs = builder.TakePlanned();
        (buildDone, buildTotal) = (0, jobs.Count);
        CancellationToken cancel;
        lock (buildLock)
        {
            (buildsFor, buildCancel) = (mapPath, new());
            cancel = buildCancel.Token;
        }
        building = Task.Run(() =>
        {
            // the skipped ones are planned again when their map is previewed
            try { ItemBuilder.RunBuilds(jobs, n => buildDone = n, cancel); }
            catch (OperationCanceledException) { Console.WriteLine($"preview builds: stopped after {buildDone} of {jobs.Count}, another map opened"); }
            catch (Exception e) { Console.Error.WriteLine($"preview builds: {e.Message}"); }
            // results from before point at pieces that exist now
            Interlocked.Increment(ref builds);
        });
    }

    public (byte[] Manifest, byte[] Geometry)? AddedScene(string id)
    {
        lock (addedScenes) return addedScenes.TryGetValue(id, out var scene) ? scene : null;
    }

    // Settings: the working copy, what a convert uses now; Seed: no settings of its own
    public sealed record MapState(System.Text.Json.Nodes.JsonObject Settings, string From, System.Text.Json.Nodes.JsonObject Seed, List<Option> Defaults, List<string> Unreadable,
        bool Dirty, bool CanUndo, bool CanRedo);

    // nothing worked out; the map's run with these settings starts in the background, so Preview is quick
    public MapState StateOf(string mapPath)
    {
        Want(mapPath);
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(mapPath);
        var log = SettingsStore.Default;
        var data = ConversionRules.Load(dataDir);
        var (settings, from) = log.Current(header.MapUid);
        var document = log.Document(header.MapUid);
        var seed = MapSettings.Empty;
        var defaults = data.For("", new ConversionRules()).Options;
        _ = Task.Run(async () =>
        {
            try { await For(mapPath, settings); }
            catch (Superseded) { }
            catch (Exception e) { Console.Error.WriteLine($"preview warm-up {mapPath}: {e.Message}"); }
        });
        return new MapState(settings.Json, from, seed.Json, OptionList(defaults, defaults), log.Problems, document.Dirty, document.CanUndo, document.CanRedo);
    }

    // as For, once the pieces it needs are built
    public async Task<Result> Settled(string mapPath, MapSettings settings)
    {
        for (;;)
        {
            var result = await For(mapPath, settings);
            while (Building().Running) await Task.Delay(500);
            if (Building().Builds == result.Builds) return result;
        }
    }

    public async Task<Result> For(string mapPath, MapSettings settings)
    {
        Want(mapPath);
        await one.WaitAsync();
        try
        {
            if (wanted != mapPath) throw new Superseded();
            var clock = Stopwatch.StartNew();
            SetUpIfDataChanged();
            var key = $"{mapPath}|{File.GetLastWriteTimeUtc(mapPath).Ticks}|{settings.Json.ToJsonString()}";
            if (recent.TryGetValue(key, out var known))
            {
                if (known.Builds == builds) return known with { Milliseconds = clock.ElapsedMilliseconds };
                // the plan stands (meshes are made while planning); only the built pieces are new
                if (drawings.FirstOrDefault(d => d.Key == key).Draw is { } draw)
                {
                    var (redrawn, again) = draw();
                    Remember(key, redrawn, again);
                    return redrawn with { Milliseconds = clock.ElapsedMilliseconds };
                }
            }
            var (result, scene, drawing) = Run(mapPath, settings, key);
            StartBuilds(mapPath);
            Remember(key, result, scene);
            drawings.RemoveAll(d => d.Key == key);
            drawings.Add((key, drawing));
            if (drawings.Count > 3) drawings.RemoveAt(0);
            return result with { Milliseconds = clock.ElapsedMilliseconds };
        }
        finally { one.Release(); }
    }

    // an edited data file sets the converter up again
    private void SetUpIfDataChanged()
    {
        var stamp = Directory.EnumerateFiles(dataDir, "*.jsonc", SearchOption.AllDirectories).Max(File.GetLastWriteTimeUtc);
        if (converter is not null && stamp == dataStamp) return;
        dataRules = ConversionRules.Load(dataDir);
        var shapes = catalog = new BlockCatalog(Paths.ExtractedStadium);
        converter = new MapConverter(ConvertCommand.KnownBlocks(dataDir), dataRules, shapes, (builder = new ItemBuilder(dataDir) { Planning = true, MeshWhilePlanning = true }));
        dataStamp = stamp;
        recent.Clear();
        drawings.Clear();
        themeLooks = ThemeLooks(shapes);
        picks = PickCatalog.Load(dataDir);
        groups = GroupCatalog.Load(dataDir);
    }

    private void Remember(string key, Result result, (byte[] Manifest, byte[] Geometry) scene)
    {
        if (recent.Count >= 16)
        {
            recent.Clear();
            lock (addedScenes) addedScenes.Clear();
        }
        lock (addedScenes) addedScenes[result.AddedScene] = scene;
        recent[key] = result;
    }

    // the map as it was: the run changes it in place
    private sealed record Before(List<CGameCtnBlock> Blocks, List<string> Names, List<string> Keys, List<string> PlainKeys, List<int> Flags, List<bool> PillarOrFree,
        List<(Direction, Vec3?, Vec3?)> Placements, List<(CGameCtnAnchoredObject Item, string Id, Vec3 Position)> Items)
    {
        public static Before Of(CGameCtnChallenge map)
        {
            var blocks = map.GetBlocks().ToList();
            var ambiguous = PieceKeys.Ambiguous(map);
            return new(blocks,
                blocks.Select(b => b.Name).ToList(),
                blocks.Select(b => KeyOf(b.Name, b, ambiguous)).ToList(),
                blocks.Select(b => KeyOf(b.Name, b, [])).ToList(),
                blocks.Select(b => b.Flags).ToList(),
                blocks.Select(b => b.IsPillar || b.IsFree).ToList(),
                blocks.Select(b => (b.Direction, b.AbsolutePositionInMap, b.YawPitchRoll)).ToList(),
                (map.AnchoredObjects ?? []).Select(o => (Item: o, o.ItemModel.Id, o.AbsolutePositionInMap)).ToList());
        }
    }

    private (Result, (byte[] Manifest, byte[] Geometry), Func<(Result, (byte[] Manifest, byte[] Geometry))>) Run(string mapPath, MapSettings settings, string key)
    {
        var clock = Stopwatch.StartNew();
        var map = Gbx.ParseNode<CGameCtnChallenge>(mapPath);
        var problems = new List<string>();
        var own = settings.Read(problems);
        var rulesNow = dataRules!.For(map.MapName, own);
        var defaults = dataRules.For(map.MapName, new ConversionRules()).Options;
        var before = Before.Of(map);
        var run = converter!.Run(map, own);

        var blockDecisions = before.Blocks.Index().ToDictionary(b => b.Index, b => BlockDecision(run, before, b.Index, rulesNow, groups!));
        var now = new HashSet<object>(map.AnchoredObjects ?? [], ReferenceEqualityComparer.Instance);
        var itemDecisions = before.Items.Index().ToDictionary(o => o.Index, o => ItemDecision(run, o.Item, now, rulesNow, groups!));

        // what the conversion changed, drawn from the converted map where a real mesh exists; the originals those replace get hidden
        var blockOrigin = before.Blocks.Index().ToDictionary(b => (object)b.Item, b => b.Index, ReferenceEqualityComparer.Instance);
        var itemOrigin = before.Items.Index().ToDictionary(o => (object)o.Item.Item, o => o.Index, ReferenceEqualityComparer.Instance);
        var converted = map.GetBlocks().ToList();
        var convertedItems = (map.AnchoredObjects ?? []).ToList();
        var changedBlocks = ChangedBlocks(run, before, converted, blockOrigin);
        var changedItems = convertedItems.Where(o => !itemOrigin.ContainsKey(o) || itemDecisions[itemOrigin[o]].Outcome != "unchanged").ToHashSet();
        var drawCatalog = catalog!;
        var environment = drawCatalog.Environment;
        (Result, (byte[] Manifest, byte[] Geometry)) Draw()
        {
            // shared with later runs of other maps: native materials resolve per environment
            drawCatalog.Environment = environment;
            var part = new MapView(drawCatalog).BuildPart(map, changedBlocks, changedItems);
            var stillThere = converted.ToHashSet(ReferenceEqualityComparer.Instance);
            var hiddenBlocks = before.Blocks.Index().Where(b => !stillThere.Contains(b.Item)).Select(b => b.Index).ToHashSet();
            var hiddenItems = before.Items.Index().Where(o => !now.Contains(o.Item.Item)).Select(o => o.Index).ToHashSet();
            // origin: the original it replaces, for the properties picture
            foreach (var info in part.Instances.Select(i => i.Info))
            {
                if (info.TryGetValue("block", out var b) && blockOrigin.TryGetValue(converted[(int)b], out var ob)) { hiddenBlocks.Add(ob); info["originBlock"] = ob; }
                if (info.TryGetValue("item", out var it) && itemOrigin.TryGetValue(convertedItems[(int)it], out var oi)) { hiddenItems.Add(oi); info["originItem"] = oi; }
            }
            var addedCount = convertedItems.Count(o => !itemOrigin.ContainsKey(o));
            var sceneId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{builds}")))[..16];
            var scene = SceneBundle.InMemory(part);
            var keys = before.Keys.Concat(before.PlainKeys).Concat(before.Items.Select(o => ItemKey(o.Id, o.Position))).Select(Normal).ToHashSet();
            var stale = rulesNow.Pieces.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
            var result = new Result(OptionList(rulesNow.Options, defaults), blockDecisions, itemDecisions, addedCount, clock.ElapsedMilliseconds, themeLooks.Swaps, themeLooks.Looks,
                sceneId, hiddenBlocks.Order().ToList(), hiddenItems.Order().ToList(), builds, problems, stale)
                {
                    Choices = picks!.Choices,
                    KindLabels = picks.KindLabels,
                    PendingBlocks = [.. before.Blocks.Index().Where(b => run.Waiting.TryGetValue(b.Item, out var file) && !File.Exists(file) || run.Customs.TryGetValue(b.Item, out var c) && c.Waiting).Select(b => b.Index)],
                    PendingItems = [.. before.Items.Index().Where(o => run.WaitingItems.TryGetValue(o.Item.Item, out var file) && !File.Exists(file)).Select(o => o.Index)],
                };
            return (result, scene);
        }
        var (first, firstScene) = Draw();
        return (first, firstScene, Draw);
    }

    private static Decision BlockDecision(ConversionRun run, Before before, int i, ConversionRules rulesNow, GroupCatalog groups)
    {
        var d = Of(run, before.Blocks[i]);
        var ground = (before.Flags[i] & BlockFlags.Ground) != 0;
        var (name, kind) = (before.Names[i], KindOf(run, before.Names[i], ground, before.PillarOrFree[i], d.Rule));
        return d with
        {
            Kind = kind,
            Group = groups.PathOf(kind, name, run.Shapes.Waypoint(name), tree: false),
            Key = before.Keys[i],
            // a shared block picked by its cell's key before keys told them apart
            Picked = rulesNow.Pieces.GetValueOrDefault(Normal(before.Keys[i])) ?? rulesNow.Pieces.GetValueOrDefault(Normal(before.PlainKeys[i])),
        };
    }

    private static Decision ItemDecision(ConversionRun run, (CGameCtnAnchoredObject Item, string Id, Vec3 Position) was, HashSet<object> now, ConversionRules rulesNow, GroupCatalog groups)
    {
        var (item, id, position) = was;
        Decision d = !now.Contains(item) ? new("removed", null, null)
            : item.ItemModel.Id != id ? new(Paths.IsOurs(item.ItemModel.Id) ? "ice" : "swapped", null, item.ItemModel.Id)
            : item.AbsolutePositionInMap != position ? new("sunk", null, null)
            : new("unchanged", null, null);
        var itemKey = ItemKey(id, position);
        // no kind: nothing to pick
        if (run.DataRules.Untouched(id)) return d with { Key = itemKey };
        return d with
        {
            Kind = "items",
            Key = itemKey,
            Picked = rulesNow.Pieces.GetValueOrDefault(Normal(itemKey)),
            Group = groups.PathOf("items", id, null, tree: ItemSwaps.CoverSwap(run, id) is not null),
        };
    }

    // every piece's target key by index in the map's own order, before any run: the page marks what the next run is working on
    public sealed record KeyList(List<string> Blocks, List<string> PlainBlocks, List<string> Items);

    public static KeyList KeysOf(string mapPath)
    {
        var before = Before.Of(Gbx.ParseNode<CGameCtnChallenge>(mapPath));
        return new([.. before.Keys.Select(Normal)], [.. before.PlainKeys.Select(Normal)], [.. before.Items.Select(o => Normal(ItemKey(o.Id, o.Position)))]);
    }

    private static string ItemKey(string id, Vec3 position) => $"{id} {Inv(position.X)}, {Inv(position.Y)}, {Inv(position.Z)}";

    // flags too: a pillar set from ground to air draws other clips
    private static HashSet<CGameCtnBlock> ChangedBlocks(ConversionRun run, Before before, List<CGameCtnBlock> converted, Dictionary<object, int> blockOrigin)
    {
        bool Changed(CGameCtnBlock b) => !blockOrigin.TryGetValue(b, out var o) || run.Customs.ContainsKey(b) || run.Renamed.Contains(b) || b.Flags != before.Flags[o]
            || (b.Direction, b.AbsolutePositionInMap, b.YawPitchRoll) != before.Placements[o];
        var changed = converted.Where(Changed).ToHashSet();
        // the terrain a native ground block hid shows again under its custom block
        var underCustoms = run.Customs.Keys.Where(b => b.IsGround && run.Cells.ContainsKey(b) && !(blockOrigin.TryGetValue(b, out var o) && BlockNames.OnTerrainShape(before.Names[o])))
            .SelectMany(b => run.Cells[b]).ToHashSet();
        changed.UnionWith(converted.Where(b => run.Shapes.IsTerrain(b.Name) && run.Cells.TryGetValue(b, out var cells) && cells.Any(underCustoms.Contains)));
        // a pillar's walls follow the block it holds up
        var parents = PillarParents.Find(run.Shapes, converted.Where(run.Cells.ContainsKey).Select(b => (b, (IReadOnlyList<Int3>)run.Cells[b])));
        changed.UnionWith(parents.Where(p => changed.Contains(p.Value)).Select(p => p.Key));
        return changed;
    }

    private static string Normal(string key) => System.Text.RegularExpressions.Regex.Replace(key.Trim(), @"\s*,\s*", ",");

    private static Decision Of(ConversionRun run, CGameCtnBlock block)
    {
        var rule = run.DecidedBy.GetValueOrDefault(block);
        var custom = run.Customs.GetValueOrDefault(block);
        if (run.Skinned.Contains(block)) return new("skin", rule, null);
        if (run.Replaced.Contains(block)) return new("item", rule, null);
        if (run.EngineOffGates.Contains(block)) return new("removed", rule, null);
        if (run.Renamed.Contains(block)) return new("twin", rule, block.Name);
        // tops iced into a custom block are still a tops-only change
        if (run.Topped.Contains(block)) return new("tops", rule, custom?.Ident.Id);
        if (custom is not null) return new("custom", rule, custom.Ident.Id);
        if (run.SceneryKept.Contains(block)) return new("kept", rule, null);
        return new("unchanged", rule, null);
    }

    private static List<Option> OptionList(ConvertOptions options, ConvertOptions mapOptions)
    {
        var map = mapOptions.All().ToDictionary(o => o.Name, o => o.Value);
        var described = ConvertOptions.Described().ToDictionary(o => o.Name);
        return [.. options.All().Select(o => new Option(o.Name, o.Value, map[o.Name], described[o.Name].Category, described[o.Name].Description))];
    }
}
