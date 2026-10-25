"""Builds a map scene in Blender, saves map.blend and renders a few views.

blender --background --factory-startup --python view_map.py -- <scene.json> <out dir> [width]   (width 0: .blend only)
"""
import json
import math
import os
import re
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
scene_path, out_dir = args[0], args[1]
width = int(args[2]) if len(args) > 2 else 1600

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

with open(scene_path) as f:
    data = json.load(f)

# TM (x, y, z) -> Blender (x, -z, y)
C = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
C_INV = C.inverted()


def to_blender(m):
    rows = [m[i * 4:i * 4 + 4] for i in range(4)]
    return C @ Matrix(rows).transposed() @ C_INV


# --- materials

TILE = 1 / 32 # guess
ALPHA_TESTED = ("GrassFence", "Fence")


def fallback_color(name):
    n = name.lower()
    for key, rgb in (("ice", (0.62, 0.78, 0.9)), ("snow", (0.9, 0.92, 0.95)), ("decohill", (0.88, 0.9, 0.93)),
                     ("grass", (0.25, 0.45, 0.15)), ("dirt", (0.45, 0.3, 0.18)), ("water", (0.1, 0.3, 0.4)),
                     ("border", (0.85, 0.85, 0.85)), ("tech", (0.35, 0.37, 0.4)), ("wall", (0.5, 0.5, 0.52)),
                     ("deco", (0.55, 0.55, 0.55))):
        if key in n:
            return rgb
    return (0.6, 0.6, 0.6)


images = {}


def image(path, color):
    if path not in images:
        try:
            img = bpy.data.images.load(path, check_existing=True)
            img.colorspace_settings.name = "sRGB" if color else "Non-Color"
            img.alpha_mode = "CHANNEL_PACKED"
        except RuntimeError:
            img = None
        images[path] = img
    return images[path]


def mapping_of(name):
    return "top" if "Py" in name else "side" if "Pxz" in name else "uv"


