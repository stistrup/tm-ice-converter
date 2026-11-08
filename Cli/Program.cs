using GBX.NET;
using GBX.NET.LZO;
using GBX.NET.ZLib;
using TmIceConverter;

Gbx.LZO = new Lzo();
Gbx.ZLib = new ZLib();

var dataDir = FindDataDir();
Paths.LocalExample = Path.Combine(dataDir, "local.example.jsonc");
var inspect = new InspectCommands(dataDir);

switch (args)
{
    // a double-click: the GUI
    case []:
        return new GuiCommand(dataDir).Run([]);
    case ["gui", .. var rest]:
        return new GuiCommand(dataDir).Run(rest);
    case ["convert", .. var rest]:
        return new ConvertCommand(dataDir).Run(rest);
    case ["runs", .. var rest]:
        return new RunsCommand().Runs(rest);
    case ["settings", .. var rest]:
        return new RunsCommand().Settings(rest);
    case ["plan", .. var rest]:
        return new ConvertCommand(dataDir).Plan(rest);
    case ["watch", .. var rest]:
        return new ConvertCommand(dataDir).Watch(rest);
    case ["scan", .. var rest]:
        return ToolCommands.Scan(rest.Length > 0 ? rest : [Paths.Maps], Path.Combine(dataDir, "known-blocks.txt"));
    case ["make-item", var block]:
        return new ItemBuilder(dataDir).BuildOne(block) is { } error ? CliArgs.Fail(error) : 0;
    case ["show", .. var rest] when rest.Length > 0:
        return inspect.Show(rest);
    case ["view", .. var rest] when rest.Length > 0:
        return inspect.View(rest);
    case ["inspect", .. var rest] when rest.Length > 0:
        return inspect.Inspect(rest);
    case ["compress", .. var rest] when rest.Length > 0:
        return ToolCommands.Compress(rest);
    case ["clipcheck", .. var rest] when rest.Length > 0:
        return InspectCommands.CheckClips(rest);
    case ["learn-clip-turns", .. var rest] when rest.Length > 0:
        return ToolCommands.LearnClipTurns(rest);
    default:
        Console.WriteLine($"""
            Usage:
              gui [--port <n>] [--no-open]
                  Local web interface: browse maps and look through them in 3D. Build the page first: npm run build in web/.
            {ConvertCommand.Usage.TrimEnd()}
              runs <map.Gbx>
                  The conversions of this map and which settings a convert would use now.
              settings <map.Gbx> [saved | seed | <run id>]
                  Print settings as JSON (default: what a convert would use); fields that don't read are listed.
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

              clipcheck <map.Gbx | folder>... [-v <kind>]
                  Compare the clips the converter resolves with the ones the game baked into each map, per kind
                  (top, side, bottom, vertical); -v lists that kind's mismatches by clip name.
              learn-clip-turns <map.Gbx | folder>...
                  Learn how the game turns each block type's shaped top/bottom clips from the clips baked into the maps,
                  into data/clip-turns.jsonc. Block types no map shows fall back to fitting the clip mesh.
              compress <map.Gbx | folder>...
                  Recompress the custom blocks embedded in maps, in place (the game's shadow save writes them uncompressed).

            Drop folder: {Paths.DropFolder}
            """);
        return 1;
}

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "TmIceConverter.csproj")))
            return Path.Combine(dir.FullName, "data");
    return Path.Combine(AppContext.BaseDirectory, "data");
}
