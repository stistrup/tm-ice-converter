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

// migrate [--dry-run]: maps still on rules (saved settings and working copies) get those rules as conversion targets, once
internal static class MigrateCommand
{
    public static int Run(string dataDir, string[] rest)
    {
        var dry = rest.Contains("--dry-run");
        var store = SettingsStore.Default;
        var onRules = store.SavedUids().Where(uid => OnRules(store.SavedOf(uid)?.Settings) || OnRules(store.WorkingOf(uid)?.Settings)).ToHashSet();
        if (onRules.Count == 0) { Console.WriteLine("nothing on rules"); return 0; }
        var maps = MapsByUid(onRules);
        var preview = new ConversionPreview(dataDir);
        foreach (var uid in onRules.Order())
        {
            if (!maps.TryGetValue(uid, out var path)) { Console.WriteLine($"{uid}: no map with this UID under Maps, left as it is"); continue; }
            if (store.SavedOf(uid) is { } saved && OnRules(saved.Settings))
            {
                var targets = Targets(preview, path, new MapSettings(saved.Settings));
                Console.WriteLine($"{Path.GetFileName(path)}: saved, {Count(targets)} targets");
                if (!dry) store.Save(uid, new MapSettings(targets));
            }
            if (store.WorkingOf(uid) is { } working && OnRules(working.Settings))
            {
                var targets = Targets(preview, path, new MapSettings(working.Settings));
                Console.WriteLine($"{Path.GetFileName(path)}: working copy, {Count(targets)} targets (undo goes back)");
                if (!dry) store.Edit(uid, targets);
            }
        }
        return 0;
    }

    private static bool OnRules(System.Text.Json.Nodes.JsonObject? settings) => settings?["rules"]?.GetValue<bool>() == true;

    private static System.Text.Json.Nodes.JsonObject Targets(ConversionPreview preview, string path, MapSettings settings)
        => PresetApply.Settings(preview.Settled(path, settings).GetAwaiter().GetResult(), settings);

    private static int Count(System.Text.Json.Nodes.JsonObject settings) => (settings["pieces"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;

    private static Dictionary<string, string> MapsByUid(IReadOnlySet<string> wanted)
    {
        var found = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(Paths.Maps, "*.Map.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }))
        {
            try
            {
                var uid = GBX.NET.Gbx.ParseHeaderNode<GBX.NET.Engines.Game.CGameCtnChallenge>(file).MapUid;
                if (wanted.Contains(uid)) found.TryAdd(uid, file);
            }
            catch (Exception) { }
        }
        return found;
    }
}
