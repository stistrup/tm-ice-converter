using GBX.NET.Engines.Game;

namespace TmIceConverter;

// bits 0-5 mobil row (GBX.NET Variant), 6-11 mobil entry (SubVariant), 21-26 block info variant
internal static class BlockFlags
{
    public const int Ghost = 0x10000000;

    public static int VariantOf(CGameCtnBlock block) => (block.Flags >> 21) & 0x3F;

    public static int MobilOf(CGameCtnBlock block) => block.Flags & 0xFFF;
}
