using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// the converter's decisions for one map in planning mode: nothing built or saved
internal sealed partial class ConversionPreview(string dataDir)
{
    public sealed record Option(string Name, bool Value, bool MapValue, string Description);

    // per block / item index in the map's own order
    public sealed record Decision(string Outcome, string? Rule, string? To)
    {
        // what the manual flow groups it under, and the key a pick is stored by
        public string Kind { get; init; } = "";
        public string Key { get; init; } = "";
        public string? Picked { get; init; }
    }

    public sealed record Choice(string Value, string Label);

    // per kind what a piece can be picked to become; "" = as the rules say
    public static readonly Dictionary<string, List<Choice>> Choices = new()
    {
        ["roads"] = [new("", "As the rules say"), new("keep", "Keep original"), new("custom", "Custom ice block"), new("item", "Ice item"), new("skin", "Ice skin")],
        ["waypoints"] = [new("", "As the rules say"), new("keep", "Keep original"), new("custom", "Custom ice block"), new("skin", "Ice skin")],
        ["deco"] = [new("", "As the rules say"), new("keep", "Keep original"), new("snow", "Snow"), new("tops", "Ice on tops")],
        // pillars and free-placed blocks: the snow rule leaves them alone
        ["decononsnow"] = [new("", "As the rules say"), new("keep", "Keep original"), new("tops", "Ice on tops")],
        ["terrain"] = [new("", "As the rules say"), new("keep", "Keep original"), new("snow", "Snow")],
        ["shore"] = [new("", "As the rules say"), new("keep", "Keep original"), new("snow", "Snow")],
        ["special"] = [new("", "As the rules say"), new("keep", "Keep original"), new("plain", "Plain ice")],
        ["specialground"] = [new("", "As the rules say"), new("keep", "Keep original"), new("plain", "Plain ice"), new("snow", "Snow ground")],
        ["items"] = [new("", "As the rules say"), new("keep", "Keep original"), new("remove", "Remove"), new("frozen", "Frozen (trees)")],
    };

    public static readonly Dictionary<string, string> KindLabels = new()
    {
        ["roads"] = "Roads, platforms", ["waypoints"] = "Start, checkpoint, finish", ["deco"] = "Deco, walls", ["decononsnow"] = "Pillars, free-placed deco",
        ["terrain"] = "Terrain", ["shore"] = "Shore, water", ["special"] = "Special", ["specialground"] = "Special, on the ground", ["items"] = "Items",
    };

    private static readonly HashSet<string> SceneryRules = ["Pillar", "Concrete", "WallsOnly", "NoIceWanted", "DecoSceneryOff", "DecoInTerrain", "SnowOverIceTwin"];

    private static string KindOf(ConversionRun run, string name, bool ground, bool pillarOrFree, string? rule)
        => run.Shapes.IsTerrain(name) ? System.Text.RegularExpressions.Regex.IsMatch(name, "Lake|Sea|Water|River") ? "shore" : "terrain"
            : run.Shapes.Waypoint(name) is not null ? "waypoints"
            : BlockNames.IsGameplayBlock(name) ? ground ? "specialground" : "special"
            : name.StartsWith("Deco") || rule is not null && SceneryRules.Contains(rule) ? pillarOrFree || run.Shapes.IsPillarBlock(name) ? "decononsnow" : "deco"
            : "roads";

    private static string Inv(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string KeyOf(string name, CGameCtnBlock block)
        => block.IsFree && block.AbsolutePositionInMap is { } p ? $"{name} {Inv(p.X)}, {Inv(p.Y)}, {Inv(p.Z)}" : Translations.KeepKey(name, block.Coord.X, block.Coord.Y, block.Coord.Z);

    public sealed record Result(List<Option> Options, Dictionary<int, Decision> Blocks, Dictionary<int, Decision> Items, int AddedItems, long Milliseconds,
        Dictionary<string, string> Swaps, Dictionary<string, Look> Looks, string AddedScene,
        // originals drawn for real in the added scene (or gone): hidden in the view
        List<int> HiddenBlocks, List<int> HiddenItems,
        // builds finished before this result; a later one means the missing pieces are there now
        int Builds,
        // settings fields that didn't read, and picks for pieces this map doesn't have
        List<string> Problems, List<string> StalePicks)
    {
        public Dictionary<string, List<Choice>> Choices => ConversionPreview.Choices;
        public Dictionary<string, string> KindLabels => ConversionPreview.KindLabels;
    }

    // the pieces previews needed and nobody had built, built in the background
    public sealed record BuildStatus(bool Running, int Done, int Total, int Builds);

    // a material the swaps lead to, as the page's scene materials list it (images relative to GameData)
    public sealed record Look(string Name, string? Shader, List<LookTexture> Textures, List<string>? Tints);

    public sealed record LookTexture(string Role, string Name, string? Image);

    // data/ice-materials.jsonc as is, and every material it names
    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) IceLooks(BlockCatalog shapes)
    {
        var swaps = Jsonc.Load<Dictionary<string, string>>(Path.Combine(dataDir, "ice-materials.jsonc"));
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

    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) iceLooks = ([], []);

