using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TmIceConverter;

public sealed record MapRef(string Uid, string File, string Name, string? Fingerprint);

// one conversion: what went in, what came out, and the settings with every option spelled out
public sealed record RunRecord(int Version, string Id, DateTime Time, string? Commit, string Kind, MapRef Source, MapRef Output, JsonObject Settings, List<string>? Problems);

// a map's settings as last edited, by its UID
public sealed record SavedSettings(int Version, string Uid, DateTime Updated, JsonObject Settings);

// runs/ and maps/ under the settings store; one file each, read leniently
public sealed class RunLog(string root)
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RunLog Default => new(Paths.SettingsStore);

    // files that didn't read, from the last Runs
    public List<string> Problems { get; } = [];

    public RunRecord Add(string kind, MapRef source, MapRef output, MapSettings settings, List<string> problems)
    {
        var time = DateTime.UtcNow;
        var run = new RunRecord(Version, $"{time.ToLocalTime():yyyyMMdd-HHmmss}-{SafeName(output.Uid)[..Math.Min(6, output.Uid.Length)]}", time, Commit.Value, kind,
            source, output, settings.Clone().Json, problems.Count > 0 ? problems : null);
        Write(Path.Combine(Dir("runs"), run.Id + ".json"), run);
        return run;
    }

    public List<RunRecord> Runs() => ReadAll<RunRecord>("runs", r => r.Source is { Uid.Length: > 0 } && r.Output is { Uid.Length: > 0 } && r.Settings is not null)
        .OrderBy(r => r.Time).ToList();

    // a run by its id or its file
    public RunRecord? Run(string idOrFile)
    {
        var file = File.Exists(idOrFile) ? idOrFile : Path.Combine(Dir("runs"), SafeName(idOrFile) + ".json");
        return File.Exists(file) ? ReadOne<RunRecord>(file) : null;
    }

    public List<RunRecord> RunsOf(string uid) => Runs().Where(r => r.Source.Uid == uid).ToList();

    private string SavedFile(string uid) => Path.Combine(Dir("maps"), SafeName(uid) + ".json");

    public SavedSettings? SavedOf(string uid)
        => SavedFile(uid) is var file && File.Exists(file) && ReadOne<SavedSettings>(file) is { Settings: not null } saved ? saved : null;

    public void Save(string uid, MapSettings settings) => Write(SavedFile(uid), new SavedSettings(Version, uid, DateTime.UtcNow, settings.Clone().Json));

    public void DropSaved(string uid) => File.Delete(SavedFile(uid));

    // what a conversion goes by unless told otherwise: the map's saved settings, else the defaults
    public (MapSettings Settings, string From) Current(string uid)
        => SavedOf(uid) is { } saved
            ? (new MapSettings(saved.Settings), $"saved {saved.Updated.ToLocalTime():yyyy-MM-dd HH:mm}")
            : (MapSettings.Empty, "defaults");

    // the name its settings give it, if any
    public string? NameOf(string uid) => SavedOf(uid) is { } saved ? new MapSettings(saved.Settings).Read([]).Name : null;

    private string Dir(string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private List<T> ReadAll<T>(string dir, Func<T, bool> whole) where T : class
        => Directory.EnumerateFiles(Dir(dir), "*.json").Select(ReadOne<T>).OfType<T>().Where(r =>
        {
            if (whole(r)) return true;
            Problems.Add($"{dir}: a record with fields missing");
            return false;
        }).ToList();

    private T? ReadOne<T>(string file) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json); }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Problems.Add($"{Path.GetFileName(file)}: {e.Message}");
            return null;
        }
    }

    private static void Write<T>(string file, T value)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, file, true);
    }

    private static string SafeName(string uid) => string.Concat(uid.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    // the code a run came from; "+" when tracked files had changes
    private static readonly Lazy<string?> Commit = new(() =>
    {
        try
        {
            var repo = Path.GetDirectoryName(Path.GetFullPath(Paths.LocalSettings))!;
            var head = Git(repo, "rev-parse --short HEAD");
            return head is { Length: > 0 } ? head + (Git(repo, "status --porcelain --untracked-files=no") is { Length: > 0 } ? "+" : "") : null;
        }
        catch (Exception) { return null; }
    });

    private static string? Git(string dir, string args)
    {
        using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{dir}\" {args}") { RedirectStandardOutput = true, RedirectStandardError = true });
        var output = git!.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 ? output : null;
    }
}
