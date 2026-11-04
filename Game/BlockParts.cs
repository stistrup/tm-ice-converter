using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// the prefabs and clips that make up a placed block
internal static class BlockParts
{
    public static List<(string Path, Matrix4x4 Transform)>? Of(ClipFit fit, string infoPath, CGameCtnBlockInfoVariant variant, int mobil, string pillarShapeDirs,
        Func<int, int, External<CGameCtnBlockInfoClip>, bool, int?> show)
    {
        var parts = new List<(string, Matrix4x4)>();
        var walls = new List<(string Path, Matrix4x4 Transform)>();
        var main = MobilPrefab(variant, infoPath, mobil);
        if (main is not null) { parts.Add((main.Value.Path, main.Value.Transform)); walls.Add(parts[^1]); }

        var ancestor = Gbx.ParseHeader(infoPath).RefTable?.AncestorLevel ?? 0;
        var blockName = Path.GetFileName(infoPath).Split('.')[0];
        // baked clips say how the game turns it
        Matrix4x4? Learned(int bit, CGameCtnBlockInfoClip node) => ClipTurns.Learned(blockName, bit, node.Ident.Id) is { } k ? AroundUnitCenter(-k * MathF.PI / 2) : null;
        var rise = Vector2.Zero;
        if (main is null)
            foreach (var (unitOrigin, bit, clips, _, probe) in ClipSlots(variant))
                if (bit % 6 < 4 && clips is { Length: > 0 })
                    rise += Vector2.Normalize(new Vector2(probe.X - unitOrigin.X - 16, probe.Z - unitOrigin.Z - 16));
        var tops = new List<((string Path, Matrix4x4 Transform) Part, Matrix4x4 Place, bool ClipOnly, Matrix4x4? Learned)>();
        foreach (var (unitOrigin, bit, clips, place, _) in ClipSlots(variant))
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < (clips?.Length ?? 0); i++)
            {
                var clip = clips![i];
                if (clip.File?.FilePath is not { } filePath || !seen.Add(filePath) || show(bit, i, clip, main is null) is not { } column) continue;
                if (clip.Node is not { } node || clip.File is not { } file) continue;
                var clipPath = GameFiles.ResolveRef(infoPath, ancestor, file.FilePath);
                var prefab = IsVertical(clip) && VerticalMobil(node, column) is ({ } vertical, var position)
                    ? MobilPrefab(vertical, clipPath, position, exact: true)
                    : ((CGameCtnBlockInfoVariant?)node.VariantBaseAir ?? node.VariantBaseGround) is { } clipVariant
                        ? MobilPrefab(clipVariant, clipPath) : null;
                if (prefab is { } part)
                {
                    if (bit % 6 == 5) { tops.Add((part, place * Matrix4x4.CreateTranslation(unitOrigin), main is null, Learned(bit, node))); continue; }
                    var turn = bit % 6 != 4 ? Matrix4x4.Identity
                        : Learned(bit, node) ?? (main is { } m ? fit.BottomClipTurn(pillarShapeDirs, part, m) : Matrix4x4.Identity);
                    if (bit % 6 == 4 && turn.IsIdentity && Learned(bit, node) is null) turn = fit.InFootprintTurn(part, place * Matrix4x4.CreateTranslation(unitOrigin), variant);
                    parts.Add((part.Path, part.Transform * turn * place * Matrix4x4.CreateTranslation(unitOrigin)));
                    if (bit % 6 < 4) walls.Add(parts[^1]);
                }
            }
        }
        if (tops.Count > 0)
        {
            var corners = walls.Count > 0 ? fit.Corners(walls) : [];
            foreach (var (part, place, clipOnly, learned) in tops)
            {
                var sloped = fit.Slope(part, upOnly: false).Length() >= 0.1f;
                // the fit is noise on clip-only blocks
                var turn = learned ?? (!sloped ? Matrix4x4.Identity : clipOnly ? fit.TopClipTurn(part, rise) : corners.Count > 0 ? fit.FittedTopTurn(part, place, corners) : Matrix4x4.Identity);
                parts.Add((part.Path, part.Transform * turn * place));
            }
        }
        return parts.Count > 0 ? parts : null;
    }

    // vertical clip mobils: air 0 middle, 1 top, 2 bottom, 3 both; ground 0 bottom, 1 both
    private static (CGameCtnBlockInfoVariant? Variant, int Mobil) VerticalMobil(CGameCtnBlockInfo clip, int column)
        => (column & ClipKey.ColumnOnGround) != 0 && clip.VariantBaseGround is { Mobils.Length: > 0 } ground
            ? (ground, column & ClipKey.ColumnTop)
            : (clip.VariantBaseAir, column & (ClipKey.ColumnTop | ClipKey.ColumnBottom));

    public static IEnumerable<(Vector3 UnitOrigin, int Bit, External<CGameCtnBlockInfoClip>[]? Clips, Matrix4x4 Place, Vector3 Probe)> ClipSlots(CGameCtnBlockInfoVariant variant)
    {
        var units = variant.BlockUnitModels ?? [];
        for (int ui = 0; ui < units.Length; ui++)
        {
            var unit = units[ui];
            var unitOrigin = new Vector3(unit.RelativeOffset.X * 32, unit.RelativeOffset.Y * 8, unit.RelativeOffset.Z * 32);
            // clips are authored in the neighbouring unit's space
            var side = Matrix4x4.CreateTranslation(0, 0, -32);
            var sideProbe = new Vector3(16, 4, -1);
            var slots = new (External<CGameCtnBlockInfoClip>[]?, Matrix4x4, Vector3)[]
            {
                (unit.ClipsNorth, side * AroundUnitCenter(MathF.PI), Vector3.Transform(sideProbe, AroundUnitCenter(MathF.PI))),
                (unit.ClipsEast, side * AroundUnitCenter(MathF.PI / 2), Vector3.Transform(sideProbe, AroundUnitCenter(MathF.PI / 2))),
                (unit.ClipsSouth, side, sideProbe),
                (unit.ClipsWest, side * AroundUnitCenter(-MathF.PI / 2), Vector3.Transform(sideProbe, AroundUnitCenter(-MathF.PI / 2))),
                (unit.ClipsBottom, Matrix4x4.CreateTranslation(0, -8, 0), new Vector3(16, -1, 16)),
                (unit.ClipsTop, Matrix4x4.CreateTranslation(0, 8, 0), new Vector3(16, 9, 16)),
            };
            for (int si = 0; si < slots.Length; si++)
                yield return (unitOrigin, ui * slots.Length + si, slots[si].Item1, slots[si].Item2, unitOrigin + slots[si].Item3);
        }
    }

    public static Matrix4x4 AroundUnitCenter(float yaw)
        => Matrix4x4.CreateTranslation(-16, 0, -16) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(16, 0, 16);

    // mobil as in block flags: row in bits 0-5, entry in bits 6-11
    public static (string Path, Matrix4x4 Transform)? MobilPrefab(CGameCtnBlockInfoVariant variant, string ownerPath, int mobil = 0, bool exact = false)
    {
        var mobils = variant.Mobils ?? [];
        var row = mobils.ElementAtOrDefault(mobil & 0x3F);
        var chosen = (row?.ElementAtOrDefault(mobil >> 6) is { PrefabFidFile: not null } entry ? entry : row?.FirstOrDefault(m => m.PrefabFidFile is not null))
            ?? (exact ? null : mobils.SelectMany(m => m).FirstOrDefault(m => m.PrefabFidFile is not null));
        if (chosen is not { PrefabFidFile: { } file } picked) return null;
        var ancestor = Gbx.ParseHeader(ownerPath).RefTable?.AncestorLevel ?? 0;
        // degrees
        const float deg = MathF.PI / 180;
        var transform = picked.HasGeomTransformation
            ? Matrix4x4.CreateFromYawPitchRoll(picked.GeomRotation.Y * deg, picked.GeomRotation.X * deg, picked.GeomRotation.Z * deg)
                * Matrix4x4.CreateTranslation(picked.GeomTranslation.X, picked.GeomTranslation.Y, picked.GeomTranslation.Z)
            : Matrix4x4.Identity;
        return (GameFiles.ResolveRef(ownerPath, ancestor, file.FilePath), transform);
    }

    public static bool IsVertical(External<CGameCtnBlockInfoClip> clip)
        => clip.File?.FilePath.EndsWith(".EDVerticalClip.Gbx", StringComparison.OrdinalIgnoreCase) == true;
}
