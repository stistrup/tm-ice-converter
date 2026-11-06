using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// convert, watch
internal sealed class ConvertCommand(string dataDir)
{
    // testrun: validated with a dummy time. release: the user as author, no times, its own UID apart from the test runs
    private sealed record Job(string OutDir, string? Tag, IReadOnlyList<(string, bool)>? Settings = null, bool Testrun = false, bool Release = false, string? MapName = null, bool RewriteBlocks = false)
    {
        public string Suffix => Testrun || Release ? PublishedSuffix : TagSuffix(Tag);
    }

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
        if ((testrun || release) && tag is not null) return Fail("--testrun and --release make \"<name> - AN Ice\"; leave out -t");
        if (final && tag is not null) return Fail("--final makes \"<name> (ICE)\"; leave out -t");
        var settings = new List<(string, bool)>();
        while (Option(ref rest, "--set") is { } set)
        {
            if (set.Split('=') is not [var name, var value] || !bool.TryParse(value, out var on)) return Fail($"--set takes name=true|false, not '{set}'");
            try { new ConvertOptions().With(name, on); } catch (FormatException e) { return Fail(e.Message); }
            settings.Add((name, on));
        }
        var job = new Job(outDir, tag, settings, testrun, release, mapName, rewriteBlocks);
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

    private int ConvertAll(IReadOnlyList<string> maps, Job job)
    {
        Directory.CreateDirectory(job.OutDir);
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { RewriteBlocks = job.RewriteBlocks };
        var translations = Translations.Load(dataDir);
        translations.RunSettings.AddRange(job.Settings ?? []);
        var converter = new MapConverter(KnownBlocks(dataDir), translations, shapes, items);

        BuildItems(maps, converter, items);

        var failed = 0;
        foreach (var path in maps)
        {
            Console.WriteLine($"{Path.GetFileName(path)}:");
            try { ConvertMap(path, job, converter, translations, items); }
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
    private static void BuildItems(IReadOnlyList<string> maps, MapConverter converter, ItemBuilder items)
    {
        items.Planning = true;
        for (var round = 0; round < 5; round++)
        {
            foreach (var path in maps)
                try { converter.Convert(Gbx.ParseNode<CGameCtnChallenge>(path)); }
                catch { }
            if (!items.BuildPlanned()) break;
        }
        items.Planning = false;
    }

    private static void ConvertMap(string path, Job job, MapConverter converter, Translations translations, ItemBuilder items)
    {
        var gbx = Gbx.Parse<CGameCtnChallenge>(path);
        var report = converter.Convert(gbx.Node);
        var suffix = job.Suffix;
        var name = job.MapName ?? translations.NameFor(Regex.Replace(gbx.Node.MapName, @"\s*\(ice[^)]*\)$", "", RegexOptions.IgnoreCase)) + suffix;
        MapOutput.Prepare(gbx.Node, name, UidOf(gbx.Node.MapUid, job), job.Testrun, job.Release);
        MapOutput.Embed(gbx.Node, report);

        var outPath = job.MapName is null ? OutputPath(path, job.OutDir, suffix, translations) : Path.Combine(job.OutDir, job.MapName + ".Map.Gbx");
        MapOutput.Save(gbx, outPath);
        File.WriteAllText(outPath[..^".Map.Gbx".Length] + ".report.md", ReportWriter.Format(path, report, items.Failures));

        var snow = report.SnowTerrain.Count > 0 ? $" {report.SnowTerrain.Values.Sum()} terrain in snow," : "";
        Console.WriteLine($"  {report.Swapped.Values.Sum()} swapped, {report.ReplacedByBlock.Values.Sum()} custom blocks,{snow}"
            + $" {report.ReplacedByItem.Values.Sum()} replaced by items, {report.Skinned.Values.Sum()} skinned, {report.Left} left as they were -> {outPath}");
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
