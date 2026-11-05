using GBX.NET;
using GBX.NET.Engines.MwFoundations;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

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
        return Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/')));
    }
}
