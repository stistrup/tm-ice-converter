using System.Numerics;
using GBX.NET;
using GBX.NET.Components;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Meta;
using GBX.NET.Engines.Plug;

namespace TmIceConverter;

// prefabs from the extraction -> mesh pieces
public sealed class MeshExporter(GameFiles files, IReadOnlyDictionary<string, string> materialMap)
{
    public Func<string, bool>? KeepMaterial { get; init; }

    // skin skirt angle from the surface, degrees (90 = straight down)
    public float SkirtAngle { get; init; } = 90;

    public float Lift { get; init; }

    public Vector3 Up { get; init; } = Vector3.UnitY;

    public Func<Vector3, bool>? KeepFace { get; init; }

    // moving parts (flags, pistons) in their rest pose: for viewing only, a block can't move them
    public bool DynaObjects { get; init; }

    // game tree models (VegetTreeModel) as meshes, one LOD: for viewing only
    public bool VegetTrees { get; init; }
    public bool PlatformLayout { get; init; }

    // material for visual faces that touch a border (the road lip under it): null = as usual, "" = left out
    public Func<string, string?>? LipAs { get; init; }

    private Lips? lips;
    private bool collectingBorders;

    private readonly HashSet<MeshPiece> roadLaid = new(ReferenceEqualityComparer.Instance);

    public List<(string Model, Matrix4x4 Place)> Trees(IEnumerable<(string Path, Matrix4x4 Transform)> parts)
    {
        var trees = new List<(string, Matrix4x4)>();
        foreach (var (path, transform) in parts) if (files.LoadPrefab(path) is { } prefab) AddPrefab(prefab, path, transform, null, 0, trees);
        return trees;
    }

    // variant: the placed one (item flags bits 8-12), refs in variant order when GBX.NET can't read the entity
    public List<MeshPiece>? ExportItem(string itemPath, out string? reason, int variant = 0)
    {
        reason = null;
        var item = Gbx.ParseNode<CGameItemModel>(itemPath);
        var ancestor = Gbx.ParseHeader(itemPath).RefTable?.AncestorLevel ?? 0;
        var pieces = new List<MeshPiece>();
        switch (item.EntityModel)
        {
            case NPlugItem_SVariantList { Variants: [{ EntityModelFile: { } first }, ..] variants }:
                var file = variant < variants.Length && variants[variant].EntityModelFile is { } placed ? placed : first;
                return PrefabOrStatic(GameFiles.ResolveRef(itemPath, ancestor, file.FilePath), pieces, out reason);
            case CPlugPrefab prefab:
                AddPrefab(prefab, itemPath, Matrix4x4.Identity, pieces, depth: 0);
                break;
            case CGameCommonItemEntityModel { StaticObject: { } som }:
                AddStaticObject(som, Matrix4x4.Identity, Path.GetFileNameWithoutExtension(itemPath), pieces, itemPath);
                break;
            // entity in a separate file: take it from the ref table
            case null when Gbx.ParseHeader(itemPath).RefTable?.Files
                    .Where(f => f.FilePath.EndsWith(".Prefab.Gbx", StringComparison.OrdinalIgnoreCase) || f.FilePath.EndsWith(".VegetTreeModel.Gbx", StringComparison.OrdinalIgnoreCase))
                    .ToList() is [var firstRef, ..] refs:
                return PrefabOrStatic(GameFiles.ResolveRef(itemPath, ancestor, (variant < refs.Count ? refs[variant] : firstRef).FilePath), pieces, out reason);
            case var other:
                reason = other?.GetType().Name ?? "no entity model";
                return null;
        }
        return pieces;
    }

