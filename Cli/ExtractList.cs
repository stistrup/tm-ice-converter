namespace TmIceConverter;

// files the extract plugin still has to fetch from the game
internal static class ExtractList
{
    public static int Add(IEnumerable<string> paths)
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
}