def build_material(key, info):
    mat = bpy.data.materials.new(info["name"])
    nt = mat.node_tree
    nodes, links = nt.nodes, nt.links
    bsdf = nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.6
    shader = info.get("shader") or ""
    decal = "decal" in info["name"].lower() or "decal" in shader.lower()
    cutout = any(k in info["name"] for k in ALPHA_TESTED)

    geo = nodes.new("ShaderNodeNewGeometry")
    world = nodes.new("ShaderNodeMapping")
    world.inputs["Scale"].default_value = (TILE, TILE, TILE)
    links.new(geo.outputs["Position"], world.inputs["Vector"])
    up = nodes.new("ShaderNodeSeparateXYZ")
    links.new(geo.outputs["Normal"], up.inputs["Vector"])
    top_factor = nodes.new("ShaderNodeMapRange")
    top_factor.inputs["From Min"].default_value, top_factor.inputs["From Max"].default_value = 0.55, 0.8
    links.new(up.outputs["Z"], top_factor.inputs["Value"])

    def textures(role):
        found = {}
        for t in info["textures"]:
            if t["role"] != role or not t.get("image"):
                continue
            m = mapping_of(t["name"])
            if m not in found or (m == "uv" and t["name"].startswith(info["name"])):
                found[m] = t
        return found

    def sample(t, color, mapping, only):
        img = image(t["image"], color)
        if img is None:
            return None
        node = nodes.new("ShaderNodeTexImage")
        node.image = img
        if mapping != "uv":
            node.projection = "BOX" if only or mapping == "side" else "FLAT"
            node.projection_blend = 0.3
            links.new(world.outputs["Vector"], node.inputs["Vector"])
        return node

    def layered(role, color, output="Color"):
        found = textures(role)
        if not found:
            return None, None
        base_t = found.get("side") or found.get("uv")
        top_t = found.get("top")
        base = sample(base_t, color, mapping_of(base_t["name"]), only=top_t is None) if base_t else None
        top = sample(top_t, color, "top", only=base_t is None) if top_t else None
        if base and top:
            mix = nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            links.new(top_factor.outputs["Result"], mix.inputs["Factor"])
            links.new(base.outputs[output], mix.inputs["A"])
            links.new(top.outputs[output], mix.inputs["B"])
            return mix.outputs["Result"], base
        node = base or top
        return (node.outputs[output], node) if node else (None, None)

    if "Layered" in shader:
        layered_ice(info, nodes, links, bsdf, textures, sample)
        return mat

    d, d_node = layered("D", True)
    if d:
        links.new(d, bsdf.inputs["Base Color"])
        if (decal or cutout) and d_node:
            links.new(d_node.outputs["Alpha"], bsdf.inputs["Alpha"])
    else:
        bsdf.inputs["Base Color"].default_value = (*fallback_color(info["name"]), 1)
        if decal:
            bsdf.inputs["Alpha"].default_value = 0.0

    r, r_node = layered("R", False)
    if r:
        sep = nodes.new("ShaderNodeSeparateColor")
        links.new(r, sep.inputs["Color"])
        links.new(sep.outputs["Red"], bsdf.inputs["Roughness"])
        if r_node and r_node.image.depth > 8:
            links.new(sep.outputs["Green"], bsdf.inputs["Metallic"])

    n_tex = textures("N").get("uv")
    n = sample(n_tex, False, "uv", only=True) if n_tex else None
    if n:
        sep = nodes.new("ShaderNodeSeparateColor")
        links.new(n.outputs["Color"], sep.inputs["Color"])
        x = nodes.new("ShaderNodeMath"); x.operation = "MULTIPLY_ADD"; x.inputs[1].default_value = 2; x.inputs[2].default_value = -1
        y = nodes.new("ShaderNodeMath"); y.operation = "MULTIPLY_ADD"; y.inputs[1].default_value = -2; y.inputs[2].default_value = 1
        links.new(sep.outputs["Red"], x.inputs[0])
        links.new(sep.outputs["Green"], y.inputs[0])
        comb = nodes.new("ShaderNodeCombineXYZ")
        links.new(x.outputs[0], comb.inputs["X"])
        links.new(y.outputs[0], comb.inputs["Y"])
        length = nodes.new("ShaderNodeVectorMath"); length.operation = "DOT_PRODUCT"
        links.new(comb.outputs[0], length.inputs[0]); links.new(comb.outputs[0], length.inputs[1])
        z = nodes.new("ShaderNodeMath"); z.operation = "SUBTRACT"; z.inputs[0].default_value = 1
        links.new(length.outputs["Value"], z.inputs[1])
        zs = nodes.new("ShaderNodeMath"); zs.operation = "SQRT"; zs.use_clamp = True
        links.new(z.outputs[0], zs.inputs[0])
        full = nodes.new("ShaderNodeCombineXYZ")
        links.new(x.outputs[0], full.inputs["X"]); links.new(y.outputs[0], full.inputs["Y"]); links.new(zs.outputs[0], full.inputs["Z"])
        to_color = nodes.new("ShaderNodeVectorMath"); to_color.operation = "MULTIPLY_ADD"
        to_color.inputs[1].default_value = (0.5, 0.5, 0.5); to_color.inputs[2].default_value = (0.5, 0.5, 0.5)
        links.new(full.outputs[0], to_color.inputs[0])
        nmap = nodes.new("ShaderNodeNormalMap")
        nmap.uv_map = "UV"
        links.new(to_color.outputs[0], nmap.inputs["Color"])
        links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])

    i_tex = textures("I").get("uv")
    i = sample(i_tex, True, "uv", only=True) if i_tex else None
    if i:
        links.new(i.outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 2.0

    if decal:
        mat.surface_render_method = "BLENDED"
    return mat


def layered_ice(info, nodes, links, bsdf, textures, sample):
    """Approximation of the ice platform shader."""
    def stretched(t, mapping, lo, hi):
        node = sample(t, False, mapping, only=True)
        if node is None:
            return None
        rng = nodes.new("ShaderNodeMapRange")
        rng.inputs["From Min"].default_value, rng.inputs["From Max"].default_value = lo, hi
        links.new(node.outputs["Color"], rng.inputs["Value"])
        return rng.outputs["Result"]

    frost_white = (0.93, 0.96, 1.0, 1)
    # rink markings are faint: boost them
    ice_blue, mean, gain = (0.42, 0.62, 0.88), (0.70, 0.74, 0.78), 2.0
    d_t = textures("D").get("uv")
    d_node = sample(d_t, False, "uv", only=True) if d_t else None
    if d_node:
        boost = nodes.new("ShaderNodeVectorMath"); boost.operation = "MULTIPLY_ADD"
        links.new(d_node.outputs["Color"], boost.inputs[0])
        boost.inputs[1].default_value = (gain, gain, gain)
        boost.inputs[2].default_value = tuple(b - m * gain for b, m in zip(ice_blue, mean))
        clamp = nodes.new("ShaderNodeMix"); clamp.data_type = "RGBA"; clamp.clamp_result = True
        clamp.inputs["Factor"].default_value = 0.0
        links.new(boost.outputs[0], clamp.inputs["A"])
        color = clamp.outputs["Result"]
    else:
        rgb = nodes.new("ShaderNodeRGB"); rgb.outputs[0].default_value = (*ice_blue, 1)
        color = rgb.outputs[0]

    roughness = nodes.new("ShaderNodeValue"); roughness.outputs[0].default_value = 0.06
    rough = roughness.outputs[0]
    for t, mapping, lo, hi, amount in ((textures("R").get("top"), "top", 0.17, 0.25, 0.15),
                                       (textures("M").get("uv"), "uv", 0.22, 0.45, 0.12)):
        if t is None:
            continue
        layer = stretched(t, mapping, lo, hi)
        if layer is None:
            continue
        mix = nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"
        weight = nodes.new("ShaderNodeMath"); weight.operation = "MULTIPLY"; weight.inputs[1].default_value = amount
        links.new(layer, weight.inputs[0])
        links.new(weight.outputs[0], mix.inputs["Factor"])
        links.new(color, mix.inputs["A"])
        mix.inputs["B"].default_value = frost_white
        color = mix.outputs["Result"]
        more = nodes.new("ShaderNodeMath"); more.operation = "MULTIPLY_ADD"; more.inputs[1].default_value = 0.4
        links.new(layer, more.inputs[0]); links.new(rough, more.inputs[2])
        rough = more.outputs[0]
    links.new(color, bsdf.inputs["Base Color"])
    links.new(rough, bsdf.inputs["Roughness"])


materials = {key: build_material(key, info) for key, info in data["materials"].items()}
textured = sum(1 for info in data["materials"].values() if any(t.get("image") and t["role"] == "D" for t in info["textures"]))

# --- meshes


def build_mesh(key, pieces):
    used = sorted({p["material"] for p in pieces})
    slot = {m: i for i, m in enumerate(used)}
    pos, nrm, uv, idx, mat_idx = [], [], [], [], []
    base = 0
    for p in pieces:
        v = np.asarray(p["positions"], dtype=np.float32).reshape(-1, 3)
        count = len(v)
        pos.append(v)
        nrm.append(np.asarray(p["normals"], dtype=np.float32).reshape(-1, 3) if p.get("normals") else np.zeros((count, 3), np.float32))
        uv.append(np.asarray(p["uv0"], dtype=np.float32).reshape(-1, 2) if p.get("uv0") else np.zeros((count, 2), np.float32))
        tris = np.asarray(p["indices"], dtype=np.int64).reshape(-1, 3)
        idx.append(tris + base)
        mat_idx.append(np.full(len(tris), slot[p["material"]], dtype=np.int32))
        base += count
    pos, nrm, uv = np.concatenate(pos), np.concatenate(nrm), np.concatenate(uv)
    idx, mat_idx = np.concatenate(idx), np.concatenate(mat_idx)

    pos = pos[:, [0, 2, 1]] * np.array([1, -1, 1], np.float32)
    nrm = nrm[:, [0, 2, 1]] * np.array([1, -1, 1], np.float32)

    mesh = bpy.data.meshes.new(key)
    mesh.vertices.add(len(pos))
    mesh.vertices.foreach_set("co", pos.ravel())
    mesh.loops.add(idx.size)
    mesh.loops.foreach_set("vertex_index", idx.ravel().astype(np.int32))
    mesh.polygons.add(len(idx))
    mesh.polygons.foreach_set("loop_start", np.arange(0, idx.size, 3, dtype=np.int32))
    mesh.polygons.foreach_set("material_index", mat_idx)
    layer = mesh.uv_layers.new(name="UV")
    # no V flip
    loop_uv = uv[idx.ravel()]
    layer.data.foreach_set("uv", loop_uv.ravel())
    for m in used:
        mesh.materials.append(materials[m])
    mesh.update(calc_edges=True)
    mesh.validate(clean_customdata=False)
    if np.any(nrm):
        mesh.normals_split_custom_set_from_vertices(nrm.tolist())
    return mesh


meshes = {key: build_mesh(key, pieces) for key, pieces in data["meshes"].items()}

def describe(obj, info, mesh_key):
    where = info.get("cell") or [round(v) for v in info.get("position", [])]
    label = info.get("sourceBlock") or info["name"].split("\\")[-1]
    obj.name = f"{label} ({', '.join(map(str, where))})"
    obj["tm_mesh"] = mesh_key
    for k, v in info.items():
        obj["tm_" + k] = ", ".join(map(str, v)) if isinstance(v, list) else v


track = bpy.data.collections.new("Map")
scene.collection.children.link(track)
groups = {}
for kind, label in (("block", "Blocks"), ("ice item", "Ice items"), ("item", "Items")):
    groups[kind] = bpy.data.collections.new(label)
    track.children.link(groups[kind])
# hidden at first: rough, covers the track
terrain = bpy.data.collections.new("Terrain")
track.children.link(terrain)
for n, inst in enumerate(data["instances"]):
    obj = bpy.data.objects.new(inst["mesh"], meshes[inst["mesh"]])
    obj.matrix_world = to_blender(inst["m"])
    describe(obj, inst["info"], inst["mesh"])
    is_terrain = "terrain" in inst["info"].get("tags", "").split(", ")
    (terrain if is_terrain else groups[inst["info"]["kind"]]).objects.link(obj)

TREE_COLORS = (("Fall", (0.75, 0.35, 0.08)), ("Spring", (0.45, 0.65, 0.25)), ("Fir", (0.1, 0.3, 0.15)),
               ("Summer", (0.2, 0.5, 0.15)), ("Winter", (0.85, 0.9, 0.95)))
trees = {}


def tree_mesh(kind):
    if kind not in trees:
        bpy.ops.mesh.primitive_cone_add(vertices=10, radius1=3.5, depth=10, location=(0, 0, 8))
        crown = bpy.context.object
        bpy.ops.mesh.primitive_cylinder_add(vertices=6, radius=0.4, depth=4, location=(0, 0, 2))
        trunk = bpy.context.object
        for o, rgb in ((crown, next(c for k, c in TREE_COLORS if k == kind)), (trunk, (0.25, 0.15, 0.08))):
            m = bpy.data.materials.new(f"_tree_{kind}_{o.name}")
            m.diffuse_color = (*rgb, 1)
            m.use_nodes = True
            m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*rgb, 1)
            o.data.materials.append(m)
        with bpy.context.temp_override(selected_editable_objects=[crown, trunk], active_object=crown):
            bpy.ops.object.join()
        trees[kind] = crown.data
        bpy.data.objects.remove(crown)
    return trees[kind]


