using System.Numerics;
using GBX.NET;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

internal static class RampPass
{
    // skins sit above the native surface: a custom block sharing a cell face with one ramps its ice up at that side
    public static void Run(ConversionRun run)
    {
        var skinCells = run.Skinned.Where(run.Cells.ContainsKey).SelectMany(b => run.Cells[b]).ToHashSet();
        if (skinCells.Count == 0) return;
        Int3[] steps = [new(-1, 0, 0), new(1, 0, 0), new(0, 0, -1), new(0, 0, 1)];
        foreach (var block in run.Customs.Keys.Where(b => run.BuiltAs.ContainsKey(b) && run.Cells.ContainsKey(b)).ToList())
        {
            var own = run.Cells[block].ToHashSet();
            var size = Placement.Footprint(run.Shapes.Units(block.Name, block.IsGround, VariantOf(block)));
            Matrix4x4.Invert(Placement.Of(block, size), out var toLocal);
            var sides = 0;
            foreach (var c in own)
                foreach (var step in steps)
                {
                    var next = new Int3(c.X + step.X, c.Y, c.Z + step.Z);
                    if (own.Contains(next) || !skinCells.Contains(next)) continue;
                    // face middle in the block's frame → nearest footprint side
                    var p = Vector3.Transform(new Vector3(c.X * 32 + 16 + step.X * 16, (c.Y - 8) * 8, c.Z * 32 + 16 + step.Z * 16), toLocal);
                    float[] d = [MathF.Abs(p.X), MathF.Abs(size.X * 32 - p.X), MathF.Abs(p.Z), MathF.Abs(size.Z * 32 - p.Z)];
                    sides |= 1 << Array.IndexOf(d, d.Min());
                }
            if (sides == 0) continue;
            var (archetype, baked, lift) = run.BuiltAs[block];
            if (run.Items.BlockFor(block.Name, archetype, block.IsGround, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, baked, lift: lift, ramp: sides) is not { } ramped) continue;
            run.Customs[block] = ramped;
            Bump(run.Report.Ramped, block.Name);
        }
    }
}
