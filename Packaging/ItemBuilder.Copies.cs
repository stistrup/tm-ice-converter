using GBX.NET;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// themed copies of placed items that drive on a replaced surface (mats, tubes, ramps)
public sealed partial class ItemBuilder
{
    private readonly Dictionary<string, BuiltItem?> itemCopies = [];
    private readonly Dictionary<string, (string ItemId, string? Environment)> plannedCopies = [];

    public BuiltItem? ForItem(string itemId)
    {
        var key = CopyKey(Environment, itemId);
        lock (NameLock("copy:" + key))
        {
            lock (itemCopies) if (itemCopies.TryGetValue(key, out var known)) return known;
            var copy = BuildItemCopy(itemId, Environment, Console.Out, Planning);
            bool planning;
            lock (plannedCopies) planning = plannedCopies.ContainsKey(key);
            if (!planning) lock (itemCopies) itemCopies[key] = copy;
            return copy;
        }
    }

    private static string CopyKey(string? environment, string itemId) => $"{environment}:{itemId}";

    private List<Action<TextWriter>> TakeCopies()
    {
        List<(string ItemId, string? Environment)> copies;
        lock (plannedCopies)
        {
            copies = [.. plannedCopies.Values];
            plannedCopies.Clear();
        }
        return [.. copies.Select(c => (Action<TextWriter>)(log =>
        {
            var copy = BuildItemCopy(c.ItemId, c.Environment, log, plan: false);
            lock (itemCopies) itemCopies[CopyKey(c.Environment, c.ItemId)] = copy;
        }))];
    }

    private BuiltItem? BuildItemCopy(string itemId, string? environment, TextWriter log, bool plan)
    {
        var look = new BlockCatalog(Paths.ExtractedStadium) { Environment = environment };
        if (look.ItemFile(itemId) is not { } file) return null;
        try
        {
            var materialMap = Theme.Map("item:");
            if (!DrivesOnReplaced(look, file, materialMap)) return null;
            var name = CopyName(itemId, file);
            var path = ItemPath(name);
            var ident = ItemIdent(name);
            if (!stamps.IsCurrent(name, path, out var builtFrom))
            {
                if (plan)
                {
                    lock (plannedCopies) plannedCopies.TryAdd(CopyKey(environment, itemId), (itemId, environment));
                    return new BuiltItem(ident, path, (1, 1));
                }
                var catalog = new BlockCatalog(Paths.ExtractedStadium) { Environment = environment };
                var pieces = new MeshExporter(catalog.Files, materialMap).ExportItem(file, out var reason) ?? throw new InvalidDataException(reason);
                lock (Missing) Missing.UnionWith(catalog.Missing);
                if (Finish(name, pieces, null, catalog, builtFrom, log) is { } error) throw new InvalidDataException(error);
            }
            var header = Gbx.ParseHeaderNode<CGameItemModel>(path).Ident;
            return new BuiltItem(ident with { Collection = header.Collection, Author = header.Author }, path, (1, 1));
        }
        catch (Exception e)
        {
            lock (failures) failures[itemId] = Reason(e);
            log.WriteLine($"  no {Theme.Tag.ToLowerInvariant()} copy of item {itemId}: {Reason(e)}");
            return null;
        }
    }

    // false also when the item can't be read (InvalidCast) or has no mesh
    private bool DrivesOnReplaced(BlockCatalog look, string file, Dictionary<string, string> materialMap)
    {
        List<MeshPiece>? plain;
        try { plain = look.Plain.ExportItem(file, out _); }
        catch (InvalidCastException) { return false; }
        lock (Missing) Missing.UnionWith(look.Missing);
        bool Converts(string m) => materialMap.ContainsKey(m)
            ? Theme.SwappedIn(materialMap, m)
            : MaterialLib.Surface(m) is { } own && Theme.Replaces.Contains(own);
        return plain is not null && plain.Any(p => Converts(p.Material));
    }

    private string CopyName(string itemId, string file)
    {
        var env = Path.GetRelativePath(Path.GetDirectoryName(Paths.ExtractedStadium)!, file).Split('/')[0];
        return Theme.ItemPrefix + itemId + Theme.NameTag + (env == "Stadium" ? "" : "_" + env);
    }
}
