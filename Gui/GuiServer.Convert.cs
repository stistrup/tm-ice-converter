using System.Text.Json.Nodes;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// conversions and the author they can carry
internal sealed partial class GuiServer
{
    // Maps: several at once, each with its default name and its own settings (saved, else the defaults); else ?map= with Name and Settings
    private sealed record ConvertRequest(JsonObject? Settings, string? Name, string? Folder, bool FakeAuthorTime, bool MeAsAuthor, bool RebuildBlocks, bool Overwrite,
        List<string>? Maps, string? Suffix);

    private sealed record AuthorEntry(string Login, string? Nickname, string? Zone);

    // starts it in the background; GET /api/convert follows it
    private async Task Convert(Call c)
    {
        var ask = Read<ConvertRequest>(c.Body) ?? throw new Refused(400, "nothing asked");
        var batch = ask.Maps is { Count: > 0 };
        var suffix = ask.Suffix ?? MapNames.DefaultSuffix;
        var maps = batch ? ask.Maps!.Select(m => Inside(Paths.Maps, m)).ToList() : [MapFile(c.Request)];
        if (maps.Any(m => m is null || !m.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase) || !File.Exists(m))) throw new Refused(404, "no such map");
        var name = ask.Name?.Trim() ?? "";
        if (!batch) CheckName(name);
        var folder = ask.Folder?.Trim() ?? "";
        var outDir = (folder.Length == 0 ? Paths.Maps : Inside(Paths.Maps, folder)) ?? throw new Refused(400, "the folder must be under Maps");
        var names = batch ? maps.Select(m => DefaultNameOrNull(m!, suffix)).OfType<string>().ToList() : [name];
        if (!ask.Overwrite && names.Any(n => TargetExists(folder, n))) throw new Refused(409, "a map of that name is already there");
        if (ask.MeAsAuthor && Paths.TestrunAuthorIfSet is null) throw new Refused(400, "no user ID saved for Me as author");
        var choices = new ConvertCommand.Choices(outDir, batch ? null : name, ask.FakeAuthorTime, ask.MeAsAuthor, ask.RebuildBlocks, ask.Overwrite)
            { Settings = batch ? null : new MapSettings(ask.Settings ?? new JsonObject()), Suffix = suffix };
        if (jobs.Start(maps!, choices, batch ? $"{maps.Count} maps" : name) is not { } started) throw new Refused(409, "a conversion is already running");
        await Json(c.Response, started);
    }

    private static void CheckName(string name)
    {
        if (name.Length == 0) throw new Refused(400, "a map name is needed");
        if (MapNames.FileName(name) is null) throw new Refused(400, "the name can't hold < > : \" / \\ | ? * or end in a dot");
    }

    // one that doesn't read fails in the run, with its own message
    private static string? DefaultNameOrNull(string map, string suffix)
    {
        try { return OutputName(map, suffix); }
        catch (Exception) { return null; }
    }

    // nickname and zone as the game wrote them into your own maps; typed only when none has the ID
    private async Task SetAuthor(Call c)
    {
        var author = Read<AuthorEntry>(c.Body);
        if (author is not { Login.Length: > 0 }) throw new Refused(400, "a user ID is needed");
        var login = author.Login.Trim();
        var (nickname, zone) = author.Nickname?.Trim() is { Length: > 0 } typed
            ? (typed, author.Zone?.Trim() is { Length: > 0 } z ? z : "World")
            : await Task.Run(() => KnownAuthor(login)) ?? throw new Refused(404, "no map of yours carries this ID");
        Paths.SaveTestrunAuthor(login, nickname, zone);
        await Json(c.Response, AuthorNow());
    }

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
}
