using System.Numerics;
using GBX.NET;

namespace TmIceConverter;

public interface IBlockShapes
{
    (int X, int Z) BlockSize(string blockName, bool ground, int variantIndex) => (1, 1);
    string? Environment { get; set; }
    bool RecordMissing { get; set; }
    IReadOnlyList<Int3>? Units(string blockName, bool ground, int variantIndex);
    IReadOnlyList<ClipSide> ClipSides(string blockName, bool ground, int variantIndex);
    bool IsPillarBlock(string blockName);
    bool IsTerrain(string blockName);
    // null = unknown
    bool? EnvironmentHas(string blockName);
    int? NoPillarBelowVariant(string blockName, bool ground, int variantIndex);
    // null = no waypoint
    string? Waypoint(string blockName);
    int ShapedClipTurn(string blockName, bool ground, int variantIndex, int bit, ClipInfo clip);
    // null = not extracted
    IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Material)>? CollisionTriangles(string blockName, bool ground, int variantIndex, int mobil);
    // null = not extracted
    IReadOnlySet<string>? Materials(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips);
    // tree models the prefab places, in block space; empty if none or not extracted
    IReadOnlyList<(string Model, Matrix4x4 Place)> Trees(string blockName, bool ground, int variantIndex, int mobil, IReadOnlySet<int> clips);
}
