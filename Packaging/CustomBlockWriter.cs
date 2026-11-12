using System.Numerics;
using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.MwFoundations;
using GBX.NET.Engines.Plug;
using GBX.NET.Serialization.Chunking;

namespace TmSurfaceEditor;

public sealed record BuiltBlock(Ident Ident, string FilePath);

// custom block = a native block (archetype) with its variant's body replaced; the game keeps clips, pillars and joins
public static class CustomBlockWriter
{
    public const int CustomFlag = 0x8000;

    // matches a placement's ground flag, variant and mobil entry; mobil rows other than 0 (pillars, terrain) unknown
    public static int? VariantId(bool ground, int variant, int mobil)
        => (mobil & 0x3F) == 0 ? (ground ? 0 : 1_000_000) + variant * 1000 + (mobil >> 6) : null;

    // block from a built ice item's mesh
    // lift: footprint in units; ice raised to sit over the same native block
    public static void Write(
        string itemPath,
        string blockPath,
        Ident ident,
        string block,
        string archetype,
        int variantId,
        (Vector3 At, float Pitch, float Roll)? spawn = null,
        (int X, int Z)? lift = null,
        float raise = 0,
        string? snowAs = null,
        (int Sides, float Lift, (int X, int Z) Size)? ramp = null,
        bool lightmapFromAbove = false,
        bool hollow = false)
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
        if (lift is { } size) SurfaceShaping.LiftSurface(mesh, size.X * 32, size.Z * 32);
        if (ramp is { } r) SurfaceShaping.RampSurface(mesh, r.Sides, r.Lift, r.Size.X * 32, r.Size.Z * 32);
        if (hollow) CoverBlocks.Hollow(mesh.Mesh);
        if (lightmapFromAbove) Lightmaps.FromAbove(mesh.Mesh, snowAs);
        if (raise != 0)
            foreach (var positions in (mesh.Mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()?.Positions).OfType<Vec3[]>().Distinct())
                for (int i = 0; i < positions.Length; i++) positions[i] = positions[i] with { Y = positions[i].Y + raise };
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(ident.Id.Split('\\')[^1]));
        Directory.CreateDirectory(Path.GetDirectoryName(blockPath)!);
        // the game saves blocks uncompressed but loads compressed ones too: half the size
        new Gbx<CGameItemModel>(Container(ident, name, block, archetype, ident.Collection, [(variantId, mesh)], spawn)) { BodyCompression = GbxCompression.Compressed }.Save(blockPath);
    }

    // same bytes the game writes for a compiled custom block
    private static CGameItemModel Container(Ident ident, string name, string page, string archetype, Id collection, IEnumerable<(int Id, CPlugStaticObjectModel Mesh)> variants, (Vector3 At, float Pitch, float Roll)? spawn)
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
        // the editor lists it next to the block it replaces
        item.PageName = page;
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
