using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// convert, watch
internal sealed class ConvertCommand(string dataDir)
{
    // testrun: validated with a dummy time. release: the user as author, no times, its own UID apart from the test runs
    // from: --settings (a settings or run file, a run id, "draft", "seed"); null = the map's draft, else maps.jsonc
    private sealed record Job(string OutDir, string? Tag, IReadOnlyList<(string, bool)>? Settings = null, bool Testrun = false, bool Release = false, string? MapName = null,
        bool RewriteBlocks = false, bool Final = false, string? From = null)
    {
        public string Suffix => Testrun || Release ? PublishedSuffix : TagSuffix(Tag);
        public string Kind => Release ? "release" : Testrun ? "testrun" : Final ? "final" : Tag is null ? "progress" : $"tag {Tag}";
    }

    // one map with what it converts by
    private sealed record Prepared(string Path, MapSettings Settings, Translations Own, string From, List<string> Problems);

    private const string PublishedSuffix = " - AN Ice";

    private static string TagSuffix(string? tag) => tag is null ? " (ICE)" : $" (ice {tag})";

    public int Run(string[] rest)
    {
        var final = rest.Contains("--final");
        var testrun = rest.Contains("--testrun");
        var release = rest.Contains("--release");
        var rewriteBlocks = rest.Contains("--rewrite-blocks");
        rest = rest.Where(a => a is not ("--final" or "--testrun" or "--release" or "--rewrite-blocks")).ToArray();
        if ((final ? 1 : 0) + (testrun ? 1 : 0) + (release ? 1 : 0) > 1) return Fail("--final, --testrun and --release go to different folders; pick one");
        var outDir = Option(ref rest, "-o") ?? (final || release ? Paths.FinishedFolder : testrun ? Paths.TestrunFolder : Paths.OutFolder);
        var tag = Option(ref rest, "-t");
        var mapName = Option(ref rest, "--name");
        var from = Option(ref rest, "--settings");
        if ((testrun || release) && tag is not null) return Fail("--testrun and --release make \"<name> - AN Ice\"; leave out -t");
        if (final && tag is not null) return Fail("--final makes \"<name> (ICE)\"; leave out -t");
        var settings = new List<(string, bool)>();
        while (Option(ref rest, "--set") is { } set)
        {
            if (set.Split('=') is not [var name, var value] || !bool.TryParse(value, out var on)) return Fail($"--set takes name=true|false, not '{set}'");
            try { new ConvertOptions().With(name, on); } catch (FormatException e) { return Fail(e.Message); }
            settings.Add((name, on));
        }
        var job = new Job(outDir, tag, settings, testrun, release, mapName, rewriteBlocks, final, from);
        var dropFolder = rest.Length == 0;
        var maps = dropFolder ? ChangedDropMaps(outDir, job.Suffix) : ExpandMaps(rest).ToList();
        if (dropFolder && maps.Count == 0) Console.WriteLine($"Nothing new in {Paths.DropFolder}");
        if (mapName is not null && maps.Count != 1) return Fail("--name takes one map");
        return ConvertAll(maps, job);
    }

    // polling: the game writes maps in steps
    public int Watch(string[] rest)
    {
        var outDir = Option(ref rest, "-o") ?? Paths.OutFolder;
        var tag = Option(ref rest, "-t");
        Console.WriteLine($"Watching {Paths.DropFolder} (Ctrl+C to stop)");
        var sizes = new Dictionary<string, long>();
        while (true)
        {
            var ready = ChangedDropMaps(outDir, TagSuffix(tag)).Where(p =>
            {
                var size = new FileInfo(p).Length;
                var stable = sizes.TryGetValue(p, out var last) && last == size;
                sizes[p] = size;
                return stable;
            }).ToList();
            if (ready.Count > 0) ConvertAll(ready, new Job(outDir, tag));
            Thread.Sleep(3000);
        }
    }

    private List<string> ChangedDropMaps(string outDir, string suffix)
    {
        Directory.CreateDirectory(Paths.DropFolder);
        var translations = Translations.Load(dataDir);
        return ExpandMaps([Paths.DropFolder]).Where(p =>
        {
            var output = OutputPath(p, outDir, suffix, translations);
            return !File.Exists(output) || File.GetLastWriteTimeUtc(output) < File.GetLastWriteTimeUtc(p);
        }).ToList();
    }

