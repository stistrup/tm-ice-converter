namespace TmIceConverter;

public enum Source { Table, Rule }

public sealed class ConversionReport
{
    public Dictionary<(string From, string To, Source Source), int> Swapped { get; } = [];
    public Dictionary<string, int> Kept { get; } = [];
    public Dictionary<string, int> Unmatched { get; } = [];
    public Dictionary<string, int> ReplacedByItem { get; } = [];
    public Dictionary<string, int> Skinned { get; } = [];
    public Dictionary<string, int> SpecialKept { get; } = [];
    public Dictionary<string, int> SpecialSkinned { get; } = [];
    public Dictionary<string, int> KeptByList { get; } = [];
    public Dictionary<string, int> RemovedByList { get; } = [];
    public Dictionary<string, int> Recoloured { get; } = [];
    public Dictionary<string, int> Sunk { get; } = [];
    public Dictionary<string, int> Gated { get; } = [];
    public HashSet<IceItem> UsedItems { get; } = [];
    public Dictionary<string, int> ReplacedByBlock { get; } = [];
    public HashSet<IceBlock> UsedBlocks { get; } = [];
    public Dictionary<string, int> KeptByItem { get; } = [];
    public Dictionary<string, int> Water { get; } = [];
    public Dictionary<(string Block, string Reason), int> ItemReasons { get; } = [];
    public Dictionary<string, int> Concrete { get; } = [];
    public Dictionary<string, int> Walls { get; } = [];
    public Dictionary<string, int> TopsIced { get; } = [];
    public Dictionary<string, int> TopsInBlock { get; } = [];
    public List<string> CameraClipsRemoved { get; } = [];
    public Dictionary<(string From, string To), int> EngineOff { get; } = []; // "" = removed
    public Dictionary<(string From, string To), int> SwappedItems { get; } = [];
    public Dictionary<(string Model, string Item), int> Trees { get; } = [];
    public Dictionary<string, int> TreesMissing { get; } = [];
    public Dictionary<string, int> KeptUnder { get; } = [];
    public Dictionary<string, int> SnowTerrain { get; } = [];
    public Dictionary<string, int> SceneryKept { get; } = [];
    public Dictionary<string, int> TerrainItems { get; } = [];
    public Dictionary<string, int> Ramped { get; } = [];
    public Dictionary<string, int> IcedItems { get; } = [];
    public Dictionary<string, int> ItemsNotIced { get; } = [];
    public int FreeBlockOffsetsSkipped { get; set; }

    public int Left => Unmatched.Values.Sum() + SpecialKept.Values.Sum();
}

internal static class Counts
{
    public static void Bump<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
        => counts[key] = counts.GetValueOrDefault(key) + 1;
}
