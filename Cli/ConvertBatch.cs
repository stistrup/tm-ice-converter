using System.Text.Json.Nodes;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.ConvertCommand;

namespace TmIceConverter;

// one convert over several maps: read, plan, build pieces, convert and save
internal sealed class ConvertBatch
{
    private const int MaxWorkers = 4;

    private readonly Choices choices;
    private readonly ConvertProgress progress;
    private readonly CancellationToken cancel;
    private readonly ItemBuilder items;
    private readonly Translations translations;
    private readonly HashSet<string> known;
    private readonly RunLog log = RunLog.Default;
    private List<(BlockCatalog Shapes, MapConverter Converter)> workers = [];
    private readonly List<MapResult> results = [];

    public ConvertBatch(string dataDir, Choices choices, ConvertProgress progress, CancellationToken cancel)
    {
        (this.choices, this.progress, this.cancel) = (choices, progress, cancel);
        items = new ItemBuilder(dataDir) { RewriteBlocks = choices.RebuildBlocks, MeshWhilePlanning = true };
        translations = Translations.Load(dataDir);
        known = KnownBlocks(dataDir);
    }

    private sealed record Prepared(string Path, MapSettings Settings, Translations Own, string From, List<string> Problems, string Name, string Target);

    public List<MapResult> Run(IReadOnlyList<string> paths)
    {
        // several maps at once: each worker its own catalog and converter (their caches aren't shared), the item builder is
        workers = Enumerable.Range(0, Math.Clamp(Math.Min(paths.Count, System.Environment.ProcessorCount / 2), 1, MaxWorkers))
            .Select(_ => new BlockCatalog(Paths.ExtractedStadium)).Select(shapes => (Shapes: shapes, Converter: new MapConverter(known, translations, shapes, items))).ToList();
        // rough seconds per map, measured on the campaign maps
        progress.Expect("Read map", 0.1 * paths.Count);
        progress.Expect("Plan pieces", 1.2 * paths.Count);
        progress.Expect("Convert and save", 1.8 * paths.Count);
        var maps = ReadAll(paths);
        Directory.CreateDirectory(choices.Folder);
        PlanAll(maps);
        BuildPlanned();
        SaveAll(maps);
        var missing = NoteMissing();
        progress.Finish();
        var done = missing == 0 ? results : results.Select(r => r.Done is null ? r : r with { Done = r.Done with { MissingFiles = missing } });
        return [.. done.OrderBy(r => paths.ToList().IndexOf(r.Map))];
    }

    private List<Prepared> ReadAll(IReadOnlyList<string> paths)
    {
        progress.Start("Read map", paths.Count);
        var maps = new List<Prepared>();
        foreach (var (i, path) in paths.Index())
        {
            try { maps.Add(Prepare(path)); }
            catch (Exception e) when (e is FormatException or IOException or InvalidOperationException) { results.Add(new(path, null, e.Message)); }
            progress.Advance(i + 1);
        }
        return maps;
    }

    // a dry run collects the pieces (their meshes worked out on the way), then they build in parallel.
    // Whatever the plan missed is built while converting: slower there, never wrong
    private void PlanAll(List<Prepared> maps)
    {
        items.Planning = true;
        progress.Start("Plan pieces", maps.Count);
        OnWorkers(maps, workers, (worker, map, _) =>
        {
            try { worker.Converter.Convert(Gbx.ParseNode<CGameCtnChallenge>(map.Path), map.Own); }
            catch { }
        }, progress.Advance, cancel);
    }

    private void BuildPlanned()
    {
        // an importer run is ~6 s, spread over the parallel builds; a piece that only gets a new stamp is quick
        var (imports, all) = items.PlannedWork;
        var buildSeconds = imports * 6.0 / ItemBuilder.BuildsAtOnce + (all - imports) * 0.05;
        items.BuildPlanned((done, total) =>
        {
            if (done > 0) progress.Advance(done);
            else progress.Start("Build pieces", total, weight: buildSeconds);
        }, cancel);
        cancel.ThrowIfCancellationRequested();
        items.Planning = false;
    }

    // each map saved as soon as it's converted: only the ones in hand stay in memory. Cancel: the saved ones stay
    private void SaveAll(List<Prepared> maps)
    {
        progress.Start("Convert and save", maps.Count);
        OnWorkers(maps, workers, (worker, map, _) =>
        {
            MapResult result;
            try { result = ConvertAndSave(worker.Converter, map); }
            catch (Exception e) { result = new(map.Path, null, $"{e.GetType().Name}: {e.Message}"); }
            lock (results) results.Add(result);
        }, progress.Advance, CancellationToken.None, stop: cancel);
    }

