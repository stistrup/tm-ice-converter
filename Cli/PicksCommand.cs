using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.CliArgs;

namespace TmSurfaceEditor;

// picks <map> [settings file] --out <file>: the map's rules run once, written out as picks alone
internal static class PicksCommand
{
    public static int Run(string dataDir, string[] rest)
    {
        if (Option(ref rest, "--out") is not { } output || rest is not [var path, .. var from] || from.Length > 1) return Fail("picks takes a map, optionally a settings file, and --out <file>");
        var map = Path.GetFullPath(path);
        var settings = from is [var file] ? MapSettings.Parse(File.ReadAllText(file))
            : SettingsStore.Default.Current(Gbx.ParseHeaderNode<CGameCtnChallenge>(map).MapUid).Settings;
        var result = new ConversionPreview(dataDir).Settled(map, settings).GetAwaiter().GetResult();
        var picks = new MapSettings(PresetApply.Settings(result, settings));
        File.WriteAllText(output, picks.ToString());
        Console.WriteLine($"{Path.GetFileName(map)}: {picks.Json["pieces"]?.AsObject().Count ?? 0} picks -> {output}");
        return 0;
    }
}
