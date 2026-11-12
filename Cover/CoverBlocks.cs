using GBX.NET;

namespace TmSurfaceEditor;

internal static class CoverBlocks
{
    // snow terrain: the terrain block's own shape on a ground block that clears the native grass (a terrain archetype crashes the game)
    private const string CoverTerrainArchetype = "DecoPlatformBase";
    private const float CoverTerrainRaise = 0f;
    // under a deco block reaching over its cell: level with it in places, it would flicker and bake dark
    private const float CoverUnderDecoRaise = -1f;
    // WhiteShore's snow ground, with its frosted grass decals; loads in other environments too
    private const string CoverTerrainMaterial = @"WhiteShore\Media\Material\Land";

    public static BuiltBlock? CoverTerrainFor(this ItemBuilder items, string block, int variant, int mobil, Id collection, bool underDeco = false, IReadOnlyList<System.Numerics.Vector2>? snowKeep = null)
    {
        if (items.For(block, true, variant, mobil, new HashSet<int>(), ItemKind.CoveredTerrain, snowKeep: snowKeep) is not { } item || item == BuiltItem.Nothing) return null;
        var itemName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath));
        var name = itemName + (underDeco ? "_snowhollow" : "_snowland");
        var ident = new Ident($"{Paths.ItemFolder}\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Blocks, name + ".Block.Gbx");
        items.WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, block, CoverTerrainArchetype, 0,
            raise: underDeco ? CoverUnderDecoRaise : CoverTerrainRaise, snowAs: CoverTerrainMaterial, lightmapFromAbove: true, hollow: underDeco));
        return new BuiltBlock(ident, path);
    }

    // deco in a snow terrain area: its own archetype keeps the ground away, its deco surface in the same snow
    // also for a snow pick on a block; on the ground it's the ground body, its Deco ground in snow
    public static BuiltBlock? CoverDecoFor(this ItemBuilder items, string block, int variant, int mobil, Id collection, bool ground = false)
    {
        if (CustomBlockWriter.VariantId(ground, variant, mobil) is not { } variantId
            || items.For(block, ground, variant, mobil, new HashSet<int>(), ground ? ItemKind.CoveredGround : ItemKind.None) is not { } item || item == BuiltItem.Nothing) return null;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + "_decosnow2";
        var ident = new Ident($"{Paths.ItemFolder}\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Blocks, name + ".Block.Gbx");
        items.WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, block, block, variantId, snowAs: CoverTerrainMaterial, lightmapFromAbove: true));
        return new BuiltBlock(ident, path);
    }

    // a gameplay block's ground in snow, on its terrain cell; the block itself goes to the air and keeps its trigger
    public static BuiltBlock? GameplayGroundFor(this ItemBuilder items, string block, int variant, int mobil, Id collection)
    {
        if (items.For(block, true, variant, mobil, new HashSet<int>(), ItemKind.CoveredGround) is not { } item || item == BuiltItem.Nothing) return null;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + "_snowground";
        var ident = new Ident($"{Paths.ItemFolder}\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Blocks, name + ".Block.Gbx");
        items.WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, block, CoverTerrainArchetype, 0, snowAs: CoverTerrainMaterial, lightmapFromAbove: true));
        return new BuiltBlock(ident, path);
    }

    // nothing to see or bake, the block still keeps the terrain away: a 3 cm speck at the cell's middle, its floor
    public static void Hollow(GBX.NET.Engines.Plug.CPlugSolid2Model mesh)
    {
        foreach (var stream in (mesh.Visuals ?? []).OfType<GBX.NET.Engines.Plug.CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()).OfType<GBX.NET.Engines.Plug.CPlugVertexStream>().Distinct())
            if (stream.Positions is { } positions)
                for (int i = 0; i < positions.Length; i++) positions[i] = new Vec3(16 + (positions[i].X - 16) / 1000, positions[i].Y / 1000, 16 + (positions[i].Z - 16) / 1000);
    }
}
