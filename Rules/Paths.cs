namespace TmIceConverter;

// from local.jsonc in the user folder
public static class Paths
{
    public static string TrackmaniaUser => Local.Folder("trackmania");
    public static string Maps => Path.Combine(TrackmaniaUser, "Maps");
    public static string Game => Local.Folder("game");
    public static string Openplanet => Local.Folder("openplanet");

    public static string ExtractedStadium => Path.Combine(Openplanet, "Extract", "GameData", "Stadium");
    public static IEnumerable<string> BlockInfoDirs(string extractedStadium)
        => Directory.EnumerateDirectories(Path.GetDirectoryName(extractedStadium)!)
            .Select(d => Path.Combine(d, "GameCtnBlockInfo")).Where(Directory.Exists);

    // null for Stadium
    public static string? EnvironmentBlockInfos(string extractedStadium, string? collection)
        => collection is null or "Stadium2020" ? null
            : Path.Combine(Path.GetDirectoryName(extractedStadium)!, collection, "GameCtnBlockInfo") is var dir && Directory.Exists(dir) ? dir : null;

    public static string ExtractPlugin => Path.Combine(Openplanet, "Plugins", "IceConverterExtract");

    public static string DropFolder => Path.Combine(Maps, "To Ice");
    public static string OutFolder => Path.Combine(Maps, "ICE in progress");

    // relative to Maps with "/" when under it, else the full path
    public static string MapsRelativeOrFull(string path)
        => Path.GetRelativePath(Maps, Path.GetFullPath(path)) is var relative && !relative.StartsWith("..") && !Path.IsPathRooted(relative)
            ? relative.Replace('\\', '/') : Path.GetFullPath(path);

    // everything of the user's (config, saved settings, cache): out of the app folder, so an update replaces the app and keeps these
    public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TmIceConverter");

    public static string LocalSettings { get; set; } = Path.Combine(UserData, "local.jsonc");

    // what a new local.jsonc starts from (in the app's data/)
    public static string LocalExample { get; set; } = "local.example.jsonc";
    // the shipped data folder (set at start)
    public static string Data { get; set; } = "data";

    // conversion runs and each map's settings (RunLog); "settings" in local.jsonc, else in the user folder
    public static string SettingsStore => Local.Paths?.GetValueOrDefault("settings") is { Length: > 0 }
        ? Local.Folder("settings") : Path.Combine(UserData, "settings");

    public static (string Login, string Nickname, string Zone) TestrunAuthor => TestrunAuthorIfSet
        ?? throw new InvalidOperationException($"no \"testrunAuthor\" in {LocalSettings}");

    public static (string Login, string Nickname, string Zone)? TestrunAuthorIfSet => savedAuthor
        ?? (Local.TestrunAuthor is { Login.Length: > 0 } a ? (a.Login, a.Nickname, a.Zone) : null);

    private static (string, string, string)? savedAuthor;

    // into local.jsonc as text: its comments stay
    public static void SaveTestrunAuthor(string login, string nickname, string zone)
    {
        var entry = "\"testrunAuthor\": " + System.Text.Json.JsonSerializer.Serialize(new { login, nickname, zone });
        var text = File.ReadAllText(LocalSettings);
        var existing = System.Text.RegularExpressions.Regex.Match(text, "\"testrunAuthor\"\\s*:\\s*\\{[^}]*\\}");
        if (existing.Success) text = text[..existing.Index] + entry + text[(existing.Index + existing.Length)..];
        else
        {
            // the comma goes after the last value, not into a comment line after it
            var end = text.LastIndexOf('}');
            var lines = text[..end].TrimEnd().Split('\n').ToList();
            var last = lines.FindLastIndex(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("//"));
            if (last >= 0 && lines[last].TrimEnd() is var value && !value.EndsWith(',') && !value.EndsWith('{')) lines[last] = value + ",";
            text = string.Join('\n', lines) + "\n  " + entry + "\n" + text[end..];
        }
        File.WriteAllText(LocalSettings, text);
        savedAuthor = (login, nickname, zone);
    }

    private static LocalFile? local;
    private static LocalFile Local => local ??= File.Exists(LocalSettings)
        ? Jsonc.Load<LocalFile>(LocalSettings)
        : throw new InvalidOperationException($"no {LocalSettings}: set up the folders in the GUI, or copy data/local.example.jsonc");

    public static bool Configured => File.Exists(LocalSettings);
    public static string? PathIfSet(string name) => File.Exists(LocalSettings) && Local.Paths?.GetValueOrDefault(name) is { Length: > 0 } ? Local.Folder(name) : null;

    // into local.jsonc as text (from the example when there is none yet): its comments stay
    public static void SavePath(string name, string value)
    {
        var text = File.Exists(LocalSettings) ? File.ReadAllText(LocalSettings)
            : File.ReadAllText(LocalExample);
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var existing = System.Text.RegularExpressions.Regex.Match(text, $"\"{System.Text.RegularExpressions.Regex.Escape(name)}\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"");
        if (existing.Success) text = text[..existing.Index] + $"\"{name}\": {json}" + text[(existing.Index + existing.Length)..];
        else
        {
            var paths = System.Text.RegularExpressions.Regex.Match(text, "\"paths\"\\s*:\\s*\\{");
            if (!paths.Success) throw new InvalidOperationException($"no \"paths\" in {LocalSettings}");
            text = text[..(paths.Index + paths.Length)] + $"\n    \"{name}\": {json}," + text[(paths.Index + paths.Length)..];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(LocalSettings)!);
        File.WriteAllText(LocalSettings, text);
        local = null;
    }

    private sealed record LocalFile(Dictionary<string, string>? Paths, Author? TestrunAuthor)
    {
        public string Folder(string name) => Paths?.GetValueOrDefault(name) is { Length: > 0 } dir
            ? dir.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + dir[1..] : dir
            : throw new InvalidOperationException($"no \"paths\": {{ \"{name}\": … }} in {LocalSettings}");
    }

    private sealed record Author(string Login, string Nickname, string Zone);

    // our folder under Items and Blocks; item idents start with it
    public const string ItemFolder = "IceConverter";

    public static string Items => Path.Combine(TrackmaniaUser, "Items", ItemFolder);
    // the importer's input only; it reads nothing outside Work
    public static string Work => Path.Combine(TrackmaniaUser, "Work", "Items", ItemFolder);
    // our own files, outside the game's folders
    public static string Cache => Local.Paths?.GetValueOrDefault("cache") is { Length: > 0 }
        ? Local.Folder("cache")
        : UserData;
    public static string Pieces => Path.Combine(Cache, "Mesh");
    public static string Blocks => Path.Combine(Cache, "Blocks");
    public static string Views => Path.Combine(Cache, "Views");

    // "blender" in local.jsonc, else the one on PATH
    public static string Blender => PathIfSet("blender") ?? "blender";

    // only needed on Linux
    public static string ImporterWinePrefix => Local.Folder("importerWinePrefix");
}
