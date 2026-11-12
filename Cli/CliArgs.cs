namespace TmSurfaceEditor;

internal static class CliArgs
{
    public static int Fail(string error)
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    public static string? Option(ref string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return null;
        var value = args[i + 1];
        args = [.. args[..i], .. args[(i + 2)..]];
        return value;
    }

    public static IEnumerable<string> ExpandMaps(IEnumerable<string> inputs) => inputs.SelectMany(p =>
        Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.Map.Gbx", SearchOption.AllDirectories) : [p]);
}
