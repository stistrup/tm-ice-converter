"""Render a view's map.blend from a camera in game coordinates.

blender --background <map.blend> --python render_at.py -- <out.png> ex ey ez tx ty tz [lens]
"""
import sys
import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
out = args[0]
ex, ey, ez, tx, ty, tz = map(float, args[1:7])
lens = float(args[7]) if len(args) > 7 else 24
game = lambda x, y, z: Vector((x, -z, y))
eye, target = game(ex, ey, ez), game(tx, ty, tz)

scene = bpy.context.scene
cam = bpy.data.objects.new("At", bpy.data.cameras.new("At"))
cam.location = eye
cam.rotation_euler = (target - eye).to_track_quat("-Z", "Y").to_euler()
cam.data.lens = lens
cam.data.clip_end = 5000
scene.collection.objects.link(cam)
scene.camera = cam
scene.render.resolution_x, scene.render.resolution_y = 1280, 720
scene.render.filepath = out
bpy.ops.render.render(write_still=True)
print("wrote", out)
