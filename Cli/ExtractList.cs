namespace TmSurfaceEditor;

// files the extract plugin still has to fetch from the game
internal static class ExtractList
{
    // the ones of these still to fetch; files the game can't give (built at run time) don't count
    public static List<string> Add(IEnumerable<string> paths)
    {
        var unavailable = Unavailable();
        var asked = paths.Select(p => p.Trim()).Where(p => p.Length > 0 && !unavailable.Contains(p)).ToHashSet();
        var file = Path.Combine(Paths.ExtractPlugin, "view-refs.txt");
        var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var listed = (File.Exists(file) ? File.ReadAllLines(file) : []).Concat(asked)
            .Select(p => p.Trim()).Where(p => p.Length > 0 && !unavailable.Contains(p) && !OnDisk(Path.Combine(gameData, p)))
            .ToHashSet().Order(StringComparer.Ordinal).ToList();
        File.WriteAllLines(file, listed);
        return [.. listed.Where(asked.Contains)];

        static bool OnDisk(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };
    }

    // from the last extract's log, relative to GameData
    private static HashSet<string> Unavailable()
    {
        var log = Path.Combine(Paths.ExtractPlugin, "dump-log.txt");
        if (!File.Exists(log)) return [];
        return File.ReadLines(log)
            .Select(l => l.StartsWith("unavailable GameData/") ? l["unavailable GameData/".Length..]
                : l.StartsWith("listed unavailable ") ? l["listed unavailable ".Length..] : null)
            .OfType<string>().Select(p => p.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
