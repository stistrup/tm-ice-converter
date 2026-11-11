using System.Text.RegularExpressions;
using GBX.NET;

namespace TmIceConverter;

// gameplay blocks: the effect's pad and its look
internal static class Pads
{
    public static bool IsPadLook(string material)
        => material.StartsWith("SpecialFX") || material.StartsWith("DecalSpecial") || material.StartsWith("SpecialSign") || material.StartsWith("TriggerFX");

    // leave the pad uncovered but keep it icy: its own collision, lifted, keeps its gameplay
    public static List<MeshPiece> AroundPad(List<MeshPiece> skin, List<MeshPiece> all, string? gameplay, float skinLift)
    {
        var pads = all.Where(p => !p.Visible && PadGameplay(p.Name) is not null).ToList();
        var glow = all.Where(p => p.Material.StartsWith("SpecialFX")).SelectMany(p => p.Indices.Select(i => (X: p.Positions[3 * i], Z: p.Positions[3 * i + 2])));
        var area = glow.Concat(pads.SelectMany(p => p.Indices.Select(i => (X: p.Positions[3 * i], Z: p.Positions[3 * i + 2])))).ToList();
        if (area.Count == 0) return skin;
        var (x0, x1, z0, z1) = (area.Min(v => v.X), area.Max(v => v.X), area.Min(v => v.Z), area.Max(v => v.Z));
        bool UnderPad(MeshPiece p, int t)
        {
            var xs = Enumerable.Range(0, 3).Select(k => p.Positions[3 * p.Indices[t + k]]).ToArray();
            var zs = Enumerable.Range(0, 3).Select(k => p.Positions[3 * p.Indices[t + k] + 2]).ToArray();
            return xs.Max() > x0 + 0.01f && xs.Min() < x1 - 0.01f && zs.Max() > z0 + 0.01f && zs.Min() < z1 - 0.01f;
        }
        var around = skin.Select(p => PieceOps.KeepTriangles(p, t => !UnderPad(p, t)))
            .Where(p => p.Indices.Length > 0 && (!p.Visible || HasFloor(p))).ToList();
        around.AddRange(pads.Select((p, i) => p with
        {
            Name = $"_notvisible_pad{i}_g{gameplay ?? PadGameplay(p.Name)}", Material = PadMaterial,
            Positions = p.Positions.Select((v, k) => k % 3 == 1 ? v + skinLift : v).ToArray(),
        }));
        return around;
    }

    // skirts alone have no lightmap area, which the importer rejects
    private static bool HasFloor(MeshPiece p)
    {
        if (p.Uv1 is not { } uv) return true;
        float Area(int t)
        {
            var (a, b, c) = (p.Indices[t], p.Indices[t + 1], p.Indices[t + 2]);
            return MathF.Abs((uv[2 * b] - uv[2 * a]) * (uv[2 * c + 1] - uv[2 * a + 1]) - (uv[2 * c] - uv[2 * a]) * (uv[2 * b + 1] - uv[2 * a + 1]));
        }
        return Enumerable.Range(0, p.Indices.Length / 3).Any(i => Area(3 * i) > 1e-7f);
    }

    // full item: the pad's collision as ice with the block's effect
    public static List<MeshPiece> IcyPads(List<MeshPiece> pieces, string? gameplay)
        => pieces.Select((p, i) => p.Visible || PadGameplay(p.Name) is not { } own ? p
            : p with { Name = $"_notvisible_pad{i}_g{gameplay ?? own}", Material = PadMaterial }).ToList();

    // special blocks share the turbo look; the effect's modifier swaps it
    public static Dictionary<string, string> EffectLook(string effect)
    {
        string Lib(string name, string fallback) => Regex.IsMatch(MaterialLib.Text, $@"DMaterial\({Regex.Escape(name)}\)") ? name : fallback;
        return new()
        {
            ["SpecialFXTurbo"] = Lib($"{effect}_SpecialFX", "SpecialFXTurbo"),
            ["DecalSpecialTurbo"] = Lib($"{effect}_Decal", "DecalSpecialTurbo"),
            ["SpecialSignTurbo"] = Lib($"{effect}_Sign", "SpecialSignTurbo"),
            ["SpecialSignOff"] = Lib($"{effect}_SignOff", "SpecialSignOff"),
            ["TriggerFXTurbo"] = Lib($"{effect}_TriggerFX", ""),
        };
    }

    // the effect's ice collision material says which gameplay; Turbo has none (the mesh's own)
    public static string? PadGameplayOf(string block)
    {
        if (BlockNames.Effect(block) is not { } effect) return null;
        var path = Path.Combine(Paths.ExtractedStadium, "Media", "Modifier", effect, "CollisionRoadIce.Material.Gbx");
        return File.Exists(path) && Gbx.ParseNode<GBX.NET.Engines.Plug.CPlugMaterial>(path) is { GameplayId: { } g } ? g.ToString() : null;
    }

    public static string? PadGameplay(string piece) => Regex.Match(piece, @"_g([A-Za-z0-9]+)$") is { Success: true } m ? m.Groups[1].Value : null;

    // own surface so the fix-up can tell it apart
    private const string PadMaterial = "CustomIce";
}
