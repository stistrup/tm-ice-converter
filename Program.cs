using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.ZLib;
using TmEssentials;
using TmIceConverter;

Gbx.LZO = new Lzo();
Gbx.ZLib = new ZLib();

var dataDir = FindDataDir();
Paths.LocalSettings = Path.Combine(dataDir, "local.jsonc");
var knownPath = Path.Combine(dataDir, "known-blocks.txt");

switch (args)
{
    case ["convert", .. var rest]:
        return Convert(rest);
    case ["watch", .. var rest]:
        return Watch(rest);
    case ["scan", .. var rest]:
        return Scan(rest.Length > 0 ? rest : [Paths.Maps]);
    case ["make-item", var block]:
        return new ItemBuilder(dataDir).Build(block) is { } error ? Fail(error) : 0;
    case ["show", .. var rest] when rest.Length > 0:
        return Show(rest);
    case ["view", .. var rest] when rest.Length > 0:
        return View(rest);
    case ["inspect", .. var rest] when rest.Length > 0:
        return Inspect(rest);
    default:
        Console.WriteLine($"""
            Usage:
              convert [map.Gbx | folder]... [-o <out dir>] [-t <tag> | --final | --testrun] [--set <option>=true|false]...
                  Convert maps to ice. Without maps: everything in the drop folder that changed since its last
                  conversion. Output goes to Maps/ICE in progress as "<name> (ICE).Map.Gbx" ("(ice <tag>)" with -t);
                  --final writes to the finished folder (finishedFolder in data/local.jsonc). Options (removeForcedCameras, keepPlasticWalls)
                  default from "options" in data/translations.jsonc, per map from "maps"; --set overrides them for the run.
                  --testrun writes "<name> - AN Ice" to Maps/Ice testrun, validated with a 1:00:00.000 author time, ready
                  for a club campaign without driving them.
              watch [-o <out dir>] [-t <tag>]
                  Keep converting maps as they land in the drop folder.
              scan [folder]...
                  Add block names from maps to data/known-blocks.txt (native ice blocks are used once seen there or extracted).
              make-item <BlockName>
                  Build one ice item now (convert builds them on demand anyway).
              show <item | BlockName | pattern*>... [-o <out dir>]
                  Render built items as a collage (four corners, top, underside, collision, materials) to PNG.
              view <map.Gbx> [-o <out dir>] [--no-render]
                  Build a textured Blender scene of a map (.blend to fly around in) and render a few views.
              inspect <map.Gbx | view dir> [-o <out dir>]
                  Open a map's scene in Blender with the Ice inspector (sidebar, Tool tab): what each block/item is, marks with notes
                  (saved to marks.json in the view dir), filter by name. A map is rebuilt first; a view dir opens as is.

            Drop folder: {Paths.DropFolder}
            """);
        return 1;
}

int Convert(string[] rest)
{
    var final = rest.Contains("--final");
    var testrun = rest.Contains("--testrun");
    rest = rest.Where(a => a is not ("--final" or "--testrun")).ToArray();
    if (final && testrun) return Fail("--final and --testrun go to different folders; pick one");
    var outDir = Option(ref rest, "-o") ?? (final ? Paths.FinishedFolder : testrun ? Paths.TestrunFolder : Paths.OutFolder);
    var tag = Option(ref rest, "-t");
    if (testrun && tag is not null) return Fail("--testrun makes \"<name> - AN Ice\"; leave out -t");
    if (final && tag is not null) return Fail("--final makes \"<name> (ICE)\"; leave out -t");
    var settings = new List<(string, bool)>();
    while (Option(ref rest, "--set") is { } set)
    {
        if (set.Split('=') is not [var name, var value] || !bool.TryParse(value, out var on)) return Fail($"--set takes name=true|false, not '{set}'");
        try { new ConvertOptions().With(name, on); } catch (FormatException e) { return Fail(e.Message); }
        settings.Add((name, on));
    }
    var dropFolder = rest.Length == 0;
    var maps = dropFolder ? ChangedDropMaps(outDir, testrun ? TestrunSuffix() : Suffix(tag)) : ExpandMaps(rest).ToList();
    if (dropFolder && maps.Count == 0) Console.WriteLine($"Nothing new in {Paths.DropFolder}");
    return ConvertAll(maps, outDir, tag, settings, testrun);
}