scenery = bpy.data.collections.new("Placeholders")
scene.collection.children.link(scenery)
for n, ph in enumerate(data["placeholders"]):
    kind = next((k for k, _ in TREE_COLORS if k in ph["mesh"]), None)
    if kind is None:
        continue
    obj = bpy.data.objects.new(ph["mesh"], tree_mesh(kind))
    obj.matrix_world = to_blender(ph["m"])
    describe(obj, ph["info"], ph["mesh"])
    scenery.objects.link(obj)

BOX_COLORS = {"terrain": (0.8, 0.55, 0.25, 0.4), "other": (0.9, 0.1, 0.8, 0.4)}
TERRAIN = re.compile(r"^(Dirt|Water|DecoLake|DecoTerrain|Land|Beach|Sea)")
box_meshes = {}


def box_mesh(size, kind):
    key = (*size, kind)
    if key not in box_meshes:
        sx, sy, sz = 32 * size[0], 8 * size[1], 32 * size[2]
        corners = [(x, -z, y) for x in (0, sx) for y in (0, sy) for z in (0, sz)]
        faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        mesh = bpy.data.meshes.new(f"_box_{kind}_{sx}x{sy}x{sz}")
        mesh.from_pydata(corners, [], faces)
        mat = bpy.data.materials.get(f"_box_{kind}")
        if mat is None:
            mat = bpy.data.materials.new(f"_box_{kind}")
            mat.use_nodes = True
            bsdf = mat.node_tree.nodes["Principled BSDF"]
            bsdf.inputs["Base Color"].default_value = BOX_COLORS[kind]
            bsdf.inputs["Alpha"].default_value = BOX_COLORS[kind][3]
            mat.diffuse_color = BOX_COLORS[kind]
            if hasattr(mat, "surface_render_method"):
                mat.surface_render_method = "BLENDED"
        mesh.materials.append(mat)
        box_meshes[key] = mesh
    return box_meshes[key]


