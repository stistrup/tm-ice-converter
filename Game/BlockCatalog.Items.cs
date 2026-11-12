using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Meta;

namespace TmSurfaceEditor;

// the game's item files
public sealed partial class BlockCatalog
{
    private static readonly Dictionary<string, ILookup<string, string>> itemFolders = [];

    // env's own version first
    public string? ItemFile(string itemId)
    {
        var gameData = Path.GetDirectoryName(stadiumDir)!;
        var dirs = environment is null or "Stadium2020" ? [stadiumDir] : new[] { Path.Combine(gameData, environment), stadiumDir };
        foreach (var dir in dirs.Select(d => Path.Combine(d, "Items")))
        {
            ILookup<string, string> files;
            lock (itemFolders)
                if (!itemFolders.TryGetValue(dir, out files!))
                    itemFolders[dir] = files = (Directory.Exists(dir)
                        ? Directory.EnumerateFiles(dir, "*.Item.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                        : []).ToLookup(f => Path.GetFileName(f).Split('.')[0], StringComparer.OrdinalIgnoreCase);
            if (files[itemId].FirstOrDefault() is { } file) return file;
        }
        return null;
    }

    private static readonly Dictionary<string, List<string>?> itemVariants = [];

    // model names of a variant list item: FallTreeMedium, Fall.Prefab…
    public List<string>? ItemVariants(string itemId)
    {
        if (ItemFile(itemId) is not { } file) return null;
        lock (itemVariants)
            if (itemVariants.TryGetValue(file, out var known)) return known;
        var found = Gbx.ParseNode<CGameItemModel>(file).EntityModel is NPlugItem_SVariantList { Variants: { } variants }
            ? variants.Select(v => Path.GetFileName(v.EntityModelFile?.FilePath ?? "") is var f && f.EndsWith(".Prefab.Gbx") ? f[..^".Gbx".Length] : f.Split('.')[0]).ToList()
            : null;
        lock (itemVariants) itemVariants[file] = found;
        return found;
    }
}
