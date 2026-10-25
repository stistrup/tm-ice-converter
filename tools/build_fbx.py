"""FBX for NadeoImporter.

blender --background --factory-startup --python build_fbx.py -- <pieces.json> <out.fbx>
"""
import json
import sys

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
pieces_path, out_path = args[0], args[1]
lightmap_only = set(filter(None, args[2].split(","))) if len(args) > 2 else set()

bpy.ops.wm.read_factory_settings(use_empty=True)

with open(pieces_path) as f:
    pieces = json.load(f)

materials = {}


def tri_area(a, b, c):
    u = [b[i] - a[i] for i in range(len(a))]
    v = [c[i] - a[i] for i in range(len(a))]
    if len(a) == 2:
        return abs(u[0] * v[1] - u[1] * v[0]) / 2
    cx, cy, cz = u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]
    return (cx * cx + cy * cy + cz * cz) ** 0.5 / 2


# margins sized for low-density maps, so edges don't bleed (dark seams)
TEXELS_PER_METRE = 0.5
MARGIN_TEXELS = 1.0
MIN_GROUP_METRES = 2.0
MAX_BOOST = 3.0
GROUP_GAP = 0.004


def uv_groups(p):
    uv, idx = p["uv1"], p["indices"]
    pos = triples(p["positions"])
    n = len(uv) // 2
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        parent[find(a)] = find(b)

    tris = triples(idx)
    for a, b, c in tris:
        union(a, b)
        union(a, c)
    islands = {}
    for v in set(idx):
        islands.setdefault(find(v), []).append(v)
    boxes = {k: (min(uv[2 * v] for v in vs), min(uv[2 * v + 1] for v in vs),
                 max(uv[2 * v] for v in vs), max(uv[2 * v + 1] for v in vs)) for k, vs in islands.items()}
    keys = list(boxes)
    for i, a in enumerate(keys):
        for b in keys[i + 1:]:
            A, B = boxes[a], boxes[b]
            if A[0] - GROUP_GAP <= B[2] and B[0] - GROUP_GAP <= A[2] and A[1] - GROUP_GAP <= B[3] and B[1] - GROUP_GAP <= A[3]:
                union(islands[a][0], islands[b][0])
    groups = {}
    for v in set(idx):
        groups.setdefault(find(v), []).append(v)
    areas = {k: [0.0, 0.0] for k in groups}
    for a, b, c in tris:
        area = areas[find(a)]
        area[0] += tri_area(pos[a], pos[b], pos[c])
        area[1] += tri_area(uv[2 * a:2 * a + 2], uv[2 * b:2 * b + 2], uv[2 * c:2 * c + 2])
    out = []
    for k, vs in groups.items():
        box = (min(uv[2 * v] for v in vs), min(uv[2 * v + 1] for v in vs),
               max(uv[2 * v] for v in vs), max(uv[2 * v + 1] for v in vs))
        out.append((vs, box, areas[k][0], areas[k][1]))
    return out


