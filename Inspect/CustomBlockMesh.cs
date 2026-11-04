using System.Text.RegularExpressions;

namespace TmIceConverter;

// a placed custom block's name -> the item it was built from
internal sealed partial record CustomBlockMesh(string Item, string Source, int Variant)
{
    private const string Prefix = "IceConverter\\";
    private const string Suffix = ".Block.Gbx_CustomBlock";

    public static CustomBlockMesh? Parse(string blockName)
    {
        if (!blockName.StartsWith(Prefix) || !blockName.EndsWith(Suffix)) return null;
        var item = BlockExtras().Replace(blockName[Prefix.Length..^Suffix.Length], "");
        var ice = item.IndexOf("_Ice");
        if (ice <= 0) return null;
        var variant = VariantMark().Match(item);
        return new CustomBlockMesh(item, item[..ice], variant.Success ? int.Parse(variant.Groups[1].Value) : 0);
    }

    // see ItemBuilder.BlockFor and SnowBlocks
    [GeneratedRegex(@"(_on_[^_]+)?(_lift1)?(_r\d+(l\d+)?)?(_snowland|_snowhollow|_decosnow2)?$")]
    private static partial Regex BlockExtras();

    [GeneratedRegex(@"_v(\d+)m\d+_")]
    private static partial Regex VariantMark();
}
