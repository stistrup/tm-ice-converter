using GBX.NET;

namespace TmSurfaceEditor;

public sealed record BuiltItem(Ident Ident, string FilePath, (int X, int Z) Size)
{
    public static readonly BuiltItem Nothing = new(new Ident("", new Id(26), ""), "", (0, 0));
}