def pack_lightmaps(pieces):
    """Pack all lightmap UVs into one atlas."""
    rects = []
    world_total = 0.0
    for p in pieces:
        # decals: no lightmap UVs of their own
        uv = p.get("uv1") or (None if p["name"].startswith("_notvisible_") else p.get("uv0"))
        if not uv:
            continue
        p["uv1"] = list(uv)
        for vs, box, world, uv_area in uv_groups(p):
            if uv_area == 0 or world == 0:
                continue
            density = (world / uv_area) ** 0.5
            w, h = (box[2] - box[0]) * density, (box[3] - box[1]) * density
            boost = min(MAX_BOOST, max(1.0, MIN_GROUP_METRES / max(min(w, h), 1e-6)))
            world_total += world
            rects.append({"piece": p, "verts": vs, "box": box, "scale": density * boost,
                          "w": max(w, h) * boost, "h": min(w, h) * boost, "turn": h > w,
                          "uv_area": uv_area * (density * boost) ** 2})

    def margin_at(k):
        used = sum(r["uv_area"] for r in rects) * k * k
        side = (world_total / used) ** 0.5 * TEXELS_PER_METRE if used > 0 else 1
        return MARGIN_TEXELS / max(side, 1)

    def place(k):
        """skyline packing"""
        margin = margin_at(k)
        skyline = [(0.0, margin, 1.0)]
        out = []
        for r in sorted(rects, key=lambda r: -r["w"] * r["h"]):
            best = None
            for turn in (False, True):
                w, h = (r["h"], r["w"]) if turn else (r["w"], r["h"])
                w, h = w * k + margin, h * k + margin
                for i, (x, _, _) in enumerate(skyline):
                    x += margin if x == 0 else 0
                    if x + w > 1:
                        break
                    top, j, end = 0.0, i, x + w
                    while j < len(skyline) and skyline[j][0] < end - 1e-12:
                        top = max(top, skyline[j][1])
                        j += 1
                    if top + h <= 1 and (best is None or (top + h, x) < (best[0], best[1])):
                        best = (top + h, x, top, w, turn)
            if best is None:
                return None
            bottom_top, x, y, w, turn = best
            out.append((r, x, y, turn))
            new_line, end = [], x + w
            for sx, sy, sw in skyline:
                for a0, a1 in ((sx, min(sx + sw, x)), (max(sx, end), sx + sw)):
                    if a1 > a0 + 1e-12:
                        new_line.append((a0, sy, a1 - a0))
            new_line.append((x, bottom_top, w))
            new_line.sort()
            merged = []
            for seg in new_line:
                if merged and abs(merged[-1][1] - seg[1]) < 1e-12 and abs(merged[-1][0] + merged[-1][2] - seg[0]) < 1e-9:
                    merged[-1] = (merged[-1][0], seg[1], merged[-1][2] + seg[2])
                else:
                    merged.append(seg)
            skyline = merged
        return out

    if not rects:
        return
    lo_k, hi_k = 0.0, 1e-3
    while place(hi_k):
        hi_k *= 2
    for _ in range(40):
        mid = (lo_k + hi_k) / 2
        lo_k, hi_k = (mid, hi_k) if place(mid) else (lo_k, mid)
    placed = place(lo_k) or []
    moved = {}
    for r, x, y, turn in placed:
        p, (u0, v0, _, _) = r["piece"], r["box"]
        s = r["scale"] * lo_k
        uv = p["uv1"]
        turn = turn != r["turn"]
        for v in r["verts"]:
            du, dv = (uv[2 * v] - u0) * s, (uv[2 * v + 1] - v0) * s
            uv[2 * v], uv[2 * v + 1] = (x + dv, y + du) if turn else (x + du, y + dv)
        moved.setdefault(id(p), set()).update(r["verts"])
    # the importer counts (0,0) of UV-less pieces: park them in a corner
    if placed:
        corner = (min(x for _, x, _, _ in placed), min(y for _, _, y, _ in placed))
        for p in pieces:
            n = len(p["positions"]) // 3
            if not p.get("uv1"):
                p["uv1"] = list(corner) * n
            else:
                done = moved.get(id(p), set())
                for v in range(n):
                    if v not in done:
                        p["uv1"][2 * v], p["uv1"][2 * v + 1] = corner


def material(name):
    if name not in materials:
        materials[name] = bpy.data.materials.new(name)
    return materials[name]


def triples(flat):
    return [tuple(flat[i:i + 3]) for i in range(0, len(flat), 3)]


pack_lightmaps(pieces)

for piece in pieces:
    verts = [(x, -z, y) for x, y, z in triples(piece["positions"])]
    faces = triples(piece["indices"])
    # the importer reads a socket's object location
    origin = (0.0, 0.0, 0.0)
    if piece["name"].startswith("_socket_"):
        origin = verts[0]
        verts = [tuple(v[i] - origin[i] for i in range(3)) for v in verts]

    mesh = bpy.data.meshes.new(piece["name"])
    mesh.from_pydata(verts, [], faces)
    mesh.materials.append(material(piece["material"]))

    layers = (("Lightmap", "uv1"),) if piece["material"] in lightmap_only else (("BaseMaterial", "uv0"), ("Lightmap", "uv1"))
    for layer_name, key in layers:
        uv = piece.get(key)
        layer = mesh.uv_layers.new(name=layer_name)
        if not uv:
            continue
        # no V flip
        for loop in mesh.loops:
            layer.data[loop.index].uv = (uv[loop.vertex_index * 2], uv[loop.vertex_index * 2 + 1])

    if piece.get("colors"):
        col = mesh.color_attributes.new("Color", "BYTE_COLOR", "POINT")
        c = piece["colors"]
        for i, v in enumerate(col.data):
            v.color = c[i * 4:i * 4 + 4]

    if piece.get("normals"):
        normals = [(x, -z, y) for x, y, z in triples(piece["normals"])]
        mesh.normals_split_custom_set_from_vertices(normals)

    mesh.validate()
    obj = bpy.data.objects.new(piece["name"], mesh)
    obj.location = origin
    bpy.context.scene.collection.objects.link(obj)

bpy.ops.export_scene.fbx(
    filepath=out_path,
    use_selection=False,
    object_types={"MESH"},
    axis_forward="-Z",
    axis_up="Y",
    apply_unit_scale=True,
    mesh_smooth_type="FACE",
)

with open(out_path[:-len(".fbx")] + ".objects.json", "w") as f:
    json.dump([{
        "name": o.name,
        "material": o.data.materials[0].name if o.data.materials else "",
        "triangles": sum(len(p.vertices) - 2 for p in o.data.polygons),
    } for o in bpy.data.objects], f)

print(f"wrote {out_path} with {len(pieces)} objects, materials: {sorted(materials)}")
