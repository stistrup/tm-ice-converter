using System.Numerics;
using System.Text.RegularExpressions;

namespace TmIceConverter;

internal sealed record ItemMeshResult(List<MeshPiece> Pieces, string? Waypoint, BlockCatalog Catalog, string? Error = null);

// a block's mesh pieces, as its recipe wants them
internal sealed class ItemMesh(IceMaterials iceMaterials, HashSet<string> missing)
{
    public const string NothingToCover = "nothing to cover";

    private const float TopMinY = 0.02f;
    private const float TopOfIceMinArea = 256;
    // m; was 0.3 with the whole bank in snow
    private const float SnowUnderWater = 0.1f;

    public ItemMeshResult Make(ItemRequest r)
    {
        var catalog = new BlockCatalog(Paths.ExtractedStadium) { Environment = r.Environment };
        var recipe = ItemRecipe.For(r, catalog);
        ItemMeshResult Stop(string error) => new([], null, catalog, error);

        var (materialMap, native) = Materials(r, recipe, catalog.Environment);
        var tilt = Matrix4x4.CreateFromYawPitchRoll(0, r.Tilt.X, r.Tilt.Y);
        var export = Exporter(r, recipe, catalog, materialMap, tilt);
        var parts = catalog.FindPlacedBlockParts(r.Block, r.Ground, r.Variant, r.Mobil, r.Clips)
            ?? throw new InvalidDataException($"{r.Block} has nothing to draw");
        var waypoint = recipe.Skin ? null : catalog.Waypoint(r.Block);
        // water only drawn: its physics is the native block's water
        var pieces = export.Export(parts).Where(p => p.Visible || p.Material != "Water").ToList();
        if (recipe.TopsBody)
        {
            if (WithIcedTops(pieces, parts, recipe, materialMap, export, tilt) is not { } withTops) return Stop(NothingToCover);
            pieces = withTops;
        }
        if (!r.Style.TerrainSnow && ItemRecipe.OnTerrain(r.Block, r.Ground, r.Kind)) pieces = PieceOps.WithoutFoot(pieces);
        // terrain layer masks: another environment's terrain material reads them as its own layers
        if (recipe.Body && catalog.IsTerrain(r.Block)) pieces = pieces.Select(p => p with { Colors = null }).ToList();
        // shore: snow down to just under the water
        if (recipe.Body && catalog.IsTerrain(r.Block) && pieces.FirstOrDefault(p => p.Visible && p.Material == "Water") is { } water)
            pieces = PieceOps.SnowAbove(pieces, water.Positions[1] - SnowUnderWater, "PlatformIce_DecoHill");
        var gameplay = BlockNames.IsGameplayBlock(r.Block);
        if (recipe.Skin && gameplay) pieces = Pads.AroundPad(pieces, new BlockCatalog(Paths.ExtractedStadium).Plain.Export(parts), Pads.PadGameplayOf(r.Block), r.Style.SkinLift);
        else if (gameplay) pieces = Pads.IcyPads(pieces, Pads.PadGameplayOf(r.Block));
        if (recipe.Stacked) pieces = pieces.Where(p => !IsTerrainFace(p, catalog.Environment) && !p.Material.StartsWith("Deco")).ToList();
        else if (recipe.OnTerrainBody)
        {
            // terrain shaders light by position over the whole lightmap, the road's part too: visible terrain goes to its own item
            pieces = pieces.Where(p => !p.Visible || IsTerrainFace(p, catalog.Environment) == recipe.TerrainFaces).ToList();
            pieces = TerrainFaces.AsTerrainBelow(pieces, catalog.Environment);
            if (recipe.TerrainFaces) pieces = pieces.Where(p => p.Visible).ToList();
            if (recipe.TerrainFaces && pieces.Count == 0) return Stop(NothingToCover);
        }
        pieces = pieces.Select(p => (native.Contains(p.Material) || recipe.Body && !materialMap.ContainsKey(p.Material) && !(MaterialLib.Surface(p.Material) is { } own && !MaterialLib.NotIce.Contains(own)))
            && MaterialLib.NativeLink(p.Material, catalog.Environment) is not null ? p with { Material = MaterialLib.NativePrefix + p.Material } : p).ToList();
        lock (missing) missing.UnionWith(catalog.Missing);
        if (pieces.Count == 0) return Stop(catalog.Missing.Count > 0 ? "its prefab isn't extracted yet" : recipe.Top || recipe.Skin && gameplay ? NothingToCover : "no geometry found");
        if (r.Kind == ItemKind.TopOfIce && pieces.Where(p => p.Visible).Sum(PieceOps.Area) < TopOfIceMinArea) return Stop(NothingToCover);
        if (waypoint is not null) pieces.AddRange(Waypoints.Pieces(waypoint, pieces).ToList());
        // tilt baked into the mesh; custom blocks are tilted by the game
        if (r.Tilt != default && !recipe.TopsBody) pieces = pieces.Select(p => PieceOps.Turned(p, tilt)).ToList();
        return new ItemMeshResult(pieces, waypoint, catalog);
    }