// polling: the game writes maps in steps
int Watch(string[] rest)
{
    var outDir = Option(ref rest, "-o") ?? Paths.OutFolder;
    var tag = Option(ref rest, "-t");
    Console.WriteLine($"Watching {Paths.DropFolder} (Ctrl+C to stop)");
    var sizes = new Dictionary<string, long>();
    while (true)
    {
        var ready = ChangedDropMaps(outDir, Suffix(tag)).Where(p =>
        {
            var size = new FileInfo(p).Length;
            var stable = sizes.TryGetValue(p, out var last) && last == size;
            sizes[p] = size;
            return stable;
        }).ToList();
        if (ready.Count > 0) ConvertAll(ready, outDir, tag);
        Thread.Sleep(3000);
    }
}

List<string> ChangedDropMaps(string outDir, string suffix)
{
    Directory.CreateDirectory(Paths.DropFolder);
    var translations = Translations.Load(Path.Combine(dataDir, "translations.jsonc"));
    return ExpandMaps([Paths.DropFolder]).Where(p =>
    {
        var output = OutputPath(p, outDir, suffix, translations);
        return !File.Exists(output) || File.GetLastWriteTimeUtc(output) < File.GetLastWriteTimeUtc(p);
    }).ToList();
}

int ConvertAll(IReadOnlyList<string> maps, string outDir, string? tag, IEnumerable<(string, bool)>? settings = null, bool dummyTimes = false)
{
    var suffix = dummyTimes ? TestrunSuffix() : Suffix(tag);
    Directory.CreateDirectory(outDir);
    var shapes = new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>());
    var items = new ItemBuilder(dataDir);
    var translations = Translations.Load(Path.Combine(dataDir, "translations.jsonc"));
    translations.RunSettings.AddRange(settings ?? []);
    var converter = new Converter(KnownBlocks(), translations, shapes, items);

    // dry runs to collect items, build them in parallel; built items can show new tops, so repeat
    items.Planning = true;
    for (var round = 0; round < 5; round++)
    {
        foreach (var path in maps)
            try { converter.Convert(Gbx.ParseNode<CGameCtnChallenge>(path)); }
            catch { }
        if (!items.BuildPlanned()) break;
    }
    items.Planning = false;

    var failed = 0;
    foreach (var path in maps)
    {
        Console.WriteLine($"{Path.GetFileName(path)}:");
        try
        {
            var gbx = Gbx.Parse<CGameCtnChallenge>(path);
            var report = converter.Convert(gbx.Node);
            gbx.Node.MapName = translations.NameFor(Regex.Replace(gbx.Node.MapName, @"\s*\(ice[^)]*\)$", "", RegexOptions.IgnoreCase)) + suffix;
            // own UID, stable across reconverts
            gbx.Node.MapUid = DerivedMapUid(gbx.Node.MapUid, dummyTimes ? $"{suffix}{Paths.TestrunAuthor.Login}#{Paths.TestrunGeneration}" : suffix);
            gbx.Node.Kind = CGameCtnChallenge.MapKind.InProgress;
            EnsureHeaderChunks(gbx.Node);
            ClearValidation(gbx.Node);
            if (dummyTimes) { DummyValidation(gbx.Node); AsTestrunAuthor(gbx.Node); }
            // clubs reject maps without a lightmap declared
            (gbx.Node.LightmapVersion, gbx.Node.LightmapFrames, gbx.Node.LightmapCache) = (null, null, null);
            if (report.UsedItems.Count > 0)
            {
                // keep embedded entries findable by name
                var embedded = gbx.Node.ExpectedEmbeddedItemModels ?? [];
                gbx.Node.UpdateEmbeddedZipData(zip =>
                {
                    foreach (var ident in embedded)
                    {
                        var want = "Items/" + ident.Id.Replace('\\', '/');
                        if (zip.GetEntry(want) is not null) continue;
                        if (zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/" + ident.Id.Replace('\\', '/'))) is not { } odd) continue;
                        using (var from = odd.Open())
                        using (var to = zip.CreateEntry(want).Open())
                            from.CopyTo(to);
                        odd.Delete();
                    }
                    foreach (var item in report.UsedItems)
                        zip.CreateEntryFromFile(item.FilePath, "Items/" + item.Ident.Id.Replace('\\', '/'));
                });
            }

            var outPath = OutputPath(path, outDir, suffix, translations);
            gbx.Save(outPath);
            // second save fixes the header's lightmap version
            Gbx.Parse<CGameCtnChallenge>(outPath).Save(outPath);
            File.WriteAllText(outPath[..^".Map.Gbx".Length] + ".report.md", FormatReport(path, report, items.Failures));

            Console.WriteLine($"  {report.Swapped.Values.Sum()} swapped, {report.ReplacedByItem.Values.Sum()} replaced by items, {report.Skinned.Values.Sum()} skinned, {report.Left} left as they were -> {outPath}");
            Console.WriteLine(dummyTimes ? "  Validated with a dummy 1:00:00.000 author time; no shadows."
                : "  In game: open it in the editor, compute shadows (Normal if the map is near 7.1 MB), validate, save.");
        }
        catch (Exception e)
        {
            failed++;
            Console.Error.WriteLine($"  FAILED: {e.GetType().Name}: {e.Message}");
        }
    }
    var missingFiles = items.Missing.Concat(shapes.Missing).ToHashSet();
    if (missingFiles.Count > 0)
    {
        ListForExtraction(missingFiles);
        Console.WriteLine($"{missingFiles.Count} files the conversion needs aren't extracted; listed in the extract plugin's view-refs.txt.");
        Console.WriteLine("In game: Openplanet > Extract block meshes, then convert again (those items rebuild).");
    }
    return failed == 0 ? 0 : 2;
}

