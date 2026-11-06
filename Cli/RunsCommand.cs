using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// runs, settings: what a map was converted with, across the UIDs it has had
internal sealed class RunsCommand(string dataDir)
{
    public int Runs(string[] rest)
    {
        if (rest is not [var path]) return Fail("runs takes one map");
        var log = RunLog.Default;
        var (header, family) = FamilyOf(path, log);
        var (_, from) = log.Current(family, Translations.Load(dataDir), header.MapName);
        Console.WriteLine($"{header.MapName} ({header.MapUid})");
        Console.WriteLine(family.Root == header.MapUid ? "  the original" : $"  from {family.RootName} ({family.Root})");
        Console.WriteLine($"  {family.Uids.Count} UID(s) in the family");
        Console.WriteLine($"  settings now: {from}");
        if (log.DraftOf(family) is not null) Console.WriteLine($"  draft: {Path.Combine(Paths.SettingsStore, "drafts", family.Root + ".json")}");
        foreach (var run in family.Runs)
            Console.WriteLine($"  {run.Id}  {run.Time.ToLocalTime():yyyy-MM-dd HH:mm}  {run.Kind,-9} {run.Commit ?? "?",-9} -> {run.Output.File}"
                + (run.Problems is { Count: > 0 } p ? $"  ({p.Count} left out)" : ""));
        if (family.Runs.Count == 0) Console.WriteLine("  no runs yet");
        foreach (var problem in log.Problems) Console.Error.WriteLine($"  unreadable: {problem}");
        return 0;
    }

    // the settings as JSON; which: draft, seed, a run id; default what a convert would use
    public int Settings(string[] rest)
    {
        if (rest is not [var path, .. var which] || which.Length > 1) return Fail("settings takes a map and optionally draft, seed or a run id");
        var log = RunLog.Default;
        var (header, family) = FamilyOf(path, log);
        var data = Translations.Load(dataDir);
        var settings = which switch
        {
            [] => log.Current(family, data, header.MapName).Settings,
            ["seed"] => MapSettings.Seed(data, family.RootName is { Length: > 0 } n ? n : header.MapName),
            ["draft"] => log.DraftOf(family) is { } d ? new MapSettings(d.Settings) : null,
            [var id] => log.Run(id) is { } r ? new MapSettings(r.Settings) : null,
        };
        if (settings is null) return Fail($"no {which[0]} for this map");
        Console.WriteLine(settings);
        var problems = new List<string>();
        settings.Read(problems);
        foreach (var problem in problems) Console.Error.WriteLine($"left out: {problem}");
        return 0;
    }

    private static (CGameCtnChallenge Header, MapFamily Family) FamilyOf(string path, RunLog log)
    {
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        return (header, log.FamilyOf(header.MapUid, () => ConvertCommand.Layout(path)));
    }
}
