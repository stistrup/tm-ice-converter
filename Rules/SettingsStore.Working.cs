using System.Text.Json.Nodes;

namespace TmSurfaceEditor;

// Past, Future: the edits to step through
public sealed record WorkingCopy(int Version, string Uid, DateTime Updated, JsonObject Settings, List<JsonObject> Past, List<JsonObject> Future);

public sealed record DocumentState(JsonObject Settings, bool Dirty, bool CanUndo, bool CanRedo);

public sealed partial class SettingsStore
{
    private const int HistoryLength = 200;

    private string WorkingFile(string uid) => Path.Combine(Dir("working"), SafeName(uid) + ".json");

    public WorkingCopy? WorkingOf(string uid)
        => WorkingFile(uid) is var file && File.Exists(file) && ReadOne<WorkingCopy>(file) is { Settings: not null } w
            ? w with
            {
                Settings = MapSettings.Upgrade(w.Settings, w.Version),
                Past = (w.Past ?? []).Select(s => MapSettings.Upgrade(s, w.Version)).ToList(),
                Future = (w.Future ?? []).Select(s => MapSettings.Upgrade(s, w.Version)).ToList(),
            }
            : null;

    private JsonObject SavedOrEmpty(string uid) => SavedOf(uid)?.Settings.DeepClone().AsObject() ?? [];

    public bool Dirty(string uid) => WorkingOf(uid) is { } w && !Same(w.Settings, SavedOrEmpty(uid));

    public DocumentState Document(string uid)
    {
        var w = WorkingOf(uid);
        var settings = w?.Settings ?? SavedOrEmpty(uid);
        return new(settings, w is not null && !Same(settings, SavedOrEmpty(uid)), w?.Past.Count > 0, w?.Future.Count > 0);
    }

    // one undo step
    public DocumentState Edit(string uid, JsonObject settings)
    {
        var w = WorkingOf(uid) ?? NewWorking(uid);
        settings = settings.DeepClone().AsObject();
        if (Same(w.Settings, settings)) return Document(uid);
        var past = w.Past.Append(w.Settings).TakeLast(HistoryLength).ToList();
        WriteWorking(w with { Settings = settings, Past = past, Future = [] });
        return Document(uid);
    }

    public DocumentState Undo(string uid)
    {
        if (WorkingOf(uid) is { Past.Count: > 0 } w)
            WriteWorking(w with { Settings = w.Past[^1], Past = w.Past[..^1], Future = [.. w.Future, w.Settings] });
        return Document(uid);
    }

    public DocumentState Redo(string uid)
    {
        if (WorkingOf(uid) is { Future.Count: > 0 } w)
            WriteWorking(w with { Settings = w.Future[^1], Future = w.Future[..^1], Past = [.. w.Past, w.Settings] });
        return Document(uid);
    }

    // nothing of its own drops the saved file
    public DocumentState SaveWorking(string uid)
    {
        if (WorkingOf(uid) is { } w)
        {
            if (w.Settings.Count == 0) DropSaved(uid);
            else Save(uid, new MapSettings(w.Settings));
        }
        return Document(uid);
    }

    // a step of its own: undo brings the edits back
    public DocumentState Revert(string uid) => Edit(uid, SavedOrEmpty(uid));

    private WorkingCopy NewWorking(string uid) => new(Version, uid, DateTime.UtcNow, SavedOrEmpty(uid), [], []);

    private void WriteWorking(WorkingCopy w) => Write(WorkingFile(w.Uid), w with { Version = Version, Updated = DateTime.UtcNow });

    private static bool Same(JsonNode a, JsonNode b) => JsonNode.DeepEquals(a, b);
}
