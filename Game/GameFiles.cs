using GBX.NET;
using GBX.NET.Engines.MwFoundations;
using GBX.NET.Engines.Plug;

namespace TmSurfaceEditor;

// the extracted game data: loads prefabs, remembers what's missing
public sealed class GameFiles(string stadiumDir)
{
    private readonly Dictionary<string, CMwNod?> cache = new(StringComparer.OrdinalIgnoreCase);

    public string StadiumDir => stadiumDir;

    public List<string> Warnings { get; } = [];

    public HashSet<string> Missing { get; } = [];

    // off while only looking blocks up
    public bool RecordMissing { get; set; } = true;

    public string RelativeToGameData(string path) => Path.GetRelativePath(Path.GetDirectoryName(stadiumDir)!, path).Replace('\\', '/');

    public CPlugPrefab? LoadPrefab(string path) => Load<CPlugPrefab>(path);

    public CPlugStaticObjectModel? LoadStaticObject(string path) => Load<CPlugStaticObjectModel>(path);

    public CPlugSolid2Model? LoadMesh(string path) => Load<CPlugSolid2Model>(path);

    public CPlugDynaObjectModel? LoadDynaObject(string path) => Load<CPlugDynaObjectModel>(path);

    private T? Load<T>(string path) where T : CMwNod, new()
    {
        path = OnDisk(path);
        if (cache.TryGetValue(path, out var cached)) return cached as T;
        T? node = null;
        if (!File.Exists(path))
        {
            Warnings.Add($"missing {Path.GetRelativePath(stadiumDir, path)} (not extracted)");
            Missing.Add(RelativeToGameData(path));
        }
        else
        {
            try { node = Gbx.ParseNode<T>(path); }
            catch (Exception e) { Warnings.Add($"cannot read {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }
        cache[path] = node;
        return node;
    }

    public static string ResolveRef(string fromFile, int ancestorLevel, string refPath)
    {
        var dir = Path.GetDirectoryName(fromFile)!;
        for (int i = 0; i < ancestorLevel; i++) dir = Path.GetDirectoryName(dir)!;
        return OnDisk(Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/'))));
    }

    private static readonly Dictionary<string, string> onDisk = [];

    // the game's names ignore case, a Linux disk doesn't (DecoHillSlope2Curve2Out is extracted as …2curve2Out): the real path, or the path as given
    public static string OnDisk(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return path;
        lock (onDisk)
        {
            if (onDisk.TryGetValue(path, out var known)) return known;
            var found = Path.GetPathRoot(path) ?? "";
            foreach (var part in path[found.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var exact = Path.Combine(found, part);
                if (File.Exists(exact) || Directory.Exists(exact)) { found = exact; continue; }
                var match = Directory.Exists(found)
                    ? Directory.EnumerateFileSystemEntries(found).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (match is null) return onDisk[path] = path;
                found = match;
            }
            return onDisk[path] = found;
        }
    }
}
