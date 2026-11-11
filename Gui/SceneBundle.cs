using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// a map's scene for the browser: manifest.json + geometry.bin, cached per map file
internal static class SceneBundle
{
    // bump when the output changes
    private const int Format = 35;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string GeometryOf(string map) => Path.Combine(CacheDir(map), "geometry.bin");

    // returns the manifest's path
    public static string Ensure(string map, bool rebuild)
    {
        var dir = CacheDir(map);
        var (manifest, stampFile) = (Path.Combine(dir, "manifest.json"), Path.Combine(dir, "stamp"));
        var info = new FileInfo(map);
        var stamp = $"{Format}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        if (!rebuild && File.Exists(manifest) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return manifest;

        Directory.CreateDirectory(dir);
        File.Delete(stampFile);
        var node = Gbx.ParseNode<CGameCtnChallenge>(map);
        var shapes = new BlockCatalog(Paths.ExtractedStadium) { Environment = node.Collection?.ToString() };
        var view = new MapView(shapes);
        var scene = view.Build(node);
        var notExtracted = ExtractList.Add(view.Missing);
        using (var geometry = File.Create(GeometryOf(map)))
            File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(Write(scene, notExtracted, geometry), JsonOptions));
        File.WriteAllText(stampFile, stamp);
        return manifest;
    }

    private static string CacheDir(string map)
        => Path.Combine(Paths.Cache, "Gui", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(map)))[..16]);

    // a scene kept in memory (the preview's added items): manifest json, geometry
    public static (byte[] Manifest, byte[] Geometry) InMemory(SceneData scene)
    {
        using var geometry = new MemoryStream();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(Write(scene, [], geometry), JsonOptions);
        return (manifest, geometry.ToArray());
    }

    private static Manifest Write(SceneData scene, List<string> notExtracted, Stream geometry)
    {
        var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var materialKeys = scene.Materials.Keys.ToList();
        var materialIndex = materialKeys.Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
        var materials = materialKeys.Select(key => scene.Materials[key]).Select(m => new ManifestMaterial(m.Name, m.Shader,
            m.Textures.Select(t => new ManifestTexture(t.Role, t.Name, t.Image is null ? null : Path.GetRelativePath(gameData, t.Image))).ToList(), m.Tints)).ToList();

        var meshKeys = scene.Meshes.Keys.ToList();
        var meshIndex = meshKeys.Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
        var meshes = meshKeys.Select(key => new ManifestMesh(key, scene.Meshes[key]
            .GroupBy(p => p.Material)
            .Select(group => WritePiece(geometry, materialIndex[group.Key], group.ToList()))
            .ToList())).ToList();

        return new Manifest(scene.Map, scene.Environment, geometry.Length, notExtracted, materials, meshes,
            scene.Instances.Select(i => new ManifestInstance(meshIndex[i.Mesh], i.M, i.Info)).ToList(),
            scene.Placeholders, scene.Boxes, scene.Skipped);
    }

    // one material's pieces merged. Layout at Offset: positions, normals, uvs (floats), indices (uint), then rgba (floats) when Colors
    private static ManifestPiece WritePiece(Stream stream, int material, List<ScenePiece> pieces)
    {
        var offset = stream.Position;
        var vertices = pieces.Sum(p => p.Positions.Length / 3);
        foreach (var p in pieces) Put(stream, p.Positions);
        foreach (var p in pieces) Put(stream, p.Normals ?? new float[p.Positions.Length]);
        foreach (var p in pieces) Put(stream, p.Uv0 ?? new float[p.Positions.Length / 3 * 2]);
        var (baseVertex, indexCount) = (0, 0);
        foreach (var p in pieces)
        {
            var shifted = p.Indices.Select(i => i + baseVertex).ToArray();
            Put(stream, shifted);
            baseVertex += p.Positions.Length / 3;
            indexCount += shifted.Length;
        }
        // some shaders scale by them (TAddModCV: waypoint trigger glow)
        var colors = pieces.Any(p => p.Colors is not null);
        if (colors)
            foreach (var p in pieces) Put(stream, p.Colors ?? Enumerable.Repeat(1f, p.Positions.Length / 3 * 4).ToArray());
        return new ManifestPiece(material, offset, vertices, indexCount, colors);
    }

    private static void Put<T>(Stream stream, T[] values) where T : struct => stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));

    private sealed record Manifest(
        string Map,
        string? Environment,
        long GeometryBytes,
        List<string> NotExtracted,
        List<ManifestMaterial> Materials,
        List<ManifestMesh> Meshes,
        List<ManifestInstance> Instances,
        List<SceneInstance> Placeholders,
        List<SceneInstance> Boxes,
        Dictionary<string, int> Skipped);

    private sealed record ManifestMaterial(string Name, string? Shader, List<ManifestTexture> Textures, List<string>? Tints);

    private sealed record ManifestTexture(string Role, string Name, string? Image);

    private sealed record ManifestMesh(string Key, List<ManifestPiece> Pieces);

    private sealed record ManifestPiece(int Material, long Offset, int Vertices, int Indices, bool Colors);

    private sealed record ManifestInstance(int Mesh, float[] M, Dictionary<string, object> Info);
}
