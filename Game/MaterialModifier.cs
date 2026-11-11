using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// a block's TerrainModifier: a material folder plus a game skin listing which materials it swaps.
// Swaps: game material name -> the name to load from Folder instead
public sealed record MaterialModifier(string Folder, IReadOnlyDictionary<string, string> Swaps)
{
    // GBX.NET can't read the modifier's body; its paths are plain text
    public static MaterialModifier? Read(string blockPath, string modifierRef, string stadiumDir)
    {
        var ancestor = Gbx.ParseHeader(blockPath).RefTable?.AncestorLevel ?? 0;
        var modifierFile = GameFiles.ResolveRef(blockPath, ancestor, modifierRef);
        if (!File.Exists(modifierFile)) return null;
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(modifierFile));
        var gameData = Path.GetDirectoryName(stadiumDir)!;
        var folder = Regex.Match(text, @"[A-Za-z0-9_]+\\Media\\Modifier\\[A-Za-z0-9_]+\\") is { Success: true } f
            ? Path.Combine(gameData, f.Value.Replace('\\', '/').TrimEnd('/')) : null;
        var skin = Regex.Match(text, @"([A-Za-z0-9_]+)\.GameSkin\.gbx", RegexOptions.IgnoreCase) is { Success: true } k
            ? Path.Combine(stadiumDir, "GameSkin", k.Groups[1].Value + ".GameSkin.gbx") : null;
        if (folder is null || !Directory.Exists(folder) || skin is null || !File.Exists(skin)) return null;
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fid in Gbx.ParseHeaderNode<CPlugGameSkin>(skin).HeaderFids ?? [])
            if (fid.Directory is { } source)
                swaps.TryAdd(Path.GetFileName(source.Replace('\\', '/')).Split('.')[0], fid.Name!.TrimStart('*'));
        return new MaterialModifier(folder, swaps);
    }
}