HashSet<string> KnownBlocks()
{
    var names = File.ReadAllLines(knownPath).ToHashSet();
    foreach (var blockInfos in Paths.BlockInfoDirs(Paths.ExtractedStadium))
        names.UnionWith(Directory.EnumerateFiles(blockInfos, "*.Gbx", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).Split('.')[0]));
    return names;
}

int Scan(string[] folders)
{
    var names = File.Exists(knownPath) ? File.ReadAllLines(knownPath).ToHashSet() : [];
    var before = names.Count;
    foreach (var path in ExpandMaps(folders))
    {
        try { names.UnionWith(Gbx.ParseNode<CGameCtnChallenge>(path).GetBlocks().Select(b => b.Name)); }
        catch (Exception e) { Console.Error.WriteLine($"skip {Path.GetFileName(path)}: {e.GetType().Name}"); }
    }
    File.WriteAllLines(knownPath, names.Order(StringComparer.Ordinal));
    Console.WriteLine($"{names.Count} known block names ({names.Count - before} new)");
    return 0;
}

int Show(string[] rest)
{
    var outDir = Option(ref rest, "-o") ?? Path.Combine(Paths.Work, "Previews");
    var meshDir = Path.Combine(Paths.Work, "Mesh");
    var script = Path.Combine(dataDir, "..", "tools", "render_item.py");
    Directory.CreateDirectory(outDir);
    var failed = 0;
    foreach (var arg in rest)
    {
        var pattern = arg.Contains('*') || arg.Contains("_Ice") ? arg : arg + "_Ice";
        var found = Directory.GetFiles(meshDir, pattern + ".pieces.json");
        if (found.Length == 0) { failed++; Console.Error.WriteLine($"{arg}: no built item (build it with make-item)"); continue; }
        foreach (var pieces in found.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(pieces)[..^".pieces.json".Length];
            var png = Path.Combine(outDir, name + ".png");
            if (ItemBuilder.RunQuiet("blender", ["--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", pieces, png], out var log))
                Console.WriteLine(png);
            else { failed++; Console.Error.WriteLine($"{name}: Blender failed:\n{log}"); }
        }
    }
    return failed == 0 ? 0 : 2;
}

