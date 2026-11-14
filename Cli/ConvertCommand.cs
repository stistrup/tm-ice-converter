using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.CliArgs;

namespace TmSurfaceEditor;

// convert, watch: the options the GUI's Convert popup has
internal sealed class ConvertCommand(string dataDir)
{
    // Folder: where the maps go (absolute). Name: one map only, null = "<map name> (ICE)".
    // Settings: handed over whole (the GUI's working ones); else From (--settings) or the map's saved settings / the defaults, Set over them
    public sealed record Choices(string Folder, string? Name = null, bool FakeAuthorTime = false, bool MeAsAuthor = false, bool RebuildBlocks = false, bool Overwrite = false)
    {
        public MapSettings? Settings { get; init; }
        // after each default name
        public string Suffix { get; init; } = MapNames.DefaultSuffix;
        public string? From { get; init; }
        // applied on top of the settings (data/presets.jsonc)
        public string? Preset { get; init; }
        public IReadOnlyList<(string, bool)> Set { get; init; } = [];

        public string Kind => string.Join(", ", new[] { "convert", FakeAuthorTime ? "fake author time" : null, MeAsAuthor ? "me as author" : null }.OfType<string>());
    }

    // Output: absolute
    public sealed record Outcome(string Output, string Name, string RunId, string Report, string From, string Summary, List<string> Problems, List<string> Failures, int MissingFiles);

    public sealed record MapResult(string Map, Outcome? Done, string? Error);

    public const string Usage = """
          convert <map.Gbx | folder>... [options]
              Convert maps to ice, each to "<map name> (ICE).Map.Gbx"; the report goes with the run (settings/runs).
              --folder <dir>        where they go: a folder under Maps, or an absolute path (default: ICE in progress)
              --name <map name>     the converted map's name (one map only); the file is named after it
              --fake-author-time    validated with a 1:00:00.000 author time, so it can go to a club without driving it
              --me-as-author        your account (testrunAuthor in local.jsonc) as the map's author
              --rebuild-blocks      write the custom blocks again even when their items didn't change
              --overwrite           replace a map of the same file name; it keeps that map's UID
              --settings <from>     current (the map's working copy from the GUI, else its saved settings), saved,
                                    a settings file, or a run id or file
              --preset <name>       a preset from data/presets.jsonc; alone it's all the map converts by, with --settings it goes on top
                                    One of the two is needed: nothing converts by default.
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
        var dataRules = ConversionRules.Load(dataDir);
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
                var output = DefaultTarget(p, choices.Folder, dataRules);
                return stable && (output is null || !File.Exists(output) || File.GetLastWriteTimeUtc(output) < File.GetLastWriteTimeUtc(p));
            }).ToList();
            if (ready.Count > 0) Report(ConvertWithBar(ready, choices));
            Thread.Sleep(3000);
        }
    }

    // --preset alone: nothing under it
    internal const string None = "none";

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
        var preset = Option(ref rest, "--preset");
        if (from is null && preset is null)
        {
            Fail("which settings? --settings current (the GUI's working copy, else saved), --settings saved, a settings file or run id; or --preset <name>");
            return null;
        }
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
        return new Choices(dir, name, fake, me, rebuild, overwrite) { From = from ?? None, Set = set, Preset = preset };
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
            Console.WriteLine($"  run {done.RunId}, report {done.Report}");
        }
        if (results.Select(r => r.Done?.MissingFiles ?? 0).DefaultIfEmpty().Max() is > 0 and var missing)
        {
            Console.WriteLine($"{missing} files the conversion needs aren't extracted; listed in the extract plugin's view-refs.txt.");
            Console.WriteLine("In game: Openplanet > Extract game files, then convert again (those items rebuild).");
        }
        return results.All(r => r.Error is null) ? 0 : 2;
    }

    // the whole run, terminal and GUI alike; one map's failure leaves the others going
    // cancel: stops before the next map or step; pieces being built finish, maps saved so far stay
    public List<MapResult> Convert(IReadOnlyList<string> paths, Choices choices, ConvertProgress progress, CancellationToken cancel = default)
        => new ConvertBatch(dataDir, choices, progress, cancel).Run(paths);

    // the file in the folder whatever its case (the game's file system ignores case), else the new path
    internal static string Existing(string folder, string fileName)
        => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.Map.Gbx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)) is { } there
            ? there : Path.Combine(folder, fileName);

    private static string? DefaultTarget(string map, string folder, ConversionRules dataRules)
    {
        try
        {
            var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(map);
            return MapNames.FileName(MapNames.DefaultOutput(header.MapName, SettingsStore.Default.NameOf(header.MapUid))) is { } file ? Existing(folder, file) : null;
        }
        catch (Exception) { return null; }
    }

    // decisions only, nothing built or saved: times one map's conversion in planning mode
    public int Plan(string[] rest)
    {
        if (rest is not [var path]) return Fail("plan takes one map");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { Planning = true };
        var dataRules = ConversionRules.Load(dataDir);
        var converter = new MapConverter(KnownBlocks(dataDir), dataRules, shapes, items);
        Console.WriteLine($"setup {clock.ElapsedMilliseconds} ms");
        for (var i = 1; i <= 3; i++)
        {
            clock.Restart();
            var map = Gbx.ParseNode<CGameCtnChallenge>(path);
            var parse = clock.ElapsedMilliseconds;
            var report = converter.Convert(map, new ConversionRules { RulesOn = true });
            Console.WriteLine($"run {i}: parse {parse} ms, decide {clock.ElapsedMilliseconds - parse} ms, custom blocks {report.UsedBlocks.Count}");
        }
        return 0;
    }

    internal static HashSet<string> KnownBlocks(string dataDir)
    {
        var names = File.ReadAllLines(Path.Combine(dataDir, "known-blocks.txt")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var blockInfos in Paths.BlockInfoDirs(Paths.ExtractedStadium))
            names.UnionWith(Directory.EnumerateFiles(blockInfos, "*.Gbx", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).Split('.')[0]));
        return names;
    }
}
