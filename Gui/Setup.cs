using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TmIceConverter;

// the GUI's first-run checklist: folders, tools, Openplanet, the plugin and the one-time extract
internal sealed partial class Setup(string dataDir)
{
    // state: ok, todo, wait (an earlier step first), running
    // ActionId: what the button does when not the step's own; ActionEnabled false: Note says what's missing
    public sealed record Step(string Id, string Title, string State, string Text, string? Path = null, List<string>? Found = null,
        string? Action = null, string? Link = null, double? Progress = null, bool Required = true,
        string? ActionId = null, bool ActionEnabled = true, string? Note = null);

    public sealed record Status(List<Step> Steps, bool Ready, bool GameRunning);

    private string PluginSource => System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, "..", "openplanet", "IceConverterExtract"));

    public Status Check()
    {
        var running = InstallSearch.GameRunning();
        var steps = new List<Step>();
        var game = Folder("game", "Game folder", InstallSearch.Games(), dir => File.Exists(System.IO.Path.Combine(dir, "Trackmania.exe")),
            "Where Trackmania.exe is", "No Trackmania.exe here");
        var user = Folder("trackmania", "Trackmania user folder", InstallSearch.UserFolders(), dir => Directory.Exists(System.IO.Path.Combine(dir, "Maps")),
            "Documents/Trackmania, with your Maps", "No Maps folder here. Start the game once");
        steps.Add(game);
        steps.Add(user);
        steps.Add(Openplanet(game.State == "ok"));
        steps.Add(Importer(game.State == "ok"));
        steps.Add(BlenderStep());
        if (!OperatingSystem.IsWindows()) steps.Add(Wine());
        var openplanet = steps.First(s => s.Id == "openplanet").State == "ok";
        var plugin = Plugin(openplanet);
        steps.Add(plugin);
        // developer mode and the running game are only needed while extracting: they gate the button, not setup
        steps.Add(Extract(plugin.State == "ok", running));
        return new Status(steps, steps.All(s => s.State == "ok" || !s.Required), running);
    }

    public void SetPath(string name, string value)
    {
        if (name is not ("game" or "trackmania" or "openplanet" or "blender")) throw new ArgumentException($"no setting {name}");
        Paths.SavePath(name, value.Trim());
    }

    public void Act(string id)
    {
        switch (id)
        {
            case "devmode":
                if (InstallSearch.GameRunning()) throw new InvalidOperationException("Close the game first");
                SetDevMode();
                break;
            case "plugin":
                var target = Paths.ExtractPlugin;
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(PluginSource)) File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), true);
                break;
            case "extract":
                if (!InstallSearch.GameRunning()) throw new InvalidOperationException("Start the game first");
                if (!PluginAlive()) throw new InvalidOperationException("The plugin isn't loaded in the game");
                File.WriteAllText(System.IO.Path.Combine(Paths.ExtractPlugin, "extract-request.txt"), "");
                File.Delete(System.IO.Path.Combine(Paths.ExtractPlugin, "extract-progress.txt"));
                break;
            case "extract-stop":
                File.Delete(System.IO.Path.Combine(Paths.ExtractPlugin, "extract-request.txt"));
                File.WriteAllText(System.IO.Path.Combine(Paths.ExtractPlugin, "extract-stop.txt"), "");
                break;
            default: throw new ArgumentException($"no action {id}");
        }
    }

    // a folder setting: kept when set, else the first one found is saved
    private static Step Folder(string name, string title, IEnumerable<string> candidates, Func<string, bool> valid, string hint, string bad)
    {
        var found = candidates.Where(Directory.Exists).Where(valid).Distinct().ToList();
        var path = Paths.PathIfSet(name);
        if (path is null && found.Count > 0) Paths.SavePath(name, path = found[0]);
        if (path is null) return new Step(name, title, "todo", "Not found: " + hint, null, found);
        return Directory.Exists(path) && valid(path)
            ? new Step(name, title, "ok", hint, path, found)
            : new Step(name, title, "todo", bad, path, found);
    }

    private static Step Openplanet(bool gameOk)
    {
        const string title = "Openplanet";
        if (!gameOk) return new Step("openplanet", title, "wait", "Needs the game folder");
        var installed = File.Exists(System.IO.Path.Combine(Paths.Game, "Openplanet.dll"));
        var folder = Folder("openplanet", title, InstallSearch.OpenplanetFolders(), dir => File.Exists(System.IO.Path.Combine(dir, "Settings.ini")),
            "Its OpenplanetNext folder", "No OpenplanetNext here. Start the game once with Openplanet");
        if (!installed) return folder with { State = "todo", Text = "Install Openplanet, then start the game once", Link = "https://openplanet.dev/download" };
        return folder;
    }

    private static Step Importer(bool gameOk)
    {
        const string title = "NadeoImporter";
        if (!gameOk) return new Step("importer", title, "wait", "Needs the game folder");
        return File.Exists(System.IO.Path.Combine(Paths.Game, "NadeoImporter.exe"))
            ? new Step("importer", title, "ok", "Nadeo's item importer, in the game folder")
            : new Step("importer", title, "todo", "Download it and unzip it into the game folder",
                Link: "https://doc.trackmania.com/create/nadeo-importer/01-download-and-install");
    }

    private static Step BlenderStep()
    {
        var set = Paths.PathIfSet("blender");
        var found = InstallSearch.BlenderCandidates().ToList();
        if (set is null && InstallSearch.OnPath("blender") is null && found.Count > 0) Paths.SavePath("blender", set = found[0]);
        var path = set ?? InstallSearch.OnPath("blender");
        return path is not null && File.Exists(path)
            ? new Step("blender", "Blender", "ok", "Builds the ice pieces' models", path, found)
            : new Step("blender", "Blender", "todo", set is null ? "Not found: install Blender" : "No Blender at this path", set, found,
                Link: "https://www.blender.org/download/");
    }

    private static Step Wine() => InstallSearch.OnPath("wine") is { } wine
        ? new Step("wine", "Wine", "ok", "Runs NadeoImporter on Linux", wine)
        : new Step("wine", "Wine", "todo", "Install wine from your distribution's packages");

    private static string? DevModeLine()
    {
        var ini = System.IO.Path.Combine(Paths.Openplanet, "Settings.ini");
        return File.Exists(ini) ? File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("DeveloperMode=")) : null;
    }

    // Openplanet reads it at start and writes it back on exit
    private static void SetDevMode()
    {
        var ini = System.IO.Path.Combine(Paths.Openplanet, "Settings.ini");
        var lines = File.ReadAllLines(ini).ToList();
        var at = lines.FindIndex(l => l.StartsWith("DeveloperMode="));
        if (at >= 0) lines[at] = "DeveloperMode=true";
        else if (lines.FindIndex(l => l.Trim() == "[App]") is var app and >= 0) lines.Insert(app + 1, "DeveloperMode=true");
        else lines.AddRange(["", "[App]", "DeveloperMode=true"]);
        File.WriteAllLines(ini, lines);
    }

    private Step Plugin(bool ready)
    {
        const string title = "Extract plugin";
        if (!ready) return new Step("plugin", title, "wait", "Needs Openplanet");
        var source = Directory.GetFiles(PluginSource);
        var missing = source.Count(f => System.IO.Path.Combine(Paths.ExtractPlugin, System.IO.Path.GetFileName(f)) is var t
            && !(File.Exists(t) && File.ReadAllBytes(t).AsSpan().SequenceEqual(File.ReadAllBytes(f))));
        var fresh = !Directory.Exists(Paths.ExtractPlugin);
        if (missing > 0) return new Step("plugin", title, "todo", fresh ? "Not installed" : "Out of date", Action: fresh ? "Install" : "Update");
        return new Step("plugin", title, "ok", "Installed in Openplanet's Plugins");
    }

    // the plugin rewrites it every second while loaded
    private static bool PluginAlive()
        => new FileInfo(System.IO.Path.Combine(Paths.ExtractPlugin, "alive.txt")) is { Exists: true } alive
            && DateTime.UtcNow - alive.LastWriteTimeUtc < TimeSpan.FromSeconds(10);

    private static Step Extract(bool ready, bool running)
    {
        const string title = "Game files";
        if (!ready) return new Step("extract", title, "wait", "Needs the plugin");
        var dir = Paths.ExtractPlugin;
        var progress = ReadProgress(System.IO.Path.Combine(dir, "extract-progress.txt"));
        var asked = File.Exists(System.IO.Path.Combine(dir, "extract-request.txt"));
        var extracted = Directory.Exists(System.IO.Path.Combine(Paths.Openplanet, "Extract", "GameData", "Stadium", "GameCtnBlockInfo"));
        var state = progress.GetValueOrDefault("state");
        if (state == "running" && running)
        {
            var total = Number(progress, "total");
            var phase = progress.GetValueOrDefault("phase") ?? "";
            var share = phase == "Extracting files" && total > 0 ? Number(progress, "handled") / total : (double?)null;
            var text = phase + (share is { } s ? $": {Number(progress, "handled"):0} of {total:0}" : "") + $" · {Number(progress, "new"):0} new";
            return new Step("extract", title, "running", text + " · keep the game open", Action: "Stop", Progress: share);
        }
        if (asked) return new Step("extract", title, "running", running ? "Waiting for the plugin" : "Starts when the game runs");
        var done = state == "done" && extracted;
        var said = done ? "Extracted. (On game update, you might need to do this again)" : state switch
        {
            "running" => "Stopped when the game closed. Extract again to go on",
            "stopped" => "Stopped. Extract again to go on",
            _ => "Copies the game's blocks and models to build ice from. Only needed once. The extracted files take 6-7 GB",
        };
        var step = new Step("extract", title, done ? "ok" : "todo", said, Action: done ? "Extract again" : "Extract");
        // what extracting needs, in order: developer mode (Openplanet only reads it at start), the game, the plugin loaded in it
        if (DevModeLine() != "DeveloperMode=true")
            return running ? step with { Action = "Turn on developer mode", ActionEnabled = false, Note = "Close the game to turn on developer mode" }
                : step with { Action = "Turn on developer mode", ActionId = "devmode" };
        if (!running) return step with { ActionEnabled = false, Note = "Start the game to extract" };
        // Openplanet loads plugins at game start: one installed or updated later isn't running yet
        if (!PluginAlive()) return step with { ActionEnabled = false, Note = "Restart the game, or load/reload the plugin in Openplanet's Developer menu" };
        return step;
    }

    private static Dictionary<string, string> ReadProgress(string file) => File.Exists(file)
        ? File.ReadLines(file).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim())
        : [];

    private static double Number(Dictionary<string, string> values, string key)
        => double.TryParse(values.GetValueOrDefault(key), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
}