    private List<MeshPiece>? PrefabOrStatic(string target, List<MeshPiece> pieces, out string? reason)
    {
        reason = null;
        var kind = Path.GetExtension(Path.GetFileNameWithoutExtension(target)).TrimStart('.');
        if (kind is not ("Prefab" or "StaticObject") && !(kind == "VegetTreeModel" && VegetTrees)) { reason = kind; return null; }
        if (!File.Exists(target))
        {
            files.Missing.Add(files.RelativeToGameData(target));
            reason = "not extracted";
            return null;
        }
        if (kind == "VegetTreeModel")
        {
            // a cut extract: the plain cone stays
            if (!AddTree(target, Matrix4x4.Identity, pieces)) { reason = kind; return null; }
        }
        else if (kind == "Prefab") AddPrefab(target, Matrix4x4.Identity, pieces, depth: 0);
        else if (files.LoadStaticObject(target) is { } som) AddStaticObject(som, Matrix4x4.Identity, Path.GetFileNameWithoutExtension(target), pieces, target);
        return pieces;
    }

    public List<MeshPiece> Export(IEnumerable<(string Path, Matrix4x4 Transform)> parts)
    {
        var pieces = new List<MeshPiece>();
        var list = parts.ToList();
        lips = null;
        if (LipAs is not null)
        {
            (lips, collectingBorders) = (new Lips(), true);
            foreach (var (path, transform) in list) AddPrefab(path, transform, pieces, depth: 0);
            collectingBorders = false;
        }
        roadLaid.Clear();
        foreach (var (path, transform) in list) AddPrefab(path, transform, pieces, depth: 0);
        RoadLayout.Apply(pieces, roadLaid);
        return pieces;
    }

    private void AddPrefab(string path, Matrix4x4 transform, List<MeshPiece>? pieces, int depth, List<(string, Matrix4x4)>? trees = null)
    {
        if (files.LoadPrefab(path) is { } prefab) AddPrefab(prefab, path, transform, pieces, depth, trees);
    }

