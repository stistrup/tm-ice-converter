using System.Numerics;

namespace TmSurfaceEditor;

internal sealed record ItemMeshResult(List<MeshPiece> Pieces, string? Waypoint, BlockCatalog Catalog, string? Error = null);

// a block's mesh pieces, as its recipe wants them
internal sealed class ItemMesh(Theme theme, HashSet<string> missing)
{
    public const string NothingToCover = "nothing to cover";

    private const float TopOfTwinMinArea = 256;
    // m; was 0.3 with the whole bank in snow
    private const float CoverUnderWater = 0.1f;
    // m above flat ground where the snow stops up a slope
    private const float CoverLip = 0.3f;

    // a step skips blocks it doesn't apply to; null: nothing to cover
    private delegate List<MeshPiece>? Step(List<MeshPiece> pieces, ItemJob job);

    // each kind's steps, in order. Two families: surfaces (skins, tops, plain items) and ground bodies, which differ in one step
    private Step[] PipelineFor(ItemKind kind) => kind switch
    {
        ItemKind.GroundBody or ItemKind.TerrainFaces => [WithoutLayerMasks, WithPads, TerrainOrRest, WithNativeLinks],
        ItemKind.StackedBody => [WithoutLayerMasks, WithPads, WithoutKeptNativeFaces, WithNativeLinks],
        ItemKind.CoveredTerrain => [WithoutLayerMasks, CoverTerrain, WithPads, TerrainOrRest, WithNativeLinks],
        ItemKind.CoveredGround => [WithoutLayerMasks, WithPads, TerrainOrRest, CoverGround, WithNativeLinks],
        // a body only on the ground
        ItemKind.TopsBody or ItemKind.TopOfTwinBody => [WithThemedTops, WithoutLayerMasks, WithPads, TerrainOrRest, WithNativeLinks],
        _ => [WithoutFoot, WithPads, WithNativeLinks],
    };

    public ItemMeshResult Make(ItemRequest r)
    {
        var job = ItemJob.For(r, theme);
        var pieces = Drawn(job);
        foreach (var step in PipelineFor(job.Kind))
        {
            if (step(pieces, job) is not { } next) return Stop(job, NothingToCover);
            pieces = next;
        }
        lock (missing) missing.UnionWith(job.Catalog.Missing);
        if (WhyNothing(pieces, job) is { } error) return Stop(job, error);
        if (job.Waypoint is { } waypoint) pieces.AddRange(Waypoints.Pieces(waypoint, pieces, theme.Road).ToList());
        return new ItemMeshResult(Tilted(pieces, job), job.Waypoint, job.Catalog);
    }

    private static ItemMeshResult Stop(ItemJob job, string error) => new([], null, job.Catalog, error);

    // water only drawn: its physics is the native block's water
    private static List<MeshPiece> Drawn(ItemJob job)
        => job.Export.Export(job.Parts).Where(p => p.Visible || p.Material != "Water").ToList();

    private static string? WhyNothing(List<MeshPiece> pieces, ItemJob job)
    {
        if (pieces.Count == 0 && job.Catalog.Missing.Count > 0) return "its prefab isn't extracted yet";
        if (pieces.Count == 0) return job.Recipe.Top || job.Recipe.Skin && job.Gameplay ? NothingToCover : "no geometry found";
        if (job.Kind == ItemKind.TopOfTwin && VisibleArea(pieces) < TopOfTwinMinArea) return NothingToCover;
        return null;
    }

    // custom blocks are tilted by the game
    private static List<MeshPiece> Tilted(List<MeshPiece> pieces, ItemJob job)
        => job.Request.Tilt == default || job.Recipe.TopsBody ? pieces : pieces.Select(p => PieceOps.Turned(p, job.Tilt)).ToList();

    private static float VisibleArea(List<MeshPiece> pieces) => pieces.Where(p => p.Visible).Sum(PieceOps.Area);

    // wall faces up iced as placed, the rest as sides; null: too little faces up
    private List<MeshPiece>? WithThemedTops(List<MeshPiece> pieces, ItemJob job)
    {
        var topFiles = new GameFiles(Paths.ExtractedStadium);
        var iced = ItemExporters.ThemedTops(job, topFiles).Export(job.Parts);
        var area = VisibleArea(iced);
        if (area == 0 || job.Kind == ItemKind.TopOfTwinBody && area < TopOfTwinMinArea) return null;
        lock (missing) missing.UnionWith(topFiles.Missing);
        var sides = ItemExporters.Sides(job).Export(job.Parts);
        return [.. pieces, .. sides.Select(p => p with { Name = p.Name + "_side" }), .. iced.Select(p => p with { Name = p.Name + "_top" })];
    }

