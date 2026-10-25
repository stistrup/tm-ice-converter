using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// the importer leaves all collision at surface 0: write the real ones
public static class ItemPhysics
{
    public static Dictionary<CPlugSurface.MaterialId, int> Fix(string itemPath,
        IReadOnlyList<(string Name, string Physics, int Triangles)> collisionObjects)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        var som = (gbx.Node.EntityModel as CGameCommonItemEntityModel)?.StaticObject as CPlugStaticObjectModel
            ?? throw new InvalidDataException("not a static object item");
        if (som.Shape is not CPlugSurface { Surf: CPlugSurface.Mesh { Triangles: { } tris } }) return [];

        var groups = collisionObjects.OrderBy(o => o.Name, StringComparer.Ordinal)
            .GroupBy(o => o.Physics).ToList();
        var actual = tris.GroupBy(t => (int)t.SurfaceIndex).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < groups.Count; i++)
        {
            var expected = groups[i].Sum(o => o.Triangles);
            if (Math.Abs(actual.GetValueOrDefault(i) - expected) > Math.Max(2, expected / 200))
                throw new InvalidDataException($"collision surface {i} ({groups[i].Key}): expected {expected} triangles, item has {actual.GetValueOrDefault(i)}");
        }

        // "Physics:Gameplay" for pads
        var ids = groups.Select(g => Enum.Parse<CPlugSurface.MaterialId>(g.Key.Split(':')[0])).ToArray();
        var gameplays = groups.Select(g => g.Key.Split(':') is [_, var gp] ? (byte)Enum.Parse<CPlugSurface.GameplayId>(gp) : (byte)0).ToArray();
        var written = new Dictionary<CPlugSurface.MaterialId, int>();
        for (int i = 0; i < tris.Length; i++)
        {
            var id = ids[tris[i].SurfaceIndex];
            tris[i] = tris[i] with { U02 = (byte)id, U03 = gameplays[tris[i].SurfaceIndex] };
            written[id] = written.GetValueOrDefault(id) + 1;
        }
        gbx.Save(itemPath);
        return written;
    }
}
