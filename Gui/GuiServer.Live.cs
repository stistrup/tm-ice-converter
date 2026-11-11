using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TmIceConverter;

// the open page: its state, screenshots, picks, commands to it
internal sealed partial class GuiServer
{
    private async Task SetState(Call c)
    {
        live.SetState(Encoding.UTF8.GetString(c.Body));
        await Text(c.Response, 200, "ok");
    }

    private Task Answer(Call c) => Text(c.Response, live.Answered(c.Query("id"), c.Body) ? 200 : 404, "");

    private async Task Shot(Call c)
    {
        if (await live.Shot() is not { } png) throw new Refused(503, "no page answered");
        c.Response.Headers["X-Shot-File"] = LiveLink.ShotFile;
        await Bytes(c.Response, 200, "image/png", png);
    }

    // x, y: 0..1 across the page's 3D view, from the top left
    private async Task Pick(Call c)
    {
        if (!double.TryParse(c.Query("x"), CultureInfo.InvariantCulture, out var x) || !double.TryParse(c.Query("y"), CultureInfo.InvariantCulture, out var y))
            throw new Refused(400, "x and y: 0..1");
        var fields = string.Create(CultureInfo.InvariantCulture, $"\"x\":{x},\"y\":{y}");
        if (await live.Ask("pick", fields) is not { } json) throw new Refused(503, "no page answered");
        await Bytes(c.Response, 200, "application/json", json);
    }

    private async Task Command(Call c)
    {
        var command = Encoding.UTF8.GetString(c.Body);
        try { JsonDocument.Parse(command).Dispose(); }
        catch (JsonException e) { throw new Refused(400, e.Message); }
        var pages = await live.Send(command);
        await Text(c.Response, pages > 0 ? 200 : 503, pages > 0 ? $"sent to {pages} page(s)" : "no page open");
    }

    private async Task SetupStep(Call c)
    {
        try
        {
            if (c.Request.Url!.AbsolutePath == "/api/setup-path") setup.SetPath(c.Query("name") ?? "", JsonSerializer.Deserialize<string>(c.Body) ?? "");
            else setup.Act(c.Query("id") ?? "");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
        {
            throw new Refused(400, e.Message);
        }
        await Json(c.Response, await Task.Run(setup.Check));
    }
}
