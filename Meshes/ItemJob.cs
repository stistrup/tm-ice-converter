using System.Numerics;

namespace TmSurfaceEditor;

// what one item is made from
internal sealed record ItemJob(Theme Theme, ItemRequest Request, ItemRecipe Recipe, BlockCatalog Catalog, ItemMaterials Materials, Matrix4x4 Tilt,
    MeshExporter Export, List<(string Path, Matrix4x4 Transform)> Parts, string? Waypoint)
{
    public string Block => Request.Block;
    public ItemKind Kind => Request.Kind;
    public string? Environment => Catalog.Environment;
    public bool Gameplay => BlockNames.IsGameplayBlock(Block);

    public static ItemJob For(ItemRequest r, Theme theme)
    {
        var catalog = new BlockCatalog(Paths.ExtractedStadium) { Environment = r.Environment };
        var recipe = ItemRecipe.For(r, catalog);
        var materials = ItemMaterials.For(r, recipe, theme, catalog.Environment);
        var tilt = Matrix4x4.CreateFromYawPitchRoll(0, r.Tilt.X, r.Tilt.Y);
        var export = ItemExporters.For(r, recipe, catalog, materials.Map, theme, tilt);
        var parts = catalog.FindPlacedBlockParts(r.Block, r.Ground, r.Variant, r.Mobil, r.Clips)
            ?? throw new InvalidDataException($"{r.Block} has nothing to draw");
        var waypoint = recipe.Skin ? null : catalog.Waypoint(r.Block);
        return new(theme, r, recipe, catalog, materials, tilt, export, parts, waypoint);
    }

    public bool IsTerrainFace(MeshPiece p) => TerrainFaces.IsTerrain(p.Material, Environment);

    // left as the game's own material, linked after import
    public bool LinksNative(string material)
        => (Materials.Native.Contains(material) || Recipe.Body && !Materials.Map.ContainsKey(material) && !Theme.KeepsOwnSurface(material))
            && MaterialLib.NativeLink(material, Environment) is not null;
}