no_mesh = bpy.data.collections.new("No mesh (boxes)")
track.children.link(no_mesh)
for b in data.get("boxes", []):
    name = b["info"]["name"]
    kind = "terrain" if TERRAIN.match(name) else "other"
    obj = bpy.data.objects.new(name, box_mesh(b["info"]["units"], kind))
    obj.matrix_world = to_blender(b["m"])
    describe(obj, b["info"], b["mesh"])
    obj["tm_kind"] = "block, no mesh (box)"
    (terrain if kind == "terrain" else no_mesh).objects.link(obj)
terrain.hide_viewport = terrain.hide_render = True

if (data.get("environment") or "Stadium").startswith("Stadium"):
    bpy.ops.mesh.primitive_plane_add(size=48 * 32, location=(24 * 32, -24 * 32, 8 - 0.05))
    ground = bpy.context.object
    ground.name = "Ground"
    gm = bpy.data.materials.new("_ground")
    gm.use_nodes = True
    gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.16, 0.3, 0.09, 1)
    gm.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.9
    ground.data.materials.append(gm)

# --- light and sky

world = bpy.data.worlds.new("Sky")
scene.world = world
world.use_nodes = True
bg = world.node_tree.nodes["Background"]
try:
    sky = world.node_tree.nodes.new("ShaderNodeTexSky")
    for kind in ("MULTIPLE_SCATTERING", "SINGLE_SCATTERING", "NISHITA", "HOSEK_WILKIE"):
        try:
            sky.sky_type = kind
            break
        except TypeError:
            continue
    if hasattr(sky, "sun_elevation"):
        sky.sun_elevation = math.radians(40)
        sky.sun_rotation = math.radians(135)
    world.node_tree.links.new(sky.outputs["Color"], bg.inputs["Color"])
    bg.inputs["Strength"].default_value = 1.0
    print(f"view: sky {sky.sky_type}")
