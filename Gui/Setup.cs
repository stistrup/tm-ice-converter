using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TmIceConverter;

// the GUI's first-run checklist: folders, tools, Openplanet, the plugin and the one-time extract
internal sealed partial class Setup(string dataDir)
{
    // state: ok, todo, wait (an earlier step first), running
    public sealed record Step(string Id, string Title, string State, string Text, string? Path = null, List<string>? Found = null,
        string? Action = null, string? Link = null, double? Progress = null, bool Required = true);

    public sealed record Status(List<Step> Steps, bool Ready, bool GameRunning);

    private string PluginSource => System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, "..", "openplanet", "IceConverterExtract"));

    public Status Check()
    {
        var running = GameRunning();
        var steps = new List<Step>();
        var game = Folder("game", "Game folder", Games(), dir => File.Exists(System.IO.Path.Combine(dir, "Trackmania.exe")),
            "Where Trackmania.exe is", "No Trackmania.exe here");
        var user = Folder("trackmania", "Trackmania user folder", UserFolders(), dir => Directory.Exists(System.IO.Path.Combine(dir, "Maps")),
            "Documents/Trackmania, with your Maps", "No Maps folder here. Start the game once");
        steps.Add(game);
        steps.Add(user);
        steps.Add(Openplanet(game.State == "ok"));
        steps.Add(Importer(game.State == "ok"));
        steps.Add(BlenderStep());
        if (!OperatingSystem.IsWindows()) steps.Add(Wine());
        var openplanet = steps.First(s => s.Id == "openplanet").State == "ok";
        steps.Add(DevMode(openplanet, running));
        var plugin = Plugin(openplanet && steps.First(s => s.Id == "devmode").State == "ok", running);
        steps.Add(plugin);
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
                if (GameRunning()) throw new InvalidOperationException("Close the game first");
                SetDevMode();
                break;
            case "plugin":
                var target = Paths.ExtractPlugin;
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(PluginSource)) File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), true);
                break;
            case "extract":
                if (!GameRunning()) throw new InvalidOperationException("Start the game first");
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
        var folder = Folder("openplanet", title, OpenplanetFolders(), dir => File.Exists(System.IO.Path.Combine(dir, "Settings.ini")),
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
        var found = BlenderCandidates().ToList();
        if (set is null && OnPath("blender") is null && found.Count > 0) Paths.SavePath("blender", set = found[0]);
        var path = set ?? OnPath("blender");
        return path is not null && File.Exists(path)
            ? new Step("blender", "Blender", "ok", "Builds the ice pieces' models", path, found)
            : new Step("blender", "Blender", "todo", set is null ? "Not found: install Blender" : "No Blender at this path", set, found,
                Link: "https://www.blender.org/download/");
    }

    private static Step Wine() => OnPath("wine") is { } wine
        ? new Step("wine", "Wine", "ok", "Runs NadeoImporter on Linux", wine)
        : new Step("wine", "Wine", "todo", "Install wine from your distribution's packages");

    private static string? DevModeLine()
    {
        var ini = System.IO.Path.Combine(Paths.Openplanet, "Settings.ini");
        return File.Exists(ini) ? File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("DeveloperMode=")) : null;
    }

    private static Step DevMode(bool openplanetOk, bool running)
    {
        const string title = "Openplanet developer mode";
        if (!openplanetOk) return new Step("devmode", title, "wait", "Needs Openplanet");
        if (DevModeLine() == "DeveloperMode=true") return new Step("devmode", title, "ok", "Lets the extract plugin run");
        return new Step("devmode", title, "todo", running ? "Close the game, then turn it on" : "Lets the extract plugin run", Action: running ? null : "Turn on");
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

    private Step Plugin(bool ready, bool running)
    {
        const string title = "Extract plugin";
        if (!ready) return new Step("plugin", title, "wait", "Needs Openplanet in developer mode");
        var source = Directory.GetFiles(PluginSource);
        var missing = source.Count(f => System.IO.Path.Combine(Paths.ExtractPlugin, System.IO.Path.GetFileName(f)) is var t
            && !(File.Exists(t) && File.ReadAllBytes(t).AsSpan().SequenceEqual(File.ReadAllBytes(f))));
        var fresh = !Directory.Exists(Paths.ExtractPlugin);
        if (missing > 0) return new Step("plugin", title, "todo", fresh ? "Not installed" : "Out of date", Action: fresh ? "Install" : "Update");
        // Openplanet loads plugins at game start: one installed or updated later isn't running yet
        if (running && !PluginAlive()) return new Step("plugin", title, "todo", "Installed. Restart the game, or load/reload it in Openplanet's Developer menu");
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
        if (state == "done" && extracted) return new Step("extract", title, "ok", "Extracted. (On game update, you might need to do this again)", Action: "Extract again");
        var why = state switch
        {
            "running" => "Stopped when the game closed. Extract again to go on",
            "stopped" => "Stopped. Extract again to go on",
            _ => "Copies the game's blocks and models to build ice from. Only needed once. The extracted files take 6-7 GB",
        };
        return new Step("extract", title, "todo", why, Action: "Extract");
    }

    private static Dictionary<string, string> ReadProgress(string file) => File.Exists(file)
        ? File.ReadLines(file).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim())
        : [];

    private static double Number(Dictionary<string, string> values, string key)
        => double.TryParse(values.GetValueOrDefault(key), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    // under Proton the process name is cut short; its command line has the exe
    private static bool GameRunning() => OperatingSystem.IsWindows()
        ? Process.GetProcessesByName("Trackmania").Length > 0
        : Directory.EnumerateDirectories("/proc").Any(dir =>
        {
            try
            {
                return File.ReadAllText(System.IO.Path.Combine(dir, "cmdline")).Split('\0')
                    .Any(arg => arg.EndsWith("Trackmania.exe", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        });

    private static string? OnPath(string name)
    {
        var names = OperatingSystem.IsWindows() ? [name + ".exe"] : new[] { name };
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(n => System.IO.Path.Combine(dir, n))).FirstOrDefault(File.Exists);
    }

    // ---- where to look

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> SteamLibraries()
    {
        string[] roots = OperatingSystem.IsWindows()
            ? [System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")]
            : [System.IO.Path.Combine(Home, ".local/share/Steam"), System.IO.Path.Combine(Home, ".steam/steam"),
                System.IO.Path.Combine(Home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"), System.IO.Path.Combine(Home, "snap/steam/common/.local/share/Steam")];
        var libraries = new List<string>();
        foreach (var root in roots)
        {
            var vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            libraries.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        return libraries.Distinct();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();

    // Proton's Windows user inside the game's prefix
    private static IEnumerable<string> ProtonUsers()
        => SteamLibraries().Select(lib => System.IO.Path.Combine(lib, "steamapps", "compatdata", "2225070", "pfx", "drive_c", "users", "steamuser"));

    private static IEnumerable<string> Games()
    {
        foreach (var lib in SteamLibraries()) yield return System.IO.Path.Combine(lib, "steamapps", "common", "Trackmania");
        if (!OperatingSystem.IsWindows()) yield break;
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return System.IO.Path.Combine(x86, "Ubisoft", "Ubisoft Game Launcher", "games", "Trackmania");
        yield return System.IO.Path.Combine(programs, "Epic Games", "TrackmaniaNext");
        yield return System.IO.Path.Combine(programs, "Epic Games", "Trackmania");
    }

    private static IEnumerable<string> UserFolders()
    {
        if (!OperatingSystem.IsWindows()) return ProtonUsers().Select(u => System.IO.Path.Combine(u, "Documents", "Trackmania"));
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return [System.IO.Path.Combine(documents, "Trackmania"), System.IO.Path.Combine(documents, "Trackmania2020"),
            System.IO.Path.Combine(Home, "OneDrive", "Documents", "Trackmania")];
    }

    private static IEnumerable<string> OpenplanetFolders()
    {
        // next to Documents, in the same Windows user
        if (Paths.PathIfSet("trackmania") is { } user && Directory.GetParent(user)?.Parent is { } profile)
            yield return System.IO.Path.Combine(profile.FullName, "OpenplanetNext");
        if (OperatingSystem.IsWindows()) yield return System.IO.Path.Combine(Home, "OpenplanetNext");
        else foreach (var u in ProtonUsers()) yield return System.IO.Path.Combine(u, "OpenplanetNext");
    }

    private static IEnumerable<string> BlenderCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var foundation = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            return Directory.Exists(foundation)
                ? Directory.GetDirectories(foundation).OrderDescending().Select(d => System.IO.Path.Combine(d, "blender.exe")).Where(File.Exists)
                : [];
        }
        return new[] { "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender", "/var/lib/flatpak/exports/bin/org.blender.Blender" }.Where(File.Exists);
    }
}
