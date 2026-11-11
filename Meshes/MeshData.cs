using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// raw vertex data and material names out of the game's mesh nodes
internal static class MeshData
{
    public sealed record Verts(Vec3[] Positions, Vec3[]? Normals, float[]? Uv0, float[]? Uv1, float[]? Colors);

    // older meshes keep vertices on the visual; frames past the indexed ones are vertex animation (flags)
    public static Verts? VertsOf(CPlugVisualIndexedTriangles vis, int[] indices)
    {
        if (vis.VertexStreams?.FirstOrDefault() is { Positions: { } pos } stream)
            return new Verts(pos, stream.Normals, Uv(stream, 0), Uv(stream, 1), Colors(stream));
        if (vis.Vertices is not { Length: > 0 } all || indices.Length == 0) return null;
        var count = indices.Max() + 1;
        if (count > all.Length) return null;
        var frame = all[..count];
        var normals = frame.All(v => v.Normal is not null) ? frame.Select(v => v.Normal!.Value).ToArray() : null;
        var colors = frame.All(v => v.Color is not null) ? frame.SelectMany(v => new[] { v.Color!.Value.X, v.Color.Value.Y, v.Color.Value.Z, v.Color.Value.W }).ToArray() : null;
        return new Verts(frame.Select(v => v.Position).ToArray(), normals, LegacyUv(vis, 0, count), LegacyUv(vis, 1, count), colors);
    }

    public static string MaterialName(CPlugSolid2Model mesh, int index)
    {
        if (mesh.Materials is { } mats && index < mats.Length && mats[index] is { } ext && ext.File?.FilePath is { } fp)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fp));
        if (mesh.Materials is { } embedded && index < embedded.Length && embedded[index]?.Node?.ShaderFile?.FilePath is { } shader)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(shader));
        if (mesh.MaterialIds is { } ids && index < ids.Length) return ids[index];
        // importer-built: our items keep the importer's name, our custom blocks only the game link
        if (mesh.CustomMaterials is { } custom && index < custom.Length && custom[index].MaterialUserInst is { } inst)
        {
            if (inst.MaterialName is { Length: > 0 } user) return user;
            if (inst.Link is { Length: > 0 } link) return LinkName(link);
        }
        return "Unknown";
    }

    public static float[] Transform(Vec3[] points, Matrix4x4 m)
        => points.SelectMany(p => { var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m); return new[] { v.X, v.Y, v.Z }; }).ToArray();

    public static float[] TransformNormals(Vec3[] normals, Matrix4x4 m)
        => normals.SelectMany(p => { var v = Vector3.Normalize(Vector3.TransformNormal(new Vector3(p.X, p.Y, p.Z), m)); return new[] { v.X, v.Y, v.Z }; }).ToArray();

    // tree V runs the other way (checked: leaf cards land on the alpha only flipped)
    public static float[]? FlipV(float[]? uv) => uv?.Select((x, i) => i % 2 == 1 ? 1 - x : x).ToArray();

    // Stadium\Media\Modifier\PlatformIce\PlatformTech -> PlatformIce_PlatformTech (as the importer names it); Media\Material\X -> X
    private static string LinkName(string link)
    {
        var parts = link.Split('\\', '/');
        var modifier = Array.FindIndex(parts, p => p.Equals("Modifier", StringComparison.OrdinalIgnoreCase));
        return modifier >= 0 && modifier + 2 < parts.Length ? parts[modifier + 1] + "_" + parts[^1] : parts[^1];
    }

    // V counts up here, down in vertex streams (flag cloth vs its pole, both checked against the game)
    private static float[]? LegacyUv(CPlugVisualIndexedTriangles vis, int set, int count)
        => vis.TexCoords is { } sets && set < sets.Length && sets[set].TexCoords is { } uvs && uvs.Length >= count
            ? uvs[..count].SelectMany(t => new[] { t.UV.X, 1 - t.UV.Y }).ToArray()
            : null;

    private static float[]? Colors(CPlugVertexStream stream)
        => stream.Colors is { } sets && sets.TryGetValue(0, out var c)
            ? c.SelectMany(argb => new[] { (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, (argb >> 24) & 0xFF }.Select(b => b / 255f)).ToArray()
            : null;

    private static float[]? Uv(CPlugVertexStream stream, int set)
        => stream.UVs is { } uvs && uvs.TryGetValue(set, out var uv) ? uv.SelectMany(v => new[] { v.X, v.Y }).ToArray() : null;
}
