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
    public static string FinishedFolder => Path.Combine(Maps, Local.Value.Paths?.GetValueOrDefault("finishedFolder") is { Length: > 0 } f ? f : "ICE finished");

    public static string TestrunFolder => Path.Combine(Maps, "Ice testrun");
    // bump for fresh test run UIDs
    public const int TestrunGeneration = 7;

    public static string LocalSettings { get; set; } = "local.jsonc";

    public static (string Login, string Nickname, string Zone) TestrunAuthor => testrunAuthor.Value;

    private static readonly Lazy<(string, string, string)> testrunAuthor = new(() =>
        Local.Value.TestrunAuthor is { } a ? (a.Login, a.Nickname, a.Zone)
            : throw new InvalidOperationException($"--testrun needs \"testrunAuthor\" in {LocalSettings}"));

    private static readonly Lazy<LocalFile> Local = new(() => File.Exists(LocalSettings)
        ? ItemBuilder.LoadJsonc<LocalFile>(LocalSettings)
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