int ListForExtraction(IEnumerable<string> paths)
{
    var file = Path.Combine(Paths.ExtractPlugin, "view-refs.txt");
    var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
    var listed = (File.Exists(file) ? File.ReadAllLines(file) : []).Concat(paths)
        .Select(p => p.Trim()).Where(p => p.Length > 0 && !OnDisk(Path.Combine(gameData, p)))
        .ToHashSet().Order(StringComparer.Ordinal).ToList();
    File.WriteAllLines(file, listed);
    return listed.Count;

    static bool OnDisk(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };
}

int View(string[] rest, bool blendOnly = false)
{
    var noRender = rest.Contains("--no-render");
    rest = rest.Where(a => a != "--no-render").ToArray();
    var map = rest[0];
    var name = Path.GetFileName(map).Replace(".Map.Gbx", "", StringComparison.OrdinalIgnoreCase);
    var outDir = Option(ref rest, "-o") ?? Path.Combine(Paths.Views, name);
    Directory.CreateDirectory(outDir);

    var shapes = new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>());
    var view = new MapView(shapes);
    var scene = Path.Combine(outDir, "scene.json");
    var node = Gbx.ParseNode<CGameCtnChallenge>(map);
    shapes.Environment = node.Collection?.ToString();
    view.Write(node, scene);
    Console.WriteLine($"Scene: {scene}");
    foreach (var (what, n) in view.Skipped.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  skipped {what} x{n}");

    if (ListForExtraction(view.Missing) is > 0 and var missing)
    {
        Console.WriteLine($"  {missing} files not extracted yet (textures show as plain colours); listed in the extract plugin's view-refs.txt.");
        Console.WriteLine("  In game: Openplanet > Extract block meshes, then run view again.");
    }
    if (noRender) return 0;

    var script = Path.Combine(dataDir, "..", "tools", "view_map.py");
    string[] width = blendOnly ? ["0"] : [];
    if (!ItemBuilder.RunQuiet("blender", ["--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", scene, outDir, .. width], out var log))
        return Fail("Blender failed:\n" + log);
    foreach (var line in log.Split('\n').Where(l => l.StartsWith("wrote ") || l.StartsWith("view: "))) Console.WriteLine("  " + line);
    return 0;
}

int Inspect(string[] rest)
{
    var outDir = Directory.Exists(rest[0]) ? rest[0] : null;
    if (outDir is null)
    {
        var map = rest[0];
        outDir = Option(ref rest, "-o") ?? Path.Combine(Paths.Views, Path.GetFileName(map).Replace(".Map.Gbx", "", StringComparison.OrdinalIgnoreCase));
        if (View([map, "-o", outDir], blendOnly: true) is not 0 and var code) return code;
    }
    var blend = Path.Combine(outDir, "map.blend");
    if (!File.Exists(blend)) return Fail($"No map.blend in {outDir}");
    var script = Path.GetFullPath(Path.Combine(dataDir, "..", "tools", "inspect_map.py"));
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("blender", [blend, "--python", script]) { UseShellExecute = false });
    Console.WriteLine($"Opened {blend}; marks go to {Path.Combine(outDir, "marks.json")}");
    return 0;
}

static int Fail(string error)
{
    Console.Error.WriteLine(error);
    return 2;
}

static string Suffix(string? tag) => tag is null ? " (ICE)" : $" (ice {tag})";

static string TestrunSuffix() => " - AN Ice";

static string OutputPath(string map, string outDir, string suffix, Translations translations)
    => Path.Combine(outDir, translations.NameFor(Regex.Replace(Path.GetFileName(map), @"\.Map\.Gbx$", "", RegexOptions.IgnoreCase)) + suffix + ".Map.Gbx");