    private MapResult ConvertAndSave(MapConverter converter, Prepared map)
    {
        var gbx = Gbx.Parse<CGameCtnChallenge>(map.Path);
        var source = new MapRef(gbx.Node.MapUid, MapsRelative(map.Path), gbx.Node.MapName, MapFingerprint.Of(gbx.Node));
        var report = converter.Convert(gbx.Node, map.Own);
        MapOutput.Prepare(gbx.Node, map.Name, UidOf(gbx.Node.MapUid, map), choices.FakeAuthorTime, choices.MeAsAuthor);
        MapOutput.Embed(gbx.Node, report);
        MapOutput.Save(gbx, map.Target);
        // pieces built while converting can fail too
        var itemFailures = items.Failures;
        var failures = itemFailures.Select(f => $"{f.Key}: {f.Value}").ToList();
        File.WriteAllText(map.Target[..^".Map.Gbx".Length] + ".report.md", ReportWriter.Format(map.Path, report, itemFailures));
        var used = map.Settings.WithOptions(translations.For(source.Name, map.Own).Options.All());
        var run = log.Add(choices.Kind, source, new MapRef(gbx.Node.MapUid, MapsRelative(map.Target), map.Name, MapFingerprint.Of(gbx.Node)), used, map.Problems);
        return new(map.Path, new Outcome(map.Target, map.Name, run.Id, map.From, Summary(report), map.Problems, failures, 0), null);
    }

    private static string Summary(ConversionReport report)
    {
        var snow = report.SnowTerrain.Count > 0 ? $" {report.SnowTerrain.Values.Sum()} terrain in snow," : "";
        return $"{report.Swapped.Values.Sum()} swapped, {report.ReplacedByBlock.Values.Sum()} custom blocks,{snow}"
            + $" {report.ReplacedByItem.Values.Sum()} replaced by items, {report.Skinned.Values.Sum()} skinned, {report.Left} left as they were";
    }

    // known once every map is through; how many files aren't extracted
    private int NoteMissing()
    {
        var missing = items.Missing.Concat(workers.SelectMany(w => w.Shapes.Missing)).ToHashSet();
        if (missing.Count > 0) ExtractList.Add(missing);
        return missing.Count;
    }

    // each item on the next free worker, each worker on its own thread (the item builder keeps per-thread state);
    // done: how many finished so far. cancel throws once the started ones are through; stop only stops starting new ones
    private static void OnWorkers<T, W>(IReadOnlyList<T> list, IReadOnlyList<W> workers, Action<W, T, int> work, Action<int> done,
        CancellationToken cancel, CancellationToken stop = default)
    {
        var next = -1;
        var finished = 0;
        var threads = workers.Select(worker => Task.Factory.StartNew(() =>
        {
            while (!cancel.IsCancellationRequested && !stop.IsCancellationRequested && Interlocked.Increment(ref next) is var i && i < list.Count)
            {
                work(worker, list[i], i);
                done(Interlocked.Increment(ref finished));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(threads);
        cancel.ThrowIfCancellationRequested();
    }

    // the map's settings, name and target file; a target that's there needs Overwrite
    private Prepared Prepare(string path)
    {
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        var (settings, from) = choices.Settings is { } handed ? (handed, "the GUI's settings") : choices.From switch
        {
            null => log.Current(header.MapUid),
            "defaults" => (MapSettings.Empty, "defaults"),
            "saved" => log.SavedOf(header.MapUid) is { } saved ? (new MapSettings(saved.Settings), $"saved {saved.Updated.ToLocalTime():yyyy-MM-dd HH:mm}")
                : throw new FormatException("no saved settings for this map"),
            var given when File.Exists(given) => (SettingsFile(given), given),
            var id => log.Run(id) is { } run ? (new MapSettings(run.Settings), $"run {run.Id}") : throw new FormatException($"--settings: no file or run '{id}'"),
        };
        if (choices.Set is { Count: > 0 } set) (settings, from) = (settings.WithOptions(set), from + " with --set");
        var problems = new List<string>();
        var own = settings.Read(problems);
        var name = choices.Name ?? MapNames.DefaultOutput(header.MapName, own.Name, choices.Suffix);
        var target = Existing(choices.Folder, MapNames.FileName(name) ?? throw new FormatException($"\"{name}\" makes no file name"));
        if (File.Exists(target) && !choices.Overwrite) throw new IOException($"{target} is already there (overwrite to replace it)");
        return new Prepared(path, settings, own, from, problems, name, target);
    }

    // an overwritten map keeps its UID (re-releases stay the same map); a new file: from the original and the name
    private static string UidOf(string original, Prepared map)
    {
        if (File.Exists(map.Target))
            try { if (Gbx.ParseHeaderNode<CGameCtnChallenge>(map.Target).MapUid is { Length: > 0 } uid) return uid; }
            catch (Exception) { }
        return MapOutput.DerivedUid(original, "#" + MapNames.Plain(map.Name));
    }

    // a settings object, or a run or saved-settings file holding one
    private static MapSettings SettingsFile(string file)
    {
        var json = MapSettings.Parse(File.ReadAllText(file));
        return json.Json["settings"] is JsonObject inner && (json.Json.ContainsKey("source") || json.Json.ContainsKey("updated"))
            ? new MapSettings(inner.DeepClone().AsObject()) : json;
    }

    private static string MapsRelative(string path)
        => Path.GetRelativePath(Paths.Maps, Path.GetFullPath(path)) is var relative && !relative.StartsWith("..") ? relative : Path.GetFullPath(path);
}
