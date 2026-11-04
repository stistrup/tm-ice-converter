using System.Numerics;

namespace TmIceConverter;

// raises a skin off the surface below it
internal static class SkinLift
{
    // along the normal, not straight up (no ledge at borders)
    public static float[] Lifted(float[] positions, int[] indices, float lift, Vector3 up)
    {
        if (lift == 0) return positions;
        var normals = new Vector3[positions.Length / 3];
        Vector3 At(int v) => new(positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            var n = Vector3.Cross(At(b) - At(a), At(c) - At(a));
            if (Vector3.Dot(n, up) < 0) n = -n;
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        var lifted = (float[])positions.Clone();
        for (int v = 0; v < normals.Length; v++)
        {
            if (normals[v] == Vector3.Zero) continue;
            var d = Vector3.Normalize(normals[v]) * lift;
            (lifted[3 * v], lifted[3 * v + 1], lifted[3 * v + 2]) = (lifted[3 * v] + d.X, lifted[3 * v + 1] + d.Y, lifted[3 * v + 2] + d.Z);
        }
        return lifted;
    }

    // closes the lift gap at open edges, facing out
    // reach: how far the skirt's foot lies outside the edge (0 = vertical)
    public static MeshPiece WithSkirt(MeshPiece piece, float[] unlifted, float reach)
    {
        if (reach < 0.0001f) reach = 0;
        var (pos, idx) = (piece.Positions, piece.Indices);
        Vector3 At(float[] p, int v) => new(p[3 * v], p[3 * v + 1], p[3 * v + 2]);
        (int, int, int) Key(int v) => ((int)MathF.Round(unlifted[3 * v] * 1000), (int)MathF.Round(unlifted[3 * v + 1] * 1000), (int)MathF.Round(unlifted[3 * v + 2] * 1000));
        var edges = new Dictionary<((int, int, int), (int, int, int)), (int A, int B, int C, int Count)>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var (a, b, c) = (idx[t + k], idx[t + (k + 1) % 3], idx[t + (k + 2) % 3]);
                var (ka, kb) = (Key(a), Key(b));
                if (ka == kb) continue;
                var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
                edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : (a, b, c, 1);
            }
        var open = edges.Values.Where(e => e.Count == 1).ToList();
        if (open.Count == 0) return piece;
        var (positions, normals, uv0, uv1, colors) = (new List<float>(pos), piece.Normals is { } n0 ? new List<float>(n0) : null,
            piece.Uv0 is { } u0 ? new List<float>(u0) : null, piece.Uv1 is { } u1 ? new List<float>(u1) : null, piece.Colors is { } c0 ? new List<float>(c0) : null);
        var indices = new List<int>(idx);
        foreach (var (a, b, c, _) in open)
        {
            var (ta, tb) = (At(pos, a), At(pos, b));
            var edge = tb - ta;
            var outward = At(pos, c) - ta;
            outward -= edge * Vector3.Dot(outward, edge) / edge.LengthSquared();
            outward = -Vector3.Normalize(outward);
            var first = positions.Count / 3;
            var (fa, fb) = (At(unlifted, a) + outward * reach, At(unlifted, b) + outward * reach);
            var slope = Vector3.Normalize(Vector3.Cross(tb - ta, fa - ta));
            if (Vector3.Dot(slope, outward) < 0) slope = -slope;
            foreach (var (v, p) in new[] { (a, ta), (b, tb), (b, fb), (a, fa) })
            {
                positions.AddRange([p.X, p.Y, p.Z]);
                normals?.AddRange([slope.X, slope.Y, slope.Z]);
                uv0?.AddRange(piece.Uv0!.AsSpan(2 * v, 2));
                uv1?.AddRange(piece.Uv1!.AsSpan(2 * v, 2));
                colors?.AddRange(piece.Colors!.AsSpan(4 * v, 4));
            }
            var face = Vector3.Cross(tb - ta, fb - ta);
            indices.AddRange(Vector3.Dot(face, outward) >= 0 ? [first, first + 1, first + 2, first, first + 2, first + 3] : [first, first + 2, first + 1, first, first + 3, first + 2]);
        }
        return piece with
        {
            Positions = [.. positions], Normals = normals?.ToArray(), Uv0 = uv0?.ToArray(), Uv1 = uv1?.ToArray(), Colors = colors?.ToArray(), Indices = [.. indices],
        };
    }
}