except RuntimeError:
    bg.inputs["Color"].default_value = (0.55, 0.7, 0.9, 1)

sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
sun.data.energy = 3.5
sun.data.angle = math.radians(2)
sun.rotation_euler = (math.radians(50), 0, math.radians(135))
scene.collection.objects.link(sun)

scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = (width or 1600), (width or 1600) * 9 // 16
scene.render.image_settings.file_format = "PNG"
if hasattr(scene.eevee, "taa_render_samples"):
    scene.eevee.taa_render_samples = 32
if hasattr(scene.eevee, "use_shadows"):
    scene.eevee.use_shadows = True
if hasattr(scene.eevee, "use_raytracing"):
    scene.eevee.use_raytracing = True
scene.view_settings.view_transform = "AgX"
for look in ("AgX - Medium High Contrast", "Medium High Contrast"):
    try:
        scene.view_settings.look = look
        break
    except TypeError:
        continue
scene.view_settings.exposure = -1.4

# --- cameras

pts = [o.matrix_world @ Vector(c) for o in track.all_objects if terrain not in o.users_collection for c in o.bound_box]
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
center, size = (lo + hi) / 2, (hi - lo).length


def camera(name, eye, target, lens=28, ortho=None):
    cam = bpy.data.objects.new(name, bpy.data.cameras.new(name))
    cam.location = eye
    cam.rotation_euler = (Vector(target) - Vector(eye)).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens
    cam.data.clip_end = 5000
    if ortho:
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = ortho
    scene.collection.objects.link(cam)
    return cam


views = [
    camera("Overview SW", center + Vector((-0.55, -0.55, 0.45)) * size, center, lens=30),
    camera("Overview NE", center + Vector((0.55, 0.55, 0.45)) * size, center, lens=30),
    camera("Top", center + Vector((0, 0, size)), center, ortho=max(hi.x - lo.x, (hi.y - lo.y) * 16 / 9) * 1.05),
]
counts = {}
for wp in data["waypoints"]:
    m = to_blender(wp["m"])
    eye = m @ (C @ Vector((16, 9, -22)))
    target = m @ (C @ Vector((16, 3, 40)))
    counts[wp["kind"]] = counts.get(wp["kind"], 0) + 1
    views.append(camera(f"{wp['kind']} {counts[wp['kind']]}", eye, target, lens=24))

scene.camera = next((v for v in views if v.name.startswith("Start")), views[0])
os.makedirs(out_dir, exist_ok=True)
blend = os.path.join(out_dir, "map.blend")
bpy.ops.wm.save_as_mainfile(filepath=blend)
print(f"wrote {blend}")
print(f"view: {len(data['instances'])} instances of {len(meshes)} meshes, {len(materials)} materials ({textured} with colour textures), {len(scenery.objects)} tree placeholders")

for cam in views if width > 0 else []:
    scene.camera = cam
    scene.render.filepath = os.path.join(out_dir, re.sub(r"[^\w]+", "-", cam.name.lower()).strip("-") + ".png")
    bpy.ops.render.render(write_still=True)
    print(f"wrote {scene.render.filepath}")
