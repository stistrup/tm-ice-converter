using System.Numerics;

namespace TmIceConverter;

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
}
