using GBX.NET;

namespace TmIceConverter;

internal static class SnowBlocks
{
    // snow terrain: the terrain block's own shape on a ground block that clears the native grass (a terrain archetype crashes the game)
    private const string SnowTerrainArchetype = "DecoPlatformBase";
    private const float SnowTerrainRaise = 0f;
    // under a deco block reaching over its cell: level with it in places, it would flicker and bake dark
    private const float SnowUnderDecoRaise = -1f;
    // WhiteShore's snow ground, with its frosted grass decals; loads in other environments too
    private const string SnowTerrainMaterial = @"WhiteShore\Media\Material\Land";

    public static IceBlock? SnowTerrainFor(this ItemBuilder items, string block, int variant, int mobil, Id collection, bool underDeco = false)
    {
        if (items.For(block, true, variant, mobil, new HashSet<int>(), ItemKind.GroundBody) is not { } item || item == IceItem.Nothing) return null;
        var itemName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath));
        var name = itemName + (underDeco ? "_snowhollow" : "_snowland");
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Work, "Blocks", name + ".Block.Gbx");
        items.WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, SnowTerrainArchetype, 0, raise: underDeco ? SnowUnderDecoRaise : SnowTerrainRaise, snowAs: SnowTerrainMaterial, lightmapFromAbove: true, hollow: underDeco));
        return new IceBlock(ident, path);
    }

    // deco in a snow terrain area: its own archetype keeps the ground away, its deco surface in the same snow
    public static IceBlock? SnowDecoFor(this ItemBuilder items, string block, int variant, int mobil, Id collection)
    {
        if (CustomBlockWriter.VariantId(false, variant, mobil) is not { } variantId
            || items.For(block, false, variant, mobil, new HashSet<int>(), ItemKind.None) is not { } item || item == IceItem.Nothing) return null;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath)) + "_decosnow2";
        var ident = new Ident($"IceConverter\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Work, "Blocks", name + ".Block.Gbx");
        items.WriteBlock(path, item.FilePath, to => CustomBlockWriter.Write(item.FilePath, to, ident, block, variantId, snowAs: SnowTerrainMaterial, lightmapFromAbove: true));
        return new IceBlock(ident, path);
    }

    // nothing to see or bake, the block still keeps the terrain away: a 3 cm speck at the cell's middle, its floor
    public static void Hollow(GBX.NET.Engines.Plug.CPlugSolid2Model mesh)
    {
        foreach (var stream in (mesh.Visuals ?? []).OfType<GBX.NET.Engines.Plug.CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()).OfType<GBX.NET.Engines.Plug.CPlugVertexStream>().Distinct())
            if (stream.Positions is { } positions)
                for (int i = 0; i < positions.Length; i++) positions[i] = new Vec3(16 + (positions[i].X - 16) / 1000, positions[i].Y / 1000, 16 + (positions[i].Z - 16) / 1000);
    }
}
