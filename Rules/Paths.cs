namespace TmIceConverter;

// from data/local.jsonc
public static class Paths
{
    public static string TrackmaniaUser => Local.Value.Folder("trackmania");
    public static string Maps => Path.Combine(TrackmaniaUser, "Maps");
    public static string Game => Local.Value.Folder("game");
    public static string Openplanet => Local.Value.Folder("openplanet");

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

    public static string LocalSettings { get; set; } = "local.jsonc";

    // conversion runs, drafts and map links (RunLog); "settings" in local.jsonc, else settings/ next to data/
    public static string SettingsStore => Local.Value.Paths?.GetValueOrDefault("settings") is { Length: > 0 }
        ? Local.Value.Folder("settings") : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LocalSettings))!, "..", "settings"));

    public static (string Login, string Nickname, string Zone) TestrunAuthor => TestrunAuthorIfSet
        ?? throw new InvalidOperationException($"no \"testrunAuthor\" in {LocalSettings}");

    public static (string Login, string Nickname, string Zone)? TestrunAuthorIfSet => savedAuthor
        ?? (Local.Value.TestrunAuthor is { Login.Length: > 0 } a ? (a.Login, a.Nickname, a.Zone) : null);

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

    private static readonly Lazy<LocalFile> Local = new(() => File.Exists(LocalSettings)
        ? Jsonc.Load<LocalFile>(LocalSettings)
        : throw new InvalidOperationException($"no {LocalSettings}: copy data/local.example.jsonc and fill in your folders"));

    private sealed record LocalFile(Dictionary<string, string>? Paths, Author? TestrunAuthor)
    {
        public string Folder(string name) => Paths?.GetValueOrDefault(name) is { Length: > 0 } dir
            ? dir.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + dir[1..] : dir
            : throw new InvalidOperationException($"no \"paths\": {{ \"{name}\": … }} in {LocalSettings}");
    }

    private sealed record Author(string Login, string Nickname, string Zone);

    public static string Items => Path.Combine(TrackmaniaUser, "Items", "IceConverter");
    public static string Work => Path.Combine(TrackmaniaUser, "Work", "Items", "IceConverter");
    public static string Views => Path.Combine(Work, "Views");

    // only needed on Linux
    public static string ImporterWinePrefix => Local.Value.Folder("importerWinePrefix");
}
