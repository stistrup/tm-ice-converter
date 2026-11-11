using System.Numerics;

namespace TmIceConverter;

internal static class Waypoints
{
    public static readonly Vector3 SpawnPoint = new(16, 2, 16);

    // spawn socket + trigger box for waypoint items
    private const string HelperMaterial = "TrackBorders";
    public static IEnumerable<MeshPiece> Pieces(string waypoint, IReadOnlyList<MeshPiece> block, string socketMaterial)
    {
        yield return new MeshPiece("_socket_start", socketMaterial, Visible: false, Collidable: true,
            [SpawnPoint.X, SpawnPoint.Y, SpawnPoint.Z, SpawnPoint.X + 0.1f, SpawnPoint.Y, SpawnPoint.Z, SpawnPoint.X, SpawnPoint.Y, SpawnPoint.Z + 0.1f],
            null, null, null, [0, 1, 2]);
        if (waypoint != "Start")
            // tech roads at 2 m, dirt at 0.5 m
        {
            var gate = GateZ(block);
            yield return Box("_trigger_gate", new Vector3(2.7f, 0, gate - 0.5f), new Vector3(29.3f, 10, gate + 0.5f));
        }
    }

    private static float GateZ(IReadOnlyList<MeshPiece> block)
    {
        var z = block.Where(p => p.Visible && p.Name.Contains("Chrono"))
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => p.Positions[i * 3 + 2])).ToList();
        return z.Count == 0 ? 16 : (z.Min() + z.Max()) / 2;
    }

    private static MeshPiece Box(string name, Vector3 lo, Vector3 hi)
    {
        var p = new List<float>();
        for (int i = 0; i < 8; i++)
            p.AddRange([(i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z]);
        int[] faces = [0, 2, 3, 1, /* -Z */ 4, 5, 7, 6, /* +Z */ 0, 1, 5, 4, /* -Y */ 2, 6, 7, 3, /* +Y */ 0, 4, 6, 2, /* -X */ 1, 3, 7, 5 /* +X */];
        var idx = new List<int>();
        for (int f = 0; f < faces.Length; f += 4)
            idx.AddRange([faces[f], faces[f + 1], faces[f + 2], faces[f], faces[f + 2], faces[f + 3]]);
        return new MeshPiece(name, HelperMaterial, Visible: false, Collidable: false, p.ToArray(), null, null, null, idx.ToArray());
    }
}
