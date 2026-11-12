using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// what's placed, not how it's lit: a game save with shadows keeps it, though it gives the map a new UID
public static class MapFingerprint
{
    public static string Of(CGameCtnChallenge map)
    {
        var lines = new List<string>();
        foreach (var block in map.GetBlocks())
            lines.Add(block.IsFree && block.AbsolutePositionInMap is { } p
                ? $"b {block.Name} {Cm(p.X)} {Cm(p.Y)} {Cm(p.Z)}"
                : $"b {block.Name} {block.Coord.X},{block.Coord.Y},{block.Coord.Z} {(int)block.Direction}");
        foreach (var item in map.AnchoredObjects ?? [])
            lines.Add($"i {item.ItemModel.Id} {Cm(item.AbsolutePositionInMap.X)} {Cm(item.AbsolutePositionInMap.Y)} {Cm(item.AbsolutePositionInMap.Z)}");
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..24].ToLowerInvariant();
    }

    private static string Cm(float metres) => MathF.Round(metres * 100).ToString(CultureInfo.InvariantCulture);
}
