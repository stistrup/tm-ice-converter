using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TmIceConverter;

public sealed record MapRef(string Uid, string File, string Name, string? Fingerprint);

// one conversion: what went in, what came out, and the settings with every option spelled out
public sealed record RunRecord(int Version, string Id, DateTime Time, string? Commit, string Kind, MapRef Source, MapRef Output, JsonObject Settings, List<string>? Problems);

// the settings being worked on for a map family, before any run
public sealed record Draft(int Version, string Family, DateTime Updated, string? From, JsonObject Settings);

// a map saved under a new UID (the game's shadow save), found by its layout
public sealed record MapLink(int Version, string Uid, string SameAs, string File, DateTime Time);

// every UID a map has had: the original (Root), its conversions and their saves
public sealed record MapFamily(string Root, string RootName, IReadOnlyList<string> Uids, IReadOnlyList<RunRecord> Runs);

// runs/, drafts/, links/ under the settings store; one file each, read leniently
public sealed class RunLog(string root)
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RunLog Default => new(Paths.SettingsStore);

    // files that didn't read, from the last Runs / FamilyOf
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

    // layout: worked out only when the UID is new to the log (a game save), then remembered as a link
    public MapFamily FamilyOf(string uid, Func<(string Fingerprint, string File)?> layout)
    {
        var runs = Runs();
        var links = ReadAll<MapLink>("links", l => l.Uid is { Length: > 0 } && l.SameAs is { Length: > 0 });
        var edges = new Dictionary<string, HashSet<string>>();
        void Edge(string a, string b)
        {
            (edges.TryGetValue(a, out var x) ? x : edges[a] = []).Add(b);
            (edges.TryGetValue(b, out var y) ? y : edges[b] = []).Add(a);
        }
        foreach (var run in runs) Edge(run.Source.Uid, run.Output.Uid);
        foreach (var link in links) Edge(link.Uid, link.SameAs);

        if (!edges.ContainsKey(uid) && layout() is { } seen
            && (runs.LastOrDefault(r => r.Output.Fingerprint == seen.Fingerprint)?.Output ?? runs.LastOrDefault(r => r.Source.Fingerprint == seen.Fingerprint)?.Source) is { } same)
        {
            Write(Path.Combine(Dir("links"), SafeName(uid) + ".json"), new MapLink(Version, uid, same.Uid, seen.File, DateTime.UtcNow));
            Edge(uid, same.Uid);
        }

        var family = new HashSet<string> { uid };
        var queue = new Queue<string>([uid]);
        while (queue.TryDequeue(out var next))
            foreach (var other in edges.GetValueOrDefault(next) ?? [])
                if (family.Add(other)) queue.Enqueue(other);
        var familyRuns = runs.Where(r => family.Contains(r.Source.Uid)).ToList();
        var outputs = familyRuns.Select(r => r.Output.Uid).ToHashSet();
        var first = familyRuns.FirstOrDefault(r => !outputs.Contains(r.Source.Uid))?.Source;
        return new MapFamily(first?.Uid ?? uid, first?.Name ?? "", family.Order(StringComparer.Ordinal).ToList(), familyRuns);
    }

    public Draft? DraftOf(MapFamily family)
        => Path.Combine(Dir("drafts"), SafeName(family.Root) + ".json") is var file && File.Exists(file) && ReadOne<Draft>(file) is { Settings: not null } draft ? draft : null;

    public Draft SaveDraft(MapFamily family, MapSettings settings, string? from)
    {
        var draft = new Draft(Version, family.Root, DateTime.UtcNow, from, settings.Clone().Json);
        Write(Path.Combine(Dir("drafts"), SafeName(family.Root) + ".json"), draft);
        return draft;
    }

    public void DropDraft(MapFamily family) => File.Delete(Path.Combine(Dir("drafts"), SafeName(family.Root) + ".json"));

    // what a conversion goes by unless told otherwise: the family's draft, else the original's maps.jsonc entry
    public (MapSettings Settings, string From) Current(MapFamily family, Translations data, string mapName)
        => DraftOf(family) is { } draft
            ? (new MapSettings(draft.Settings), $"draft of {draft.Updated.ToLocalTime():yyyy-MM-dd HH:mm}")
            : (MapSettings.Seed(data, family.RootName is { Length: > 0 } n ? n : mapName), "maps.jsonc");

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
