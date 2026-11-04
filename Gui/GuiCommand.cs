using System.Diagnostics;
using static TmIceConverter.CliArgs;

namespace TmIceConverter;

internal sealed class GuiCommand(string dataDir)
{
    public int Run(string[] rest)
    {
        var noOpen = rest.Contains("--no-open");
        rest = rest.Where(a => a != "--no-open").ToArray();
        var port = int.TryParse(Option(ref rest, "--port"), out var p) ? p : 5199;
        var webRoot = Path.GetFullPath(Path.Combine(dataDir, "..", "web", "dist"));
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            Console.WriteLine($"No built page in {webRoot}: run npm install and npm run build in web/ (the API still runs).");

        var url = $"http://localhost:{port}/";
        var server = new GuiServer(url, webRoot);
        Console.WriteLine($"GUI on {url} (Ctrl+C to stop)");
        if (!noOpen) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        server.RunAsync().GetAwaiter().GetResult();
        return 0;
    }
}
