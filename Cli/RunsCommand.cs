using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// runs, settings: what a map was converted with
internal sealed class RunsCommand
{
    public int Runs(string[] rest)
    {
        if (rest is not [var path]) return Fail("runs takes one map");
        var log = RunLog.Default;
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        var (_, from) = log.Current(header.MapUid);
        Console.WriteLine($"{header.MapName} ({header.MapUid})");
        Console.WriteLine($"  settings now: {from}");
        if (log.SavedOf(header.MapUid) is not null) Console.WriteLine($"  saved: {Path.Combine(Paths.SettingsStore, "maps", header.MapUid + ".json")}");
        var runs = log.RunsOf(header.MapUid);
        foreach (var run in runs)
            Console.WriteLine($"  {run.Id}  {run.Time.ToLocalTime():yyyy-MM-dd HH:mm}  {run.Kind,-9} {run.Commit ?? "?",-9} -> {run.Output.File}"
                + (run.Problems is { Count: > 0 } p ? $"  ({p.Count} left out)" : ""));
        if (runs.Count == 0) Console.WriteLine("  no runs yet");
        foreach (var problem in log.Problems) Console.Error.WriteLine($"  unreadable: {problem}");
        return 0;
    }

    // the settings as JSON; which: saved, seed, a run id; default what a convert would use
    public int Settings(string[] rest)
    {
        if (rest is not [var path, .. var which] || which.Length > 1) return Fail("settings takes a map and optionally saved, defaults or a run id");
        var log = RunLog.Default;
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(path);
        var settings = which switch
        {
            [] => log.Current(header.MapUid).Settings,
            ["defaults"] => MapSettings.Empty,
            ["saved"] => log.SavedOf(header.MapUid) is { } d ? new MapSettings(d.Settings) : null,
            [var id] => log.Run(id) is { } r ? new MapSettings(r.Settings) : null,
        };
        if (settings is null) return Fail($"no {which[0]} for this map");
        Console.WriteLine(settings);
        var problems = new List<string>();
        settings.Read(problems);
        foreach (var problem in problems) Console.Error.WriteLine($"left out: {problem}");
        return 0;
    }
}
