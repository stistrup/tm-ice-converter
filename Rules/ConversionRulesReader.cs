using System.Text.Json;

namespace TmSurfaceEditor;

// swaps.jsonc and a map's own settings part -> ConversionRules
internal static class ConversionRulesReader
{
    public static ConversionRules FromFiles(params string[] paths)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var result = new ConversionRules();
        foreach (var path in paths)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
            var part = Parse(doc.RootElement);
            foreach (var (k, v) in part.Blocks) result.Blocks[k] = v;
            result.IgnorePrefixes.AddRange(part.IgnorePrefixes);
            foreach (var (k, v) in part.UnavailableIn) result.UnavailableIn[k] = v;
            foreach (var (k, v) in part.Items) result.Items[k] = v;
            foreach (var (k, v) in part.ItemsOnCover) result.ItemsOnCover[k] = v;
            result.UntouchedItems.AddRange(part.UntouchedItems);
            result.Settings.AddRange(part.Settings);
            result.Keep.UnionWith(part.Keep);
            result.KeepItems.UnionWith(part.KeepItems);
            result.RemoveItems.UnionWith(part.RemoveItems);
        }
        return result;
    }

    // problems null: the first bad field throws (data files); else each bad field or entry is skipped and noted
    public static ConversionRules Parse(JsonElement root, List<string>? problems = null)
    {
        var result = new ConversionRules();
        void Read(string what, Action read)
        {
            if (problems is null) { read(); return; }
            try { read(); }
            catch (FormatException e) { problems.Add($"{what}: {e.Message}"); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
            {
                problems.Add($"{what}: wrong type");
            }
        }
        IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new FormatException("not a list");
        IEnumerable<JsonProperty> Object(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : throw new FormatException("not an object");
        string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FormatException($"{e} is not text");

        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("settings must be an object");

        if (root.TryGetProperty("name", out var newName)) Read("name", () => result.Name = Text(newName));
        if (root.TryGetProperty("rules", out var rulesOn)) Read("rules", () => result.RulesOn = rulesOn.GetBoolean());

        if (root.TryGetProperty("options", out var options))
            Read("options", () =>
            {
                foreach (var option in Object(options))
                    Read($"option {option.Name}", () =>
                    {
                        if (!new ConvertOptions().All().Any(o => o.Name == option.Name)) throw new FormatException("unknown option");
                        if (option.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"{option.Value} is not true/false");
                        result.Settings.Add((option.Name, option.Value.GetBoolean()));
                    });
            });

        foreach (var (key, set, normal) in new[] { ("keep", result.Keep, true), ("keepItems", result.KeepItems, false), ("removeItems", result.RemoveItems, false) })
            if (root.TryGetProperty(key, out var list))
                Read(key, () =>
                {
                    foreach (var entry in Array(list))
                        Read($"{key} entry", () => set.Add(normal ? ConversionRules.NormalizeKeep(Text(entry)) : Text(entry).Trim()));
                });
        if (root.TryGetProperty("pieces", out var pieces))
            Read("pieces", () =>
            {
                foreach (var piece in Object(pieces))
                    Read($"piece {piece.Name}", () => result.Pieces[ConversionRules.NormalizeKeep(piece.Name)] = PieceChoice(piece.Value));
            });
        if (root.TryGetProperty("skinLiftCm", out var lift)) Read("skinLiftCm", () => result.SkinLift = lift.GetSingle() / 100);
        if (root.TryGetProperty("skinEdgeDeg", out var edge)) Read("skinEdgeDeg", () => result.SkinEdge = edge.GetSingle());
        foreach (var (key, areas) in new[] { ("snowTerrain", result.CoverAreas), ("skinned", result.Skinned) })
            if (root.TryGetProperty(key, out var list))
                Read(key, () =>
                {
                    foreach (var area in Array(list))
                        Read($"{key} area", () =>
                        {
                            if (Array(area).Select(e => e.GetInt32()).ToArray() is not [var x0, var z0, var x1, var z1])
                                throw new FormatException($"{area} is not [x0, z0, x1, z1]");
                            areas.Add((Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1)));
                        });
                });

        if (root.TryGetProperty("ignorePrefixes", out var ignore))
            Read("ignorePrefixes", () => result.IgnorePrefixes.AddRange(Array(ignore).Select(Text)));

        if (root.TryGetProperty("untouchedItems", out var untouched))
            Read("untouchedItems", () => result.UntouchedItems.AddRange(Array(untouched).Select(Text)));

        if (root.TryGetProperty("unavailableIn", out var unavailable))
            Read("unavailableIn", () =>
            {
                foreach (var env in Object(unavailable))
                    result.UnavailableIn[env.Name] = Array(env.Value).Select(Text).ToList();
            });

        foreach (var (name, table) in new[] { ("items", result.Items), ("itemsOnSnow", result.ItemsOnCover) })
            if (root.TryGetProperty(name, out var itemTable))
                Read(name, () =>
                {
                    foreach (var entry in Object(itemTable))
                        Read($"{name} {entry.Name}", () => table[entry.Name] = ItemTarget(entry));
                });

        if (root.TryGetProperty("blocks", out var blocks))
            Read("blocks", () =>
            {
                foreach (var entry in Object(blocks))
                    Read($"block {entry.Name}", () => result.Blocks[entry.Name] = ParseEntry(entry.Name, entry.Value));
            });
        return result;
    }

    private static string PieceChoice(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || !ConversionRules.PieceChoices.Contains(value.GetString())) throw new FormatException($"unknown choice {value}");
        return value.GetString()!;
    }

    private static string ItemTarget(JsonProperty entry)
        => entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } to
            ? to : throw new FormatException($"item '{entry.Name}' needs a target item id");

    private static Translation ParseEntry(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new Translation(null),
        JsonValueKind.String when value.GetString() is { Length: > 0 } to => new Translation(to),
        JsonValueKind.String => throw new FormatException($"'{name}' has an empty target; use null to keep the block"),
        JsonValueKind.Object => new Translation(
            value.TryGetProperty("to", out var to) ? to.GetString() : name,
            value.TryGetProperty("rotate", out var rot) ? rot.GetInt32() : 0,
            value.TryGetProperty("offset", out var off) ? off.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null,
            value.TryGetProperty("variant", out var v) ? v.GetByte() : null,
            value.TryGetProperty("subVariant", out var sv) ? sv.GetByte() : null),
        _ => throw new FormatException($"'{name}' must be a string, null or an object"),
    };
}
