using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

// scan, compress, learn-clip-turns
internal static class ToolCommands
{
    public static int Scan(string[] folders, string knownPath)
    {
        var names = File.Exists(knownPath) ? File.ReadAllLines(knownPath).ToHashSet() : [];
        var before = names.Count;
        foreach (var path in ExpandMaps(folders))
        {
            try { names.UnionWith(Gbx.ParseNode<CGameCtnChallenge>(path).GetBlocks().Select(b => b.Name)); }
            catch (Exception e) { Console.Error.WriteLine($"skip {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }
        File.WriteAllLines(knownPath, names.Order(StringComparer.Ordinal));
        Console.WriteLine($"{names.Count} known block names ({names.Count - before} new)");
        return 0;
    }

    public static int Compress(string[] rest)
    {
        foreach (var path in rest.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.Map.Gbx") : [p]))
        {
            var before = new FileInfo(path).Length;
            var gbx = Gbx.Parse<CGameCtnChallenge>(path);
            if (gbx.Node.EmbeddedZipData is null) continue;
            var changed = 0;
            gbx.Node.UpdateEmbeddedZipData(zip =>
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".Block.Gbx", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var data = new MemoryStream();
                    using (var from = entry.Open()) from.CopyTo(data);
                    data.Position = 0;
                    var block = Gbx.Parse<GBX.NET.Engines.GameData.CGameItemModel>(data);
                    if (block.BodyCompression == GbxCompression.Compressed) continue;
                    block.BodyCompression = GbxCompression.Compressed;
                    var name = entry.FullName;
                    entry.Delete();
                    using var to = zip.CreateEntry(name).Open();
                    block.Save(to);
                    changed++;
                }
            });
            if (changed == 0) { Console.WriteLine($"{Path.GetFileName(path)}: nothing to compress"); continue; }
            gbx.Save(path);
            Console.WriteLine($"{Path.GetFileName(path)}: {changed} blocks, {before / 1048576.0:F1} -> {new FileInfo(path).Length / 1048576.0:F1} MB");
        }
        return 0;
    }

    public static int LearnClipTurns(string[] rest)
    {
        var shapes = new MeshExport(Paths.ExtractedStadium, new Dictionary<string, string>());
        var seen = ClipTurns.Learn(ExpandMaps(rest).Select(p => Gbx.ParseNode<CGameCtnChallenge>(p)), shapes);
        ClipTurns.Write(seen);
        var mixed = seen.Count(kv => kv.Value.Count > 1);
        Console.WriteLine($"{seen.Count} block/clip turns -> {ClipTurns.FilePath}{(mixed > 0 ? $"; {mixed} not unanimous (see the comments)" : "")}");
        return 0;
    }
}
