"""Preview collage of a built item.

blender --background --factory-startup --python render_item.py -- <pieces.json> <out.png> [tile px]
"""
import colorsys
import hashlib
import json
import math
import os
import shutil
import sys
import tempfile

import bpy
import numpy as np
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
pieces_path, out_path = args[0], args[1]
tile_w = int(args[2]) if len(args) > 2 else 640
tile_h = tile_w * 3 // 4
COLUMNS = 4

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

with open(pieces_path) as f:
    pieces = json.load(f)


def triples(flat):
    return [tuple(flat[i:i + 3]) for i in range(0, len(flat), 3)]


def color_for(name):
    if "Ice" in name and "DecoHill" not in name:
        return (0.55, 0.8, 1.0)
    if "DecoHill" in name or "Snow" in name:
        return (0.92, 0.94, 0.97)
    h = int(hashlib.md5(name.encode()).hexdigest()[:6], 16) / 0xFFFFFF
    return colorsys.hsv_to_rgb(h, 0.6, 0.9)


materials = {}


def material(name):
    if name not in materials:
        m = bpy.data.materials.new(name)
        m.diffuse_color = (*color_for(name), 1.0)
        materials[name] = m
    return materials[name]


visible, collision = [], []
for piece in pieces:
    if piece["name"].startswith("_socket_") or piece["name"].startswith("_trigger_"):
        continue
    verts = [(x, -z, y) for x, y, z in triples(piece["positions"])]
    mesh = bpy.data.meshes.new(piece["name"])
    mesh.from_pydata(verts, [], triples(piece["indices"]))
    mesh.materials.append(material(piece["material"]))
    mesh.validate()
    obj = bpy.data.objects.new(piece["name"], mesh)
    scene.collection.objects.link(obj)
    (visible if piece["visible"] else collision).append(obj)


def bounds(objs):
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
    if not pts:
        return Vector((0, 0, 0)), 1.0
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (lo + hi) / 2, max((hi - lo).length, 1.0)


# red +X, green up, blue +Z
_, size = bounds(visible or collision)
marker = []
for axis, rgb in (((1, 0, 0), (0.9, 0.1, 0.1)), ((0, 0, 1), (0.1, 0.8, 0.1)), ((0, -1, 0), (0.15, 0.3, 1.0))):
    bpy.ops.mesh.primitive_cylinder_add(radius=size * 0.006, depth=size * 0.12)
    bar = bpy.context.object
    bar.rotation_mode = "QUATERNION"
    bar.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(Vector(axis))
    bar.location = Vector(axis) * size * 0.06
    m = bpy.data.materials.new("_axis")
    m.diffuse_color = (*rgb, 1.0)
    bar.data.materials.append(m)
    marker.append(bar)

camera = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
camera.data.type = "ORTHO"
scene.collection.objects.link(camera)
scene.camera = camera

scene.render.engine = "BLENDER_WORKBENCH"
scene.render.resolution_x, scene.render.resolution_y = tile_w, tile_h
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGB"
shading = scene.display.shading
shading.light = "STUDIO"
shading.color_type = "MATERIAL"
shading.show_cavity = True
shading.show_object_outline = True
shading.object_outline_color = (0.05, 0.05, 0.05)
scene.world = bpy.data.worlds.new("world")
scene.world.color = (0.16, 0.17, 0.19)
scene.display_settings.display_device = "sRGB"
scene.view_settings.view_transform = "Standard"

render = scene.render
for prop in dir(render):
    if prop.startswith("use_stamp_") and isinstance(getattr(render, prop), bool):
        setattr(render, prop, False)
render.use_stamp = True
render.use_stamp_note = True
render.stamp_font_size = max(12, tile_w // 32)
render.stamp_background = (0, 0, 0, 0.6)


def aim(direction, objs, up=None):
    direction = Vector(direction).normalized()
    camera.rotation_euler = (-direction).to_track_quat("-Z", up or "Y").to_euler()
    rot = camera.rotation_euler.to_matrix()
    right, upv = rot.col[0], rot.col[1]
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box] or [Vector((0, 0, 0))]
    xs, ys = [p.dot(right) for p in pts], [p.dot(upv) for p in pts]
    w, h = max(max(xs) - min(xs), 1.0), max(max(ys) - min(ys), 1.0)
    camera.data.ortho_scale = max(w, h * tile_w / tile_h) * 1.12
    center = right * (max(xs) + min(xs)) / 2 + upv * ((max(ys) + min(ys)) / 2 + camera.data.ortho_scale * 0.02)
    depth = max(p.dot(direction) for p in pts)
    span = max((p - pts[0]).length for p in pts) + 1
    camera.location = center + direction * (depth + span)
    camera.data.clip_end = span * 4