    private readonly SemaphoreSlim one = new(1);
    private MapConverter? converter;
    private Translations? translations;
    // newest data file the converter was set up from: an edited jsonc sets it up again
    private DateTime dataStamp;
    private readonly Dictionary<string, Result> recent = [];
    // the items a run adds, as a small scene of their own; by Result.AddedScene
    private readonly Dictionary<string, (byte[] Manifest, byte[] Geometry)> addedScenes = [];
    private BlockCatalog? catalog;
    private ItemBuilder? builder;
    private Task building = Task.CompletedTask;
    private int buildDone, buildTotal, builds, cachedBuilds;
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

    // what the page starts from: the settings a convert would use now and what "no settings of its own" is
    public sealed record MapState(System.Text.Json.Nodes.JsonObject Settings, string From, System.Text.Json.Nodes.JsonObject Seed, List<Option> Defaults, List<string> Unreadable);

    // nothing worked out; the map's run with these settings starts in the background, so Preview is quick
    public MapState StateOf(string mapPath)
    {
        Want(mapPath);
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(mapPath);
        var log = RunLog.Default;
        var data = Translations.Load(dataDir);
        var (settings, from) = log.Current(header.MapUid);
        var seed = MapSettings.Empty;
        var defaults = data.For("", new Translations()).Options;
        _ = Task.Run(async () =>
        {
            try { await For(mapPath, settings); }
            catch (Superseded) { }
            catch (Exception e) { Console.Error.WriteLine($"preview warm-up {mapPath}: {e.Message}"); }
        });
        return new MapState(settings.Json, from, seed.Json, OptionList(defaults, defaults), log.Problems);
    }

    // settings null: back to the defaults
    public void Save(string mapPath, MapSettings? settings)
    {
        var uid = Gbx.ParseHeaderNode<CGameCtnChallenge>(mapPath).MapUid;
        if (settings is null) RunLog.Default.DropSaved(uid);
        else RunLog.Default.Save(uid, settings);
    }

