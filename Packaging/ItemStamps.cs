using System.Security.Cryptography;
using System.Text;

namespace TmSurfaceEditor;

// next to each item's pieces: what built it, so unchanged items aren't built again
internal sealed class ItemStamps(string dataDir, Theme theme)
{
    // bump to rebuild every item
    private const int PipelineVersion = 15;

    private string? tools;

    public string BuildScript => Path.Combine(dataDir, "..", "tools", "build_fbx.py");

    public static string PiecesPath(string name) => Path.Combine(Paths.Pieces, name + ".pieces.json");

    private static string StampPath(string name) => Path.Combine(Paths.Pieces, name + ".pipeline");

    // rebuild only when the tools and the exported input both changed
    public bool IsCurrent(string name, string itemPath, out string? builtFrom)
    {
        builtFrom = null;
        if (!File.Exists(itemPath)) return false;
        var stampPath = StampPath(name);
        var lines = File.Exists(stampPath) ? File.ReadAllLines(stampPath) : [];
        if (lines is [var tool, var input]) { builtFrom = input; return tool == Tools(); }
        if (lines is not ["incomplete"] && File.Exists(PiecesPath(name))) builtFrom = InputHash(File.ReadAllText(PiecesPath(name)));
        return false;
    }

    public string InputHash(string piecesJson) => Hash(Encoding.UTF8.GetBytes(PipelineVersion + "\n" + File.ReadAllText(BuildScript) + "\n" + piecesJson));

    public void Write(string name, string input) => File.WriteAllLines(StampPath(name), [Tools(), input]);

    // a failure after this must not leave the pieces looking built
    public void WriteIncomplete(string name) => File.WriteAllText(StampPath(name), "incomplete");

    // nothing to cover leaves no item file: remember it, or every run exports it again
    public void WriteEmpty(string name) => File.WriteAllLines(StampPath(name), ["empty", Tools()]);

    public bool IsKnownEmpty(string name)
        => File.Exists(StampPath(name)) && File.ReadAllLines(StampPath(name)) is ["empty", var tool] && tool == Tools();

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    // the code: our dll, or in a single-file release the executable (the dll has no file of its own there)
    private static string CodeFile => typeof(ItemStamps).Assembly.Location is { Length: > 0 } dll ? dll : Environment.ProcessPath!;

    private string Tools() => tools ??= Hash(File.ReadAllBytes(CodeFile)
        .Concat(File.ReadAllBytes(BuildScript)).Concat(theme.Files.SelectMany(File.ReadAllBytes)).ToArray());
}