def show(objs):
    for o in visible + collision + marker + legend:
        o.hide_render = o not in objs


legend = []
tiles = []
tmp = tempfile.mkdtemp(prefix="render_item_")


def shoot(label, objs, direction, up=None, frame=None):
    show(objs)
    aim(direction, frame or objs, up)
    render.stamp_note_text = label
    path = os.path.join(tmp, f"{len(tiles)}.png")
    render.filepath = path
    bpy.ops.render.render(write_still=True)
    tiles.append(path)


def tm(x, y, z):
    return (x, -z, y)


body = visible + marker
for label, d in (("from -X -Z (origin corner)", tm(-1, 0.8, -1)), ("from +X -Z", tm(1, 0.8, -1)),
                 ("from +X +Z", tm(1, 0.8, 1)), ("from -X +Z", tm(-1, 0.8, 1))):
    shoot(label, body, d, frame=visible)
shoot("top (+Z down, +X right)", body, tm(0, 1, 0), up="Y", frame=visible)
shoot("underside", body, tm(-1, -0.7, -1), frame=visible)
shoot(f"collision ({sum(len(o.data.polygons) for o in collision)} tris)", collision + marker,
      tm(-1, 0.8, -1), frame=collision or visible)

used = sorted({o.data.materials[0].name for o in visible + collision})
far = Vector((100000, 0, 0))
row = 1.6
text_w = max(len(n) for n in used) * 0.55 + 1.5 + 2.5
aspect = tile_w / tile_h


def legend_scale(cols):
    return max(cols * text_w, (math.ceil(len(used) / cols) + 1.5) * row * aspect)


cols = min(range(1, 5), key=legend_scale)
col_w = text_w
per_col = math.ceil(len(used) / cols)
text_mat = bpy.data.materials.new("_legend_text")
text_mat.diffuse_color = (0.9, 0.9, 0.9, 1)
for i, name in enumerate(used):
    c, r = divmod(i, per_col)
    pos = far + Vector((c * col_w, -r * row, 0))
    bpy.ops.mesh.primitive_plane_add(size=1, location=pos)
    swatch = bpy.context.object
    swatch.data.materials.append(materials[name])
    bpy.ops.object.text_add(location=pos + Vector((0.9, -0.35, 0)))
    text = bpy.context.object
    text.data.body = name + ("  (collision only)" if not any(o.data.materials[0].name == name for o in visible) else "")
    text.data.materials.append(text_mat)
    legend += [swatch, text]
show(legend)
scale = legend_scale(cols) * 1.02
camera.location = far + Vector((scale / 2 - 0.8, -(scale / aspect) / 2 + 2.4, 50))
camera.rotation_euler = (0, 0, 0)
camera.data.ortho_scale = scale
shading.show_cavity = shading.show_object_outline = False
render.stamp_note_text = "materials"
render.filepath = os.path.join(tmp, f"{len(tiles)}.png")
bpy.ops.render.render(write_still=True)
tiles.append(render.filepath)


gap = 4
rows = math.ceil(len(tiles) / COLUMNS)
W, H = COLUMNS * tile_w + (COLUMNS + 1) * gap, rows * tile_h + (rows + 1) * gap
sheet = np.full((H, W, 4), 0.08, dtype=np.float32)
sheet[..., 3] = 1
for i, path in enumerate(tiles):
    img = bpy.data.images.load(path)
    px = np.empty(tile_w * tile_h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    r, c = divmod(i, COLUMNS)
    y0 = H - (r + 1) * (tile_h + gap)
    x0 = gap + c * (tile_w + gap)
    sheet[y0:y0 + tile_h, x0:x0 + tile_w] = px.reshape(tile_h, tile_w, 4)
out = bpy.data.images.new("collage", W, H, alpha=False)
out.colorspace_settings.name = "sRGB"
out.pixels.foreach_set(sheet.ravel())
out.filepath_raw = out_path
out.file_format = "PNG"
out.save()
shutil.rmtree(tmp, ignore_errors=True)
print(f"wrote {out_path}: {len(visible)} visible, {len(collision)} collision objects, materials {used}")
