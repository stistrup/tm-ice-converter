using System.Text.Json;
using System.Text.Json.Nodes;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// maps under Maps: lists, scenes, previews and their settings
internal sealed partial class GuiServer
{
    private readonly Dictionary<string, (DateTime Modified, string Name)> names = [];

    private sealed record MapEntry(string Path, string Name, string Folder, long Size, DateTime Modified);

    private sealed record FolderList(List<string> Folders, string Progress);

    private List<MapEntry> ListMaps()
    {
        var root = Paths.Maps;
        return Directory.EnumerateFiles(root, "*.Map.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(file => new FileInfo(file))
            .Select(info => new MapEntry(
                Path.GetRelativePath(root, info.FullName),
                DisplayName(info),
                Path.GetDirectoryName(Path.GetRelativePath(root, info.FullName)) ?? "",
                info.Length,
                info.LastWriteTimeUtc))
            .OrderBy(m => m.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // as the game lists it
    private string DisplayName(FileInfo file)
    {
        lock (names)
            if (names.TryGetValue(file.FullName, out var known) && known.Modified == file.LastWriteTimeUtc) return known.Name;
        string name;
        try { name = MapNames.Plain(Gbx.ParseHeaderNode<CGameCtnChallenge>(file.FullName).MapName); }
        catch (Exception) { name = file.Name[..^".Map.Gbx".Length]; }
        lock (names) names[file.FullName] = (file.LastWriteTimeUtc, name);
        return name;
    }

    // every folder under Maps, empty ones too, "/" separated; and where a conversion goes by default
    private static FolderList ListFolders()
    {
        static string Rel(string dir) => Path.GetRelativePath(Paths.Maps, dir).Replace('\\', '/');
        var folders = Directory.EnumerateDirectories(Paths.Maps, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Select(Rel).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new FolderList(folders, Rel(Paths.OutFolder));
    }

    private static bool TargetExists(string folder, string name)
    {
        if (MapNames.FileName(name) is not { } file || Inside(Paths.Maps, Path.Combine(folder.Trim(), "x")) is not { } inside) return false;
        var dir = Path.GetDirectoryName(inside)!;
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.Map.Gbx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .Any(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
    }

    // what a conversion names it by default (a name from its settings included)
    private static string OutputName(string map, string suffix = MapNames.DefaultSuffix)
        => Gbx.ParseHeaderNode<CGameCtnChallenge>(map) is var header ? MapNames.DefaultOutput(header.MapName, SettingsStore.Default.NameOf(header.MapUid), suffix) : "";

    private async Task Scene(Call c)
    {
        var map = MapFileOf(c);
        await building.WaitAsync();
        string manifest;
        try { manifest = await Task.Run(() => SceneBundle.Ensure(map, c.Query("rebuild") == "1")).ConfigureAwait(false); }
        finally { building.Release(); }
        await SendFile(c.Response, manifest, "application/json");
    }

    private async Task Geometry(Call c)
    {
        var file = SceneBundle.GeometryOf(MapFileOf(c));
        if (!File.Exists(file)) throw new Refused(404, "scene not built");
        await SendFile(c.Response, file, "application/octet-stream");
    }

    private async Task Texture(Call c)
    {
        var file = Inside(Path.GetDirectoryName(Paths.ExtractedStadium)!, c.Query("path"));
        if (file is null || !File.Exists(file)) throw new Refused(404, "no such texture");
        c.Response.Headers["Cache-Control"] = "max-age=3600";
        await SendFile(c.Response, file, "application/octet-stream");
    }

    private async Task PreviewScene(Call c)
    {
        if (preview.AddedScene(c.Query("id") ?? "") is not { } scene) throw new Refused(404, "no such preview");
        if (c.Query("part") == "geometry") await Bytes(c.Response, 200, "application/octet-stream", scene.Geometry);
        else await Bytes(c.Response, 200, "application/json", scene.Manifest);
    }

    // POST body: { "settings": { …as in settings/maps/<uid>.json… } }
    private async Task Preview(Call c)
    {
        var map = MapFileOf(c);
        var settings = SettingsIn(c.Body);
        try { await Json(c.Response, await Task.Run(() => preview.For(map, settings ?? MapSettings.Parse("{}"))).ConfigureAwait(false)); }
        catch (ConversionPreview.Superseded e) { throw new Refused(409, e.Message); }
    }

    // the map's working copy: POST /api/edit with { "settings": {…} } is one undoable step; undo, redo, save (working → saved), revert (→ saved, a step)
    private async Task Document(Call c, Func<SettingsStore, string, DocumentState> act)
    {
        var map = MapFileOf(c);
        var state = await Task.Run(() =>
        {
            lock (documents) return act(SettingsStore.Default, Gbx.ParseHeaderNode<CGameCtnChallenge>(map).MapUid);
        }).ConfigureAwait(false);
        await Json(c.Response, state);
    }

    private readonly object documents = new();

    private sealed record PresetAsk(string Preset, List<string>? Only);

    // POST { "preset": name, "only": [piece keys] | null }: one step on the working copy; the map converts by picks from then on
    private async Task ApplyPreset(Call c)
    {
        var map = MapFileOf(c);
        var ask = Read<PresetAsk>(c.Body) ?? throw new Refused(400, "no preset named");
        var preset = Preset.Load(dataDir).FirstOrDefault(p => p.Name == ask.Preset) ?? throw new Refused(404, "no such preset");
        var uid = Gbx.ParseHeaderNode<CGameCtnChallenge>(map).MapUid;
        DocumentState working;
        lock (documents) working = SettingsStore.Default.Document(uid);
        var only = ask.Only?.Select(PresetApply.Normal).ToHashSet();
        JsonObject settings;
        try { settings = await PresetRun.Apply(preview, map, new MapSettings(working.Settings), preset, only); }
        catch (ConversionPreview.Superseded e) { throw new Refused(409, e.Message); }
        DocumentState state;
        lock (documents) state = SettingsStore.Default.Edit(uid, settings);
        await Json(c.Response, state);
    }

    private Task Edit(Call c)
    {
        var settings = SettingsIn(c.Body) ?? new MapSettings([]);
        return Document(c, (log, uid) => log.Edit(uid, settings.Json));
    }

    private static MapSettings? SettingsIn(byte[] body)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException e) { throw new Refused(400, e.Message); }
        if (parsed is not JsonObject root) throw new Refused(400, "body must be an object");
        return root["settings"] switch
        {
            JsonObject o => new MapSettings(o.DeepClone().AsObject()),
            null => null,
            _ => throw new Refused(400, "settings must be an object or null"),
        };
    }
}
