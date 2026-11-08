using System.Text.RegularExpressions;

namespace TmIceConverter;

// a placed custom block's name -> the item it was built from; Archetype = the native block the game draws its clips from
internal sealed partial record CustomBlockMesh(string Item, string Source, int Variant, int Mobil = 0)
{
    public string Archetype { get; init; } = Source;
    // ice raised over the same native block, which stays placed under it
    public bool Stacked { get; init; }

    private const string Prefix = "IceConverter\\";
    private const string Suffix = ".Block.Gbx_CustomBlock";

    public static CustomBlockMesh? Parse(string blockName)
    {
        if (!blockName.StartsWith(Prefix) || !blockName.EndsWith(Suffix)) return null;
        var item = BlockExtras().Replace(blockName[Prefix.Length..^Suffix.Length], "");
        if (item.IndexOf("_Ice") <= 0) return null;
        var source = ItemRecipe.SourceBlock(item);
        var variant = VariantMark().Match(item);
        var on = OnArchetype().Match(blockName);
        var mesh = variant.Success
            ? new CustomBlockMesh(item, source, int.Parse(variant.Groups[1].Value), int.Parse(variant.Groups[2].Value))
            : new CustomBlockMesh(item, source, 0);
        return mesh with { Archetype = on.Success ? on.Groups[1].Value : mesh.Source, Stacked = blockName.Contains("_lift1") };
    }

    // see ItemBuilder.BlockFor and SnowBlocks
    [GeneratedRegex(@"(_on_[^_]+)?(_lift1)?(_r\d+(l\d+)?)?(_snowland|_snowhollow|_decosnow2)?$")]
    private static partial Regex BlockExtras();

    [GeneratedRegex(@"_on_([^_.]+)")]
    private static partial Regex OnArchetype();

    [GeneratedRegex(@"_v(\d+)m(\d+)_")]
    private static partial Regex VariantMark();
}