    private static bool IsTerrainFace(MeshPiece p, string? environment)
        => p.Material.StartsWith(TerrainFaces.TerrainShader) || TerrainFaces.ShaderOf(p.Material, environment) is not null;

    private (Dictionary<string, string> Map, HashSet<string> Native) Materials(ItemRequest r, ItemRecipe recipe, string? environment)
    {
        var block = r.Block;
        var map = iceMaterials.Map(recipe.Top ? "top:" : recipe.Skin ? "skin:" : null);
        // "<block pattern>|<material>": only for those blocks
        foreach (var (key, to) in map.Where(kv => kv.Key.Contains('|')).ToList())
            if (key.Split('|') is [var pattern, var material] && Regex.IsMatch(block, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$"))
                map[material] = to;
        if (recipe.Body) foreach (var key in map.Keys.Where(m => m.StartsWith("Deco") && !MaterialLib.SkinCovers(block, m, recipe.ZoneSides)).ToList()) map.Remove(key);
        if (recipe.OnTerrainBody) foreach (var key in map.Keys.Where(m => m.StartsWith(TerrainFaces.TerrainShader) || TerrainFaces.ShaderOf(m, environment) is not null).ToList()) map.Remove(key);
        // "native" in ice-materials.jsonc: the game's own material, linked after import
        var native = map.Where(kv => kv.Value == "native").Select(kv => kv.Key).ToHashSet();
        foreach (var key in native) map.Remove(key);
        if (recipe.Skin || recipe.Top) native.Clear();
        if (BlockNames.Effect(block) is { } effect) foreach (var (k, v) in Pads.EffectLook(effect)) map[k] = v;
        return (map, native);
    }

    private MeshExporter Exporter(ItemRequest r, ItemRecipe recipe, BlockCatalog catalog, Dictionary<string, string> map, Matrix4x4 tilt)
    {
        var (block, top, skin, environment) = (r.Block, recipe.Top, recipe.Skin, catalog.Environment);
        return new MeshExporter(catalog.Files, map)
        {
            KeepMaterial = top ? BlockNames.IsWallMaterial
                : recipe.TopsBody ? m => !BlockNames.IsWallMaterial(m)
                : r.Kind == ItemKind.Gated ? m => !Pads.IsPadLook(m)
                : skin ? m => MaterialLib.SkinCovers(block, m, recipe.ZoneSides)
                : r.Ground && !recipe.Body && !BlockNames.IsWholeOnGround(block, environment) ? m => !m.StartsWith("Deco")
                : null,
            Lift = skin ? r.Style.SkinLift : 0,
            SkirtAngle = r.Style.SkinEdge,
            Up = Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.Transpose(tilt)),
            KeepFace = top ? n => Vector3.TransformNormal(n, tilt).Y > TopMinY : null,
            // the lip keeps the road's own look (its texture's dark edge), linked to the game material
            LipAs = top ? null : m => iceMaterials.SwappedToIce(map, m) ? (skin ? "" : MaterialLib.NativeLink(m, environment) is not null ? MaterialLib.NativePrefix + m : "CustomConcrete") : null,
            PlatformLayout = top,
        };
    }

    // null: too little faces up
    private List<MeshPiece>? WithIcedTops(List<MeshPiece> pieces, List<(string Path, Matrix4x4 Transform)> parts, ItemRecipe recipe, Dictionary<string, string> map, MeshExporter export, Matrix4x4 tilt)
    {
        bool Up(Vector3 n) => Vector3.TransformNormal(n, tilt).Y > TopMinY;
        var topFiles = new GameFiles(Paths.ExtractedStadium);
        var tops = new MeshExporter(topFiles, iceMaterials.Map("top:"))
            { KeepMaterial = BlockNames.IsWallMaterial, Up = export.Up, KeepFace = Up, PlatformLayout = true };
        var sides = new MeshExporter(new GameFiles(Paths.ExtractedStadium), map)
            { KeepMaterial = BlockNames.IsWallMaterial, Up = export.Up, KeepFace = n => !Up(n), LipAs = export.LipAs };
        var iced = tops.Export(parts);
        var area = iced.Where(p => p.Visible).Sum(PieceOps.Area);
        if (area == 0 || recipe.Kind == ItemKind.TopOfIceBody && area < TopOfIceMinArea) return null;
        lock (missing) missing.UnionWith(topFiles.Missing);
        return [.. pieces, .. sides.Export(parts).Select(p => p with { Name = p.Name + "_side" }), .. iced.Select(p => p with { Name = p.Name + "_top" })];
    }
}
