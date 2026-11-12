using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TmSurfaceEditor;

// this build's version (<Version> in the csproj) and the newest release on GitHub, asked once per start
internal static class AppVersion
{
    public const string Releases = "https://github.com/stistrup/tm-surface-editor/releases";
    private const string LatestApi = "https://api.github.com/repos/stistrup/tm-surface-editor/releases/latest";

    public sealed record Info(string Current, string? Latest, string? Url, bool Newer);

    // "0.1.0+<commit>" -> "0.1.0"
    public static string Current { get; } = (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    private static Task<(string Tag, string Url)?>? latest;

    public static async Task<Info> Check()
    {
        var found = await (latest ??= Latest());
        var newer = found is { } f && Version.TryParse(f.Tag.TrimStart('v', 'V'), out var theirs) && Version.TryParse(Current, out var ours) && theirs > ours;
        return new Info(Current, found?.Tag, found?.Url, newer);
    }

    // no network, no releases yet, rate limited: just no answer
    private static async Task<(string Tag, string Url)?> Latest()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TmSurfaceEditor", Current));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));
            return doc.RootElement.GetProperty("tag_name").GetString() is { } tag ? (tag, doc.RootElement.GetProperty("html_url").GetString() ?? Releases) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return null; }
    }
}
