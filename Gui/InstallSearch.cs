using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TmSurfaceEditor;

// where the game, its user folder, Openplanet and Blender usually are
internal static partial class InstallSearch
{
    // under Proton the process name is cut short; its command line has the exe
    public static bool GameRunning() => OperatingSystem.IsWindows()
        ? Process.GetProcessesByName("Trackmania").Length > 0
        : Directory.EnumerateDirectories("/proc").Any(dir =>
        {
            try
            {
                return File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')
                    .Any(arg => arg.EndsWith("Trackmania.exe", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        });

    public static string? OnPath(string name)
    {
        var names = OperatingSystem.IsWindows() ? [name + ".exe"] : new[] { name };
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(n => Path.Combine(dir, n))).FirstOrDefault(File.Exists);
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static IEnumerable<string> SteamLibraries()
    {
        string[] roots = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")]
            : [Path.Combine(Home, ".local/share/Steam"), Path.Combine(Home, ".steam/steam"),
                Path.Combine(Home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"), Path.Combine(Home, "snap/steam/common/.local/share/Steam")];
        var libraries = new List<string>();
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            libraries.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        return libraries.Distinct();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();

    // Proton's Windows user inside the game's prefix
    public static IEnumerable<string> ProtonUsers()
        => SteamLibraries().Select(lib => Path.Combine(lib, "steamapps", "compatdata", "2225070", "pfx", "drive_c", "users", "steamuser"));

    public static IEnumerable<string> Games()
    {
        foreach (var lib in SteamLibraries()) yield return Path.Combine(lib, "steamapps", "common", "Trackmania");
        if (!OperatingSystem.IsWindows()) yield break;
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(x86, "Ubisoft", "Ubisoft Game Launcher", "games", "Trackmania");
        yield return Path.Combine(programs, "Epic Games", "TrackmaniaNext");
        yield return Path.Combine(programs, "Epic Games", "Trackmania");
    }

    public static IEnumerable<string> UserFolders()
    {
        if (!OperatingSystem.IsWindows()) return ProtonUsers().Select(u => Path.Combine(u, "Documents", "Trackmania"));
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return [Path.Combine(documents, "Trackmania"), Path.Combine(documents, "Trackmania2020"),
            Path.Combine(Home, "OneDrive", "Documents", "Trackmania")];
    }

    public static IEnumerable<string> OpenplanetFolders()
    {
        // next to Documents, in the same Windows user
        if (Paths.PathIfSet("trackmania") is { } user && Directory.GetParent(user)?.Parent is { } profile)
            yield return Path.Combine(profile.FullName, "OpenplanetNext");
        if (OperatingSystem.IsWindows()) yield return Path.Combine(Home, "OpenplanetNext");
        else foreach (var u in ProtonUsers()) yield return Path.Combine(u, "OpenplanetNext");
    }

    public static IEnumerable<string> BlenderCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var foundation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            return Directory.Exists(foundation)
                ? Directory.GetDirectories(foundation).OrderDescending().Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists)
                : [];
        }
        return new[] { "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender", "/var/lib/flatpak/exports/bin/org.blender.Blender" }.Where(File.Exists);
    }
}
