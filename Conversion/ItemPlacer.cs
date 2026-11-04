using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;

namespace TmIceConverter;

internal static class ItemPlacer
{
    public static bool Place(ConversionRun run, CGameCtnBlock block, int variant, IReadOnlySet<int> clips, ItemKind kind = ItemKind.None)
    {
        if (run.Items.For(block.Name, block.IsGround, variant, MobilOf(block), clips, kind, Placement.Tilt(block)) is not { } item) return false;
        if (item == IceItem.Nothing) return kind is not (ItemKind.Top or ItemKind.TopOfIce) && !BlockNames.IsGameplayBlock(block.Name);
        Vector3 pos;
        Vec3 rotation;
        if (block.IsFree)
        {
            var p = block.AbsolutePositionInMap ?? default;
            // tilt is baked into the mesh
            (pos, rotation) = (new Vector3(p.X, p.Y, p.Z), Placement.Tilt(block) == default ? block.YawPitchRoll ?? default : new Vec3(block.YawPitchRoll!.Value.X, 0, 0));
        }
        else
        {
            pos = Placement.Of(block, item.Size).Translation - new Vector3(0, Placement.EnvironmentDrop(run.Map.Collection?.ToString()), 0);
            rotation = new Vec3(-(int)block.Direction * MathF.PI / 2, 0, 0);
        }
        // ghost blocks: skip duplicates
        if (run.Map.GetAnchoredObjects().Any(o => o.ItemModel?.Id == item.Ident.Id && o.YawPitchRoll == rotation
                && Vector3.Distance(new Vector3(o.AbsolutePositionInMap.X, o.AbsolutePositionInMap.Y, o.AbsolutePositionInMap.Z), pos) < 0.01f))
            return true;
        var placed = run.Map.PlaceAnchoredObject(item.Ident, new Vec3(pos.X, pos.Y, pos.Z), rotation, default);
        if (!block.IsGround && kind == ItemKind.None) placed.WaypointSpecialProperty = block.WaypointSpecialProperty;
        placed.Color = block.Color;
        run.Report.UsedItems.Add(item);
        return true;
    }
}
