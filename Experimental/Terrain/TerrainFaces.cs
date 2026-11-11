using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// the game's terrain shaders and materials
internal static class TerrainFaces
{
    // embedded terrain materials are named after their shader; without collision below, the environment's first material on that shader
    public const string TerrainShader = "Tech3 Block Py";

    private static readonly Dictionary<(string, string?), string?> terrainMaterials = [];

    // each terrain face takes the material of the collision under it (rock walls, dirt floor); the layer ids it was drawn with crash custom blocks
    public static List<MeshPiece> AsTerrainBelow(List<MeshPiece> pieces, string? environment)
    {
        static Vector3 At(MeshPiece p, int v) => new(p.Positions[3 * v], p.Positions[3 * v + 1], p.Positions[3 * v + 2]);
        var below = pieces.Where(p => p.Collidable && ShaderOf(p.Material, environment) is not null)
            .SelectMany(p => Enumerable.Range(0, p.Indices.Length / 3).Select(t => (p.Material, A: At(p, p.Indices[3 * t]), B: At(p, p.Indices[3 * t + 1]), C: At(p, p.Indices[3 * t + 2])))).ToList();
        return pieces.SelectMany(p =>
        {
            if (!p.Visible || !p.Material.StartsWith(TerrainShader)) return [p];
            var own = below.Where(c => ShaderOf(c.Material, environment) == p.Material).ToList();
            if (own.Count == 0) return TerrainMaterial(p.Material, environment) is { } m ? [p with { Material = m }] : [p];
            return Enumerable.Range(0, p.Indices.Length / 3)
                .GroupBy(t => { var mid = (At(p, p.Indices[3 * t]) + At(p, p.Indices[3 * t + 1]) + At(p, p.Indices[3 * t + 2])) / 3; return own.MinBy(c => Geometry.DistanceToTriangle(mid, c.A, c.B, c.C)).Material; })
                .Select(g => p with { Name = $"{p.Name}_{g.Key}", Material = g.Key, Indices = g.SelectMany(t => p.Indices[(3 * t)..(3 * t + 3)]).ToArray() });
        }).ToList();
    }

    public static bool IsTerrain(string material, string? environment)
        => material.StartsWith(TerrainShader) || ShaderOf(material, environment) is not null;

    public static string? ShaderOf(string material, string? environment)
    {
        var path = Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, environment ?? "Stadium", "Media", "Material", material + ".Material.Gbx");
        lock (terrainMaterials)
        {
            if (shaders.TryGetValue(path, out var known)) return known;
            return shaders[path] = File.Exists(path) && Gbx.ParseNode<CPlugMaterial>(path).ShaderFile?.FilePath is { } shader && Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(shader)) is var name && name.StartsWith(TerrainShader) ? name : null;
        }
    }

    private static readonly Dictionary<string, string?> shaders = [];

    private static string? TerrainMaterial(string shader, string? environment)
    {
        if (!shader.StartsWith(TerrainShader)) return null;
        lock (terrainMaterials)
        {
            if (terrainMaterials.TryGetValue((shader, environment), out var known)) return known;
            var dir = Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, environment ?? "Stadium", "Media", "Material");
            var found = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.Material.Gbx").Order()
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(Gbx.ParseNode<CPlugMaterial>(f).ShaderFile?.FilePath ?? "")) == shader) : null;
            return terrainMaterials[(shader, environment)] = found is null ? null : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(found));
        }
    }
}
