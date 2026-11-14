namespace TmSurfaceEditor;

public sealed record SceneData(
    string Map,
    string? Environment,
    Dictionary<string, List<ScenePiece>> Meshes,
    List<SceneInstance> Instances,
    List<SceneInstance> Placeholders,
    List<SceneInstance> Boxes,
    List<SceneWaypoint> Waypoints,
    Dictionary<string, SceneMaterial> Materials,
    Dictionary<string, int> Skipped);

public sealed record ScenePiece(string Material, float[] Positions, float[]? Normals, float[]? Uv0, int[] Indices, float[]? Colors);

// M is row-major (world = local * M)
public sealed record SceneInstance(string Mesh, float[] M, Dictionary<string, object> Info);

public sealed record SceneWaypoint(string Kind, string Mesh, float[] M);

public sealed record SceneMaterial(string Name, string? Shader, List<SceneTexture> Textures, List<string>? Tints = null);

// Projected: the texture file's own mapping (texcoord scale 1/32 = by world position), null if unknown
public sealed record SceneTexture(string Role, string Name, string? Image, bool? Projected = null);