    private int ConvertAll(IReadOnlyList<string> paths, Job job)
    {
        Directory.CreateDirectory(job.OutDir);
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { RewriteBlocks = job.RewriteBlocks };
        var translations = Translations.Load(dataDir);
        var converter = new MapConverter(KnownBlocks(dataDir), translations, shapes, items);
        var log = RunLog.Default;

        var failed = 0;
        var maps = new List<Prepared>();
        foreach (var path in paths)
            try { maps.Add(Prepare(path, job, translations, log)); }
            catch (Exception e) when (e is FormatException or IOException or InvalidOperationException)
            {
                failed++;
                Console.Error.WriteLine($"{Path.GetFileName(path)}: FAILED: {e.Message}");
            }

        BuildItems(maps, converter, items);

        foreach (var map in maps)
        {
            Console.WriteLine($"{Path.GetFileName(map.Path)}:");
            Console.WriteLine($"  settings: {map.From}");
            foreach (var problem in map.Problems) Console.WriteLine($"  left out: {problem}");
            try { ConvertMap(map, job, converter, translations, items, log); }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine($"  FAILED: {e.GetType().Name}: {e.Message}");
            }
        }
        var missingFiles = items.Missing.Concat(shapes.Missing).ToHashSet();
        if (missingFiles.Count > 0)
        {
            ExtractList.Add(missingFiles);
            Console.WriteLine($"{missingFiles.Count} files the conversion needs aren't extracted; listed in the extract plugin's view-refs.txt.");
            Console.WriteLine("In game: Openplanet > Extract block meshes, then convert again (those items rebuild).");
        }
        return failed == 0 ? 0 : 2;
    }

    // dry runs to collect items, build them in parallel; built items can show new tops, so repeat
    private static void BuildItems(IReadOnlyList<Prepared> maps, MapConverter converter, ItemBuilder items)
    {
        items.Planning = true;
        for (var round = 0; round < 5; round++)
        {
            foreach (var map in maps)
                try { converter.Convert(Gbx.ParseNode<CGameCtnChallenge>(map.Path), map.Own); }
                catch { }
            if (!items.BuildPlanned()) break;
        }
        items.Planning = false;
    }

    // the map's family settings (or --settings), with --set over them
    private static Prepared Prepare(string path, Job job, Translations translations, RunLog log)
    {
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        var family = log.FamilyOf(header.MapUid, () => Layout(path));
        var seedName = family.RootName is { Length: > 0 } root ? root : header.MapName;
        var (settings, from) = job.From switch
        {
            null => log.Current(family, translations, header.MapName),
            "seed" => (MapSettings.Seed(translations, seedName), "maps.jsonc"),
            "draft" => log.DraftOf(family) is { } draft ? (new MapSettings(draft.Settings), $"draft of {draft.Updated.ToLocalTime():yyyy-MM-dd HH:mm}")
                : throw new FormatException("no draft for this map"),
            var given when File.Exists(given) => (SettingsFile(given), given),
            var id => log.Run(id) is { } run ? (new MapSettings(run.Settings), $"run {run.Id}") : throw new FormatException($"--settings: no file or run '{id}'"),
        };
        if (job.Settings is { Count: > 0 } set) (settings, from) = (settings.WithOptions(set), from + " with --set");
        var problems = new List<string>();
        return new Prepared(path, settings, settings.Read(problems), from, problems);
    }

    // a settings object, or a run or draft file holding one
    private static MapSettings SettingsFile(string file)
    {
        var json = MapSettings.Parse(File.ReadAllText(file));
        return json.Json["settings"] is JsonObject inner && (json.Json.ContainsKey("source") || json.Json.ContainsKey("family"))
            ? new MapSettings(inner.DeepClone().AsObject()) : json;
    }

    internal static (string Fingerprint, string File)? Layout(string path)
    {
        try { return (MapFingerprint.Of(Gbx.ParseNode<CGameCtnChallenge>(path)), MapsRelative(path)); }
        catch (Exception) { return null; }
    }

    internal static string MapsRelative(string path)
        => Path.GetRelativePath(Paths.Maps, Path.GetFullPath(path)) is var relative && !relative.StartsWith("..") ? relative : Path.GetFullPath(path);

    private static void ConvertMap(Prepared prepared, Job job, MapConverter converter, Translations translations, ItemBuilder items, RunLog log)
    {
        var path = prepared.Path;
        var gbx = Gbx.Parse<CGameCtnChallenge>(path);
        var source = new MapRef(gbx.Node.MapUid, MapsRelative(path), gbx.Node.MapName, MapFingerprint.Of(gbx.Node));
        var report = converter.Convert(gbx.Node, prepared.Own);
        var suffix = job.Suffix;
        var name = job.MapName ?? (prepared.Own.Name ?? translations.NameFor(Regex.Replace(gbx.Node.MapName, @"\s*\(ice[^)]*\)$", "", RegexOptions.IgnoreCase))) + suffix;
        MapOutput.Prepare(gbx.Node, name, UidOf(gbx.Node.MapUid, job), job.Testrun, job.Release);
        MapOutput.Embed(gbx.Node, report);

        var outPath = job.MapName is null ? OutputPath(path, job.OutDir, suffix, translations) : Path.Combine(job.OutDir, job.MapName + ".Map.Gbx");
        MapOutput.Save(gbx, outPath);
        File.WriteAllText(outPath[..^".Map.Gbx".Length] + ".report.md", ReportWriter.Format(path, report, items.Failures));
        var used = prepared.Settings.WithOptions(translations.For(source.Name, prepared.Own).Options.All());
        var run = log.Add(job.Kind, source, new MapRef(gbx.Node.MapUid, MapsRelative(outPath), name, MapFingerprint.Of(gbx.Node)), used, prepared.Problems);

        var snow = report.SnowTerrain.Count > 0 ? $" {report.SnowTerrain.Values.Sum()} terrain in snow," : "";
        Console.WriteLine($"  {report.Swapped.Values.Sum()} swapped, {report.ReplacedByBlock.Values.Sum()} custom blocks,{snow}"
            + $" {report.ReplacedByItem.Values.Sum()} replaced by items, {report.Skinned.Values.Sum()} skinned, {report.Left} left as they were -> {outPath}");
        Console.WriteLine($"  run {run.Id}");
        Console.WriteLine(job.Testrun ? "  Validated with a dummy 1:00:00.000 author time; no shadows."
            : "  In game: open it in the editor, compute shadows (Normal if the map is near 7.1 MB), validate, save.");
    }

    // own UID, stable across reconverts
    private static string UidOf(string original, Job job)
        => MapOutput.DerivedUid(original, (job.MapName is null ? "" : "#" + job.MapName)
            + (job.Testrun ? $"{job.Suffix}{Paths.TestrunAuthor.Login}#{Paths.TestrunGeneration}"
                : job.Release ? $"{job.Suffix}{Paths.TestrunAuthor.Login}#release{(Paths.ReleaseGeneration > 1 ? Paths.ReleaseGeneration : "")}"
                : job.Suffix));

    // decisions only, nothing built or saved: times one map's conversion in planning mode
    public int Plan(string[] rest)
    {
        if (rest is not [var path]) return Fail("plan takes one map");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { Planning = true };
        var translations = Translations.Load(dataDir);
        var converter = new MapConverter(KnownBlocks(dataDir), translations, shapes, items);
        Console.WriteLine($"setup {clock.ElapsedMilliseconds} ms");
        for (var i = 1; i <= 3; i++)
        {
            clock.Restart();
            var map = Gbx.ParseNode<CGameCtnChallenge>(path);
            var parse = clock.ElapsedMilliseconds;
            var report = converter.Convert(map);
            Console.WriteLine($"run {i}: parse {parse} ms, decide {clock.ElapsedMilliseconds - parse} ms, custom blocks {report.UsedBlocks.Count}");
        }
        return 0;
    }

    internal static HashSet<string> KnownBlocks(string dataDir)
    {
        var names = File.ReadAllLines(Path.Combine(dataDir, "known-blocks.txt")).ToHashSet();
        foreach (var blockInfos in Paths.BlockInfoDirs(Paths.ExtractedStadium))
            names.UnionWith(Directory.EnumerateFiles(blockInfos, "*.Gbx", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).Split('.')[0]));
        return names;
    }

    private static string OutputPath(string map, string outDir, string suffix, Translations translations)
        => Path.Combine(outDir, translations.NameFor(Regex.Replace(Path.GetFileName(map), @"\.Map\.Gbx$", "", RegexOptions.IgnoreCase)) + suffix + ".Map.Gbx");
}
