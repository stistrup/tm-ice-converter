using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

internal static class Placement
{
    // item origin is the block corner; turning moves the footprint, shift it back
    public static Matrix4x4 Of(CGameCtnBlock block, (int X, int Z) size)
    {
        var corner = new Vector3(block.Coord.X * 32, (block.Coord.Y - 8) * 8, block.Coord.Z * 32);
        var turn = Matrix4x4.CreateRotationY(-(int)block.Direction * MathF.PI / 2);
        var (w, l) = (size.X * 32, size.Z * 32);
        var footprint = new[] { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(0, 0, l), new Vector3(w, 0, l) }
            .Select(c => Vector3.Transform(c, turn)).ToArray();
        return turn * Matrix4x4.CreateTranslation(corner - new Vector3(MathF.Round(footprint.Min(c => c.X)), 0, MathF.Round(footprint.Min(c => c.Z))));
    }

    // the map stores the item's pivot negated: mesh point = position + turn(scale(v + PivotPosition))
    public static Matrix4x4 Of(CGameCtnAnchoredObject item)
    {
        var (p, r, pivot) = (item.AbsolutePositionInMap, item.YawPitchRoll, item.PivotPosition);
        return Matrix4x4.CreateTranslation(pivot.X, pivot.Y, pivot.Z)
            * Matrix4x4.CreateScale(item.Scale is > 0 and var s ? s : 1)
            * Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z)
            * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    }

    // how much lower grid Y sits than on Stadium
    public static float EnvironmentDrop(string? collection)
    {
        collection ??= "Stadium2020";
        lock (Drops)
        {
            if (Drops.TryGetValue(collection, out var known)) return known;
            var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
            var dir = Path.Combine(gameData, collection == "Stadium2020" ? "Stadium" : collection);
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.DecorationSize.Gbx", SearchOption.AllDirectories).FirstOrDefault() : null;
            var drop = 56f;
            if (file is not null && Gbx.ParseNode(file) is CGameCtnDecorationSize size)
                drop = 8 * ((size.OffsetBlockY ? size.BaseHeightBase + 1 : size.BaseHeightOffset) - 8);
            else Console.WriteLine($"  warning: no decoration size extracted for {collection}; assuming items sit as on RedIsland");
            return Drops[collection] = drop;
        }
    }

    private static readonly Dictionary<string, float> Drops = [];

    public static Int3 CellAt(Vector3 world) => new((int)MathF.Floor(world.X / 32), (int)MathF.Floor(world.Y / 8) + 8, (int)MathF.Floor(world.Z / 32));

    public static (int X, int Z) Footprint(IReadOnlyList<Int3>? units)
        => units is { Count: > 0 } ? (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1) : (1, 1);

    // -180° and 180° are one turn: one item, one block
    public static Vector2 Tilt(CGameCtnBlock block)
        => block.IsFree && block.YawPitchRoll is { } r && (MathF.Abs(r.Y) > 0.01f || MathF.Abs(r.Z) > 0.01f) ? new Vector2(HalfTurnUp(r.Y), HalfTurnUp(r.Z)) : default;

    private static float HalfTurnUp(float a) => a < -MathF.PI + 0.01f ? a + 2 * MathF.PI : a;

    public static bool QuarterTurns(Vector2 tilt)
    {
        static bool Square(float a) => MathF.Abs(a / (MathF.PI / 2) - MathF.Round(a / (MathF.PI / 2))) < 0.01f;
        return Square(tilt.X) && Square(tilt.Y);
    }
}
