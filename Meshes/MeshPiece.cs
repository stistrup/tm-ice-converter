using System.Text.Json;

namespace TmSurfaceEditor;

// Trackmania space: Y up, metres
public sealed record MeshPiece(
    string Name,
    string Material,
    bool Visible,
    bool Collidable,
    float[] Positions,
    float[]? Normals,
    float[]? Uv0,
    float[]? Uv1,
    int[] Indices,
    float[]? Colors = null) // decals render black without
{
    public static string Serialize(IEnumerable<MeshPiece> pieces)
        => JsonSerializer.Serialize(pieces, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
