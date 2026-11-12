using System.Text.Json;

namespace TmSurfaceEditor;

internal static class Jsonc
{
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path),
        new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true })!;
}