static IEnumerable<string> ExpandMaps(IEnumerable<string> inputs) => inputs.SelectMany(p =>
    Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.Map.Gbx", SearchOption.AllDirectories) : [p]);

// replay-extracted maps have no header chunks
static void EnsureHeaderChunks(CGameCtnChallenge map)
{
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043002>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043002>().Version = 13;
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043003>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043003>().Version = 11;
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043004>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043004>().Version = 6;
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043005>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043005>();
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043007>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043007>().Version = 1;
    if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043008>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043008>().Version = 1;
    map.KindInHeader = map.Kind;
    map.MapType ??= "TrackMania\\TM_Race";
    map.MapStyle ??= "";
    if (map.ChallengeParameters is { } p)
    {
        map.BronzeTime ??= p.BronzeTime;
        map.SilverTime ??= p.SilverTime;
        map.GoldTime ??= p.GoldTime;
        map.AuthorTime ??= p.AuthorTime;
        map.AuthorScore = p.AuthorScore;
    }
    map.Xml ??= $"""<header type="map" title="{map.TitleId}"><ident uid="{map.MapUid}" name="{System.Security.SecurityElement.Escape(map.MapName)}" author="{map.AuthorLogin}"/><desc envir="Stadium" mood="Day" type="Race" maptype="TrackMania\TM_Race" validated="0" nblaps="0"/></header>""";
}

static void ClearValidation(CGameCtnChallenge map)
{
    map.BronzeTime = map.SilverTime = map.GoldTime = map.AuthorTime = null;
    map.AuthorScore = 0;
    if (map.ChallengeParameters is { } p)
    {
        p.BronzeTime = p.SilverTime = p.GoldTime = p.AuthorTime = null;
        p.AuthorScore = 0;
        p.RaceValidateGhost = null;
    }
    if (map.Xml is { } xml)
    {
        xml = Regex.Replace(xml, @"(<ident uid="")[^""]*", m => m.Groups[1].Value + map.MapUid);
        xml = Regex.Replace(xml, @"(<ident [^>]*name="")[^""]*", m => m.Groups[1].Value + System.Security.SecurityElement.Escape(map.MapName));
        xml = Regex.Replace(xml, @"validated=""\d""", @"validated=""0""");
        xml = Regex.Replace(xml, @"<times [^>]*", m => Regex.Replace(Regex.Replace(m.Value,
            @"(bronze|silver|gold|authortime)=""[^""]*""", @"$1=""-1"""), @"authorscore=""[^""]*""", @"authorscore=""0"""));
        map.Xml = xml;
    }
}

static void DummyValidation(CGameCtnChallenge map)
{
    const int author = 3_600_000;
    static int Medal(double ratio) => (int)Math.Ceiling(author * ratio / 1000) * 1000;
    var (gold, silver, bronze) = (Medal(1.06), Medal(1.2), Medal(1.5));
    (map.AuthorTime, map.GoldTime, map.SilverTime, map.BronzeTime) = (TimeInt32.FromMilliseconds(author), TimeInt32.FromMilliseconds(gold), TimeInt32.FromMilliseconds(silver), TimeInt32.FromMilliseconds(bronze));
    if (map.ChallengeParameters is { } p)
        (p.AuthorTime, p.GoldTime, p.SilverTime, p.BronzeTime) = (map.AuthorTime, map.GoldTime, map.SilverTime, map.BronzeTime);
    if (map.Xml is { } xml)
    {
        xml = Regex.Replace(xml, @"validated=""\d""", @"validated=""1""");
        xml = Regex.Replace(xml, @"<times [^>]*", m => Regex.Replace(m.Value, @"(bronze|silver|gold|authortime)=""[^""]*""", t => t.Groups[1].Value switch
        {
            "bronze" => $@"bronze=""{bronze}""",
            "silver" => $@"silver=""{silver}""",
            "gold" => $@"gold=""{gold}""",
            _ => $@"authortime=""{author}""",
        }));
        map.Xml = xml;
    }
}

static void AsTestrunAuthor(CGameCtnChallenge map)
{
    var (login, nickname, zone) = Paths.TestrunAuthor;
    (map.AuthorLogin, map.AuthorNickname, map.AuthorZone) = (login, nickname, zone);
    if (map.MapInfo is { } info) map.MapInfo = info with { Author = login };
    map.Kind = map.KindInHeader = CGameCtnChallenge.MapKind.Multi;
    if (map.Xml is { } xml)
        map.Xml = Regex.Replace(xml, @"(<ident [^>]*)author=""[^""]*""(?: authorzone=""[^""]*"")?",
            m => $@"{m.Groups[1].Value}author=""{login}"" authorzone=""{System.Security.SecurityElement.Escape(zone)}""");
}

static string? Option(ref string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length) return null;
    var value = args[i + 1];
    args = [.. args[..i], .. args[(i + 2)..]];
    return value;
}

