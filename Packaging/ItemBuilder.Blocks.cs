using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// custom blocks around built items
public sealed partial class ItemBuilder
{
    private readonly HashSet<string> rewritten = [];
    // whether a planned item has the theme's surface on it, known from its mesh before it's built
    private readonly Dictionary<string, bool> themeSurfaces = [];

    // the item's main body plus the baked clips in a custom block of the archetype; the game adds the other clips
    // ramp: local footprint sides (bits -X, +X, -Z, +Z) whose surface rises by the skin lift to meet a skinned neighbour
    public BuiltBlock? BlockFor(string block, string archetype, bool ground, int variant, int mobil, Id collection, IReadOnlySet<int> baked, ItemKind? kind = null, Vector2 tilt = default, bool lift = false, int ramp = 0)
    {
        if (CustomBlockWriter.VariantId(ground, variant, mobil) is not { } variantId) return null;
        var itemKind = kind ?? (!ground ? ItemKind.None : lift ? ItemKind.StackedBody : ItemKind.GroundBody);
        if (For(block, ground, variant, mobil, baked, itemKind, tilt) is not { } item || item == BuiltItem.Nothing) return null;
        // none of the theme in it: clips draw the surface (penalty slopes), the game would keep them native
        if (!ThemeSurface(item.FilePath)) return null;
        var name = ItemName(item) + (archetype == block ? "" : "_on_" + archetype) + (lift ? "_lift1" : "") + RampKey(ramp);
        var ident = new Ident($"{Paths.ItemFolder}\\{name}.Block.Gbx", collection, item.Ident.Author);
        var path = Path.Combine(Paths.Blocks, name + ".Block.Gbx");
        WriteBlock(path, item.FilePath, to =>
        {
            var size = shapes.BlockSize(block, ground, variant);
            (Vector3 At, float Pitch, float Roll)? spawn = shapes.Waypoint(block) is not null ? shapes.Spawn(block, ground, variant, mobil) : null;
            CustomBlockWriter.Write(item.FilePath, to, ident, block, archetype, variantId, spawn, lift ? size : null, ramp: ramp == 0 ? null : (ramp, Style.SkinLift, size));
        });
        return new BuiltBlock(ident, path);
    }

    internal void WriteBlock(string path, string itemPath, Action<string> write)
    {
        if (Planning) return;
        lock (NameLock(path))
        {
            bool first;
            lock (rewritten) first = rewritten.Add(path);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(itemPath)) write(path);
            else if (RewriteBlocks && first) RewriteIfChanged(path, write);
        }
    }

    // an unchanged block keeps its date: the map's embedded zip stores it
    private static void RewriteIfChanged(string path, Action<string> write)
    {
        var fresh = path + ".new";
        write(fresh);
        if (File.ReadAllBytes(fresh).SequenceEqual(File.ReadAllBytes(path))) File.Delete(fresh);
        else File.Move(fresh, path, true);
    }

    private string RampKey(int ramp)
    {
        if (ramp == 0) return "";
        return $"_r{ramp}" + (Style.SkinLift != ItemStyle.DefaultSkinLift ? $"l{MathF.Round(Style.SkinLift * 1000)}" : "");
    }

    private static string ItemName(BuiltItem item) => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.FilePath));

    private bool ThemeSurface(string itemPath)
    {
        lock (themeSurfaces)
            if (themeSurfaces.TryGetValue(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(itemPath)), out var known)) return known;
        return !File.Exists(itemPath) || HasThemeSurface(itemPath);
    }

    private bool HasThemeSurface(string itemPath)
        => (Gbx.ParseNode<CGameItemModel>(itemPath).EntityModel as CGameCommonItemEntityModel)?.StaticObject?.Mesh?.CustomMaterials is { } materials
            && materials.Any(m => m.MaterialUserInst is { } u && ((u.MaterialName ?? "") + (u.Link ?? "")).Contains(Theme.Family));
}
