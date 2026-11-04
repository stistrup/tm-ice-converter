using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using TmEssentials;

namespace TmIceConverter;

// what a converted map needs before it's a valid file of its own
internal static class MapOutput
{
    public static void Prepare(CGameCtnChallenge map, string name, string uid, bool testrun, bool release)
    {
        map.MapName = name;
        map.MapUid = uid;
        map.Kind = CGameCtnChallenge.MapKind.InProgress;
        EnsureHeaderChunks(map);
        ClearValidation(map);
        if (testrun) DummyValidation(map);
        if (testrun || release) AsAuthor(map);
        // clubs reject maps without a lightmap declared
        (map.LightmapVersion, map.LightmapFrames, map.LightmapCache) = (null, null, null);
    }

    public static void Embed(CGameCtnChallenge map, ConversionReport report)
    {
        if (report.UsedItems.Count + report.UsedBlocks.Count == 0) return;
        // keep embedded entries findable by name
        var embedded = map.ExpectedEmbeddedItemModels ?? [];
        map.UpdateEmbeddedZipData(zip =>
        {
            foreach (var ident in embedded)
            {
                var want = "Items/" + ident.Id.Replace('\\', '/');
                if (zip.GetEntry(want) is not null) continue;
                if (zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/" + ident.Id.Replace('\\', '/'))) is not { } odd) continue;
                using (var from = odd.Open())
                using (var to = zip.CreateEntry(want).Open())
                    from.CopyTo(to);
                odd.Delete();
            }
            foreach (var item in report.UsedItems)
                zip.CreateEntryFromFile(item.FilePath, "Items/" + item.Ident.Id.Replace('\\', '/'));
            foreach (var block in report.UsedBlocks)
                zip.CreateEntryFromFile(block.FilePath, "Items/" + block.Ident.Id.Replace('\\', '/'));
        });
    }

    public static void Save(Gbx<CGameCtnChallenge> gbx, string path)
    {
        gbx.Save(path);
        // second save fixes the header's lightmap version
        Gbx.Parse<CGameCtnChallenge>(path).Save(path);
    }

    public static string DerivedUid(string original, string suffix)
        => System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(original + suffix)))
            .Replace('+', '-').Replace('/', '_')[..27];

    // replay-extracted maps have no header chunks
    private static void EnsureHeaderChunks(CGameCtnChallenge map)
    {
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043002>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043002>().Version = 13;
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043003>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043003>().Version = 11;
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043004>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043004>().Version = 6;
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043005>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043005>();
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043007>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043007>().Version = 1;
        if (!map.ContainsChunk<CGameCtnChallenge.HeaderChunk03043008>()) map.CreateChunk<CGameCtnChallenge.HeaderChunk03043008>().Version = 1;
        map.KindInHeader = map.Kind;
        map.MapType ??= "TrackMania\\TM_Race";
        map.MapStyle ??= "";
        if (map.ChallengeParameters is { } p)
        {
            map.BronzeTime ??= p.BronzeTime;
            map.SilverTime ??= p.SilverTime;
            map.GoldTime ??= p.GoldTime;
            map.AuthorTime ??= p.AuthorTime;
            map.AuthorScore = p.AuthorScore;
        }
        map.Xml ??= $"""<header type="map" title="{map.TitleId}"><ident uid="{map.MapUid}" name="{System.Security.SecurityElement.Escape(map.MapName)}" author="{map.AuthorLogin}"/><desc envir="Stadium" mood="Day" type="Race" maptype="TrackMania\TM_Race" validated="0" nblaps="0"/></header>""";
    }

    private static void ClearValidation(CGameCtnChallenge map)
    {
        map.BronzeTime = map.SilverTime = map.GoldTime = map.AuthorTime = null;
        map.AuthorScore = 0;
        if (map.ChallengeParameters is { } p)
        {
            p.BronzeTime = p.SilverTime = p.GoldTime = p.AuthorTime = null;
            p.AuthorScore = 0;
            p.RaceValidateGhost = null;
        }
        if (map.Xml is { } xml)
        {
            xml = Regex.Replace(xml, @"(<ident uid="")[^""]*", m => m.Groups[1].Value + map.MapUid);
            xml = Regex.Replace(xml, @"(<ident [^>]*name="")[^""]*", m => m.Groups[1].Value + System.Security.SecurityElement.Escape(map.MapName));
            xml = Regex.Replace(xml, @"validated=""\d""", @"validated=""0""");
            xml = Regex.Replace(xml, @"<times [^>]*", m => Regex.Replace(Regex.Replace(m.Value,
                @"(bronze|silver|gold|authortime)=""[^""]*""", @"$1=""-1"""), @"authorscore=""[^""]*""", @"authorscore=""0"""));
            map.Xml = xml;
        }
    }

    private static void DummyValidation(CGameCtnChallenge map)
    {
        const int author = 3_600_000;
        static int Medal(double ratio) => (int)Math.Ceiling(author * ratio / 1000) * 1000;
        var (gold, silver, bronze) = (Medal(1.06), Medal(1.2), Medal(1.5));
        (map.AuthorTime, map.GoldTime, map.SilverTime, map.BronzeTime) = (TimeInt32.FromMilliseconds(author), TimeInt32.FromMilliseconds(gold), TimeInt32.FromMilliseconds(silver), TimeInt32.FromMilliseconds(bronze));
        if (map.ChallengeParameters is { } p)
            (p.AuthorTime, p.GoldTime, p.SilverTime, p.BronzeTime) = (map.AuthorTime, map.GoldTime, map.SilverTime, map.BronzeTime);
        if (map.Xml is { } xml)
        {
            xml = Regex.Replace(xml, @"validated=""\d""", @"validated=""1""");
            xml = Regex.Replace(xml, @"<times [^>]*", m => Regex.Replace(m.Value, @"(bronze|silver|gold|authortime)=""[^""]*""", t => t.Groups[1].Value switch
            {
                "bronze" => $@"bronze=""{bronze}""",
                "silver" => $@"silver=""{silver}""",
                "gold" => $@"gold=""{gold}""",
                _ => $@"authortime=""{author}""",
            }));
            map.Xml = xml;
        }
    }

    private static void AsAuthor(CGameCtnChallenge map)
    {
        var (login, nickname, zone) = Paths.TestrunAuthor;
        (map.AuthorLogin, map.AuthorNickname, map.AuthorZone) = (login, nickname, zone);
        if (map.MapInfo is { } info) map.MapInfo = info with { Author = login };
        map.Kind = map.KindInHeader = CGameCtnChallenge.MapKind.Multi;
        if (map.Xml is { } xml)
            map.Xml = Regex.Replace(xml, @"(<ident [^>]*)author=""[^""]*""(?: authorzone=""[^""]*"")?",
                m => $@"{m.Groups[1].Value}author=""{login}"" authorzone=""{System.Security.SecurityElement.Escape(zone)}""");
    }
}