static string DerivedMapUid(string original, string suffix)
    => System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(original + suffix)))
        .Replace('+', '-').Replace('/', '_')[..27];

static string FormatReport(string source, ConversionReport report, IReadOnlyDictionary<string, string> itemFailures)
{
    var sb = new StringBuilder($"# {Path.GetFileName(source)}\n\n");
    sb.AppendLine("After loading: compute shadows (the map has none; Normal keeps it smaller, rooms take up to 7.1 MB), validate, save.\n");

    if (report.ReplacedByItem.Count > 0)
    {
        sb.AppendLine($"## Replaced by ice items ({report.ReplacedByItem.Values.Sum()})\n");
        foreach (var (name, n) in report.ReplacedByItem.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        foreach (var (name, n) in report.KeptByItem) sb.AppendLine($"- {name} x{n}: native neighbour (pillar, road…) that would change next to the items, rebuilt as it was");
        sb.AppendLine();
    }

    if (report.Skinned.Count > 0)
    {
        sb.AppendLine($"## Kept under an ice skin ({report.Skinned.Values.Sum()})\n");
        sb.AppendLine("Ground blocks and deco blocks set into the terrain stay native; their drivable surface gets an ice item on top (not the flat foot at terrain level).\n");
        foreach (var (name, n) in report.Skinned.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        sb.AppendLine();
    }

    if (report.SpecialSkinned.Count > 0)
    {
        sb.AppendLine($"## Gameplay blocks, road iced around the pad ({report.SpecialSkinned.Values.Sum()})\n");
        sb.AppendLine("The block stays for its effect; an ice skin covers its road, the native pad shows through.\n");
        foreach (var (name, n) in report.SpecialSkinned.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
        sb.AppendLine();
    }

    if (report.Left > 0)
    {
        sb.AppendLine($"## Left as they were ({report.Left})\n");
        foreach (var (name, n) in report.Unmatched.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"- {name} x{n}: {(itemFailures.TryGetValue(name, out var why) ? FirstLine(why) : "no ice version")}");
        foreach (var (name, n) in report.SpecialKept) sb.AppendLine($"- {name} x{n}: gameplay or pillar block, kept on purpose");
        sb.AppendLine("\nA block with a known ice equivalent can go in `blocks` in data/translations.jsonc.\n");
    }

    if (report.FreeBlockOffsetsSkipped > 0)
        sb.AppendLine($"Note: {report.FreeBlockOffsetsSkipped} free-placed blocks had an `offset` that was not applied.\n");

    sb.AppendLine($"## Swapped ({report.Swapped.Values.Sum()})\n");
    sb.AppendLine("| Count | From | To | Source |\n|---:|---|---|---|");
    foreach (var ((from, to, src), n) in report.Swapped.OrderByDescending(kv => kv.Value))
        sb.AppendLine($"| {n} | {from} | {to} | {src} |");

    if (report.SwappedItems.Count > 0)
    {
        sb.AppendLine($"\n## Items swapped ({report.SwappedItems.Values.Sum()})\n");
        sb.AppendLine("| Count | From | To |\n|---:|---|---|");
        foreach (var ((from, to), n) in report.SwappedItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"| {n} | {from} | {to} |");
    }

    if (report.EngineOff.Count > 0)
    {
        sb.AppendLine($"\n## Engine-off blocks ({report.EngineOff.Values.Sum()})\n");
        sb.AppendLine("Options engineOffToReset / engineOffToPlain: the ones you drive on became the block shown (reset or plain), the gates were removed.\n");
        foreach (var ((from, to), n) in report.EngineOff) sb.AppendLine($"- {from} x{n}: {(to == "" ? "removed" : to)}");
    }
    if (report.IcedItems.Count > 0)
    {
        sb.AppendLine($"\n## Items iced ({report.IcedItems.Values.Sum()})\n");
        sb.AppendLine("Placed items with a driving surface (inflatable tubes and mats, ramps), now ice copies built from their own meshes.\n");
        foreach (var (name, n) in report.IcedItems.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {name} x{n}");
    }
    if (report.ItemsNotIced.Count > 0)
    {
        sb.AppendLine($"\n## Items left as they were ({report.ItemsNotIced.Values.Sum()})\n");
        foreach (var (name, n) in report.ItemsNotIced) sb.AppendLine($"- {name} x{n}: {FirstLine(itemFailures[name])}");
    }

    if (report.CameraClipsRemoved.Count > 0)
    {
        sb.AppendLine($"\n## Forced cameras removed ({report.CameraClipsRemoved.Count})\n");
        sb.AppendLine("In-game MediaTracker clips that forced the camera (or only switched it back), taken out.\n");
        foreach (var name in report.CameraClipsRemoved) sb.AppendLine($"- {name}");
    }
    if (report.Walls.Count > 0)
    {
        sb.AppendLine($"\n## Walls, kept native ({report.Walls.Values.Sum()})\n");
        sb.AppendLine("Only walls, nothing to ice: the native wall stone stays (an item would be plain concrete).\n");
        foreach (var (name, n) in report.Walls) sb.AppendLine($"- {name} x{n}");
    }
    if (report.TopsIced.Count > 0)
    {
        sb.AppendLine($"\n## Walls and concrete, tops iced ({report.TopsIced.Values.Sum()})\n");
        sb.AppendLine("Kept native (walls, concrete, structure decks, pillar tops, flipped platforms); an ice skin covers their wall material where it faces up (tops, slopes, curves).\n");
        foreach (var (name, n) in report.TopsIced) sb.AppendLine($"- {name} x{n}");
    }
    if (report.Concrete.Count > 0)
    {
        sb.AppendLine($"\n## Concrete, kept ({report.Concrete.Values.Sum()})\n");
        sb.AppendLine("White deco blocks show concrete; with no native ice version in this environment they stay as they are.\n");
        foreach (var (name, n) in report.Concrete) sb.AppendLine($"- {name} x{n}");
    }
    if (report.Gated.Count > 0)
    {
        sb.AppendLine($"\n## Effect roads made ice under a gate ({report.Gated.Values.Sum()})\n");
        sb.AppendLine("Option effectsToGate: an ice road item without the pad, Nadeo's 32 m gate for the effect (slow motion, reset) across its middle.\n");
        foreach (var (name, n) in report.Gated) sb.AppendLine($"- {name} x{n}");
    }
    if (report.Sunk.Count > 0)
    {
        sb.AppendLine($"\n## Items sunk 2 cm under the ice ({report.Sunk.Values.Sum()})\n");
        sb.AppendLine("Their flat top lay in a converted surface and flickered with it.\n");
        foreach (var (name, n) in report.Sunk) sb.AppendLine($"- {name} x{n}");
    }
    if (report.Recoloured.Count > 0)
    {
        sb.AppendLine($"\n## Platforms given the map's platform colour ({report.Recoloured.Values.Sum()})\n");
        sb.AppendLine("Left at Default (yellow borders) among platforms that all share one colour.\n");
        foreach (var (name, n) in report.Recoloured) sb.AppendLine($"- {name} x{n}");
    }
    if (report.RemovedByList.Count > 0)
    {
        sb.AppendLine($"\n## Removed by the map's removeItems list ({report.RemovedByList.Values.Sum()})\n");
        foreach (var (name, n) in report.RemovedByList) sb.AppendLine($"- {name} x{n}");
    }
    if (report.KeptByList.Count > 0)
    {
        sb.AppendLine($"\n## Kept by the map's keep list ({report.KeptByList.Values.Sum()})\n");
        foreach (var (name, n) in report.KeptByList) sb.AppendLine($"- {name} x{n}");
    }
    if (report.Kept.Count > 0)
    {
        sb.AppendLine($"\n## Kept by table ({report.Kept.Values.Sum()})\n");
        foreach (var (name, n) in report.Kept) sb.AppendLine($"- {name} x{n}");
    }
    return sb.ToString();
}

static string FirstLine(string text) => text.Split('\n', 2)[0];

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "TmIceConverter.csproj")))
            return Path.Combine(dir.FullName, "data");
    return Path.Combine(AppContext.BaseDirectory, "data");
}
