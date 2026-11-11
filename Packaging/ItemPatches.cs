using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// fixes to the importer's output
internal static class ItemPatches
{
    // the importer ignores <Waypoint> and _socket_start: patch both in
    public static void SetWaypoint(string itemPath, string waypoint)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        gbx.Node.WaypointType = Enum.Parse<CGameItemModel.EWaypointType>(waypoint);
        var entity = (CGameCommonItemEntityModel)gbx.Node.EntityModel!;
        var chunk = entity.Chunks.Get<CGameCommonItemEntityModel.Chunk2E027000>()
            ?? throw new InvalidDataException("item entity has no chunk 0x2E027000");
        var s = Waypoints.SpawnPoint;
        chunk.U03 = new Iso4(1, 0, 0, 0, 1, 0, 0, 0, 1, s.X, s.Y, s.Z);
        gbx.Save(itemPath);
    }

    // the importer keeps colours only for DColor0 materials; without them DecoHill reads as grass. Same set the editor fills on resave.
    private static string[] ColouredMaterials(Theme theme) => ["Custom", theme.Family + "_Deco", theme.Family + "_Decal", "Decal", "Chrono", "GlassWaterWall"];

    public static void AddVertexColours(string itemPath, Theme theme)
    {
        var coloured = ColouredMaterials(theme);
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        if ((gbx.Node.EntityModel as CGameCommonItemEntityModel)?.StaticObject?.Mesh is not { } mesh) return;
        var added = false;
        foreach (var geom in mesh.ShadedGeoms ?? [])
        {
            var name = mesh.CustomMaterials is { } mats && geom.MaterialIndex < mats.Length ? mats[geom.MaterialIndex].MaterialUserInst?.MaterialName : null;
            if (name is null || !coloured.Any(name.StartsWith)) continue;
            if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles { VertexStreams: [{ Positions: { } pos, Colors: { } colors } stream, ..] }) continue;
            if (colors.ContainsKey(0)) continue;
            InsertColourDecl(stream);
            colors[0] = Enumerable.Repeat(-1, pos.Length).ToArray();
            added = true;
        }
        if (added) gbx.Save(itemPath);
    }

    // GBX.NET writes streams from their declarations: Flags1 = stride/4 << 20 | type << 8 | attribute, Flags2 = offset << 2
    private static readonly FieldInfo DataDecls = typeof(CPlugVertexStream).GetField("dataDecls", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(CPlugVertexStream), "dataDecls");

    private static void InsertColourDecl(CPlugVertexStream stream)
    {
        const uint color0 = 0x08, colorType = 0x08 << 8, space = 0x2u << 28, strideStep = 1u << 20;
        var decls = (CPlugVertexStream.DataDecl[])DataDecls.GetValue(stream)!;
        var at = Array.FindIndex(decls, d => (d.Flags1 & 0xFF) > color0);
        if (at < 0) at = decls.Length;
        var offset = at < decls.Length ? decls[at].Offset : (int)((decls[0].Flags1 >> 20) & 0xFF) * 4;
        foreach (var (d, i) in decls.Select((d, i) => (d, i)))
        {
            d.Flags1 += strideStep;
            if (i >= at) { d.Flags2 += 4 << 2; d.Offset += 4; }
        }
        var colour = new CPlugVertexStream.DataDecl
        {
            Flags1 = (decls[0].Flags1 & 0x0FF00000) | space | colorType | color0,
            Flags2 = (uint)offset << 2,
            Offset = (ushort)offset,
        };
        DataDecls.SetValue(stream, decls[..at].Append(colour).Concat(decls[at..]).ToArray());
    }

    public static void PointNative(string itemPath, string? environment)
    {
        var gbx = Gbx.Parse<CGameItemModel>(itemPath);
        var mesh = ((CGameCommonItemEntityModel)gbx.Node.EntityModel!).StaticObject!.Mesh!;
        foreach (var inst in (mesh.CustomMaterials ?? []).Select(m => m.MaterialUserInst).OfType<CPlugMaterialUserInst>().Where(u => u.MaterialName?.StartsWith(MaterialLib.NativePrefix) == true))
        {
            inst.Link = MaterialLib.NativeLink(inst.MaterialName![MaterialLib.NativePrefix.Length..], environment);
            inst.IsUsingGameMaterial = true;
            inst.SurfacePhysicId = Enum.Parse<CPlugSurface.MaterialId>(MaterialLib.NativeSurface(inst.MaterialName!, environment));
        }
        gbx.Save(itemPath);
    }
}
