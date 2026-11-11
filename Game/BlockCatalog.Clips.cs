using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;

namespace TmIceConverter;

// clip slots and how shaped clips turn
public sealed partial class BlockCatalog
{
    public IReadOnlyList<ClipSide> ClipSides(string blockName, bool ground, int variantIndex)
    {
        if (!TryLoadBlock(blockName, out var block) || PickVariant(block.Info, ground, variantIndex) is not { } variant) return [];
        var clipOnly = BlockParts.MobilPrefab(variant, block.Path) is null;
        return BlockParts.ClipSlots(variant).Select(s =>
            {
                var clips = ClipInfos(s.Clips).ToList();
                return new ClipSide(s.Bit, s.Probe)
                {
                    Center = s.UnitOrigin + new Vector3(16, 4, 16),
                    Clips = clips,
                    ClipOnly = clipOnly,
                };
            })
            .Where(s => s.Clips.Count > 0).ToList();
    }

    private static IEnumerable<ClipInfo> ClipInfos(External<CGameCtnBlockInfoClip>[]? clips)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < (clips?.Length ?? 0); i++)
        {
            if (clips![i].File?.FilePath is not { } file || !seen.Add(file)) continue;
            var node = clips[i].Node;
            yield return new ClipInfo(i, node?.Ident.Id ?? Path.GetFileName(file).Split('.')[0], node?.ClipType.ToString() ?? "",
                BlockParts.IsVertical(clips[i]), NonEmpty(node?.ClipGroupId), NonEmpty(node?.SymmetricalClipGroupId),
                NonEmpty((node as CGameCtnBlockInfoClipVertical)?.VerticalClipGroupId), NonEmpty(node?.ASymmetricalClipId),
                node?.IsFullFreeClip ?? false, node?.CanBeDeletedByFullFreeClip ?? false, IsAntiClip(node), node?.TopBottomMultiDir.ToString() ?? "SameDir");
        }
    }

    // GBX.NET's U03 is IsAntiClip
    private static bool IsAntiClip(CGameCtnBlockInfoClip? node)
        => node?.Chunks.Get<CGameCtnBlockInfoClip.Chunk03053006>() is { U03: > 0 };

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // quarter turns of a shaped top/bottom clip on top of its block's direction: learned from maps, else the fitted mesh turn
    public int ShapedClipTurn(string blockName, bool ground, int variantIndex, int bit, ClipInfo clip)
        => ClipTurns.Learned(blockName, bit, clip.Name)
            ?? (ShapedClipTurns(blockName, ground, variantIndex).TryGetValue((bit, clip.Index), out var turn) ? ClipTurns.DirOf(turn) : 0);

    private readonly Dictionary<(string, bool, int), Dictionary<(int Bit, int Index), Matrix4x4>> shapedTurns = [];

    // turn of each shaped top/bottom clip inside its block: the clip mesh fitted to the block's corners
    public IReadOnlyDictionary<(int Bit, int Index), Matrix4x4> ShapedClipTurns(string blockName, bool ground, int variantIndex)
    {
        if (shapedTurns.TryGetValue((blockName, ground, variantIndex), out var known)) return known;
        var turns = shapedTurns[(blockName, ground, variantIndex)] = [];
        if (!TryLoadBlock(blockName, out var block) || PickVariant(block.Info, ground, variantIndex) is not { } variant
            || BlockParts.MobilPrefab(variant, block.Path) is not { } main) return turns;
        var corners = fit.Corners([main]);
        var ancestor = Gbx.ParseHeader(block.Path).RefTable?.AncestorLevel ?? 0;
        foreach (var (unitOrigin, bit, clips, place, _) in BlockParts.ClipSlots(variant))
        {
            if (bit % 6 < 4) continue;
            for (int i = 0; i < (clips?.Length ?? 0); i++)
                if (clips![i] is { Node: { ASymmetricalClipId.Length: > 0 } node, File: { } file }
                    && ((CGameCtnBlockInfoVariant?)node.VariantBaseAir ?? node.VariantBaseGround) is { } clipVariant
                    && BlockParts.MobilPrefab(clipVariant, GameFiles.ResolveRef(block.Path, ancestor, file.FilePath)) is { } part)
                {
                    // sloped bottoms follow the road; corners can't tell a slope's ends apart
                    var sloped = bit % 6 == 4 ? fit.BottomClipTurn("AllDir", part, main) : Matrix4x4.Identity;
                    turns[(bit, i)] = !sloped.IsIdentity || fit.Slope(part, upOnly: false).Length() >= 0.1f && bit % 6 == 4
                        ? sloped : fit.FittedTopTurn(part, place * Matrix4x4.CreateTranslation(unitOrigin), corners);
                }
        }
        return turns;
    }
}
