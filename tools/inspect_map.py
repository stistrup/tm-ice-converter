"""Inspector panel for a view's map.blend: info, marks, filter.

blender <view dir>/map.blend --python inspect_map.py
"""
import json
import os
from datetime import datetime

import bpy
from mathutils import Vector

INFO_KEYS = ("kind", "name", "sourceBlock", "cell", "position", "direction", "variant", "subVariant", "shownVariant",
             "drawnAs", "tags", "flags", "mesh")


def marks_path():
    return os.path.join(os.path.dirname(bpy.data.filepath), "marks.json")


def load_marks():
    try:
        with open(marks_path()) as f:
            return json.load(f)["marks"]
    except (OSError, ValueError, KeyError):
        return []


marks = []


def save_marks():
    with open(marks_path(), "w") as f:
        json.dump({"map": os.path.basename(os.path.dirname(bpy.data.filepath)), "marks": marks}, f, indent=2)


def info_of(obj):
    return {k: obj["tm_" + k] for k in INFO_KEYS if "tm_" + k in obj}


def map_objects():
    return [o for o in bpy.data.objects if "tm_name" in o]


def highlight(obj, on):
    obj.show_bounds = on
    obj.show_name = on


def view3d_override():
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                region = next(r for r in area.regions if r.type == "WINDOW")
                return dict(window=window, area=area, region=region)
    return None


def select_only(objs):
    for o in bpy.context.selected_objects:
        o.select_set(False)
    for o in objs:
        if not o.hide_get():
            o.select_set(True)
    if objs:
        bpy.context.view_layer.objects.active = objs[0]


class ICE_OT_mark(bpy.types.Operator):
    """Mark the selected objects"""
    bl_idname = "ice.mark"
    bl_label = "Mark selected"

    def execute(self, context):
        note = context.window_manager.ice_note.strip()
        names = {m["object"] for m in marks}
        added = 0
        for obj in context.selected_objects:
            if "tm_name" not in obj:
                continue
            centre = sum((obj.matrix_world @ Vector(c) for c in obj.bound_box), Vector()) / 8
            mark = {"object": obj.name, "note": note, **info_of(obj),
                    "at": [round(centre.x, 1), round(centre.z, 1), round(-centre.y, 1)],
                    "time": datetime.now().isoformat(timespec="seconds")}
            if obj.name in names:
                marks[:] = [mark if m["object"] == obj.name else m for m in marks]
            else:
                marks.append(mark)
            highlight(obj, True)
            added += 1
        save_marks()
        self.report({"INFO"}, f"marked {added} → {marks_path()}")
        return {"FINISHED"}


class ICE_OT_unmark(bpy.types.Operator):
    """Remove a mark"""
    bl_idname = "ice.unmark"
    bl_label = "Unmark"
    index: bpy.props.IntProperty(default=-1)

    def execute(self, context):
        names = {marks[self.index]["object"]} if self.index >= 0 else {o.name for o in context.selected_objects}
        marks[:] = [m for m in marks if m["object"] not in names]
        for name in names:
            if name in bpy.data.objects:
                highlight(bpy.data.objects[name], False)
        save_marks()
        return {"FINISHED"}


class ICE_OT_jump(bpy.types.Operator):
    """Go to a mark"""
    bl_idname = "ice.jump"
    bl_label = "Jump"
    index: bpy.props.IntProperty()

    def execute(self, context):
        obj = bpy.data.objects.get(marks[self.index]["object"])
        if obj is None:
            self.report({"WARNING"}, "object not in this scene (view rebuilt?)")
            return {"CANCELLED"}
        obj.hide_set(False)
        select_only([obj])
        if (o := view3d_override()) is not None:
            with context.temp_override(**o):
                bpy.ops.view3d.view_selected()
        return {"FINISHED"}


class ICE_OT_select_same(bpy.types.Operator):
    """Select all with the same name"""
    bl_idname = "ice.select_same"
    bl_label = "Select same name"

    def execute(self, context):
        active = context.active_object
        if active is None or "tm_name" not in active:
            return {"CANCELLED"}
        same = [o for o in map_objects() if o["tm_name"] == active["tm_name"]]
        select_only([active] + [o for o in same if o != active])
        self.report({"INFO"}, f"{len(same)} × {active['tm_name']}")
        return {"FINISHED"}


class ICE_OT_filter(bpy.types.Operator):
    """Show only matching names"""
    bl_idname = "ice.filter"
    bl_label = "Filter"
    select: bpy.props.BoolProperty(default=False)

    def execute(self, context):
        text = context.window_manager.ice_filter.strip().lower()
        hits = []
        for o in map_objects():
            hit = not text or text in o["tm_name"].lower() or text in str(o.get("tm_sourceBlock", "")).lower()
            o.hide_set(not hit and not self.select)
            if hit and text:
                hits.append(o)
        if self.select:
            select_only(hits)
        self.report({"INFO"}, f"{len(hits)} match" if text else "showing everything")
        return {"FINISHED"}


