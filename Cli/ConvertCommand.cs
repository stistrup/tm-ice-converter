using System.Text.Json.Nodes;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// convert, watch: the options the GUI's Convert popup has
internal sealed class ConvertCommand(string dataDir)
{
    // Folder: where the maps go (absolute). Name: one map only, null = "<map name> (ICE)".
    // Settings: handed over whole (the GUI's working ones); else From (--settings) or the map's draft / maps.jsonc, Set over them
    public sealed record Choices(string Folder, string? Name = null, bool FakeAuthorTime = false, bool MeAsAuthor = false, bool RebuildBlocks = false, bool Overwrite = false)
    {
        public MapSettings? Settings { get; init; }
        public string? From { get; init; }
        public IReadOnlyList<(string, bool)> Set { get; init; } = [];

        public string Kind => string.Join(", ", new[] { "convert", FakeAuthorTime ? "fake author time" : null, MeAsAuthor ? "me as author" : null }.OfType<string>());
    }

    // Output: absolute
    public sealed record Outcome(string Output, string Name, string RunId, string From, string Summary, List<string> Problems, List<string> Failures, int MissingFiles);

    public sealed record MapResult(string Map, Outcome? Done, string? Error);

    public const string Usage = """
          convert <map.Gbx | folder>... [options]
              Convert maps to ice, each to "<map name> (ICE).Map.Gbx" with a .report.md next to it.
              --folder <dir>        where they go: a folder under Maps, or an absolute path (default: ICE in progress)
              --name <map name>     the converted map's name (one map only); the file is named after it
              --fake-author-time    validated with a 1:00:00.000 author time, so it can go to a club without driving it
              --me-as-author        your account (testrunAuthor in data/local.jsonc) as the map's author
              --rebuild-blocks      write the custom blocks again even when their items didn't change
              --overwrite           replace a map of the same file name; it keeps that map's UID
              --settings <from>     a settings file, a run id or file, "draft" or "seed" (maps.jsonc);
                                    default: the draft the GUI keeps for the map, else its maps.jsonc entry
              --set <option>=true|false   override an option for this run (repeatable)
              A new file name gets a UID of its own (from the original map and the name). Every conversion is
              logged with its settings (see runs).
          watch [options]
              Keep converting maps from the drop folder as they change (same options, no --name; always overwrites).
        """;

    public int Run(string[] rest)
    {
        if (rest.Length == 0 || rest.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("Usage:\n" + Usage);
            return rest.Length == 0 ? 1 : 0;
        }
        if (Parse(ref rest, allowName: true) is not { } choices) return 2;
        if (rest.FirstOrDefault(a => a.StartsWith('-')) is { } unknown) return Fail($"unknown option {unknown}; see convert --help");
        var maps = ExpandMaps(rest).ToList();
        if (maps.Count == 0) return Fail("no maps given");
        if (choices.Name is not null && maps.Count != 1) return Fail("--name takes one map");
        return Report(ConvertWithBar(maps, choices));
    }

    // polling: the game writes maps in steps
    public int Watch(string[] rest)
    {
        if (rest.Any(a => a is "-h" or "--help")) { Console.WriteLine("Usage:\n" + Usage); return 0; }
        if (Parse(ref rest, allowName: false) is not { } parsed) return 2;
        if (rest.Length > 0) return Fail($"watch takes options only, not {rest[0]}");
        var choices = parsed with { Overwrite = true };
        var translations = Translations.Load(dataDir);
        Console.WriteLine($"Watching {Paths.DropFolder} (Ctrl+C to stop)");
        var sizes = new Dictionary<string, long>();
        while (true)
        {
            Directory.CreateDirectory(Paths.DropFolder);
            var ready = ExpandMaps([Paths.DropFolder]).Where(p =>
            {
                var size = new FileInfo(p).Length;
                var stable = sizes.TryGetValue(p, out var last) && last == size;
                sizes[p] = size;
                var output = DefaultTarget(p, choices.Folder, translations);
                return stable && (output is null || !File.Exists(output) || File.GetLastWriteTimeUtc(output) < File.GetLastWriteTimeUtc(p));
            }).ToList();
            if (ready.Count > 0) Report(ConvertWithBar(ready, choices));
            Thread.Sleep(3000);
        }
    }

