using System.Numerics;
using System.Text.RegularExpressions;

namespace TmIceConverter;

public enum ItemKind
{
    None,
    Top,
    InTerrain,
    // only if enough concrete faces up
    TopOfIce,
    // effect comes from a gate
    Gated,
    // snow over the native ice twin
    OverTwin,
    // custom ground block: the whole block, its ground plate as the skin would leave it
    GroundBody,
    // ground body over its kept native block: no terrain/Deco faces, the native draws them
    StackedBody,
    // custom block of a kept block: wall faces up as placed iced (as Top / TopOfIce would), the rest as a full item
    TopsBody,
    TopOfIceBody,
    // skin over a native air block (skinsOnly)
    Skin,
    // terrain block in snow only at its foot, the rest its own look
    SnowBottom,
    // the visible terrain of a hill/cliff block, beside its custom block (terrain light in the block's own lightmap darkened the road)
    TerrainFaces,
    // ground body for a snow pick: its Deco ground in snow too
    SnowGround,
}

// per-map look of items; part of the item's name where it changes the mesh
public sealed record ItemStyle(bool ZoneSides = false, bool TerrainSnow = true, float SkinLift = ItemStyle.DefaultSkinLift, float SkinEdge = ItemStyle.DefaultSkinEdge)
{
    public const float DefaultSkinLift = 0.02f;

    // skirt angle from the surface; 90 = straight down
    public const float DefaultSkinEdge = 90;
}

public sealed record ItemRequest(string Block, bool Ground, int Variant, int Mobil, IReadOnlySet<int> Clips, ItemKind Kind, Vector2 Tilt, string? Environment, ItemStyle Style);

// what a kind of item takes from its block
internal sealed record ItemRecipe(ItemKind Kind, bool Top, bool TopsBody, bool Body, bool Skin, bool OnTerrainBody, bool ZoneSides)
{
    public bool Stacked => Kind == ItemKind.StackedBody;
    public bool TerrainFaces => Kind == ItemKind.TerrainFaces;

    public static ItemRecipe For(ItemRequest r, BlockCatalog catalog)
    {
        var kind = r.Kind;
        var topsBody = kind is ItemKind.TopsBody or ItemKind.TopOfIceBody;
        var body = kind is ItemKind.GroundBody or ItemKind.SnowGround or ItemKind.StackedBody or ItemKind.TerrainFaces or ItemKind.SnowBottom || topsBody && r.Ground;
        var onTerrainBody = body && BlockNames.OnTerrainShape(r.Block);
        var whole = (kind == ItemKind.InTerrain || kind == ItemKind.None && r.Ground) && BlockNames.IsWholeOnGround(r.Block, catalog.Environment);
        var skin = !whole && !body && !topsBody && kind != ItemKind.Gated && (kind != ItemKind.None || IsSkin(catalog, r.Block, r.Ground));
        return new ItemRecipe(kind, kind is ItemKind.Top or ItemKind.TopOfIce, topsBody, body, skin, onTerrainBody, r.Style.ZoneSides && !onTerrainBody);
    }

    private static bool IsSkin(BlockCatalog catalog, string block, bool ground)
        => ground && !catalog.IsPillarBlock(block) && !BlockNames.IsWholeOnGround(block, catalog.Environment);

    // file names start with it: recognisable in the editor and the items folder
    public const string Prefix = "gIce_";

    // the block a name was made from; maps built before the prefix don't have it
    public static string SourceBlock(string name)
    {
        var block = name[..name.IndexOf("_Ice")];
        return block.StartsWith(Prefix) ? block[Prefix.Length..] : block;
    }

    // no dots: the importer cuts the name there
    // skins at another lift (mm) or edge angle (degrees) than the default carry it in the name
    public static string ItemName(ItemRequest r, BlockCatalog catalog)
    {
        var name = Prefix + r.Block + "_Ice" + (catalog.DefinedIn(r.Block) is { } env ? "_" + env : "") + KindSuffix(r, catalog)
            + (!r.Style.TerrainSnow && OnTerrain(r.Block, r.Ground, r.Kind) ? "_bare" : "") + TiltKey(r.Tilt)
            + (r.Variant == 0 && r.Mobil == 0 ? "" : $"_v{r.Variant}m{r.Mobil}") + "_" + ClipKey.OfSet(r.Clips);
        if (!Regex.IsMatch(name, "_a?skin")) return name;
        var (lift, edge) = (r.Style.SkinLift, r.Style.SkinEdge);
        return name + (lift != ItemStyle.DefaultSkinLift ? $"_l{MathF.Round(lift * 1000)}" : "") + (edge != ItemStyle.DefaultSkinEdge ? $"_e{MathF.Round(edge)}" : "");
    }

    private static string KindSuffix(ItemRequest r, BlockCatalog catalog)
    {
        var (block, zoneSides) = (r.Block, r.Style.ZoneSides);
        var plainSkin = zoneSides && !block.StartsWith("Deco") ? "_skinzs" : "_skin";
        return r.Kind switch
        {
            ItemKind.Top or ItemKind.TopOfIce => "_top",
            ItemKind.Gated => "_gated",
            ItemKind.GroundBody => zoneSides && !BlockNames.OnTerrainShape(block) ? "_gbodyzs" : "_gbody",
            ItemKind.StackedBody => "_gbodys",
            ItemKind.SnowBottom => "_gbottom",
            ItemKind.SnowGround => "_gsnow",
            ItemKind.TopsBody => "_topsbody",
            ItemKind.TopOfIceBody => "_topsbodyi",
            ItemKind.InTerrain when BlockNames.IsWholeOnGround(block, catalog.Environment) => "_interrain",
            ItemKind.Skin => "_askin",
            ItemKind.TerrainFaces => "_terrain",
            ItemKind.InTerrain or ItemKind.OverTwin => plainSkin,
            _ when IsSkin(catalog, block, r.Ground) => plainSkin,
            _ => r.Ground ? "_g" : "",
        };
    }

    public static bool OnTerrain(string block, bool ground, ItemKind kind) => block.StartsWith("Deco") && (kind == ItemKind.InTerrain || kind == ItemKind.None && ground);

    private static string TiltKey(Vector2 tilt)
    {
        static string Deg(float r) => ((int)MathF.Round(r * 180 / MathF.PI)).ToString().Replace('-', 'm');
        return tilt == default ? "" : $"_t{Deg(tilt.X)}_{Deg(tilt.Y)}";
    }
}
