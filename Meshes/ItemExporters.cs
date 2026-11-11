using System.Numerics;

namespace TmIceConverter;

// how each kind of item reads its block's meshes
internal static class ItemExporters
{
    private const float TopMinY = 0.02f;

    public static bool FacesUp(Vector3 normal, Matrix4x4 tilt) => Vector3.TransformNormal(normal, tilt).Y > TopMinY;

    public static MeshExporter For(ItemRequest r, ItemRecipe recipe, BlockCatalog catalog, Dictionary<string, string> map, Theme theme, Matrix4x4 tilt)
    {
        var top = recipe.Top;
        return new MeshExporter(catalog.Files, map)
        {
            KeepMaterial = KeepMaterial(r, recipe, theme, catalog.Environment),
            Lift = recipe.Skin ? r.Style.SkinLift : 0,
            SkirtAngle = r.Style.SkinEdge,
            Up = Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.Transpose(tilt)),
            KeepFace = top ? n => FacesUp(n, tilt) : null,
            LipAs = top ? null : m => theme.SwappedIn(map, m) ? LipLook(m, recipe.Skin, catalog.Environment) : null,
            PlatformLayout = top,
        };
    }

    // the wall faces facing up, as Top would draw them
    public static MeshExporter IcedTops(ItemJob job, GameFiles files)
        => new(files, job.Theme.Map("top:"))
            { KeepMaterial = BlockNames.IsWallMaterial, Up = job.Export.Up, KeepFace = n => FacesUp(n, job.Tilt), PlatformLayout = true };

    // the wall faces not facing up, with the item's own materials
    public static MeshExporter Sides(ItemJob job)
        => new(new GameFiles(Paths.ExtractedStadium), job.Materials.Map)
            { KeepMaterial = BlockNames.IsWallMaterial, Up = job.Export.Up, KeepFace = n => !FacesUp(n, job.Tilt), LipAs = job.Export.LipAs };

    // null: all
    private static Func<string, bool>? KeepMaterial(ItemRequest r, ItemRecipe recipe, Theme theme, string? environment)
    {
        if (recipe.Top) return BlockNames.IsWallMaterial;
        if (recipe.TopsBody) return m => !BlockNames.IsWallMaterial(m);
        if (r.Kind == ItemKind.Gated) return m => !Pads.IsPadLook(m);
        if (recipe.Skin) return m => theme.SkinCovers(r.Block, m, recipe.ZoneSides);
        if (r.Ground && !recipe.Body && !BlockNames.IsWholeOnGround(r.Block, environment)) return m => !m.StartsWith("Deco");
        return null;
    }

    // the lip keeps the road's own look (its texture's dark edge), linked to the game material
    private static string LipLook(string material, bool skin, string? environment)
    {
        if (skin) return "";
        return MaterialLib.NativeLink(material, environment) is not null ? MaterialLib.NativePrefix + material : "CustomConcrete";
    }
}
