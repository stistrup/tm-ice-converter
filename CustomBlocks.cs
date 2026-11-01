using System.Numerics;
using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.MwFoundations;
using GBX.NET.Engines.Plug;
using GBX.NET.Serialization.Chunking;

namespace TmIceConverter;

public sealed record IceBlock(Ident Ident, string FilePath);

// custom block = a native block (archetype) with its variant's body replaced; the game keeps clips, pillars and joins
public static class CustomBlocks
{
    public const int CustomFlag = 0x8000;

    // matches a placement's ground flag, variant and mobil entry; mobil rows other than 0 (pillars, terrain) unknown
    public static int? VariantId(bool ground, int variant, int mobil)
        => (mobil & 0x3F) == 0 ? (ground ? 0 : 1_000_000) + variant * 1000 + (mobil >> 6) : null;

    // block from a built ice item's mesh
    // lift: footprint in units; ice raised to sit over the same native block
    public static void Write(string itemPath, string blockPath, Ident ident, string archetype, int variantId, (Vector3 At, float Pitch, float Roll)? spawn = null, (int X, int Z)? lift = null, float raise = 0, string? snowAs = null, IReadOnlyList<MeshPiece>? projectedLightmap = null)
    {
        var mesh = ((CGameCommonItemEntityModel)Gbx.ParseNode<CGameItemModel>(itemPath).EntityModel!).StaticObject!;
        var folder = mesh.Mesh!.MaterialsFolderName ?? "";
        // block bodies look materials up as game materials, by full link
        foreach (var inst in (mesh.Mesh.CustomMaterials ?? []).Select(m => m.MaterialUserInst).OfType<CPlugMaterialUserInst>())
        {
            var link = inst.Link ?? "";
            if (!link.Contains('\\')) link = folder + link;
            var file = link.Split('\\')[^1];
            inst.Link = file.StartsWith("Custom") ? @"Stadium\Media\Material_BlockCustom\" + file : link;
            // our snow → a game snow material
            if (snowAs is not null && link.Contains("PlatformIce") && file.Contains("DecoHill")) inst.Link = snowAs;
            inst.IsUsingGameMaterial = true;
            inst.MaterialName = "";
        }
        mesh.Mesh.MaterialsFolderName = null;
        if (lift is { } size) LiftIce(mesh.Mesh, size.X * 32, size.Z * 32);
        if (projectedLightmap is not null) ProjectedLightmap(mesh.Mesh, projectedLightmap, ident.Id);
        if (raise != 0)
            foreach (var positions in (mesh.Mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()?.Positions).OfType<Vec3[]>().Distinct())
                for (int i = 0; i < positions.Length; i++) positions[i] = positions[i] with { Y = positions[i].Y + raise };
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(ident.Id.Split('\\')[^1]));
        Directory.CreateDirectory(Path.GetDirectoryName(blockPath)!);
        // the game saves blocks uncompressed but loads compressed ones too: half the size
        new Gbx<CGameItemModel>(Container(ident, name, archetype, ident.Collection, [(variantId, mesh)], spawn)) { BodyCompression = GbxCompression.Compressed }.Save(blockPath);
    }

    // terrain shaders look lighting up by position over the whole cell; the importer packs lightmap UVs with a margin,
    // so they go back to the exported projection, and the box the bake fills (PreLightGen) covers them all
    internal static void ProjectedLightmap(CPlugSolid2Model mesh, IReadOnlyList<MeshPiece> native, string name)
    {
        static (int, int, int) Key(float x, float y, float z) => ((int)MathF.Round(x * 100), (int)MathF.Round(y * 100), (int)MathF.Round(z * 100));
        var uvOf = new Dictionary<(int, int, int), Vec2>();
        foreach (var p in native.Where(p => p.Visible && p.Uv1 is not null))
            for (int i = 0; i < p.Positions.Length / 3; i++)
                uvOf.TryAdd(Key(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]), new Vec2(p.Uv1![2 * i], p.Uv1[2 * i + 1]));
        int hit = 0, all = 0;
        foreach (var stream in (mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()).OfType<CPlugVertexStream>().Distinct())
        {
            if (stream.Positions is not { } positions || stream.UVs is null || !stream.UVs.TryGetValue(1, out var uv)) continue;
            for (int i = 0; i < positions.Length; i++, all++)
                if (uvOf.TryGetValue(Key(positions[i].X, positions[i].Y, positions[i].Z), out var native1)) { uv[i] = native1; hit++; }
        }
        if (hit < all) Console.WriteLine($"  {name}: projected lightmap UVs for {hit} of {all} vertices");
        var uvs = (mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>().SelectMany(v => v.VertexStreams?.FirstOrDefault()?.UVs?.GetValueOrDefault(1) ?? []).ToList();
        if (mesh.PreLightGenerator is { } gen && uvs.Count > 0)
            (gen.U04, gen.U05, gen.U06, gen.U07) = (uvs.Min(u => u.X), uvs.Min(u => u.Y), uvs.Max(u => u.X), uvs.Max(u => u.Y));
    }

    private const float IceLift = 0.01f;

    // coplanar with the native ice/dirt it covers: raise the ice, easing to zero where it meets the block edge so neighbours line up
    private static void LiftIce(CPlugSolid2Model mesh, float sizeX, float sizeZ)
    {
        var streams = (mesh.ShadedGeoms ?? [])
            .Where(g => mesh.CustomMaterials?[g.MaterialIndex].MaterialUserInst?.Link?.Contains("PlatformIce") == true)
            .Select(g => mesh.Visuals?[g.VisualIndex]).OfType<CPlugVisualIndexedTriangles>()
            .Select(v => v.VertexStreams?.FirstOrDefault()?.Positions).OfType<Vec3[]>().Distinct().ToList();
        if (streams.Count == 0) return;
        var all = streams.SelectMany(p => p).ToList();
        const float touch = 0.05f;
        var sides = new (bool On, Func<Vec3, float> Dist)[]
        {
            (all.Min(p => p.X) < touch, p => p.X), (all.Max(p => p.X) > sizeX - touch, p => sizeX - p.X),
            (all.Min(p => p.Z) < touch, p => p.Z), (all.Max(p => p.Z) > sizeZ - touch, p => sizeZ - p.Z),
        };
        foreach (var positions in streams)
            for (int i = 0; i < positions.Length; i++)
            {
                var d = sides.Where(s => s.On).Select(s => s.Dist(positions[i])).DefaultIfEmpty(16).Min();
                positions[i] = positions[i] with { Y = positions[i].Y + IceLift * MathF.Sin(MathF.PI / 2 * Math.Clamp(d / 16, 0, 1)) };
            }
    }

    // same bytes the game writes for a compiled custom block
    private static CGameItemModel Container(Ident ident, string name, string archetype, Id collection, IEnumerable<(int Id, CPlugStaticObjectModel Mesh)> variants, (Vector3 At, float Pitch, float Roll)? spawn)
    {
        var item = new CGameItemModel();
        AddChunks(item, (0x2E001003, 8, null), (0x2E001006, null, null), (0x2E002000, null, null), (0x2E002001, 0, null), (0x2E001009, null, null),
            (0x2E00100B, null, null), (0x2E00100C, null, null), (0x2E00100D, null, null), (0x2E001010, 4, null), (0x2E001011, 1, null), (0x2E001012, null, null),
            (0x2E002008, null, null), (0x2E002009, null, null), (0x2E00200C, null, null), (0x2E002012, null, null), (0x2E002015, null, null), (0x2E002019, 15, null),
            (0x2E00201A, null, null), (0x2E00201C, 5, null), (0x2E00201E, 7, null), (0x2E00201F, 13, null), (0x2E002020, 3, null), (0x2E002025, 0, null),
            (0x2E002026, 0, null), (0x2E002027, 0, null));
        SetFields(item, 0x2E001012, ("U02", 1), ("U03", 1));
        SetFields(item, 0x2E00201E, ("U02", -1));
        SetFields(item, 0x2E00201F, ("U09", -1), ("U10", -1));
        item.Ident = ident;
        item.Name = name;
        // the editor lists it next to its archetype
        item.PageName = archetype;
        item.Description = "";
        item.ItemType = item.ItemTypeE = CGameItemModel.EItemType.Block;
        item.WaypointType = CGameItemModel.EWaypointType.None;
        item.Flags = (CGameCtnCollector.ECollectorFlags)8;
        item.CatalogPosition = 1;
        item.ProdState = Enum.Parse<CGameCtnCollector.EProdState>("Release");
        item.OrbitalRadiusBase = -1;
        item.OrbitalPreviewAngle = 0.15f;
        item.DefaultWeaponName = item.ArchetypeRef = item.SkinDirectory = "";
        item.ParentCollectorId = new Id("");
        item.NadeoSkinFids = Enumerable.Range(0, 7).Select(_ => new External<CMwNod>(null, null)).ToArray();

        var placement = new CGameItemPlacementParam();
        AddChunks(placement, (0x2E020000, null, null), (0x2E020001, null, null), (0x2E020004, null, new byte[8]), (0x2E020005, null, null));
        placement.Flags = 1;
        placement.GridSnapHStep = 1;
        placement.PivotSnapDistance = -1;
        placement.PivotPositions = [];
        placement.PivotRotations = [];
        placement.PlacementClass = new NPlugItemPlacement_SClass { Version = 10, AlignToInterior = true, WorldDir = new Vec3(0, 0, 1) };
        item.DefaultPlacement = placement;

        var block = new CGameBlockItem();
        AddChunks(block, (0x2E025000, 1, null));
        // unknown, zeros in every block seen
        block.Chunks.Add(new SkippableChunk(0x2E025002) { Data = new byte[8] });
        block.Chunks.Add(new SkippableChunk(0x2E025003) { Data = new byte[5] });
        block.ArchetypeBlockInfoId = archetype;
        block.ArchetypeBlockInfoCollectionId = collection;
        block.CustomizedVariants = variants.Select(v => new CGameBlockItem.Mobil { Id = v.Id, Properties = spawn is { } at ? WithSpawn(v.Mesh, at) : new CGameBlockItem.MobilProperties { Flags = 1, StaticObject = v.Mesh } }).ToList();
        item.EntityModelEdition = block;
        return item;
    }

    // without it the car spawns at the block's corner. Flag 4: spawn x, y, z, yaw, pitch, roll in the block's frame; flag 8: unknown point
    private static CGameBlockItem.MobilProperties WithSpawn(CPlugStaticObjectModel mesh, (Vector3 At, float Pitch, float Roll) spawn)
        => new() { Flags = 1 | 4 | 8, StaticObject = mesh, U01 = new BoxAligned(spawn.At.X, spawn.At.Y, spawn.At.Z, 0, spawn.Pitch, spawn.Roll), U02 = new Vec3(0, 0, 0) };

    private static readonly MethodInfo CreateChunk = typeof(CMwNod).GetMethod(nameof(CMwNod.CreateChunk))!;

    private static void AddChunks(CMwNod node, params (uint Id, int? Version, byte[]? Raw)[] chunks)
    {
        var types = node.GetType().Assembly.GetTypes();
        foreach (var (id, version, raw) in chunks)
        {
            var type = types.First(t => t.IsNested && (t.Name == $"Chunk{id:X8}" || t.Name == $"HeaderChunk{id:X8}") && t.DeclaringType!.IsInstanceOfType(node));
            var chunk = CreateChunk.MakeGenericMethod(type).Invoke(node, null)!;
            if (version is { } v) type.GetProperty("Version")?.SetValue(chunk, v);
            if (raw is not null) type.GetProperty("Data")!.SetValue(chunk, raw);
        }
    }

    // fields GBX.NET keeps on the chunk
    private static void SetFields(CMwNod node, uint id, params (string Name, object Value)[] values)
    {
        var chunk = node.Chunks.First(c => c.Id == id);
        foreach (var (name, value) in values)
        {
            var type = chunk.GetType();
            if (type.GetProperty(name) is { CanWrite: true } property) property.SetValue(chunk, value);
            else (type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException(type.Name, name)).SetValue(chunk, value);
        }
    }
}
