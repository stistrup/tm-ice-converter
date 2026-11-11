using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmIceConverter;

// "flip" / "snowflip" picks: the block turned 180° on its own footprint; a reactor entered from the other side gives the other effect.
// After the turn snowflip is a snow pick, and a free block's pick moves to its new spot
internal static class Flips
{
    public static void Run(CGameCtnChallenge map, BlockCatalog shapes, Translations rules)
    {
        foreach (var block in map.GetBlocks())
        {
            var choice = rules.PieceChoice(block);
            if (choice is not ("flip" or "snowflip")) continue;
            var after = choice == "snowflip" ? "snow" : choice;
            // same cells after a half turn, so the coord stays
            if (!block.IsFree)
            {
                block.Direction = (Direction)(((int)block.Direction + 2) % 4);
                rules.Pieces[Translations.KeepKey(block.Name, block.Coord.X, block.Coord.Y, block.Coord.Z)] = after;
                continue;
            }
            if (block.YawPitchRoll is not { } r || block.AbsolutePositionInMap is not { } p) continue;
            var (sx, sz) = shapes.BlockSize(block.Name, false, BlockFlags.VariantOf(block));
            var turn = Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z);
            var shift = Vector3.Transform(new Vector3(sx * 32, 0, sz * 32), turn);
            var w = Matrix4x4.CreateRotationY(MathF.PI) * turn;
            var (pitch, yaw, roll) = (MathF.Asin(Math.Clamp(-w.M32, -1, 1)), MathF.Atan2(w.M31, w.M33), MathF.Atan2(w.M12, w.M22));
            foreach (var key in rules.Pieces.Keys.Where(k => Translations.IsAt(k, block.Name, new Vector3(p.X, p.Y, p.Z)) == true).ToList()) rules.Pieces.Remove(key);
            block.AbsolutePositionInMap = new Vec3(p.X + shift.X, p.Y + shift.Y, p.Z + shift.Z);
            block.YawPitchRoll = new Vec3(yaw, pitch, roll);
            var at = block.AbsolutePositionInMap.Value;
            rules.Pieces[FormattableString.Invariant($"{block.Name} {at.X:0.###},{at.Y:0.###},{at.Z:0.###}")] = after;
        }
    }
}
