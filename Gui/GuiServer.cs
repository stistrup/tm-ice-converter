using System.Net;
using System.Text;
using System.Text.Json;

namespace TmSurfaceEditor;

// localhost only; HttpListener rejects other Host headers. Endpoints by topic in the other parts.
internal sealed partial class GuiServer(string url, string webRoot, ConversionPreview preview, ConvertJobs jobs, string dataDir)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

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
    private readonly Setup setup = new(dataDir);

    private sealed record Call(HttpListenerRequest Request, HttpListenerResponse Response, byte[] Body)
    {
        public string? Query(string key) => Request.QueryString[key];
    }

    private delegate Task Handler(Call call);

    // answered as plain text with this status
    private sealed class Refused(int status, string message) : Exception(message)
    {
        public int Status => status;
    }

    private Dictionary<string, Handler>? gets, posts;

    private Dictionary<string, Handler> Gets => gets ??= new()
    {
        ["/api/state"] = c => Bytes(c.Response, 200, "application/json", Encoding.UTF8.GetBytes(live.State)),
        ["/api/events"] = c => live.Events(c.Response),
        ["/api/shot"] = Shot,
        ["/api/pick"] = Pick,
        ["/api/setup"] = async c => await Json(c.Response, await Task.Run(setup.Check)),
        ["/api/version"] = async c => await Json(c.Response, await AppVersion.Check()),
        ["/api/maps"] = c => Json(c.Response, ListMaps()),
        ["/api/folders"] = c => Json(c.Response, ListFolders()),
        ["/api/author"] = c => Json(c.Response, AuthorNow()),
        ["/api/convert"] = c => Json(c.Response, jobs.Current),
        // a conversion's target: folder + name under Maps (case as the game's file system ignores it)
        ["/api/output-name"] = c => Json(c.Response, OutputName(MapFileOf(c))),
        ["/api/exists"] = c => Json(c.Response, TargetExists(c.Query("folder") ?? "", c.Query("name") ?? "")),
        ["/api/scene"] = Scene,
        ["/api/geometry"] = Geometry,
        ["/api/texture"] = Texture,
        ["/api/preview-build"] = c => Json(c.Response, preview.Building()),
        ["/api/preview-scene"] = PreviewScene,
        ["/api/presets"] = c => Json(c.Response, Preset.Load(dataDir).Select(p => new { p.Name, p.Kinds })),
        ["/api/settings"] = async c => await Json(c.Response, await Task.Run(() => preview.StateOf(MapFileOf(c))).ConfigureAwait(false)),
    };

    private Dictionary<string, Handler> Posts => posts ??= new()
    {
        ["/api/state"] = SetState,
        ["/api/answer"] = Answer,
        ["/api/shot"] = Answer,
        ["/api/setup-path"] = SetupStep,
        ["/api/setup-action"] = SetupStep,
        ["/api/preview"] = Preview,
        ["/api/edit"] = Edit,
        ["/api/undo"] = c => Document(c, (log, uid) => log.Undo(uid)),
        ["/api/redo"] = c => Document(c, (log, uid) => log.Redo(uid)),
        ["/api/save-settings"] = c => Document(c, (log, uid) => log.SaveWorking(uid)),
        ["/api/revert"] = c => Document(c, (log, uid) => log.Revert(uid)),
        ["/api/apply-preset"] = ApplyPreset,
        ["/api/convert"] = Convert,
        ["/api/convert-cancel"] = c => Ok(c, () => jobs.Cancel(c.Query("id") ?? "")),
        ["/api/convert-dismiss"] = c => Ok(c, () => jobs.Dismiss(c.Query("id") ?? "")),
        ["/api/author"] = SetAuthor,
        ["/api/command"] = Command,
    };

    // listening: the browser can open now
    public event Action? Started;

    public async Task RunAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        Console.WriteLine($"Surface Editor is running on {url}. Close this window to quit.");
        Started?.Invoke();
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
            var post = request.HttpMethod == "POST";
            if (!post && request.HttpMethod != "GET") throw new Refused(405, "GET or POST only");
            var call = new Call(request, response, post ? await BodyOf(request) : []);
            if ((post ? Posts : Gets).TryGetValue(path, out var handler)) await handler(call);
            else if (post || path.StartsWith("/api/")) throw new Refused(404, "no such endpoint");
            else await Static(response, path);
        }
        catch (Refused e)
        {
            try { await Text(response, e.Status, e.Message); } catch (Exception) { }
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

    private static async Task<byte[]> BodyOf(HttpListenerRequest request)
    {
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body);
        return body.ToArray();
    }

    private static T? Read<T>(byte[] body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(body, ReadOptions); }
        catch (JsonException e) { throw new Refused(400, e.Message); }
    }

    private static Task Ok(Call c, Action act)
    {
        act();
        return Text(c.Response, 200, "ok");
    }

    private async Task Static(HttpListenerResponse response, string path)
    {
        var file = Inside(webRoot, path.TrimStart('/'));
        if (file is null || !File.Exists(file)) file = Path.Combine(webRoot, "index.html");
        if (!File.Exists(file)) throw new Refused(404, "page not built: npm run build in web/");
        await SendFile(response, file, ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream"));
    }

    private static string MapFileOf(Call c) => MapFile(c.Request) ?? throw new Refused(404, "no such map");

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
}
