using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmIceConverter.Counts;

namespace TmIceConverter;

internal static class ItemSwaps
{
    public static void Run(ConversionRun run, SnowSurfaces snow)
    {
        var icedAt = new Dictionary<(float, float, float), int>();
        var removed = new List<CGameCtnAnchoredObject>();
        foreach (var item in run.Map.GetAnchoredObjects())
        {
            if (item.ItemModel is not { Author: "Nadeo" } model) continue;
            var at = new Vector3(item.AbsolutePositionInMap.X, item.AbsolutePositionInMap.Y, item.AbsolutePositionInMap.Z);
            var picked = run.Rules.ChoiceAt(model.Id, at);
            if (picked == "remove" || picked is null && run.Rules.RemovesItem(model.Id, at))
            { removed.Add(item); Bump(run.Report.RemovedByList, model.Id); continue; }
            if (picked == "keep" || picked is null && run.Rules.KeepsItem(model.Id)) { Bump(run.Report.KeptByList, model.Id); continue; }
            var p = item.AbsolutePositionInMap;
            // "<Env>:<Item>" beats the bare id
            bool Find(Dictionary<string, string> table, out string to)
                => table.TryGetValue($"{run.Map.Collection}:{model.Id}", out to!) || table.TryGetValue(model.Id, out to!);
            // the swap tables are all vegetation
            // frozen: swapped whatever the options and the snow around say
            var (trees, frozen) = (run.Rules.Options.ConvertTrees || picked == "frozen", picked == "frozen");
            var to = "";
            var always = trees && Find(run.Translations.Items, out to);
            if ((always || trees && Find(run.Translations.ItemsOnSnow, out to)) && VariantSwap(run, item, to) is var (target, variant)
                && (always || frozen || snow.Touches(new Vector3(p.X, p.Y, p.Z), TreeHeight(target))))
            {
                item.ItemModel = model with { Id = target, Collection = new Id(26) }; // Stadium2020
                item.Flags = (short)(item.Flags & ~VariantBits | variant << 8);
                Bump(run.Report.SwappedItems, (model.Id, target));
            }
            // stacked copies go up 1 cm each
            else if (run.Items.ForItem(model.Id) is { } ice)
            {
                var spot = (MathF.Round(p.X, 2), MathF.Round(p.Y, 2), MathF.Round(p.Z, 2));
                var stacked = icedAt.GetValueOrDefault(spot);
                icedAt[spot] = stacked + 1;
                if (stacked > 0) item.AbsolutePositionInMap = p with { Y = p.Y + 0.01f * stacked };
                item.ItemModel = ice.Ident;
                run.Report.UsedItems.Add(ice);
                Bump(run.Report.IcedItems, model.Id);
            }
            else if (run.Items.Failures.ContainsKey(model.Id)) Bump(run.Report.ItemsNotIced, model.Id);
            else if (snow.Under(new Vector3(p.X, p.Y, p.Z)) && FlushWithIce(run, item, snow))
            {
                item.AbsolutePositionInMap = p with { Y = p.Y - FlushSink };
                Bump(run.Report.Sunk, model.Id);
            }
        }
        foreach (var item in removed) run.Map.AnchoredObjects!.Remove(item);
    }

    // placed item's variant index in its variant list
    private const int VariantBits = 0x1F00;

    // a variant list swaps per variant: by the variant's own model, clusters to the target's cluster
    private static (string Target, int Variant)? VariantSwap(ConversionRun run, CGameCtnAnchoredObject item, string to)
    {
        if (run.Shapes.ItemVariants(item.ItemModel.Id) is not { Count: > 1 } from) return (to, 0);
        var index = (item.Flags & VariantBits) >> 8;
        if (index >= from.Count) return null;
        var own = from[index];
        if (run.Translations.Items.TryGetValue($"{run.Map.Collection}:{own}", out var t) || run.Translations.Items.TryGetValue(own, out t)
            || run.Translations.ItemsOnSnow.TryGetValue($"{run.Map.Collection}:{own}", out t) || run.Translations.ItemsOnSnow.TryGetValue(own, out t))
            return (t, 0);
        if (!own.EndsWith(".Prefab") || run.Shapes.ItemVariants(to) is not { } into) return null;
        var cluster = into.FindIndex(v => v.EndsWith(".Prefab") && ClusterLetter(v) == ClusterLetter(own));
        return cluster < 0 ? null : (to, cluster);
    }

    // Fall.Prefab, FallB.Prefab, FallC.Prefab
    private static string ClusterLetter(string prefab) => prefab[..^".Prefab".Length] is var s && char.IsUpper(s[^1]) ? s[^1..] : "";

    private static float TreeHeight(string frozen) => frozen switch
    {
        "FrozenTreeVerySmall" => 3,
        "FrozenTreeSmall" => 6,
        "FrozenTreeMedium" => 10,
        "FrozenTreeBig" => 15,
        "FrozenTreeTall" => 20,
        "WinterFrozenTree" => 12,
        _ => 6,
    };

    // flat top flush with ice flickers: sink it
    private const float FlushSink = 0.02f;

    private static bool FlushWithIce(ConversionRun run, CGameCtnAnchoredObject item, SnowSurfaces snow)
    {
        var id = item.ItemModel.Id;
        var key = $"{run.Shapes.Environment}:{id}";
        if (!run.FlatTops.TryGetValue(key, out var tops))
        {
            tops = [];
            if (run.Shapes.ItemFile(id) is { } file)
                try
                {
                    foreach (var piece in run.Shapes.Plain.ExportItem(file, out _)?.Where(p => p.Visible) ?? [])
                        for (int t = 0; t + 2 < piece.Indices.Length; t += 3)
                        {
                            Vector3 V(int i) => new(piece.Positions[3 * i], piece.Positions[3 * i + 1], piece.Positions[3 * i + 2]);
                            var (a, b, c) = (V(piece.Indices[t]), V(piece.Indices[t + 1]), V(piece.Indices[t + 2]));
                            var n = Vector3.Cross(b - a, c - a);
                            if (n.Length() > 0.01f && n.Y / n.Length() > 0.99f) tops.Add((a, b, c));
                        }
                }
                catch (Exception) { }
            run.FlatTops[key] = tops;
        }
        if (tops.Count == 0) return false;
        var place = Placement.Of(item);
        var area = 0f;
        foreach (var (a, b, c) in tops)
        {
            var (wa, wb, wc) = (Vector3.Transform(a, place), Vector3.Transform(b, place), Vector3.Transform(c, place));
            if (snow.Flush((wa + wb + wc) / 3)) area += Vector3.Cross(wb - wa, wc - wa).Length() / 2;
        }
        return area >= 1;
    }
}
