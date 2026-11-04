using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// the extracted game data: loads prefabs, remembers what's missing
public sealed class GameFiles(string stadiumDir)
{
    private readonly Dictionary<string, CPlugPrefab?> prefabCache = new(StringComparer.OrdinalIgnoreCase);

    public string StadiumDir => stadiumDir;

    public List<string> Warnings { get; } = [];

    public HashSet<string> Missing { get; } = [];

    // off while only looking blocks up
    public bool RecordMissing { get; set; } = true;

    public string RelativeToGameData(string path) => Path.GetRelativePath(Path.GetDirectoryName(stadiumDir)!, path).Replace('\\', '/');

    public CPlugPrefab? LoadPrefab(string path)
    {
        if (prefabCache.TryGetValue(path, out var cached)) return cached;
        CPlugPrefab? prefab = null;
        if (!File.Exists(path))
        {
            Warnings.Add($"missing {Path.GetRelativePath(stadiumDir, path)} (not extracted)");
            Missing.Add(RelativeToGameData(path));
        }
        else
        {
            try { prefab = Gbx.ParseNode<CPlugPrefab>(path); }
            catch (Exception e) { Warnings.Add($"cannot read {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }
        return prefabCache[path] = prefab;
    }

    public static string ResolveRef(string fromFile, int ancestorLevel, string refPath)
    {
        var dir = Path.GetDirectoryName(fromFile)!;
        for (int i = 0; i < ancestorLevel; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/')));
    }
}
