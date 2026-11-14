using System.Diagnostics;

namespace TmSurfaceEditor;

// the running game: it loads items only at start-up
internal static class GameProcess
{
    // UTC start of the earliest running game, null if none (Windows: "Trackmania", Proton: "Trackmania.exe")
    public static DateTime? StartedAt()
    {
        DateTime? first = null;
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    if (process.ProcessName is not ("Trackmania" or "Trackmania.exe")) continue;
                    var start = process.StartTime.ToUniversalTime();
                    if (first is null || start < first) first = start;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return first;
    }

    // our item files written since `since` that the running game started before: it holds the old ones
    public static int ItemsNeedingRestart(DateTime since)
    {
        if (StartedAt() is not { } game || !Directory.Exists(Paths.Items)) return 0;
        var after = game > since ? game : since;
        return Directory.EnumerateFiles(Paths.Items, "*.Item.gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Count(file => File.GetLastWriteTimeUtc(file) >= after);
    }
}