    private static Choices? Parse(ref string[] rest, bool allowName)
    {
        bool Flag(ref string[] args, string name)
        {
            var on = args.Contains(name);
            args = args.Where(a => a != name).ToArray();
            return on;
        }
        var fake = Flag(ref rest, "--fake-author-time");
        var me = Flag(ref rest, "--me-as-author");
        var rebuild = Flag(ref rest, "--rebuild-blocks");
        var overwrite = Flag(ref rest, "--overwrite");
        var folder = Option(ref rest, "--folder");
        var name = allowName ? Option(ref rest, "--name") : null;
        var from = Option(ref rest, "--settings");
        var set = new List<(string, bool)>();
        while (Option(ref rest, "--set") is { } pair)
        {
            if (pair.Split('=') is not [var option, var value] || !bool.TryParse(value, out var on)) { Fail($"--set takes name=true|false, not '{pair}'"); return null; }
            try { new ConvertOptions().With(option, on); } catch (FormatException e) { Fail(e.Message); return null; }
            set.Add((option, on));
        }
        if (me && Paths.TestrunAuthorIfSet is null) { Fail($"--me-as-author needs \"testrunAuthor\" in {Paths.LocalSettings} (or set it in the GUI's Convert popup)"); return null; }
        if (name is not null && MapNames.FileName(name) is null) { Fail("--name can't hold < > : \" / \\ | ? * or end in a dot"); return null; }
        var dir = folder is null ? Paths.OutFolder : Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(Paths.Maps, folder));
        return new Choices(dir, name, fake, me, rebuild, overwrite) { From = from, Set = set };
    }

    private List<MapResult> ConvertWithBar(IReadOnlyList<string> maps, Choices choices)
    {
        var progress = new ConvertProgress();
        using var bar = new ConsoleProgress(progress);
        return Convert(maps, choices, progress);
    }

    private static int Report(List<MapResult> results)
    {
        foreach (var (map, done, error) in results)
        {
            Console.WriteLine($"{Path.GetFileName(map)}:");
            if (error is not null) { Console.Error.WriteLine($"  FAILED: {error}"); continue; }
            Console.WriteLine($"  settings: {done!.From}");
            foreach (var problem in done.Problems) Console.WriteLine($"  left out: {problem}");
            Console.WriteLine($"  {done.Summary} -> {done.Output}");
            Console.WriteLine($"  run {done.RunId}");
        }
        if (results.Select(r => r.Done?.MissingFiles ?? 0).DefaultIfEmpty().Max() is > 0 and var missing)
        {
            Console.WriteLine($"{missing} files the conversion needs aren't extracted; listed in the extract plugin's view-refs.txt.");
            Console.WriteLine("In game: Openplanet > Extract block meshes, then convert again (those items rebuild).");
        }
        return results.All(r => r.Error is null) ? 0 : 2;
    }

    private sealed record Prepared(string Path, MapSettings Settings, Translations Own, string From, List<string> Problems, string Name, string Target);

    // the whole run, terminal and GUI alike; one map's failure leaves the others going
    public List<MapResult> Convert(IReadOnlyList<string> paths, Choices choices, ConvertProgress progress)
    {
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { RewriteBlocks = choices.RebuildBlocks };
        var translations = Translations.Load(dataDir);
        var converter = new MapConverter(KnownBlocks(dataDir), translations, shapes, items);
        var log = RunLog.Default;
        var results = new List<MapResult>();

        progress.Start(0, paths.Count);
        var maps = new List<Prepared>();
        foreach (var (i, path) in paths.Index())
        {
            try { maps.Add(Prepare(path, choices, translations, log)); }
            catch (Exception e) when (e is FormatException or IOException or InvalidOperationException) { results.Add(new(path, null, e.Message)); }
            progress.Advance(i + 1);
        }
        Directory.CreateDirectory(choices.Folder);

        // dry runs to collect items, build them in parallel; built items can show new tops, so repeat
        items.Planning = true;
        for (var round = 0; round < 5; round++)
        {
            progress.Start(1, maps.Count, round == 0 ? null : $"round {round + 1}");
            foreach (var (i, map) in maps.Index())
            {
                try { converter.Convert(Gbx.ParseNode<CGameCtnChallenge>(map.Path), map.Own); }
                catch { }
                progress.Advance(i + 1);
            }
            if (!items.BuildPlanned((done, total) => { if (done == 0) progress.Start(2, total, "pieces"); else progress.Advance(done); })) break;
        }
        items.Planning = false;

        progress.Start(3, maps.Count);
        var converted = new List<(Prepared Map, Gbx<CGameCtnChallenge> Gbx, MapRef Source, ConversionReport Report)>();
        foreach (var (i, map) in maps.Index())
        {
            try
            {
                var gbx = Gbx.Parse<CGameCtnChallenge>(map.Path);
                var source = new MapRef(gbx.Node.MapUid, MapsRelative(map.Path), gbx.Node.MapName, MapFingerprint.Of(gbx.Node));
                var report = converter.Convert(gbx.Node, map.Own);
                MapOutput.Prepare(gbx.Node, map.Name, UidOf(gbx.Node.MapUid, map), choices.FakeAuthorTime, choices.MeAsAuthor);
                MapOutput.Embed(gbx.Node, report);
                converted.Add((map, gbx, source, report));
            }
            catch (Exception e) { results.Add(new(map.Path, null, $"{e.GetType().Name}: {e.Message}")); }
            progress.Advance(i + 1);
        }

        var missing = items.Missing.Concat(shapes.Missing).ToHashSet();
        if (missing.Count > 0) ExtractList.Add(missing);
        var failures = items.Failures.Select(f => $"{f.Key}: {f.Value}").ToList();
        progress.Start(4, converted.Count);
        foreach (var (i, (map, gbx, source, report)) in converted.Index())
        {
            try
            {
                MapOutput.Save(gbx, map.Target);
                File.WriteAllText(map.Target[..^".Map.Gbx".Length] + ".report.md", ReportWriter.Format(map.Path, report, items.Failures));
                var used = map.Settings.WithOptions(translations.For(source.Name, map.Own).Options.All());
                var run = log.Add(choices.Kind, source, new MapRef(gbx.Node.MapUid, MapsRelative(map.Target), map.Name, MapFingerprint.Of(gbx.Node)), used, map.Problems);
                var snow = report.SnowTerrain.Count > 0 ? $" {report.SnowTerrain.Values.Sum()} terrain in snow," : "";
                var summary = $"{report.Swapped.Values.Sum()} swapped, {report.ReplacedByBlock.Values.Sum()} custom blocks,{snow}"
                    + $" {report.ReplacedByItem.Values.Sum()} replaced by items, {report.Skinned.Values.Sum()} skinned, {report.Left} left as they were";
                results.Add(new(map.Path, new Outcome(map.Target, map.Name, run.Id, map.From, summary, map.Problems, failures, missing.Count), null));
            }
            catch (Exception e) { results.Add(new(map.Path, null, $"{e.GetType().Name}: {e.Message}")); }
            progress.Advance(i + 1);
        }
        progress.Finish();
        return [.. results.OrderBy(r => paths.ToList().IndexOf(r.Map))];
    }

    // the map's settings, name and target file; a target that's there needs Overwrite
    private static Prepared Prepare(string path, Choices choices, Translations translations, RunLog log)
    {
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        var family = log.FamilyOf(header.MapUid, () => Layout(path));
        var seedName = family.RootName is { Length: > 0 } root ? root : header.MapName;
        var (settings, from) = choices.Settings is { } handed ? (handed, "the GUI's settings") : choices.From switch
        {
            null => log.Current(family, translations, header.MapName),
            "seed" => (MapSettings.Seed(translations, seedName), "maps.jsonc"),
            "draft" => log.DraftOf(family) is { } draft ? (new MapSettings(draft.Settings), $"draft of {draft.Updated.ToLocalTime():yyyy-MM-dd HH:mm}")
                : throw new FormatException("no draft for this map"),
            var given when File.Exists(given) => (SettingsFile(given), given),
            var id => log.Run(id) is { } run ? (new MapSettings(run.Settings), $"run {run.Id}") : throw new FormatException($"--settings: no file or run '{id}'"),
        };
        if (choices.Set is { Count: > 0 } set) (settings, from) = (settings.WithOptions(set), from + " with --set");
        var problems = new List<string>();
        var own = settings.Read(problems);
        var name = choices.Name ?? MapNames.DefaultOutput(header.MapName, translations, own.Name);
        var target = Existing(choices.Folder, MapNames.FileName(name) ?? throw new FormatException($"\"{name}\" makes no file name"));
        if (File.Exists(target) && !choices.Overwrite) throw new IOException($"{target} is already there (overwrite to replace it)");
        return new Prepared(path, settings, own, from, problems, name, target);
    }

    // the file in the folder whatever its case (the game's file system ignores case), else the new path
    private static string Existing(string folder, string fileName)
        => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.Map.Gbx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)) is { } there
            ? there : Path.Combine(folder, fileName);

    private static string? DefaultTarget(string map, string folder, Translations translations)
    {
        try { return MapNames.FileName(MapNames.DefaultOutput(Gbx.ParseHeaderNode<CGameCtnChallenge>(map).MapName, translations)) is { } file ? Existing(folder, file) : null; }
        catch (Exception) { return null; }
    }

    // an overwritten map keeps its UID (re-releases stay the same map); a new file: from the original and the name
    private static string UidOf(string original, Prepared map)
    {
        if (File.Exists(map.Target))
            try { if (Gbx.ParseHeaderNode<CGameCtnChallenge>(map.Target).MapUid is { Length: > 0 } uid) return uid; }
            catch (Exception) { }
        return MapOutput.DerivedUid(original, "#" + MapNames.Plain(map.Name));
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
}