    // pieces null: only collect trees
    private void AddPrefab(CPlugPrefab prefab, string path, Matrix4x4 transform, List<MeshPiece>? pieces, int depth, List<(string, Matrix4x4)>? trees = null)
    {
        if (depth > 6) return;
        var ancestor = Gbx.ParseHeader(path).RefTable?.AncestorLevel ?? 0;
        var label = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));

        for (int i = 0; i < (prefab.Ents?.Length ?? 0); i++)
        {
            var ent = prefab.Ents![i];
            var rot = new Quaternion(ent.Rotation.X, ent.Rotation.Y, ent.Rotation.Z, ent.Rotation.W);
            var local = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(ent.Position.X, ent.Position.Y, ent.Position.Z);
            var world = local * transform;

            switch (ent.Model)
            {
                case CPlugStaticObjectModel som:
                    if (pieces is not null) AddStaticObject(som, world, $"{label}_{i}", pieces, path);
                    break;
                case CPlugDynaObjectModel dyna:
                    if (pieces is not null && DynaObjects) AddDynaObject(dyna, world, $"{label}_{i}", pieces, path);
                    break;
                case CPlugPrefab nested:
                    var nestedPath = ent.ModelFile is { } nf ? GameFiles.ResolveRef(path, ancestor, nf.FilePath) : path;
                    AddPrefab(nested, nestedPath, world, pieces, depth + 1, trees);
                    break;
                case null when ent.ModelFile is { } file:
                    var target = GameFiles.ResolveRef(path, ancestor, file.FilePath);
                    if (target.EndsWith(".Prefab.Gbx", StringComparison.OrdinalIgnoreCase))
                        AddPrefab(target, world, pieces, depth + 1, trees);
                    else if (target.EndsWith(".StaticObject.Gbx", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pieces is not null && files.LoadStaticObject(target) is { } fileSom) AddStaticObject(fileSom, world, $"{label}_{i}", pieces, target);
                    }
                    else if (target.EndsWith(".DynaObject.Gbx", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pieces is not null && DynaObjects && files.LoadDynaObject(target) is { } fileDyna) AddDynaObject(fileDyna, world, $"{label}_{i}", pieces, target);
                    }
                    else if (target.EndsWith(".VegetTreeModel.Gbx", StringComparison.OrdinalIgnoreCase))
                    {
                        trees?.Add((Path.GetFileName(target)[..^".VegetTreeModel.Gbx".Length], world));
                        if (pieces is not null && VegetTrees) AddTree(target, world, pieces);
                    }
                    else if (pieces is not null)
                        files.Warnings.Add($"skipped {file.FilePath} (not a prefab)");
                    break;
                case { } other:
                    if (pieces is not null) files.Warnings.Add($"skipped {label} entity {i}: {other.GetType().Name}");
                    break;
            }
        }
    }

    // the game's far LOD (from 100 m): a forest map has thousands, the full one is ~15x the triangles; false: cut extract
    private bool AddTree(string path, Matrix4x4 world, List<MeshPiece> pieces)
    {
        VegetTree tree;
        try { tree = VegetTree.Read(path); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return false; }
        var lod = tree.Lods[^1];
        var label = Path.GetFileName(path).Split('.')[0];
        foreach (var (part, i) in lod.Select((p, i) => (p, i)))
        {
            if (part.Visual.IndexBuffer?.Indices is not { } idx || VertsOf(part.Visual, idx) is not { } vertices) continue;
            pieces.Add(new MeshPiece($"_notcollidable_{label}_v{i}", VegetMaterialName(path, part.Material), Visible: true, Collidable: false,
                Transform(vertices.Positions, world), vertices.Normals is { } n ? TransformNormals(n, world) : null, FlipV(vertices.Uv0), null, idx));
        }
        return true;
    }

    // tree V runs the other way (checked: leaf cards land on the alpha only flipped)
    private static float[]? FlipV(float[]? uv) => uv?.Select((x, i) => i % 2 == 1 ? 1 - x : x).ToArray();

    public const string VegetPrefix = "veget:";

    private string VegetMaterialName(string treePath, int material) => $"{VegetPrefix}{files.RelativeToGameData(treePath)}#{material}";

    private CPlugSolid2Model? ExternalMesh(GbxRefTableFile? file, string source)
        => file is null ? null : files.LoadMesh(GameFiles.ResolveRef(source, Gbx.ParseHeader(source).RefTable?.AncestorLevel ?? 0, file.FilePath));

    private void AddDynaObject(CPlugDynaObjectModel dyna, Matrix4x4 world, string label, List<MeshPiece> pieces, string sourceFile)
    {
        if ((dyna.Mesh ?? ExternalMesh(dyna.MeshFile, sourceFile)) is { } mesh)
            AddStaticObject(new CPlugStaticObjectModel { Mesh = mesh }, world, label, pieces, sourceFile);
    }

    // sourceFile: the file whose ref table holds som's refs
    private void AddStaticObject(CPlugStaticObjectModel som, Matrix4x4 world, string label, List<MeshPiece> pieces, string sourceFile)
    {
        if (collectingBorders)
        {
            lips!.Collect(som, world);
            return;
        }
        if ((som.Mesh ?? ExternalMesh(som.MeshFile, sourceFile)) is { } mesh)
        {
            // best LOD only
            var geoms = mesh.ShadedGeoms ?? [];
            var hasLods = geoms.Any(g => g.LodMask != 0);
            var bestLod = geoms.Select(g => g.LodMask).Where(m => m != 0).Select(m => m & -m).DefaultIfEmpty(1).Min();
            foreach (var (geom, gi) in geoms.Select((g, gi) => (g, gi)).Where(x => x.g.LodMask == 0 ? !hasLods : (x.g.LodMask & bestLod) != 0))
            {
                if (mesh.Visuals?[geom.VisualIndex] is not CPlugVisualIndexedTriangles vis) continue;
                if (vis.IndexBuffer?.Indices is not { } idx || VertsOf(vis, idx) is not { } vertices) continue;

                var source = MaterialName(mesh, geom.MaterialIndex);
                if (MapMaterial(source) is "") continue;
                if (KeepMaterial is { } keep && !keep(source)) continue;
                var worldPos = Transform(vertices.Positions, world);
                idx = KeptFaces(worldPos, idx);
                var lipAs = LipAs?.Invoke(source);
                int[] lip = [];
                if (lipAs is not null) (idx, lip) = lips!.Split(worldPos, idx);
                if (lip.Length > 0 && lipAs is not "")
                {
                    // the game gives the hidden lip no lightmap space
                    pieces.Add(Lightmaps.Projected(new MeshPiece($"_notcollidable_{label}_v{gi}_lip", lipAs!, Visible: true, Collidable: false,
                        SkinLift.Lifted(worldPos, lip, Lift, Up), vertices.Normals is { } ln ? TransformNormals(ln, world) : null, vertices.Uv0, null, lip)));
                }
                if (idx.Length == 0) continue;
                var positions = SkinLift.Lifted(worldPos, idx, Lift, Up);
                var piece = new MeshPiece(
                    $"_notcollidable_{label}_v{gi}", MapMaterial(source), Visible: true, Collidable: false,
                    positions, vertices.Normals is { } n ? TransformNormals(n, world) : null,
                    SnowLaidOut(source) ? RoadLayout.PlainSnowUv(positions) : PlatformLayout || PlatformLaidOut.Contains(source) ? RoadLayout.PlatformUv(positions) : vertices.Uv0,
                    vertices.Uv1, idx, vertices.Colors);
                if (Lift != 0) piece = SkinLift.WithSkirt(piece, worldPos, Lift / MathF.Tan(SkirtAngle * MathF.PI / 180));
                if (piece.Uv1 is null && !source.StartsWith("Decal")) piece = Lightmaps.Projected(piece);
                if (PlatformLaidOut.Contains(source)) roadLaid.Add(piece);
                pieces.Add(piece);
            }
        }

        if (som.Shape is CPlugSurface { Surf: CPlugSurface.Mesh surf } shape && surf.Vertices is { } verts && surf.Triangles is { } tris)
        {
            var worldVerts = Transform(verts, world);
            var positions = SkinLift.Lifted(worldVerts, KeptFaces(worldVerts, tris.SelectMany(t => new[] { t.Indices.X, t.Indices.Y, t.Indices.Z }).ToArray()), Lift, Up);
            foreach (var group in tris.GroupBy(t => t.SurfaceIndex))
            {
                var name = shape.Materials is { } mats && group.Key < mats.Length
                    ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mats[group.Key].MaterialFile?.FilePath ?? "Unknown"))
                    : "Unknown";
                if (MapMaterial(name) is "" || KeepMaterial is { } keep && !keep(name)) continue;
                var kept = KeptFaces(worldVerts, group.SelectMany(t => new[] { t.Indices.X, t.Indices.Y, t.Indices.Z }).ToArray());
                if (kept.Length == 0) continue;
                var gameplay = group.GroupBy(t => t.U03).MaxBy(g => g.Count())!.Key;
                pieces.Add(new MeshPiece(
                    $"_notvisible_{label}_c{group.Key}" + (gameplay == 0 ? "" : $"_g{(CPlugSurface.GameplayId)gameplay}"), MapMaterial(name), Visible: false, Collidable: true,
                    positions, null, null, null, kept));
            }
        }
    }

    // platform UVs for roads, plain-snow UVs for snow
    private bool SnowLaidOut(string source) => source != "DecoHill" && MapMaterial(source) == "PlatformIce_DecoHill";

    private static readonly HashSet<string> PlatformLaidOut = ["RoadTech", "RoadBump", "RoadDirt", "ThemeSnowRoad"];

    private int[] KeptFaces(float[] positions, int[] indices)
    {
        if (KeepFace is null) return indices;
        Vector3 At(int i) => new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        return Enumerable.Range(0, indices.Length / 3).Select(t => 3 * t)
            .Where(t => Vector3.Cross(At(indices[t + 1]) - At(indices[t]), At(indices[t + 2]) - At(indices[t])) is var n
                && n.LengthSquared() > 0 && KeepFace(Vector3.Normalize(n)))
            .SelectMany(t => indices[t..(t + 3)]).ToArray();
    }

    private string MapMaterial(string name)
    {
        if (materialMap.TryGetValue(name, out var mapped)) return mapped;
        files.Warnings.Add($"material {name} has no mapping; using it as-is");
        return name;
    }

    public static string MaterialName(CPlugSolid2Model mesh, int index)
    {
        if (mesh.Materials is { } mats && index < mats.Length && mats[index] is { } ext && ext.File?.FilePath is { } fp)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fp));
        if (mesh.Materials is { } embedded && index < embedded.Length && embedded[index]?.Node?.ShaderFile?.FilePath is { } shader)
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(shader));
        if (mesh.MaterialIds is { } ids && index < ids.Length) return ids[index];
        return "Unknown";
    }

    private sealed record Verts(Vec3[] Positions, Vec3[]? Normals, float[]? Uv0, float[]? Uv1, float[]? Colors);

    // older meshes keep vertices on the visual; frames past the indexed ones are vertex animation (flags)
    private static Verts? VertsOf(CPlugVisualIndexedTriangles vis, int[] indices)
    {
        if (vis.VertexStreams?.FirstOrDefault() is { Positions: { } pos } stream)
            return new Verts(pos, stream.Normals, Uv(stream, 0), Uv(stream, 1), Colors(stream));
        if (vis.Vertices is not { Length: > 0 } all || indices.Length == 0) return null;
        var count = indices.Max() + 1;
        if (count > all.Length) return null;
        var frame = all[..count];
        var normals = frame.All(v => v.Normal is not null) ? frame.Select(v => v.Normal!.Value).ToArray() : null;
        var colors = frame.All(v => v.Color is not null) ? frame.SelectMany(v => new[] { v.Color!.Value.X, v.Color.Value.Y, v.Color.Value.Z, v.Color.Value.W }).ToArray() : null;
        return new Verts(frame.Select(v => v.Position).ToArray(), normals, LegacyUv(vis, 0, count), LegacyUv(vis, 1, count), colors);
    }

    // V counts up here, down in vertex streams (flag cloth vs its pole, both checked against the game)
    private static float[]? LegacyUv(CPlugVisualIndexedTriangles vis, int set, int count)
        => vis.TexCoords is { } sets && set < sets.Length && sets[set].TexCoords is { } uvs && uvs.Length >= count
            ? uvs[..count].SelectMany(t => new[] { t.UV.X, 1 - t.UV.Y }).ToArray()
            : null;

    private static float[]? Colors(CPlugVertexStream stream)
        => stream.Colors is { } sets && sets.TryGetValue(0, out var c)
            ? c.SelectMany(argb => new[] { (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, (argb >> 24) & 0xFF }.Select(b => b / 255f)).ToArray()
            : null;

    private static float[]? Uv(CPlugVertexStream stream, int set)
        => stream.UVs is { } uvs && uvs.TryGetValue(set, out var uv) ? uv.SelectMany(v => new[] { v.X, v.Y }).ToArray() : null;

    public static float[] Transform(Vec3[] points, Matrix4x4 m)
        => points.SelectMany(p => { var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m); return new[] { v.X, v.Y, v.Z }; }).ToArray();

    private static float[] TransformNormals(Vec3[] normals, Matrix4x4 m)
        => normals.SelectMany(p => { var v = Vector3.Normalize(Vector3.TransformNormal(new Vector3(p.X, p.Y, p.Z), m)); return new[] { v.X, v.Y, v.Z }; }).ToArray();
}
