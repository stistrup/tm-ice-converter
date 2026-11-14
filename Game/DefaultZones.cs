using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// what cells without a terrain block are, per environment (the collection's default zone): Sea, Grass, Dirt, Land
internal static class DefaultZones
{
    private static readonly Dictionary<string, string?> zones = [];

    public static string? Of(string? environment)
    {
        if (environment is null) return null;
        lock (zones)
        {
            if (zones.TryGetValue(environment, out var known)) return known;
            var file = GameFiles.OnDisk(Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!, "Collections", environment + ".Collection.Gbx"));
            string? zone = null;
            try { if (File.Exists(file)) zone = Gbx.ParseNode<CGameCtnCollection>(file, new GbxReadSettings { IgnoreExceptionsInBody = true }).DefaultZoneId; }
            catch (Exception e) { Console.Error.WriteLine($"default zone of {environment}: {e.Message}"); }
            return zones[environment] = zone;
        }
    }

    public static bool IsWater(string? zone) => zone is not null && Regex.IsMatch(zone, "Sea|Water|Lake");
}
