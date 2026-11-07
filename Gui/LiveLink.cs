using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace TmIceConverter;

// lets a terminal see and steer the open page: state in, commands and screenshots out over SSE
internal sealed class LiveLink
{
    private readonly List<Page> pages = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> answers = new();
    private volatile string state = "{}";

    public string State => state;

    public static string ShotFile => Path.Combine(Paths.Cache, "Gui", "shot.png");

    public void SetState(string json) => state = json;

    // stays open until the page goes away
    public async Task Events(HttpListenerResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        var page = new Page(response, new SemaphoreSlim(1));
        await Write(page, ": open\n\n");
        lock (pages) pages.Add(page);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                // a data event, not a comment: the page's watchdog needs to see it
                await Write(page, "data: {\"type\":\"ping\"}\n\n");
            }
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
        finally
        {
            lock (pages) pages.Remove(page);
        }
    }

    public async Task<int> Send(string json)
    {
        Page[] open;
        lock (pages) open = [.. pages];
        var sent = 0;
        foreach (var page in open)
        {
            try { await Write(page, $"data: {json.ReplaceLineEndings(" ")}\n\n"); sent++; }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
        }
        return sent;
    }

    public async Task<byte[]?> Shot()
    {
        if (await Ask("shot", "") is not { } png) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(ShotFile)!);
        await File.WriteAllBytesAsync(ShotFile, png);
        return png;
    }

    // a command the page answers with a POST to /api/answer?id=; fields: extra JSON members, "" for none
    public async Task<byte[]?> Ask(string type, string fields)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        answers[id] = waiting;
        try
        {
            var extra = fields.Length > 0 ? "," + fields : "";
            if (await Send($$"""{"type":"{{type}}","id":"{{id}}"{{extra}}}""") == 0) return null;
            var done = await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            return done == waiting.Task ? waiting.Task.Result : null;
        }
        finally
        {
            answers.TryRemove(id, out _);
        }
    }

    public bool Answered(string? id, byte[] body)
        => id is not null && answers.TryGetValue(id, out var waiting) && waiting.TrySetResult(body);

    private static async Task Write(Page page, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await page.Gate.WaitAsync();
        try
        {
            await page.Response.OutputStream.WriteAsync(bytes);
            await page.Response.OutputStream.FlushAsync();
        }
        finally { page.Gate.Release(); }
    }

    private sealed record Page(HttpListenerResponse Response, SemaphoreSlim Gate);
}
