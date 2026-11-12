using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TmSurfaceEditor;

// editor tile: the native piece's thumbnail with an ice cube
// parked, not built: needs a cube image of our own at data/ice-cube.png, ImageSharp, Paths.DataDir and a call from MapOutput.Embed.
// a save in the game drops the icons
public static class Thumbnails
{
    private const int Pixels = 64;
    // the cube's fringe would haze over the tile
    private const byte MinCubeAlpha = 16;
    // bits 3-7 of the flags: 1 no icon, 2 is 64, 3 is 128
    private const CGameCtnCollector.ECollectorFlags IconFlags = (CGameCtnCollector.ECollectorFlags)16;

    private static readonly Dictionary<string, byte[]?> made = [];
    private static readonly Lazy<ILookup<string, string>> natives = new(FindNatives);

    // leaves the file as it was when there's no native thumbnail
    public static void Add(string path)
    {
        var name = Path.GetFileName(path).Split('.')[0];
        try
        {
            lock (made)
            {
                if (For(ItemRecipe.SourceBlock(name, Theme.Default)) is not { } icon) return;
                if (Gbx.ParseHeaderNode<CGameItemModel>(path) is { Flags: IconFlags, IconWebP: { } has } && has.SequenceEqual(icon)) return;
                var gbx = Gbx.Parse<CGameItemModel>(path);
                if (!gbx.Node.ContainsChunk<CGameCtnCollector.HeaderChunk2E001004>())
                    gbx.Node.CreateChunk<CGameCtnCollector.HeaderChunk2E001004>().IsHeavy = true;
                gbx.Node.IconWebP = icon;
                // only its size is written, next to the WebP
                gbx.Node.Icon = new GBX.NET.Color[Pixels, Pixels];
                gbx.Node.Flags = IconFlags;
                gbx.Save(path);
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"  no thumbnail on {name}: {e.Message}"); }
    }

    private static byte[]? For(string source)
    {
        if (made.TryGetValue(source, out var known)) return known;
        var native = natives.Value[source].Select(f => ((CGameCtnCollector)Gbx.ParseHeader(f).Node!).IconWebP).FirstOrDefault(i => i is not null);
        return made[source] = native is null ? null : Compose(native);
    }

    // icons are stored upside down: the cube goes bottom right
    public static byte[] Compose(byte[] nativeWebp)
    {
        using var image = Image.Load<Rgba32>(nativeWebp);
        using var cube = Image.Load<Rgba32>(Path.Combine(Paths.DataDir, "ice-cube.png"));
        cube.Mutate(x => x.Resize(Pixels * 5 / 8, 0, KnownResamplers.Lanczos3).Flip(FlipMode.Vertical));
        var (left, top) = (image.Width - cube.Width, image.Height - cube.Height - 2);
        for (int y = 0; y < cube.Height; y++)
            for (int x = 0; x < cube.Width; x++)
                if (cube[x, y] is { A: >= MinCubeAlpha } c)
                    image[left + x, top + y] = Over(c, image[left + x, top + y]);
        // lossless like the native icons: the game draws lossy ones with an opaque square
        // preserve: else the encoder puts colour under see-through pixels, and the game shows it
        using var to = new MemoryStream();
        image.Save(to, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Method = WebpEncodingMethod.BestQuality, TransparentColorMode = WebpTransparentColorMode.Preserve });
        return to.ToArray();
    }

    // native pixels are premultiplied, and black where see-through
    private static Rgba32 Over(Rgba32 top, Rgba32 under)
    {
        var a = top.A / 255f;
        byte Mix(byte t, byte u) => (byte)Math.Clamp(t * a + u * (1 - a) + 0.5f, 0, 255);
        return new Rgba32(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B), (byte)Math.Clamp(top.A + under.A * (1 - a) + 0.5f, 0, 255));
    }

    // Stadium's version first; a block's file is its EDClassic
    private static ILookup<string, string> FindNatives()
    {
        var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive };
        return Directory.EnumerateFiles(gameData, "*.EDClassic.Gbx", options)
            .Concat(Directory.EnumerateFiles(gameData, "*.Item.Gbx", options))
            .OrderBy(f => f.StartsWith(Paths.ExtractedStadium) ? 0 : 1)
            .ToLookup(f => Path.GetFileName(f).Split('.')[0], StringComparer.OrdinalIgnoreCase);
    }
}