class ICE_OT_navigate(bpy.types.Operator):
    """Fly, frame or look through a camera"""
    bl_idname = "ice.navigate"
    bl_label = "Navigate"
    how: bpy.props.StringProperty()

    def execute(self, context):
        o = view3d_override()
        if o is None:
            return {"CANCELLED"}
        with context.temp_override(**o):
            if self.how == "fly":
                bpy.ops.view3d.walk("INVOKE_DEFAULT")
            elif self.how == "frame":
                bpy.ops.view3d.view_selected()
            elif self.how == "all":
                bpy.ops.view3d.view_all()
            else:
                r3d = o["area"].spaces.active.region_3d
                r3d.view_perspective = "PERSP"
                r3d.view_matrix = bpy.data.objects[self.how].matrix_world.inverted()
        return {"FINISHED"}


class ICE_PT_panel(bpy.types.Panel):
    bl_label = "Ice inspector"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    # scripts can't switch sidebar tabs
    bl_category = "Tool"
    bl_order = -1

    def draw(self, context):
        layout = self.layout
        wm = context.window_manager
        obj = context.active_object

        nav = layout.box()
        row = nav.row(align=True)
        row.operator("ice.navigate", text="Fly", icon="VIEW_PAN").how = "fly"
        row.operator("ice.navigate", text="Frame selected", icon="ZOOM_SELECTED").how = "frame"
        row.operator("ice.navigate", text="All", icon="VIEW_ZOOM").how = "all"
        nav.prop(wm, "ice_camera", text="Go to")

        box = layout.box()
        if obj is not None and "tm_name" in obj:
            for k, v in info_of(obj).items():
                if k != "mesh":
                    row = box.row()
                    row.label(text=k)
                    row.label(text=str(v))
            box.operator("ice.select_same", icon="RESTRICT_SELECT_OFF")
        else:
            box.label(text="Click a block or item")
        sel = sum(1 for o in context.selected_objects if "tm_name" in o)

        col = layout.column(align=True)
        col.prop(wm, "ice_note", text="", placeholder="Note, e.g. still sand")
        row = col.row(align=True)
        row.operator("ice.mark", text=f"Mark {sel} selected", icon="BOOKMARKS")
        row.operator("ice.unmark", text="Unmark", icon="X").index = -1

        col = layout.column(align=True)
        col.prop(wm, "ice_filter", text="", placeholder="Filter by name, e.g. DecoWall", icon="VIEWZOOM")
        row = col.row(align=True)
        row.operator("ice.filter", text="Show only").select = False
        row.operator("ice.filter", text="Select").select = True

        layout.label(text=f"Marks ({len(marks)})  →  marks.json")
        for i, m in enumerate(marks):
            row = layout.row(align=True)
            row.operator("ice.jump", text=m["object"], emboss=False).index = i
            row.operator("ice.unmark", text="", icon="X").index = i
            if m.get("note"):
                layout.label(text="    " + m["note"])


def camera_items(self, context):
    cams = sorted((o.name for o in bpy.data.objects if o.type == "CAMERA"),
                  key=lambda n: (not n.startswith("Start"), n.startswith("Overview") or n == "Top", n))
    camera_items.cache = [(n, n, "") for n in cams] # keep alive for Blender
    return camera_items.cache


CLASSES = (ICE_OT_navigate, ICE_OT_mark, ICE_OT_unmark, ICE_OT_jump, ICE_OT_select_same, ICE_OT_filter, ICE_PT_panel)


def setup_viewports():
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type != "VIEW_3D":
                continue
            space = area.spaces.active
            space.shading.type = "MATERIAL"
            space.shading.use_scene_world = True
            space.shading.use_scene_lights = True
            space.clip_end = 20000
            space.show_region_ui = True
            space.show_region_toolbar = False
            space.show_region_tool_header = False
            space.show_gizmo_navigate = False
            region = next(r for r in area.regions if r.type == "WINDOW")
            with bpy.context.temp_override(window=window, area=area, region=region):
                bpy.ops.view3d.view_all(center=False)
        for area in [a for a in window.screen.areas if a.type in ("DOPESHEET_EDITOR", "TIMELINE")]:
            with bpy.context.temp_override(window=window, area=area):
                bpy.ops.screen.area_close()
    return None


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.WindowManager.ice_note = bpy.props.StringProperty(name="Note")
    bpy.types.WindowManager.ice_filter = bpy.props.StringProperty(name="Filter")
    bpy.types.WindowManager.ice_camera = bpy.props.EnumProperty(
        name="Camera", items=camera_items, update=lambda self, context: bpy.ops.ice.navigate(how=self.ice_camera))
    bpy.context.preferences.inputs.walk_navigation.walk_speed = 40
    marks[:] = load_marks()
    for m in marks:
        if m["object"] in bpy.data.objects:
            highlight(bpy.data.objects[m["object"]], True)
    bpy.app.timers.register(setup_viewports, first_interval=0.5)


register()