    public async Task<Result> For(string mapPath, MapSettings settings)
    {
        Want(mapPath);
        await one.WaitAsync();
        try
        {
            if (wanted != mapPath) throw new Superseded();
            var clock = Stopwatch.StartNew();
            var stamp = Directory.EnumerateFiles(dataDir, "*.jsonc").Max(File.GetLastWriteTimeUtc);
            if (converter is null || stamp != dataStamp)
            {
                translations = Translations.Load(dataDir);
                var shapes = catalog = new BlockCatalog(Paths.ExtractedStadium);
                converter = new MapConverter(ConvertCommand.KnownBlocks(dataDir), translations, shapes, (builder = new ItemBuilder(dataDir) { Planning = true }));
                dataStamp = stamp;
                recent.Clear();
                iceLooks = IceLooks(shapes);
            }
            var key = $"{mapPath}|{File.GetLastWriteTimeUtc(mapPath).Ticks}|{settings.Json.ToJsonString()}";
            if (cachedBuilds != builds) recent.Clear();
            cachedBuilds = builds;
            if (recent.TryGetValue(key, out var known)) return known with { Milliseconds = clock.ElapsedMilliseconds };
            var map = Gbx.ParseNode<CGameCtnChallenge>(mapPath);
            var problems = new List<string>();
            var own = settings.Read(problems);
            var rulesNow = translations!.For(map.MapName, own);
            var options = rulesNow.Options;
            var defaults = translations.For(map.MapName, new Translations()).Options;

            var blocks = map.GetBlocks().ToList();
            var blockKeys = blocks.Select(b => KeyOf(b.Name, b)).ToList();
            var names = blocks.Select(b => b.Name).ToList();
            var flags = blocks.Select(b => b.Flags).ToList();
            var pillarOrFree = blocks.Select(b => b.IsPillar || b.IsFree).ToList();
            var items = (map.AnchoredObjects ?? []).Select(o => (Item: o, o.ItemModel.Id, o.AbsolutePositionInMap)).ToList();
            var run = converter.Run(map, own);

            var blockDecisions = blocks.Index().ToDictionary(b => b.Index, b => Of(run, b.Item) is var d
                ? d with { Kind = KindOf(run, names[b.Index], (flags[b.Index] & BlockFlags.Ground) != 0, pillarOrFree[b.Index], d.Rule), Key = blockKeys[b.Index], Picked = rulesNow.Pieces.GetValueOrDefault(Normal(blockKeys[b.Index])) } : null!);
            var now = (map.AnchoredObjects ?? []).ToHashSet(ReferenceEqualityComparer.Instance);
            var itemDecisions = new Dictionary<int, Decision>();
            foreach (var (i, (item, id, position)) in items.Index())
            {
                Decision d = !now.Contains(item) ? new("removed", null, null)
                    : item.ItemModel.Id != id ? new(item.ItemModel.Id.StartsWith("IceConverter\\") ? "ice" : "swapped", null, item.ItemModel.Id)
                    : item.AbsolutePositionInMap != position ? new("sunk", null, null)
                    : new("unchanged", null, null);
                var itemKey = $"{id} {Inv(position.X)}, {Inv(position.Y)}, {Inv(position.Z)}";
                // no kind: nothing to pick
                itemDecisions[i] = run.Translations.Untouched(id) ? d with { Key = itemKey }
                    : d with { Kind = "items", Key = itemKey, Picked = rulesNow.Pieces.GetValueOrDefault(Normal(itemKey)) };
            }
            // what the conversion changed, drawn from the converted map where a real mesh exists; the originals those replace get hidden
            var blockOrigin = blocks.Index().ToDictionary(b => b.Item, b => b.Index, ReferenceEqualityComparer.Instance);
            var itemOrigin = items.Index().ToDictionary(o => (object)o.Item.Item, o => o.Index, ReferenceEqualityComparer.Instance);
            var converted = map.GetBlocks().ToList();
            // flags too: a pillar set from ground to air draws other clips
            var changedBlocks = converted.Where(b => !blockOrigin.TryGetValue(b, out var o) || run.Customs.ContainsKey(b) || run.Renamed.Contains(b) || b.Flags != flags[o]).ToHashSet();
            // the terrain a native ground block hid shows again under its custom block
            var underCustoms = run.Customs.Keys.Where(b => b.IsGround && run.Cells.ContainsKey(b) && !(blockOrigin.TryGetValue(b, out var o) && BlockNames.OnTerrainShape(names[o])))
                .SelectMany(b => run.Cells[b]).ToHashSet();
            changedBlocks.UnionWith(converted.Where(b => run.Shapes.IsTerrain(b.Name) && run.Cells.TryGetValue(b, out var cells) && cells.Any(underCustoms.Contains)));
            var convertedItems = (map.AnchoredObjects ?? []).ToList();
            var changedItems = convertedItems.Where(o => !itemOrigin.ContainsKey(o) || itemDecisions[itemOrigin[o]].Outcome != "unchanged").ToHashSet();
            var part = new MapView(catalog!).BuildPart(map, changedBlocks, changedItems);
            var stillThere = converted.ToHashSet(ReferenceEqualityComparer.Instance);
            var hiddenBlocks = blocks.Index().Where(b => !stillThere.Contains(b.Item)).Select(b => b.Index).ToHashSet();
            var hiddenItems = items.Index().Where(o => !now.Contains(o.Item.Item)).Select(o => o.Index).ToHashSet();
            // origin: the original it replaces, for the properties picture
            foreach (var info in part.Instances.Select(i => i.Info))
            {
                if (info.TryGetValue("block", out var b) && blockOrigin.TryGetValue(converted[(int)b], out var ob)) { hiddenBlocks.Add(ob); info["originBlock"] = ob; }
                if (info.TryGetValue("item", out var it) && itemOrigin.TryGetValue(convertedItems[(int)it], out var oi)) { hiddenItems.Add(oi); info["originItem"] = oi; }
            }
            var addedCount = convertedItems.Count(o => !itemOrigin.ContainsKey(o));
            var sceneId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            var scene = SceneBundle.InMemory(part);
            var keys = blockKeys.Concat(items.Select(o => $"{o.Id} {Inv(o.AbsolutePositionInMap.X)}, {Inv(o.AbsolutePositionInMap.Y)}, {Inv(o.AbsolutePositionInMap.Z)}"))
                .Select(Normal).ToHashSet();
            var stale = rulesNow.Pieces.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
            var result = new Result(OptionList(options, defaults), blockDecisions, itemDecisions, addedCount, clock.ElapsedMilliseconds, iceLooks.Swaps, iceLooks.Looks,
                sceneId, hiddenBlocks.Order().ToList(), hiddenItems.Order().ToList(), builds, problems, stale);
            StartBuilds(mapPath);
            if (recent.Count >= 16)
            {
                recent.Clear();
                lock (addedScenes) addedScenes.Clear();
            }
            lock (addedScenes) addedScenes[sceneId] = scene;
            recent[key] = result;
            return result;
        }
        finally { one.Release(); }
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
        var described = ConvertOptions.Described().ToDictionary(o => o.Name, o => o.Description);
        return [.. options.All().Select(o => new Option(o.Name, o.Value, map[o.Name], described[o.Name]))];
    }
}
