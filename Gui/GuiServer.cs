using System.Text.Json.Nodes;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// localhost only; HttpListener rejects other Host headers
internal sealed partial class GuiServer(string url, string webRoot, ConversionPreview preview, ConvertJobs jobs, string dataDir)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
    };

    private readonly SemaphoreSlim building = new(1);
    private readonly LiveLink live = new();
    private readonly Dictionary<string, (DateTime Modified, string Name)> names = [];

    public async Task RunAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var (request, response) = (context.Request, context.Response);
        try
        {
            var path = request.Url!.AbsolutePath;
            if (request.HttpMethod == "POST") await Post(request, response, path);
            else if (request.HttpMethod != "GET") await Text(response, 405, "GET or POST only");
            else if (path == "/api/state") await Bytes(response, 200, "application/json", Encoding.UTF8.GetBytes(live.State));
            else if (path == "/api/events") await live.Events(response);
            else if (path == "/api/shot") await Shot(response);
            else if (path == "/api/pick") await Pick(request, response);
            else if (path == "/api/maps") await Json(response, ListMaps());
            else if (path == "/api/folders") await Json(response, ListFolders());
            else if (path == "/api/author") await Json(response, AuthorNow());
            // a conversion's target: folder + name under Maps (case as the game's file system ignores it)
            else if (path == "/api/convert") await Json(response, jobs.Current);
            else if (path == "/api/output-name")
            {
                if (MapFile(request) is { } source) await Json(response, OutputName(source));
                else await Text(response, 404, "no such map");
            }
            else if (path == "/api/exists") await Json(response, TargetExists(request.QueryString["folder"] ?? "", request.QueryString["name"] ?? ""));
            else if (path == "/api/scene") await Scene(request, response);
            else if (path == "/api/geometry") await Geometry(request, response);
            else if (path == "/api/texture") await Texture(request, response);
            else if (path == "/api/preview-build") await Json(response, preview.Building());
            else if (path == "/api/preview-scene")
            {
                if (preview.AddedScene(request.QueryString["id"] ?? "") is not { } scene) await Text(response, 404, "no such preview");
                else if (request.QueryString["part"] == "geometry") await Bytes(response, 200, "application/octet-stream", scene.Geometry);
                else await Bytes(response, 200, "application/json", scene.Manifest);
            }
            else if (path == "/api/settings")
            {
                if (MapFile(request) is { } map) await Json(response, await Task.Run(() => preview.StateOf(map)).ConfigureAwait(false));
                else await Text(response, 404, "no such map");
            }
            else if (path.StartsWith("/api/")) await Text(response, 404, "no such endpoint");
            else await Static(response, path);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { } // client went away
        catch (Exception e)
        {
            Console.Error.WriteLine($"{request.Url}: {e}");
            try { await Text(response, 500, e.Message); } catch (Exception) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    private async Task Post(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body);
        if (path == "/api/state")
        {
            live.SetState(Encoding.UTF8.GetString(body.ToArray()));
            await Text(response, 200, "ok");
        }
        else if (path is "/api/answer" or "/api/shot")
            await Text(response, live.Answered(request.QueryString["id"], body.ToArray()) ? 200 : 404, "");
        else if (path == "/api/preview") await Preview(request, response, body.ToArray());
        else if (path == "/api/draft") await Draft(request, response, body.ToArray());
        else if (path == "/api/convert") await Convert(request, response, body.ToArray());
        else if (path == "/api/convert-dismiss")
        {
            jobs.Dismiss(request.QueryString["id"] ?? "");
            await Text(response, 200, "ok");
        }
        else if (path == "/api/author")
        {
            AuthorEntry? author;
            try { author = JsonSerializer.Deserialize<AuthorEntry>(body.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException e) { await Text(response, 400, e.Message); return; }
            if (author is not { Login.Length: > 0 }) { await Text(response, 400, "a user ID is needed"); return; }
            var login = author.Login.Trim();
            // nickname and zone as the game wrote them into your own maps; typed only when none has the ID
            var (nickname, zone) = author.Nickname?.Trim() is { Length: > 0 } typed
                ? (typed, author.Zone?.Trim() is { Length: > 0 } z ? z : "World")
                : await Task.Run(() => KnownAuthor(login)) ?? (null!, null!);
            if (nickname is null) { await Text(response, 404, "no map of yours carries this ID"); return; }
            Paths.SaveTestrunAuthor(login, nickname, zone);
            await Json(response, AuthorNow());
        }
        else if (path == "/api/command")
        {
            var command = Encoding.UTF8.GetString(body.ToArray());
            try { JsonDocument.Parse(command).Dispose(); }
            catch (JsonException e) { await Text(response, 400, e.Message); return; }
            var pages = await live.Send(command);
            await Text(response, pages > 0 ? 200 : 503, pages > 0 ? $"sent to {pages} page(s)" : "no page open");
        }
        else await Text(response, 404, "no such endpoint");
    }

    private async Task Shot(HttpListenerResponse response)
    {
        if (await live.Shot() is { } png)
        {
            response.Headers["X-Shot-File"] = LiveLink.ShotFile;
            await Bytes(response, 200, "image/png", png);
        }
        else await Text(response, 503, "no page answered");
    }

    // x, y: 0..1 across the page's 3D view, from the top left
    private async Task Pick(HttpListenerRequest request, HttpListenerResponse response)
    {
        var at = (X: request.QueryString["x"], Y: request.QueryString["y"]);
        if (!double.TryParse(at.X, CultureInfo.InvariantCulture, out var x) || !double.TryParse(at.Y, CultureInfo.InvariantCulture, out var y))
        {
            await Text(response, 400, "x and y: 0..1");
            return;
        }
        var fields = string.Create(CultureInfo.InvariantCulture, $"\"x\":{x},\"y\":{y}");
        if (await live.Ask("pick", fields) is { } json) await Bytes(response, 200, "application/json", json);
        else await Text(response, 503, "no page answered");
    }

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

    private static bool TargetExists(string folder, string name)
    {
        if (MapNames.FileName(name) is not { } file || Inside(Paths.Maps, Path.Combine(folder.Trim(), "x")) is not { } inside) return false;
        var dir = Path.GetDirectoryName(inside)!;
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.Map.Gbx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .Any(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
    }

    // what a conversion names it by default (maps.jsonc names included)
    private string OutputName(string map) => MapNames.DefaultOutput(Gbx.ParseHeaderNode<CGameCtnChallenge>(map).MapName, Translations.Load(dataDir));

    private sealed record ConvertRequest(JsonObject? Settings, string? Name, string? Folder, bool FakeAuthorTime, bool MeAsAuthor, bool RebuildBlocks, bool Overwrite);

    // starts it in the background; GET /api/convert follows it
    private async Task Convert(HttpListenerRequest request, HttpListenerResponse response, byte[] body)
    {
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        ConvertRequest? ask;
        try { ask = JsonSerializer.Deserialize<ConvertRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException e) { await Text(response, 400, e.Message); return; }
        if (ask is null || ask.Name?.Trim() is not { Length: > 0 } name) { await Text(response, 400, "a map name is needed"); return; }
        if (MapNames.FileName(name) is null) { await Text(response, 400, "the name can't hold < > : \" / \\ | ? * or end in a dot"); return; }
        var folder = ask.Folder?.Trim() ?? "";
        var outDir = folder.Length == 0 ? Paths.Maps : Inside(Paths.Maps, folder);
        if (outDir is null) { await Text(response, 400, "the folder must be under Maps"); return; }
        if (TargetExists(folder, name) && !ask.Overwrite) { await Text(response, 409, "a map of that name is already there"); return; }
        if (ask.MeAsAuthor && Paths.TestrunAuthorIfSet is null) { await Text(response, 400, "no user ID saved for Me as author"); return; }
        var choices = new ConvertCommand.Choices(outDir, name, ask.FakeAuthorTime, ask.MeAsAuthor, ask.RebuildBlocks, ask.Overwrite)
            { Settings = new MapSettings(ask.Settings ?? new JsonObject()) };
        if (jobs.Start(map, choices, name) is { } started) await Json(response, started);
        else await Text(response, 409, "a conversion is already running");
    }

    private sealed record AuthorEntry(string Login, string? Nickname, string? Zone);

    // the newest map under Maps by this account: its nickname and zone
    private static (string Nickname, string Zone)? KnownAuthor(string login)
    {
        foreach (var file in Directory.EnumerateFiles(Paths.Maps, "*.Map.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var map = Gbx.ParseHeaderNode<CGameCtnChallenge>(file);
                if (map.AuthorLogin == login && map.AuthorNickname is { Length: > 0 } nickname) return (nickname, map.AuthorZone is { Length: > 0 } zone ? zone : "World");
            }
            catch (Exception) { }
        }
        return null;
    }

    // the account a converted map can carry as its author (local.jsonc "testrunAuthor"), null if none
    private static AuthorEntry? AuthorNow() => Paths.TestrunAuthorIfSet is { } a ? new AuthorEntry(a.Login, a.Nickname, a.Zone) : null;

    private sealed record FolderList(List<string> Folders, string Progress);

    // every folder under Maps, empty ones too, "/" separated; and where a conversion goes by default
    private static FolderList ListFolders()
    {
        static string Rel(string dir) => Path.GetRelativePath(Paths.Maps, dir).Replace('\\', '/');
        var folders = Directory.EnumerateDirectories(Paths.Maps, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Select(Rel).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new FolderList(folders, Rel(Paths.OutFolder));
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

    private async Task Scene(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        await building.WaitAsync();
        string manifest;
        try { manifest = await Task.Run(() => SceneBundle.Ensure(map, request.QueryString["rebuild"] == "1")).ConfigureAwait(false); }
        finally { building.Release(); }
        await SendFile(response, manifest, "application/json");
    }

    private async Task Geometry(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        var file = SceneBundle.GeometryOf(map);
        if (!File.Exists(file)) { await Text(response, 404, "scene not built"); return; }
        await SendFile(response, file, "application/octet-stream");
    }

    // POST body: { "settings": { …as a maps.jsonc entry… } }
    private async Task Preview(HttpListenerRequest request, HttpListenerResponse response, byte[] body)
    {
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        var (settings, _, error) = SettingsIn(body);
        if (error is not null) { await Text(response, 400, error); return; }
        await Json(response, await Task.Run(() => preview.For(map, settings ?? MapSettings.Parse("{}"))).ConfigureAwait(false));
    }

    // POST body: { "settings": {…} | null, "from": "<run id>" | null }; null settings drops the draft
    private async Task Draft(HttpListenerRequest request, HttpListenerResponse response, byte[] body)
    {
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        var (settings, from, error) = SettingsIn(body);
        if (error is not null) { await Text(response, 400, error); return; }
        await Task.Run(() => preview.SaveDraft(map, settings, from)).ConfigureAwait(false);
        await Text(response, 200, "ok");
    }

    private static (MapSettings? Settings, string? From, string? Error) SettingsIn(byte[] body)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject root) return (null, null, "body must be an object");
            var from = root["from"] is System.Text.Json.Nodes.JsonValue f && f.TryGetValue<string>(out var text) ? text : null;
            return root["settings"] switch
            {
                System.Text.Json.Nodes.JsonObject o => (new MapSettings(o.DeepClone().AsObject()), from, null),
                null => (null, from, null),
                _ => (null, null, "settings must be an object or null"),
            };
        }
        catch (JsonException e) { return (null, null, e.Message); }
    }

    private async Task Texture(HttpListenerRequest request, HttpListenerResponse response)
    {
        var root = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var file = Inside(root, request.QueryString["path"]);
        if (file is null || !File.Exists(file)) { await Text(response, 404, "no such texture"); return; }
        response.Headers["Cache-Control"] = "max-age=3600";
        await SendFile(response, file, "application/octet-stream");
    }

    private async Task Static(HttpListenerResponse response, string path)
    {
        var file = Inside(webRoot, path.TrimStart('/'));
        if (file is null || !File.Exists(file)) file = Path.Combine(webRoot, "index.html");
        if (!File.Exists(file)) { await Text(response, 404, "page not built: npm run build in web/"); return; }
        await SendFile(response, file, ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream"));
    }

    private static string? MapFile(HttpListenerRequest request)
        => Inside(Paths.Maps, request.QueryString["map"]) is { } file
            && file.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? file : null;

    private static string? Inside(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static async Task SendFile(HttpListenerResponse response, string file, string contentType)
    {
        await using var stream = File.OpenRead(file);
        response.ContentType = contentType;
        response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream);
    }

    private static Task Json<T>(HttpListenerResponse response, T value)
        => Bytes(response, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static Task Text(HttpListenerResponse response, int status, string text)
        => Bytes(response, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static async Task Bytes(HttpListenerResponse response, int status, string contentType, byte[] bytes)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }


    private sealed record MapEntry(string Path, string Name, string Folder, long Size, DateTime Modified);
}
