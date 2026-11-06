using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// localhost only; HttpListener rejects other Host headers
internal sealed partial class GuiServer(string url, string webRoot, ConversionPreview preview)
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
            else if (path == "/api/scene") await Scene(request, response);
            else if (path == "/api/geometry") await Geometry(request, response);
            else if (path == "/api/texture") await Texture(request, response);
            else if (path == "/api/preview") await Preview(request, response);
            else if (path == "/api/preview-build") await Json(response, preview.Building());
            else if (path == "/api/preview-scene")
            {
                if (preview.AddedScene(request.QueryString["id"] ?? "") is not { } scene) await Text(response, 404, "no such preview");
                else if (request.QueryString["part"] == "geometry") await Bytes(response, 200, "application/octet-stream", scene.Geometry);
                else await Bytes(response, 200, "application/json", scene.Manifest);
            }
            else if (path == "/api/options")
            {
                if (MapFile(request) is { } map) await Json(response, preview.OptionsOf(map));
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

    // as the game lists it
    private string DisplayName(FileInfo file)
    {
        lock (names)
            if (names.TryGetValue(file.FullName, out var known) && known.Modified == file.LastWriteTimeUtc) return known.Name;
        string name;
        try { name = FormatCodes().Replace(Gbx.ParseHeaderNode<CGameCtnChallenge>(file.FullName).MapName, ""); }
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

    // set=name=true,name=false: options over the map's own
    // POST body: { "picks": { "<piece key>": "<choice>" } }
    private async Task Preview(HttpListenerRequest request, HttpListenerResponse response, byte[]? body = null)
    {
        var picks = new Dictionary<string, string>();
        if (body is { Length: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("picks", out var list))
                    foreach (var pick in list.EnumerateObject())
                        if (pick.Value.GetString() is { } choice && Translations.PieceChoices.Contains(choice)) picks[pick.Name] = choice;
                        else { await Text(response, 400, $"bad pick for '{pick.Name}'"); return; }
            }
            catch (JsonException e) { await Text(response, 400, e.Message); return; }
        }
        if (MapFile(request) is not { } map) { await Text(response, 404, "no such map"); return; }
        var settings = new List<(string, bool)>();
        foreach (var pair in (request.QueryString["set"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.Split('=') is not [var name, var value] || !bool.TryParse(value, out var on)) { await Text(response, 400, $"bad setting '{pair}'"); return; }
            try { new ConvertOptions().With(name, on); }
            catch (FormatException e) { await Text(response, 400, e.Message); return; }
            settings.Add((name, on));
        }
        await Json(response, await Task.Run(() => preview.For(map, settings, picks)).ConfigureAwait(false));
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

    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[lLhHpP](\[[^\]]*\])?|.)")]
    private static partial Regex FormatCodes();

    private sealed record MapEntry(string Path, string Name, string Folder, long Size, DateTime Modified);
}
