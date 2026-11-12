using System.Numerics;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// how the game turns a clip isn't in the data: fit its mesh to the block
internal sealed class ClipFit(string stadiumDir)
{
    private MeshExporter Plain() => new(new GameFiles(stadiumDir), new Dictionary<string, string>());

    public List<Vector3> Corners(List<(string Path, Matrix4x4 Transform)> parts)
    {
        var plain = Plain();
        return plain.Export(parts).Where(p => p.Visible)
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
            .Distinct().ToList();
    }

    // the data doesn't say how the game turns it: pick the best fit
    public Matrix4x4 FittedTopTurn((string Path, Matrix4x4 Transform) clip, Matrix4x4 place, List<Vector3> corners)
    {
        var plain = Plain();
        int Matches(float yaw)
        {
            var turned = plain.Export([(clip.Path, clip.Transform * BlockParts.AroundUnitCenter(yaw) * place)]).Where(p => p.Visible);
            return turned.SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
                .Distinct().Count(v => corners.Any(c => Vector3.DistanceSquared(c, v) < 0.01f));
        }
        var turns = new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 };
        var best = turns.Select(t => (Turn: t, Matches: Matches(t))).ToList();
        return BlockParts.AroundUnitCenter(best.Max(b => b.Matches) > best[0].Matches ? best.MaxBy(b => b.Matches).Turn : 0);
    }

    // same, for bottom clips bigger than a unit
    public Matrix4x4 InFootprintTurn((string Path, Matrix4x4 Transform) clip, Matrix4x4 place, CGameCtnBlockInfoVariant variant)
    {
        var cells = (variant.BlockUnitModels ?? []).Select(u => (u.RelativeOffset.X, u.RelativeOffset.Z)).ToHashSet();
        bool Inside(Vector3 v) => cells.Any(c => v.X >= c.X * 32 - 0.5f && v.X <= c.X * 32 + 32.5f && v.Z >= c.Z * 32 - 0.5f && v.Z <= c.Z * 32 + 32.5f);
        var plain = Plain();
        int Outside(float yaw) => plain.Export([(clip.Path, clip.Transform * BlockParts.AroundUnitCenter(yaw) * place)]).Where(p => p.Visible)
            .SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2])))
            .Count(v => !Inside(v));
        var unturned = Outside(0);
        if (unturned == 0) return Matrix4x4.Identity;
        var best = new[] { MathF.PI / 2, MathF.PI, -MathF.PI / 2 }.Select(t => (Turn: t, Outside: Outside(t))).MinBy(b => b.Outside);
        return best.Outside < unturned ? BlockParts.AroundUnitCenter(best.Turn) : Matrix4x4.Identity;
    }

    // mirrored blocks share one clip: match the road's slope
    public Matrix4x4 BottomClipTurn(string pillarShapeDirs, (string Path, Matrix4x4 Transform) clip, (string Path, Matrix4x4 Transform) block)
    {
        var turns = pillarShapeDirs switch
        {
            "SymmetricalDirs" => new[] { 0f, MathF.PI },
            "AllDir" or "AllDirs" => [0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2],
            _ => [0f],
        };
        if (turns.Length == 1) return Matrix4x4.Identity;
        var road = Slope(block, upOnly: true);
        var clipSlope = Slope(clip, upOnly: false);
        if (road.Length() < 0.1f || clipSlope.Length() < 0.1f) return Matrix4x4.Identity;
        var best = turns.MaxBy(t => Vector2.Dot(Turned(clipSlope, t), road) - (t == 0 ? 0 : 0.01f));
        return BlockParts.AroundUnitCenter(best);
    }

    public Matrix4x4 TopClipTurn((string Path, Matrix4x4 Transform) clip, Vector2 rise)
    {
        var slope = Slope(clip, upOnly: false);
        if (slope.Length() < 0.1f || rise.Length() < 0.5f) return Matrix4x4.Identity;
        var turns = new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 };
        return BlockParts.AroundUnitCenter(turns.MaxBy(t => Vector2.Dot(Turned(slope, t), rise) - (t == 0 ? 0 : 0.01f)));
    }

    private static Vector2 Turned(Vector2 slope, float yaw)
        => new Vector2(Vector3.TransformNormal(new Vector3(slope.X, 0, slope.Y), Matrix4x4.CreateRotationY(yaw)).X,
            Vector3.TransformNormal(new Vector3(slope.X, 0, slope.Y), Matrix4x4.CreateRotationY(yaw)).Z);

    public Vector2 Slope((string Path, Matrix4x4 Transform) part, bool upOnly)
    {
        var key = (part.Path, part.Transform, upOnly);
        lock (slopes)
            if (slopes.TryGetValue(key, out var known)) return known;
        var plain = Plain();
        Vector2 sum = default;
        float area = 0;
        foreach (var piece in plain.Export([part]).Where(p => upOnly ? p.Collidable && !p.Visible : p.Visible))
            for (int t = 0; t + 2 < piece.Indices.Length; t += 3)
            {
                Vector3 V(int i) => new(piece.Positions[i * 3], piece.Positions[i * 3 + 1], piece.Positions[i * 3 + 2]);
                var n = Vector3.Cross(V(piece.Indices[t + 1]) - V(piece.Indices[t]), V(piece.Indices[t + 2]) - V(piece.Indices[t]));
                var a = n.Length();
                if (a < 1e-6f) continue;
                n /= a;
                if (upOnly ? n.Y < 0.5f : MathF.Abs(n.Y) < 0.5f) continue;
                sum += new Vector2(-n.X / n.Y, -n.Z / n.Y) * a;
                area += a;
            }
        var slope = area > 0 ? sum / area : default;
        lock (slopes) slopes[key] = slope;
        return slope;
    }

    private static readonly Dictionary<(string, Matrix4x4, bool), Vector2> slopes = [];
}