    private static List<MeshPiece> WithoutFoot(List<MeshPiece> pieces, ItemJob job)
    {
        var r = job.Request;
        if (r.Style.TerrainCover || !ItemRecipe.OnTerrain(r.Block, r.Ground, r.Kind)) return pieces;
        return PieceOps.WithoutFoot(pieces, job.Theme);
    }

    // another environment's terrain material reads them as its own layers
    private static List<MeshPiece> WithoutLayerMasks(List<MeshPiece> pieces, ItemJob job)
    {
        if (!job.Recipe.Body || !job.Catalog.IsTerrain(job.Block)) return pieces;
        return pieces.Select(p => p with { Colors = null }).ToList();
    }

    // shores down to the water, the rest its ground (flat ground, a hill's or cliff's foot); drives as it looks
    private static List<MeshPiece> CoverTerrain(List<MeshPiece> pieces, ItemJob job)
    {
        var (keep, cover) = (job.Request.CoverKeep, job.Theme.Cover);
        pieces = TerrainFaces.AsTerrainBelow(pieces, job.Environment);
        if (WaterLine(pieces) is { } line) return PieceOps.CoverAboveCollision(PieceOps.CoverAbove(pieces, line, cover, keep), line, cover, keep);
        // the ground in snow, up to the lip where it climbs; none on top yet
        return PieceOps.CoverFoot(PieceOps.CoverFoot(pieces, CoverLip, cover, false, keep), CoverLip, cover, true, keep);
    }

    private static List<MeshPiece> WithPads(List<MeshPiece> pieces, ItemJob job)
    {
        if (!job.Gameplay) return pieces;
        var pad = Pads.PadGameplayOf(job.Block);
        if (!job.Recipe.Skin) return Pads.IcyPads(pieces, pad);
        var plain = new BlockCatalog(Paths.ExtractedStadium).Plain.Export(job.Parts);
        return Pads.AroundPad(pieces, plain, pad, job.Request.Style.SkinLift);
    }

    // the kept native block under it draws these
    private static List<MeshPiece> WithoutKeptNativeFaces(List<MeshPiece> pieces, ItemJob job)
    {
        return pieces.Where(p => !job.IsTerrainFace(p) && !p.Material.StartsWith("Deco")).ToList();
    }

    // terrain shaders light by position over the whole lightmap, the road's part too: visible terrain goes to its own item
    private static List<MeshPiece>? TerrainOrRest(List<MeshPiece> pieces, ItemJob job)
    {
        if (!job.Recipe.OnTerrainBody) return pieces;
        var terrainItem = job.Recipe.TerrainFaces;
        pieces = pieces.Where(p => !p.Visible || job.IsTerrainFace(p) == terrainItem).ToList();
        pieces = TerrainFaces.AsTerrainBelow(pieces, job.Environment);
        if (!terrainItem) return pieces;
        pieces = pieces.Where(p => p.Visible).ToList();
        return pieces.Count == 0 ? null : pieces;
    }

    // a ground body holding water (a lake): its terrain above the water in snow, like a shore; rocks and the rest stay
    private static List<MeshPiece> CoverGround(List<MeshPiece> pieces, ItemJob job)
    {
        // the kept native gate draws its curtain
        pieces = pieces.Where(p => !p.Material.Contains("TriggerFX")).ToList();
        if (WaterLine(pieces) is not { } line) return pieces;
        var terrain = pieces.Where(job.IsTerrainFace).ToList();
        return [.. PieceOps.CoverAboveCollision(PieceOps.CoverAbove(terrain, line, job.Theme.Cover), line, job.Theme.Cover), .. pieces.Where(p => !job.IsTerrainFace(p))];
    }

    private static List<MeshPiece> WithNativeLinks(List<MeshPiece> pieces, ItemJob job)
        => pieces.Select(p => job.LinksNative(p.Material) ? p with { Material = MaterialLib.NativePrefix + p.Material } : p).ToList();

    private static float? WaterLine(List<MeshPiece> pieces)
        => pieces.FirstOrDefault(p => p.Visible && p.Material == "Water") is { } water ? water.Positions[1] - CoverUnderWater : null;
}
