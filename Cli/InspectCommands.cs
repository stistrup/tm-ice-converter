using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// show, view, inspect, clipcheck
internal sealed class InspectCommands(string dataDir)
{
    public int Show(string[] rest)
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
                if (Importer.RunQuiet("blender", ["--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", pieces, png], out var log))
                    Console.WriteLine(png);
                else { failed++; Console.Error.WriteLine($"{name}: Blender failed:\n{log}"); }
            }
        }
        return failed == 0 ? 0 : 2;
    }

    public int View(string[] rest, bool blendOnly = false)
    {
        var noRender = rest.Contains("--no-render");
        rest = rest.Where(a => a != "--no-render").ToArray();
        var map = rest[0];
        var name = Path.GetFileName(map).Replace(".Map.Gbx", "", StringComparison.OrdinalIgnoreCase);
        var outDir = Option(ref rest, "-o") ?? Path.Combine(Paths.Views, name);
        Directory.CreateDirectory(outDir);

        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var view = new MapView(shapes);
        var scene = Path.Combine(outDir, "scene.json");
        var node = Gbx.ParseNode<CGameCtnChallenge>(map);
        shapes.Environment = node.Collection?.ToString();
        view.Write(node, scene);
        Console.WriteLine($"Scene: {scene}");
        foreach (var (what, n) in view.Skipped.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  skipped {what} x{n}");

        if (ExtractList.Add(view.Missing) is > 0 and var missing)
        {
            Console.WriteLine($"  {missing} files not extracted yet (textures show as plain colours); listed in the extract plugin's view-refs.txt.");
            Console.WriteLine("  In game: Openplanet > Extract block meshes, then run view again.");
        }
        if (noRender) return 0;

        var script = Path.Combine(dataDir, "..", "tools", "view_map.py");
        string[] width = blendOnly ? ["0"] : [];
        if (!Importer.RunQuiet("blender", ["--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", scene, outDir, .. width], out var log))
            return Fail("Blender failed:\n" + log);
        foreach (var line in log.Split('\n').Where(l => l.StartsWith("wrote ") || l.StartsWith("view: "))) Console.WriteLine("  " + line);
        return 0;
    }

    public int Inspect(string[] rest)
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

    public static int CheckClips(string[] rest)
    {
        var verbose = Option(ref rest, "-v");
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var totals = new Dictionary<(string Kind, string What), int>();
        var details = new List<(string Map, string What, ClipCheck.Clip Clip)>();
        foreach (var path in ExpandMaps(rest))
        {
            var result = ClipCheck.Compare(Gbx.ParseNode<CGameCtnChallenge>(path), shapes);
            foreach (var (kind, n) in result.Baked) totals[(kind, "baked")] = totals.GetValueOrDefault((kind, "baked")) + n;
            foreach (var c in result.GameOnly) { totals[(c.Kind, "game")] = totals.GetValueOrDefault((c.Kind, "game")) + 1; details.Add((path, "game shows", c)); }
            foreach (var c in result.OursOnly) { totals[(c.Kind, "ours")] = totals.GetValueOrDefault((c.Kind, "ours")) + 1; details.Add((path, "we show", c)); }
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)),-30} game shows, we hide {result.GameOnly.Count,5} | we show, game hides {result.OursOnly.Count,5}");
        }
        Console.WriteLine();
        foreach (var kind in ClipCheck.Kinds)
            Console.WriteLine($"{kind,-9} baked {totals.GetValueOrDefault((kind, "baked")),6} | game shows, we hide {totals.GetValueOrDefault((kind, "game")),5} | we show, game hides {totals.GetValueOrDefault((kind, "ours")),5}");
        if (verbose is not null)
            foreach (var g in details.Where(d => d.Clip.Kind == verbose).GroupBy(d => (d.What, d.Clip.Name)).OrderByDescending(g => g.Count()))
            {
                var (map, _, c) = g.First();
                Console.WriteLine($"  {g.Count(),5} {g.Key.What}: {g.Key.Name}, e.g. {Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(map))} {c.Cell} on {c.Block} {c.BlockCoord}");
            }
        return 0;
    }
}
