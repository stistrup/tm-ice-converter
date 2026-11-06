using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.BlockFlags;
using static TmIceConverter.Counts;

namespace TmIceConverter;

// engine-off blocks and effects moved onto gates
internal static class Gates
{
    // engine-off -> reset or plain block; engine-off gates go
    public static bool EngineOff(CGameCtnBlock block, ConversionRun run)
    {
        var (name, options) = (block.Name, run.Rules.Options);
        if (!(options.EngineOffToReset || options.EngineOffToPlain) || !name.Contains("NoEngine")) return false;
        if (name.StartsWith("Gate"))
        {
            run.EngineOffGates.Add(block);
            Bump(run.Report.EngineOff, (name, ""));
            return true;
        }
        if (options.EngineOffToPlain && PlainOf(name, run) is { } plain)
        {
            Bump(run.Report.EngineOff, (name, plain));
            block.Name = plain;
        }
        else if (name.Replace("NoEngine", "Reset") is var reset && run.KnownBlocks.Contains(reset))
        {
            Bump(run.Report.EngineOff, (name, reset));
            block.Name = reset;
        }
        return false;
    }

    // the block without its effect: RoadTechSpecialTurbo -> RoadTechStraight
    public static string? PlainOf(string name, ConversionRun run)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"Special(TurboRoulette|Turbo2|Turbo|Boost2|Boost|NoEngine|NoSteering|NoBrake|SlowMotion|Fragile|Reset|Cruise)", "");
        return stripped == name ? null : new[] { stripped, stripped + "Base", stripped + "Straight" }.FirstOrDefault(run.KnownBlocks.Contains);
    }

    public static void RemoveEngineOffItems(ConversionRun run)
    {
        if (!run.Rules.Options.EngineOffToReset && !run.Rules.Options.EngineOffToPlain) return;
        foreach (var gate in run.Map.GetAnchoredObjects().Where(o => o.ItemModel?.Id is { } id && id.StartsWith("Gate") && id.Contains("NoEngine")).ToList())
        {
            run.Map.RemoveAnchoredObject(gate);
            Bump(run.Report.EngineOff, (gate.ItemModel!.Id, ""));
        }
    }

    // keep for the effect, ice around the pad
    public static bool EffectOnGate(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.Rules.Options.EffectsToGate || GateEffect(block.Name) is not { } effect || block.IsFree || !GateOn(run, block, effect, run.Variant(block), run.Clips(block))) return false;
        run.Replaced.Add(block);
        return true;
    }

    private static string? GateEffect(string name) => name.Contains("SlowMotion") ? "SlowMotion" : name.Contains("Reset") ? "Reset" : null;

    private static bool GateOn(ConversionRun run, CGameCtnBlock block, string effect, int variant, IReadOnlySet<int> clips)
    {
        // flat road only
        var middle = new Vector2(16, 16);
        float? surface = null;
        foreach (var (a, b, c, _) in run.Shapes.CollisionTriangles(block.Name, block.IsGround, variant, MobilOf(block)) ?? [])
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (MathF.Abs(n.Y) < 0.99f) continue;
            var d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (MathF.Abs(d) < 1e-6f) continue;
            var u = ((middle.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (middle.Y - a.Z)) / d;
            var v = ((b.X - a.X) * (middle.Y - a.Z) - (middle.X - a.X) * (b.Z - a.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var h = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            surface = surface is { } s ? MathF.Max(s, h) : h;
        }
        if (surface is not { } y) return false;
        if (!ItemPlacer.Place(run, block, variant, clips, ItemKind.Gated)) return false;
        var units = run.Shapes.Units(block.Name, block.IsGround, variant) ?? [new Int3(0, 0, 0)];
        var at = Vector3.Transform(new Vector3(middle.X, y, middle.Y), Placement.Of(block, (units.Max(u => u.X) + 1, units.Max(u => u.Z) + 1)))
            - new Vector3(0, Placement.EnvironmentDrop(run.Map.Collection?.ToString()), 0);
        run.Map.PlaceAnchoredObject(new Ident("GateSpecial32m" + effect, new Id(26), "Nadeo"), new Vec3(at.X, at.Y, at.Z), new Vec3(-(int)block.Direction * MathF.PI / 2, 0, 0), default);
        Bump(run.Report.Gated, block.Name);
        return true;
    }
}
