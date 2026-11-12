using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace TmSurfaceEditor;

internal static class Lightmaps
{
    // lightmap UVs for surfaces without
    public static MeshPiece Projected(MeshPiece piece)
    {
        var (pos, idx) = (piece.Positions, piece.Indices);
        Vector3 At(int i) => new(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
        var remap = new Dictionary<(int Vertex, int Patch), int>();
        var (positions, normals, uv0, uv1, colors) = (new List<float>(), new List<float>(), new List<float>(), new List<float>(), new List<float>());
        var indices = new int[idx.Length];
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var n = Vector3.Cross(At(idx[t + 1]) - At(idx[t]), At(idx[t + 2]) - At(idx[t]));
            var (ax, ay, az) = (MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));
            var patch = ay >= ax && ay >= az ? (n.Y >= 0 ? 0 : 1) : ax >= az ? (n.X >= 0 ? 2 : 3) : (n.Z >= 0 ? 4 : 5);
            for (int k = 0; k < 3; k++)
            {
                var v = idx[t + k];
                if (!remap.TryGetValue((v, patch), out var nv))
                {
                    nv = remap[(v, patch)] = positions.Count / 3;
                    positions.AddRange(pos.AsSpan(3 * v, 3));
                    if (piece.Normals is { } pn) normals.AddRange(pn.AsSpan(3 * v, 3));
                    if (piece.Uv0 is { } u0) uv0.AddRange(u0.AsSpan(2 * v, 2));
                    if (piece.Colors is { } c) colors.AddRange(c.AsSpan(4 * v, 4));
                    var p = At(v) / 32;
                    var (u, w) = patch switch { 0 or 1 => (p.X, p.Z), 2 or 3 => (p.Z, p.Y), _ => (p.X, p.Y) };
                    uv1.AddRange([u + patch * 8, w]);
                }
                indices[t + k] = nv;
            }
        }
        return piece with
        {
            Positions = [.. positions],
            Normals = piece.Normals is null ? null : [.. normals],
            Uv0 = piece.Uv0 is null ? null : [.. uv0],
            Uv1 = [.. uv1],
            Indices = indices,
            Colors = piece.Colors is null ? null : [.. colors],
        };
    }

    // terrain-only item: lightmap UVs back to the exported projection, as terrain shaders read them
    public static void BackToProjection(string itemName)
    {
        var path = Path.Combine(Paths.Items, itemName + ".Item.gbx");
        var gbx = Gbx.Parse<CGameItemModel>(path);
        var mesh = ((CGameCommonItemEntityModel)gbx.Node.EntityModel!).StaticObject!.Mesh!;
        ToNative(mesh, Jsonc.Load<List<MeshPiece>>(Path.Combine(Paths.Pieces, itemName + ".pieces.json")), itemName);
        gbx.Save(path);
    }

    // terrain shaders look lighting up by position over the whole cell; the importer packs lightmap UVs with a margin,
    // so they go back to the exported projection, and the box the bake fills (PreLightGen) covers them all
    private static void ToNative(CPlugSolid2Model mesh, IReadOnlyList<MeshPiece> native, string name)
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

    // every vertex at xz over the block's footprint, steep faces too; U02 = metres per lightmap UV, what sizes the solid's share of the lightmap
    public static void FromAbove(CPlugSolid2Model mesh, string? snowLink = null)
    {
        var streams = (mesh.Visuals ?? []).OfType<CPlugVisualIndexedTriangles>().Select(v => v.VertexStreams?.FirstOrDefault()).OfType<CPlugVertexStream>().Distinct().ToList();
        var extent = streams.SelectMany(s => s.Positions ?? []).Select(p => Math.Max(p.X, p.Z)).DefaultIfEmpty(0).Max();
        // 1 m slack: terrain overhangs its cell a little
        var size = 32 * Math.Max(1, (int)Math.Ceiling((extent - 1) / 32));
        foreach (var stream in streams)
        {
            if (stream.Positions is not { } positions || stream.UVs is null || !stream.UVs.TryGetValue(1, out var uv)) continue;
            for (int i = 0; i < positions.Length; i++) uv[i] = new Vec2(positions[i].X / size, positions[i].Z / size);
        }
        if (mesh.PreLightGenerator is { } gen) (gen.U02, gen.U04, gen.U05, gen.U06, gen.U07) = (size, 0, 0, 1, 1);
        // borders under the snow would share its texels and bake dark into it: one texel for them
        if (snowLink is null) return;
        var visuals = mesh.Visuals ?? [];
        foreach (var geom in mesh.ShadedGeoms ?? [])
            if (mesh.CustomMaterials?.ElementAtOrDefault(geom.MaterialIndex)?.MaterialUserInst?.Link != snowLink
                && visuals.ElementAtOrDefault(geom.VisualIndex) is CPlugVisualIndexedTriangles { VertexStreams: [var stream, ..] } && stream.UVs?.TryGetValue(1, out var uv) == true)
                Array.Fill(uv, new Vec2(0.002f, 0.002f));
    }
}
