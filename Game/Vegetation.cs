using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Meta;

namespace TmSurfaceEditor;

// trees a block prefab places are lost with its custom body; the environment's own vegetation items stand in
public sealed class Vegetation(string gameData)
{
    private readonly Dictionary<string, (Dictionary<string, (string Item, int Variant)> Exact, List<(string Item, int Variant)> Bushes, List<(string Item, int Variant)> Others)> catalogs = [];

    // palms reach ~17 m
    private const float TallTreeRoom = 20;

    // item and variant for a tree model; models without an item (jungle patches) get a bush/palm mix by position, only bushes under the block
    public (string Item, int Variant)? For(string environment, string model, Vector3 at, float room)
    {
        var (exact, bushes, others) = Catalog(environment);
        if (exact.TryGetValue(model, out var hit)) return hit;
        var hash = (uint)HashCode.Combine(MathF.Round(at.X), MathF.Round(at.Y), MathF.Round(at.Z));
        var pool = bushes.Count > 0 && (others.Count == 0 || hash % 4 != 0 || room < TallTreeRoom) ? bushes : others;
        return pool.Count == 0 ? null : pool[(int)(hash / 4 % (uint)pool.Count)];
    }

    // the tree model a variant of an item shows
    public string? ModelOf(string environment, (string Item, int Variant) stand)
        => Catalog(environment).Item1.FirstOrDefault(kv => kv.Value == stand).Key;

    private (Dictionary<string, (string, int)>, List<(string, int)>, List<(string, int)>) Catalog(string environment)
    {
        lock (catalogs)
        {
            if (catalogs.TryGetValue(environment, out var known)) return known;
            var exact = new Dictionary<string, (string, int)>();
            var dir = Path.Combine(gameData, environment, "Items", "Vegetation");
            var lists = new List<(string Item, string[] Models)>();
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*.Item.Gbx").Order())
                    try
                    {
                        if (Gbx.ParseNode<CGameItemModel>(file).EntityModel is NPlugItem_SVariantList list && list.Variants is { } variants)
                            lists.Add((Path.GetFileName(file)[..^".Item.Gbx".Length],
                                variants.Select(v => Path.GetFileName(v.EntityModelFile?.FilePath ?? "").Replace(".VegetTreeModel.Gbx", "")).ToArray()));
                    }
                    catch (Exception e) { Console.WriteLine($"  warning: vegetation item {Path.GetFileName(file)}: {e.Message}"); }
            // an item named after its model first, then any list holding it
            foreach (var (item, models) in lists.Where(l => l.Models.Length == 1 && l.Models[0] == l.Item)) exact[models[0]] = (item, 0);
            foreach (var (item, models) in lists)
                for (int i = 0; i < models.Length; i++) exact.TryAdd(models[i], (item, i));
            var big = exact.Where(kv => kv.Key.Contains("Big")).OrderBy(kv => kv.Key).ToList();
            var result = (exact, big.Where(kv => kv.Key.StartsWith("Bush")).Select(kv => kv.Value).ToList(), big.Where(kv => !kv.Key.StartsWith("Bush")).Select(kv => kv.Value).ToList());
            catalogs[environment] = result;
            return result;
        }
    }
}
