using GBX.NET;

namespace TmIceConverter;

public sealed record IceItem(Ident Ident, string FilePath, (int X, int Z) Size)
{
    public static readonly IceItem Nothing = new(new Ident("", new Id(26), ""), "", (0, 0));
}
