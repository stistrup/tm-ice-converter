using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmSurfaceEditor;

// game material files -> scene materials, keyed by game-data path; vistas restyle Stadium's
internal sealed class SceneMaterials(BlockCatalog shapes)
{
    private static readonly string GameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;

    private readonly ILookup<string, string> materialFiles = Directory
        .EnumerateFiles(GameData, "*.Material.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
        .ToLookup(f => Stem(f), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> restyle = [];

    public Dictionary<string, SceneMaterial> All { get; } = [];

    public HashSet<string> Missing { get; } = [];

    public void ForCollection(string? collection) => restyle = VistaRestyle(collection);

    // an importer material as our items use it (no vista restyle)
    public SceneMaterial Own(string name) => All[Key(name, null, ours: true)];

    public string Key(string name, MaterialModifier? modifier, bool ours)
    {
        if (name.StartsWith(MeshExporter.VegetPrefix)) return VegetKey(name);
        string? path = null;
        // linked to the game's own material after import
        if (name.StartsWith(MaterialLib.NativePrefix) && MaterialLib.NativeLink(name[MaterialLib.NativePrefix.Length..], shapes.Environment) is { } link
            && Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, link.Replace('\\', '/') + ".Material.Gbx") is var native && File.Exists(native))
        {
            // terrain All (Cliff, Dirt…) drawn as the terrain shader, like native terrain pieces
            if (ShaderOf(native) is { } terrain && terrain.Contains("_Ids")) return Key(terrain, modifier, ours);
            path = native;
        }
        else if (modifier?.Folder is { } folder && modifier.Swaps.TryGetValue(name, out var swap) && FindFile(folder, swap + ".Material.Gbx") is { } m) path = m;
        else if (name.IndexOf('_') is > 0 and var u
            && FindFile(Path.Combine(Paths.ExtractedStadium, "Media", "Modifier", name[..u]), name[(u + 1)..] + ".Material.Gbx") is { } i) path = i;
        else path = materialFiles[name].OrderBy(f => f.Contains("/Media/Material/") ? 0 : 1).FirstOrDefault();

        if (path is not null && !ours && restyle.GetValueOrDefault(shapes.Files.RelativeToGameData(path).ToLowerInvariant()) is { } restyled) path = restyled;
        var key = path is null ? name : shapes.Files.RelativeToGameData(path);
        if (!All.ContainsKey(key)) All[key] = path is null ? new SceneMaterial(name, null, []) : Material(name, path);
        return key;
    }

    // tree models carry their All inline: dds refs, a leaf (alpha cut) or bark kind
    private string VegetKey(string name)
    {
        if (All.ContainsKey(name)) return name;
        var hash = name.LastIndexOf('#');
        var material = VegetTree.Read(Path.Combine(GameData, name[MeshExporter.VegetPrefix.Length..hash])).Materials[int.Parse(name[(hash + 1)..])];
        var textures = new List<SceneTexture>();
        foreach (var (role, image) in new[] { "D", "N", "R" }.Zip(material.Images))
        {
            if (image.Length == 0) continue;
            if (!File.Exists(image)) Missing.Add(shapes.Files.RelativeToGameData(image));
            textures.Add(new SceneTexture(role, Stem(image), File.Exists(image) ? image : null));
        }
        All[name] = new SceneMaterial(material.Name, material.Kind == VegetKind.Leaf ? "VegetLeaf" : "VegetBark", textures);
        return name;
    }

    private static string? ShaderOf(string materialPath)
        => (Gbx.ParseHeader(materialPath).RefTable?.Files ?? []).Select(f => f.FilePath).FirstOrDefault(f => f.EndsWith(".Material.gbx", StringComparison.OrdinalIgnoreCase)) is { } sh ? Stem(sh) : null;

    private SceneMaterial Material(string name, string path)
    {
        var header = Gbx.ParseHeader(path);
        var ancestor = header.RefTable?.AncestorLevel ?? 0;
        var refs = (header.RefTable?.Files ?? []).Select(f => f.FilePath).ToList();
        var shader = refs.FirstOrDefault(f => f.EndsWith(".Material.gbx", StringComparison.OrdinalIgnoreCase)) is { } sh ? Stem(sh) : null;
        var textures = new List<SceneTexture>();
        foreach (var tex in refs.Where(f => f.EndsWith(".Texture.gbx", StringComparison.OrdinalIgnoreCase)))
        {
            var texPath = Resolve(path, ancestor, tex);
            var stem = Stem(texPath);
            var role = stem[(stem.LastIndexOf('_') + 1)..];
            string? image = null;
            if (File.Exists(texPath))
            {
                var texHeader = Gbx.ParseHeader(texPath);
                if (texHeader.RefTable?.Files.FirstOrDefault()?.FilePath is { } img)
                    image = Resolve(texPath, texHeader.RefTable.AncestorLevel, img);
                if (image?.EndsWith(".TextureArray.Gbx", StringComparison.OrdinalIgnoreCase) == true) image = ArrayImage(image, stem);
                // a decal set (the vegetation scatter) isn't the colour; the layer image sits under Image/
                else if (image?.EndsWith(".Gbx", StringComparison.OrdinalIgnoreCase) == true) image = null;
            }
            else Missing.Add(shapes.Files.RelativeToGameData(texPath));
            // a guess: textures the game builds at run time have no image anywhere, so a miss here isn't "not extracted"
            var guessed = image is null;
            image ??= Path.Combine(Path.GetDirectoryName(texPath)!, "Image", stem + ".dds");
            if (!File.Exists(image))
            {
                if (!guessed) Missing.Add(shapes.Files.RelativeToGameData(image));
                image = null;
            }
            textures.Add(new SceneTexture(role, stem, image));
        }
        var table = refs.FirstOrDefault(f => f.EndsWith(".ColorTable.gbx.json", StringComparison.OrdinalIgnoreCase)) is { } t ? Tints(Resolve(path, ancestor, t)) : null;
        return new SceneMaterial(name, shader, textures, table);
    }

    // a block's colour (White, Green, Blue, Red, Black) picks one; Default keeps the texture as painted
    private static List<string>? Tints(string tableFile)
    {
        if (!File.Exists(tableFile)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(tableFile), new JsonDocumentOptions { AllowTrailingCommas = true });
        return json.RootElement.TryGetProperty("Colors", out var colors)
            ? colors.EnumerateArray().Select(c => c.GetString() is { Length: >= 7 } hex ? hex[..7] : "#ffffff").ToList()
            : null;
    }

    // on a vista the game swaps Stadium All for <env>/Media/Modifier/StadiumOnTerrain/<name> (Deco becomes the vista's ground),
    // listed in Stadium's StadiumOnTerrain game skin; game-data path (lower case) -> restyled file
    private static Dictionary<string, string> VistaRestyle(string? collection)
    {
        if (collection is null or "Stadium2020" || !collection.All(char.IsLetterOrDigit)) return [];
        var skinFile = Path.Combine(GameData, "Stadium", "GameSkin", "StadiumOnTerrain.GameSkin.gbx");
        var dir = Path.Combine(GameData, collection, "Media", "Modifier", "StadiumOnTerrain");
        if (!File.Exists(skinFile) || !Directory.Exists(dir)) return [];
        var table = new Dictionary<string, string>();
        foreach (var fid in Gbx.ParseNode<CPlugGameSkin>(skinFile).HeaderFids ?? [])
        {
            if (fid.Directory is not { } source || FindFile(dir, fid.Name + ".Material.Gbx") is not { } target) continue;
            table[source.Replace('\\', '/').ToLowerInvariant()] = target;
        }
        return table;
    }

    // a texture array lists one image per layer; the texture's own name picks it
    private static string? ArrayImage(string arrayFile, string stem)
    {
        if (!File.Exists(arrayFile)) return null;
        var refs = Gbx.ParseHeader(arrayFile).RefTable;
        return refs?.Files.FirstOrDefault(f => Path.GetFileName(f.FilePath.Replace('\\', '/')).Equals(stem + ".dds", StringComparison.OrdinalIgnoreCase)) is { } hit
            ? Resolve(arrayFile, refs.AncestorLevel, hit.FilePath)
            : null;
    }

    private static string? FindFile(string dir, string fileName)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, fileName, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault()
            : null;

    private static string Resolve(string fromFile, int ancestorLevel, string refPath)
    {
        var dir = Path.GetDirectoryName(fromFile)!;
        for (int i = 0; i < ancestorLevel; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.GetFullPath(Path.Combine(dir, refPath.Replace('\\', '/')));
    }

    private static string Stem(string path) => Path.GetFileName(path).Split('.')[0];
}
