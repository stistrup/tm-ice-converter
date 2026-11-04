using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;

namespace TmIceConverter;

// one map's conversion: what it works from and what's been decided so far
internal sealed class ConversionRun
{
    public static readonly HashSet<int> NoClips = [];

    public CGameCtnChallenge Map { get; }
    public string Collection { get; }
    public Translations Rules { get; }
    public ConversionReport Report { get; } = new();

    public Translations Translations { get; }
    public HashSet<string> KnownBlocks { get; }
    public BlockCatalog Shapes { get; }
    public ItemBuilder Items { get; }
    public SurfaceParents SurfaceParents { get; }
    public Dictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> FlatTops { get; }

    // clips as in the original
    public List<CGameCtnBlock> Grid { get; }
    public List<ClipResolver.Placed> Placed { get; }
    public List<HashSet<int>> Target { get; }
    public Dictionary<CGameCtnBlock, List<Int3>> Cells { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CGameCtnBlock, HashSet<int>> clips = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CGameCtnBlock, int> variants = new(ReferenceEqualityComparer.Instance);
    // terrain height per column
    private readonly Dictionary<(int X, int Z), int> terrainTop;

    public SnowArea Snow { get; }

    public HashSet<CGameCtnBlock> Replaced { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<CGameCtnBlock> Renamed { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<CGameCtnBlock> Skinned { get; } = new(ReferenceEqualityComparer.Instance);
    // renamed only at the end: clips and snow work on the native name
    public Dictionary<CGameCtnBlock, IceBlock> Customs { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<CGameCtnBlock, (string Archetype, IReadOnlySet<int> Baked, bool Lift)> BuiltAs { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<CGameCtnBlock> Stacked { get; } = new(ReferenceEqualityComparer.Instance);
    public List<CGameCtnBlock> EngineOffGates { get; } = [];
    // convertScenery off: left alone by the neighbour and top passes too
    public HashSet<CGameCtnBlock> SceneryKept { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<CGameCtnBlock> Topped { get; } = new(ReferenceEqualityComparer.Instance);

    public ConversionRun(CGameCtnChallenge map, string collection, Translations rules, Translations translations, HashSet<string> knownBlocks,
        BlockCatalog shapes, ItemBuilder items, SurfaceParents surfaceParents, Dictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> flatTops)
    {
        (Map, Collection, Rules, Translations, KnownBlocks, Shapes, Items, SurfaceParents, FlatTops) = (map, collection, rules, translations, knownBlocks, shapes, items, surfaceParents, flatTops);
        var resolver = new ClipResolver(shapes);
        Grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        Placed = resolver.OfAll(Grid);
        Target = resolver.Resolve(Placed);
        for (int i = 0; i < Grid.Count; i++)
        {
            (clips[Grid[i]], variants[Grid[i]]) = (Target[i], Placed[i].Variant);
            Cells[Grid[i]] = resolver.UnitCells(Placed[i]).ToList();
        }
        terrainTop = Grid.Where(b => b.IsGround).SelectMany(b => Cells[b]).GroupBy(c => (c.X, c.Z)).ToDictionary(g => g.Key, g => g.Max(c => c.Y));
        Snow = new SnowArea(this);
    }

    // the resolver's variant, may differ from the placed one (VariantOf)
    public int Variant(CGameCtnBlock block) => block.IsFree ? VariantOf(block) : variants[block];

    public IReadOnlySet<int> Clips(CGameCtnBlock block) => block.IsFree ? BlockKinds.FreeClips(Shapes, block) : clips[block];

    // at or below the ground in one of its cells
    public bool BelowGround(CGameCtnBlock block) => Cells[block].Any(c => terrainTop.TryGetValue((c.X, c.Z), out var groundY) && c.Y <= groundY);

    // deco set into the terrain
    public bool InTerrain(CGameCtnBlock block) => !block.IsGround && !block.IsFree && block.Name.StartsWith("Deco") && BelowGround(block);

    // grid blocks by cell, free blocks by position
    public bool OnKeepList(CGameCtnBlock block)
        => block.IsFree
            ? block.AbsolutePositionInMap is { } p && Rules.Keep.Any(e => Translations.IsAt(e, block.Name, new Vector3(p.X, p.Y, p.Z)) == true)
            : Rules.Keep.Contains(Translations.KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z));
}
