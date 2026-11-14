using System.Diagnostics;
using static TmSurfaceEditor.CliArgs;

namespace TmSurfaceEditor;

internal sealed class GuiCommand(string dataDir)
{
    public int Run(string[] rest)
    {
        var noOpen = rest.Contains("--no-open");
        rest = rest.Where(a => a != "--no-open").ToArray();
        var port = int.TryParse(Option(ref rest, "--port"), out var p) ? p : 5199;
        // tools/gui-dev: the page comes from Vite; the built one here would be stale
        var devPage = Option(ref rest, "--dev");
        var webRoot = Path.GetFullPath(Path.Combine(dataDir, "..", "web", "dist"));
        if (devPage is null && !File.Exists(Path.Combine(webRoot, "index.html")))
            Console.WriteLine($"No built page in {webRoot}: run npm install and npm run build in web/ (the API still runs).");

        var url = $"http://localhost:{port}/";
        var preview = new ConversionPreview(dataDir);
        var server = new GuiServer(url, webRoot, preview, new ConvertJobs(new ConvertCommand(dataDir), preview), dataDir, devPage);
        // work in progress lasts while the app runs (tools/gui-dev: since the script started, across API restarts)
        var session = long.TryParse(Environment.GetEnvironmentVariable("SURFACE_EDITOR_SESSION"), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.UtcNow;
        server.Started += () => SettingsStore.Default.DropWorkingBefore(session);
        if (!noOpen) server.Started += () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        try { server.RunAsync().GetAwaiter().GetResult(); }
        // started twice: the first one is serving, show it
        catch (System.Net.HttpListenerException)
        {
            Console.WriteLine($"Already running on {url}");
            if (!noOpen) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        return 0;
    }
}
