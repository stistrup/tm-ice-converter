using System.Numerics;

namespace TmSurfaceEditor;

// Bit = unit * 6 + side; Probe lands in the facing cell
public sealed record ClipSide(int Bit, Vector3 Probe)
{
    public Vector3 Center { get; init; }
    public IReadOnlyList<ClipInfo> Clips { get; init; } = [];
    public bool ClipOnly { get; init; }
}

public sealed record ClipInfo(int Index, string Name, string Type, bool Vertical, string? Group, string? SymmetricGroup,
    string? VerticalGroup, string? Pair, bool Full, bool Deletable, bool Anti, string MultiDir = "SameDir");

internal static class ClipKey
{
    public static int Of(int bit, int index, int column = 0) => bit * 32 + index + (column << 16);

    public const int ColumnTop = 1, ColumnBottom = 2, ColumnOnGround = 4;

    public static string OfSet(IReadOnlySet<int> clips)
        => clips.Count == 0 ? "none" : System.Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.ASCII.GetBytes(string.Join(",", clips.Order()))))[..12];
}
