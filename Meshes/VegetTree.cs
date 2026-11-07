using System.Collections.Concurrent;
using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.Plug;
using GBX.NET.Serialization;

namespace TmIceConverter;

public enum VegetKind { Bark = 0, Leaf = 1, Blend = 2 }

// Images: D, N, R (absolute, may not exist)
public sealed record VegetMaterial(string Name, VegetKind Kind, string[] Images);

public sealed record VegetPart(int Material, CPlugVisualIndexedTriangles Visual);

// CPlugVegetTreeModel, unknown to GBX.NET: a material table, the names, then LODs of inline visuals (full-size extract only)
public sealed record VegetTree(List<VegetMaterial> Materials, List<List<VegetPart>> Lods)
{
    private static readonly ConcurrentDictionary<string, VegetTree> Cache = new(StringComparer.OrdinalIgnoreCase);

    // GBX.NET reads Ids only after a version int it never sees here
    private static readonly PropertyInfo IdVersion = typeof(GbxReader).GetProperty("IdVersion", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMemberException("GbxReader.IdVersion (GBX.NET changed)");

    private const uint VisualIndexedTriangles = 0x0901E000;

    public static VegetTree Read(string path) => Cache.GetOrAdd(path, Parse);

    private static VegetTree Parse(string path)
    {
        byte[] data;
        using (var input = File.OpenRead(path))
        using (var output = new MemoryStream())
        {
            Gbx.Decompress(input, output);
            data = output.ToArray();
        }
        int Int(int at) => BitConverter.ToInt32(data, at);

        // per material 37 bytes: 3 refs (Texture.gbx), 3 refs (dds D/N/R), 3 refs (other), kind byte; then count, Id version 3, names
        const int Entry = 37;
        var names = -1;
        for (int k = Entry + 4; k + 12 <= data.Length; k++)
            if (Int(k) is > 0 and < 16 && Int(k + 4) == 3 && Int(k + 8) == 0x40000000 && k - Entry * Int(k) - 4 >= 0 && Int(k - Entry * Int(k) - 4) == Int(k)) { names = k; break; }
        if (names < 0) throw new InvalidDataException("tree model without a material table (cut extract?)");

        var header = Gbx.ParseHeader(path);
        var ancestor = header.RefTable?.AncestorLevel ?? 0;
        var refs = header.RefTable?.Files ?? [];
        string RefPath(int index) => refs.FirstOrDefault(f => f.NodeIndex == index) is { } f ? GameFiles.ResolveRef(path, ancestor, f.FilePath) : "";

        var count = Int(names);
        using var stream = new MemoryStream(data) { Position = names + 8 };
        using var r = new GbxReader(stream);
        IdVersion.SetValue(r, (int?)3);
        var materials = new List<VegetMaterial>();
        for (int m = 0; m < count; m++)
        {
            var at = names - Entry * (count - m);
            materials.Add(new VegetMaterial(r.ReadIdAsString(), (VegetKind)data[at + 36], [RefPath(Int(at + 12)), RefPath(Int(at + 16)), RefPath(Int(at + 20))]));
        }

        // LOD: part count, then per part material (u16), visual (node ref), a byte; ends at the LOD distances
        var lods = new List<List<VegetPart>>();
        var visuals = new Dictionary<int, CPlugVisualIndexedTriangles>();
        while (stream.Position + 4 <= data.Length)
        {
            var start = stream.Position;
            var parts = r.ReadInt32();
            if (parts is < 1 or > 32) break;
            var lod = new List<VegetPart>();
            for (int p = 0; p < parts && lod is not null; p++)
            {
                var material = r.ReadUInt16();
                var index = r.ReadInt32();
                if (!visuals.TryGetValue(index, out var visual))
                {
                    if (r.ReadUInt32() != VisualIndexedTriangles || material >= count) { lod = null; break; }
                    stream.Position -= 8;
                    visuals[index] = visual = r.ReadNodeRef<CPlugVisualIndexedTriangles>()!;
                }
                r.ReadByte();
                lod.Add(new VegetPart(material, visual));
            }
            if (lod is null) { stream.Position = start; break; }
            lods.Add(lod);
        }
        if (lods.Count == 0) throw new InvalidDataException("tree model without meshes");
        return new VegetTree(materials, lods);
    }
}
