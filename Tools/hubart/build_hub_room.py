"""Builds the hub room's art (floor, walls, decor, and the three wall boards' coloured frames
with one Anchor_Board_<id> empty each, where the Unity assembler places the board's panel
prefab) from hub_layout.json, exports the FBX into the Unity project and renders review
previews. The panels themselves are uGUI prefabs (Docs/Design/StoryMode/HUB_PANEL_PREFAB.md).

Run headless (Blender 4.0.2 on the Linux box; 3.6+ on Windows):

    source ~/tools/blender-env.sh
    $BLENDER $BLENDER_FLAGS --python Tools/hubart/build_hub_room.py -- \
        [--fbx <path>] [--preview-dir /tmp] [--no-fbx] [--no-preview] [--samples 32]

Coordinates. hub_layout.json is in hub-local Unity metres (origin HubCenter, +Y up, +Z north,
left-handed). Blender is right-handed Z-up; a Unity point (x, y, z) is built at Blender
(x, z, y) and the FBX is exported with the project's prop settings (forward -Z, up Y, baked
space transform, FBX_SCALE_UNITS), the same as Docs/Art/OriginalModels/props/scripts/prop_lib.py.
That mapping is a mirror, so the script checks it with an asymmetric probe instead of trusting
it: the export's import-side check is the Unity operator's (HUB_ROOM.md, anchors read back).

Everything is generated here from primitive shapes - no reference-game asset. Materials are
named after the layout's surfaces (hub_layout.json "surfaces", e.g. Hub_FloorWood); the FBX
carries the names and metre UVs only (UV0 = world metres, see metre_uvs) and the Unity
assembler builds the matching materials with the surfaces' colours, tiling textures and
smoothness. The preview renders load the same textures (after the export).
"""

import json
import math
import os
import sys

import bpy
import bmesh
from mathutils import Euler, Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LAYOUT_PATH = os.path.join(HERE, "hub_layout.json")
DEFAULT_FBX = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Art",
                           "Hub", "HubRoomArt.fbx")
ROOM_W, ROOM_D, WALL_H = 16.0, 8.0, 3.0


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = {"fbx": DEFAULT_FBX, "preview_dir": "/tmp", "fbx_on": True, "preview_on": True,
           "samples": 32}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--fbx":
            out["fbx"] = argv[i + 1]; i += 1
        elif a == "--preview-dir":
            out["preview_dir"] = argv[i + 1]; i += 1
        elif a == "--no-fbx":
            out["fbx_on"] = False
        elif a == "--no-preview":
            out["preview_on"] = False
        elif a == "--samples":
            out["samples"] = int(argv[i + 1]); i += 1
        i += 1
    return out


# ---------------------------------------------------------------------------- frames ---

def u2b(p):
    """Unity hub-local point (or direction) -> Blender.

    The export (forward -Z, up Y, baked) writes Blender (x, y, z) as FBX (x, z, -y), and
    Unity's importer negates FBX x, so Blender (bx, by, bz) lands at Unity (-bx, bz, -by).
    Inverting that: Unity (x, y, z) is built at Blender (-x, -z, y). Both frames describe
    the same physical space (the sign flip is the handedness change), so previews rendered
    here show what Unity shows. A model facing Blender -Y faces Unity +Z, which matches the
    prop pipeline's recorded intent (Docs/Art/OriginalModels/README.md)."""
    return Vector((-p[0], -p[2], p[1]))


def board_matrix(board):
    """Unity-space frame of a board: origin centre, columns = reader-right, up, towards reader."""
    yaw = math.radians(board["yawDeg"])
    # Unity yaw about +Y (left-handed): local +X -> (cos, 0, -sin), local +Z -> (sin, 0, cos).
    right = Vector((math.cos(yaw), 0.0, -math.sin(yaw)))
    up = Vector((0.0, 1.0, 0.0))
    towards_reader = -Vector((math.sin(yaw), 0.0, math.cos(yaw)))  # readable side = local -Z
    # The reader stands on the -Z side looking along +Z; in Unity's left-handed frame their
    # right is then local +X (the same rule a world-space Canvas follows).
    return Vector(board["center"]), right, up, towards_reader


def board_point(board, x, y, depth):
    c, r, u, t = board_matrix(board)
    return c + r * x + u * y + t * depth


def board_rotation_b(board):
    """Blender rotation of a flat board part: local X = reader's right, local Z = up and
    local -Y = towards the reader (rounded_panel is built in that frame).
    right x away = up in a right-handed frame, so this is a proper rotation."""
    c, r, u, t = board_matrix(board)
    rb, ub, tb = u2b(r), u2b(u), u2b(t)
    m = Matrix((rb, -tb, ub)).transposed()  # columns: local X, local Y, local Z
    assert abs(m.determinant() - 1.0) < 1e-4, m.determinant()
    return m.to_euler()


# ------------------------------------------------------------------------- materials ---

MATERIALS = {}


def material(name, rgb, rough=0.55, emit=0.0):
    if name in MATERIALS:
        return MATERIALS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    col = (rgb[0], rgb[1], rgb[2], 1.0)
    bsdf.inputs["Base Color"].default_value = col
    bsdf.inputs["Roughness"].default_value = rough
    if emit > 0.0:
        for key in ("Emission Color", "Emission"):
            if key in bsdf.inputs:
                bsdf.inputs[key].default_value = col
                break
        if "Emission Strength" in bsdf.inputs:
            bsdf.inputs["Emission Strength"].default_value = emit
    m.diffuse_color = col
    MATERIALS[name] = m
    return m


def srgb_to_linear(c):
    return [((v + 0.055) / 1.055) ** 2.4 if v > 0.04045 else v / 12.92 for v in c]


# ---------------------------------------------------------------------------- meshes ---

ROOT = None
COLL = None


def link(obj, parent=None):
    COLL.objects.link(obj)
    obj.parent = parent if parent is not None else ROOT
    return obj


def box(name, size_u, center_u, mat, rot_b=None, bevel=0.0, parent=None):
    """Axis box with size given in Unity axes (x, y=height, z)."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size_u[0], v.co.y * size_u[2], v.co.z * size_u[1]))
    if bevel > 0.0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=3, affect="EDGES",
                        profile=0.5)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = bevel > 0.0
    ob = bpy.data.objects.new(name, me)
    ob.location = u2b(center_u)
    if rot_b is not None:
        ob.rotation_euler = rot_b
    return link(ob, parent)


def rounded_panel(name, w, h, d, radius, mat, parent):
    """Flat rounded rectangle in the panel frame (local X right, local Z up, thickness on Y)."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    r = min(radius, w * 0.5 - 1e-3, h * 0.5 - 1e-3)
    pts = []
    seg = 6
    corners = [(w / 2 - r, h / 2 - r, 0), (-w / 2 + r, h / 2 - r, 90),
               (-w / 2 + r, -h / 2 + r, 180), (w / 2 - r, -h / 2 + r, 270)]
    for cx, cz, a0 in corners:
        for k in range(seg + 1):
            a = math.radians(a0 + 90.0 * k / seg)
            pts.append((cx + r * math.cos(a), cz + r * math.sin(a)))
    front = [bm.verts.new((x, -d / 2, z)) for x, z in pts]
    back = [bm.verts.new((x, d / 2, z)) for x, z in pts]
    bm.faces.new(front[::-1])
    bm.faces.new(back)
    n = len(pts)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((front[i], front[j], back[j], back[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    return link(ob, parent)


def place_on_board(ob, board, x, y, depth):
    ob.location = u2b(board_point(board, x, y, depth))
    ob.rotation_euler = board_rotation_b(board)


def empty(name, board, x, y, depth, w, h, parent):
    ob = bpy.data.objects.new(name, None)
    ob.empty_display_type = "PLAIN_AXES"
    ob.empty_display_size = 0.05
    link(ob, parent)
    place_on_board(ob, board, x, y, depth)
    ob["w"] = w
    ob["h"] = h
    return ob


def cylinder(name, radius, height, center_u, mat, verts=24, parent=None):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=verts, radius1=radius, radius2=radius,
                          depth=height)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    ob.location = u2b(center_u)
    return link(ob, parent)


def sphere(name, radius, center_u, mat, parent=None):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=20, v_segments=12, radius=radius)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    ob.location = u2b(center_u)
    return link(ob, parent)


# ------------------------------------------------------------------------------ room ---

def build(layout):
    """Direction 1 ("warm wood and paper"): every surface is one of the layout's surfaces
    (hub_layout.json "surfaces"; textures tile on the metre UVs set by metre_uvs)."""
    global ROOT, COLL
    M = {e["name"]: material(e["name"], srgb_to_linear(e["rgb"]), rough=1.0 - e.get("smoothness", 0.2))
         for e in layout["surfaces"]}
    COLL = bpy.data.collections.new("HubRoomArt")
    bpy.context.scene.collection.children.link(COLL)
    ROOT = bpy.data.objects.new(layout["model"]["rootName"], None)
    COLL.objects.link(ROOT)

    # Floor: honey wood planks, inset 1 cm so the imported placement floor (hub_floor, 4 cm
    # slab at y=0) stays the walkable collider.
    box("Floor", [ROOM_W, 0.02, ROOM_D], [0, 0.021, 0], M["Hub_FloorWood"])
    # Reading rug in front of the level board (sage felt): the solo players' area.
    lv = next(b for b in layout["boards"] if b["id"] == "Levels")
    rug_w = lv["size"][0] - 0.8
    box("ReadingRug", [rug_w, 0.012, 2.6], [lv["center"][0], 0.037, lv["center"][2] - 1.75],
        M["Hub_RugFelt"], bevel=0.004)
    box("ReadingRugEdge", [rug_w + 0.12, 0.008, 2.72], [lv["center"][0], 0.034, lv["center"][2] - 1.75],
        M["Hub_Walnut"])

    # Walls: lime plaster, walnut skirting and chair rail, oat cornice.
    walls = [("North", [0, 0, ROOM_D / 2], [ROOM_W, 0, 0.1]),
             ("South", [0, 0, -ROOM_D / 2], [ROOM_W, 0, 0.1]),
             ("West", [-ROOM_W / 2, 0, 0], [0.1, 0, ROOM_D]),
             ("East", [ROOM_W / 2, 0, 0], [0.1, 0, ROOM_D])]
    # Inset 7 cm so these faces sit in front of the placement document's own boundary planes
    # (4 cm thick on x = +-8 / z = +-4, imported by the room import as the collision walls)
    # instead of z-fighting with them.
    walls = [(n, [c[0] * (1 - 0.14 / ROOM_W) if c[0] else 0, 0, c[2] * (1 - 0.14 / ROOM_D) if c[2]
                  else 0], s) for n, c, s in walls]
    for name, c, s in walls:
        sx, sz = (s[0], 0.08) if s[0] > 1 else (0.08, s[2])
        box("Wall%s" % name, [sx, WALL_H, sz], [c[0], WALL_H / 2, c[2]], M["Hub_WallPlaster"])
        box("Wall%s_Skirt" % name, [sx + 0.04, 0.16, sz + 0.04], [c[0], 0.08, c[2]], M["Hub_Walnut"])
        box("Wall%s_Rail" % name, [sx + 0.05, 0.06, sz + 0.05], [c[0], 1.05, c[2]], M["Hub_Walnut"])
        box("Wall%s_Cornice" % name, [sx + 0.08, 0.12, sz + 0.08], [c[0], WALL_H - 0.06, c[2]],
            M["Hub_Oat"])

    # ---- boards ----
    # The model carries only each board's frame and legs. The panel itself (face, title band,
    # cards, rows, text, buttons) is a world-space uGUI prefab
    # (Docs/Design/StoryMode/HUB_PANEL_PREFAB.md) that the Unity assembler places at
    # Anchor_Board_<id>: the board's centre, canvasDepthM in front of the board plane, just in
    # front of the frame's front face (frame depth 0.10 -> front face at 0.05). A clickable
    # board has a wooden frame; a read-only screen (Help) has a thin slate one.
    # Anchors are direct children of the root, never grandchildren: with
    # bake_space_transform the 4.0 exporter converts a node's transform by its parent's
    # *converted* local matrix, so the axis change is applied once for a root child and lost
    # one level deeper (R53). verify_hub_fbx.py checks the exported file.
    boards = {b["id"]: b for b in layout["boards"]}
    canvas_depth = layout["canvasDepthM"]
    for b in layout["boards"]:
        w, h = b["size"]
        screen = b["frame"] == "Hub_Slate"
        margin = 0.08 if screen else 0.24
        frame = rounded_panel("Board%s_Frame" % b["id"], w + margin, h + margin, 0.10,
                              0.06 if screen else 0.22, M[b["frame"]], ROOT)
        place_on_board(frame, b, 0, 0, 0.0)
        empty("Anchor_Board_%s" % b["id"], b, 0, 0, canvas_depth, w, h, ROOT)
        # Feet so the boards stand, not float.
        for side in (-1, 1):
            leg = box("Board%s_Leg%s" % (b["id"], "L" if side < 0 else "R"),
                      [0.12, b["center"][1] - h / 2, 0.12],
                      [0, 0, 0], M["Hub_Walnut"], bevel=0.02)
            p = board_point(b, side * (w / 2 - 0.4), 0, -0.02)
            leg.location = u2b([p.x, (b["center"][1] - h / 2) / 2, p.z])

    # ---- decor: a katamari, plants, paper lamps; small muted accents only ----
    ball_cols = ["Hub_Brick", "Hub_Gold", "Hub_Lake", "Hub_Paper", "Hub_Sage", "Hub_Walnut"]
    sphere("DecorBall_Core", 0.42, [-6.6, 0.47, -2.6], M["Hub_Oat"])
    for k in range(16):
        a = k * 2.399963
        z = 1 - 2 * (k + 0.5) / 16
        rr = math.sqrt(1 - z * z)
        p = [-6.6 + 0.45 * rr * math.cos(a), 0.47 + 0.45 * z, -2.6 + 0.45 * rr * math.sin(a)]
        if k % 2:
            box("DecorBall_Bit%02d" % k, [0.16, 0.16, 0.16], p, M[ball_cols[k % 6]], bevel=0.03)
        else:
            sphere("DecorBall_Bit%02d" % k, 0.09, p, M[ball_cols[k % 6]])
    for k, (x, z) in enumerate([(-7.3, 3.3), (7.3, 3.3), (-7.3, -3.3), (7.3, -3.3)]):
        cylinder("Plant%d_Pot" % k, 0.22, 0.4, [x, 0.2, z], M["Hub_Brick"])
        sphere("Plant%d_Leaves" % k, 0.36, [x, 0.72, z], M["Hub_Sage"])
        sphere("Plant%d_Leaves2" % k, 0.24, [x + 0.12, 1.02, z - 0.05], M["Hub_Sage"])
    # Paper pendant lamps (emissive in Blender only; Unity lighting is the assembler's).
    lamp_mat = material("Hub_LampGlow", srgb_to_linear([1.0, 0.95, 0.8]), emit=2.0)
    for k, lp in enumerate(layout["lampsLocal"]):
        cylinder("Lamp%d_Shade" % k, 0.35, 0.18, [lp[0], 2.85, lp[2]], M["Hub_Paper"])
        sphere("Lamp%d_Bulb" % k, 0.14, [lp[0], 2.72, lp[2]], lamp_mat)
    # Spawn mark: a terrazzo disc inlaid in the floor with a small gold star.
    sp = layout["spawnLocal"]
    cylinder("SpawnDisc", 0.55, 0.012, [sp[0], 0.037, sp[2]], M["Hub_Terrazzo"], 40)
    cylinder("SpawnDiscRim", 0.6, 0.008, [sp[0], 0.034, sp[2]], M["Hub_Walnut"], 40)
    star = star_mesh("SpawnStar", 0.26, 0.12, M["Hub_Gold"])
    star.location = u2b([sp[0], 0.044, sp[2]])
    return boards


def metre_uvs():
    """UV0 of every mesh = its world position in metres, projected along the face's dominant
    axis (floors and ceilings: x/z, walls: the wall's own horizontal axis and height). The
    materials tile 1 / metresPerTile per UV unit, so a texture keeps its size on every surface
    and lines up across neighbouring objects."""
    bpy.context.view_layer.update()
    for ob in COLL.all_objects:
        if ob.type != "MESH":
            continue
        me = ob.data
        uv = me.uv_layers.active or me.uv_layers.new(name="UVMap")
        mw = ob.matrix_world
        rot = mw.to_3x3()
        for poly in me.polygons:
            n = rot @ poly.normal
            ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
            for li in poly.loop_indices:
                co = mw @ me.vertices[me.loops[li].vertex_index].co
                if az >= ax and az >= ay:      # Blender z is up: floor / ceiling
                    u, v = co.x, co.y
                elif ax >= ay:                 # faces along Blender x
                    u, v = co.y, co.z
                else:                          # faces along Blender y
                    u, v = co.x, co.z
                uv.data[li].uv = (u, v)


def star_mesh(name, r_out, r_in, mat):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    pts = []
    for k in range(10):
        a = math.pi / 2 + k * math.pi / 5
        r = r_out if k % 2 == 0 else r_in
        pts.append((r * math.cos(a), r * math.sin(a)))
    top = [bm.verts.new((x, y, 0.012)) for x, y in pts]
    bot = [bm.verts.new((x, y, 0.0)) for x, y in pts]
    bm.faces.new(top)
    bm.faces.new(bot[::-1])
    for i in range(10):
        j = (i + 1) % 10
        bm.faces.new((bot[i], bot[j], top[j], top[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    return link(bpy.data.objects.new(name, me))


# --------------------------------------------------------------------------- preview ---

def preview_panels(layout, boards):
    """Flat rectangles in each board's face colour where the panel prefabs go, preview only
    (Unity draws the panels)."""
    pal = {e["name"]: srgb_to_linear(e["rgb"]) for e in layout["surfaces"]}
    for b in layout["boards"]:
        w, h = b["size"]
        face_mat = material("Preview_Face_" + b["face"], pal[b["face"]])
        face = rounded_panel("PreviewPanel_%s" % b["id"], w, h, 0.002, 0.16, face_mat, None)
        COLL.objects.unlink(face)
        bpy.context.scene.collection.objects.link(face)
        face.parent = None
        place_on_board(face, b, 0, 0, layout["canvasDepthM"])


def render(path, cam_u, look_u, lens, samples, res=(1600, 900)):
    scene = bpy.context.scene
    cam = bpy.data.objects.get("PreviewCam")
    if cam is None:
        cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
        scene.collection.objects.link(cam)
    cam.data.lens = lens
    cam.data.clip_end = 200
    cam.location = u2b(cam_u)
    direction = u2b(look_u) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = res
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("[hubart] preview " + path)


def preview_textures(layout):
    """Preview only, after the FBX export: give the textured surfaces their image (tiled on the
    metre UVs), so the renders show what Unity will draw. The exported FBX carries material
    names only; the assembler builds the real materials."""
    tex = {t["name"]: t["file"] for t in layout.get("textures", [])}
    for e in layout["surfaces"]:
        m = MATERIALS.get(e["name"])
        if m is None or not e.get("texture"):
            continue
        path = os.path.join(REPO, "VRChatKatamariDanacyUnity", tex[e["texture"]])
        if not os.path.exists(path):
            continue
        nodes, links = m.node_tree.nodes, m.node_tree.links
        bsdf = nodes.get("Principled BSDF")
        img = nodes.new("ShaderNodeTexImage")
        img.image = bpy.data.images.load(path, check_existing=True)
        coord = nodes.new("ShaderNodeTexCoord")
        mapping = nodes.new("ShaderNodeMapping")
        k = 1.0 / e["metresPerTile"]
        mapping.inputs["Scale"].default_value = (k, k, 1.0)
        tint = nodes.new("ShaderNodeMixRGB")
        tint.blend_type = "MULTIPLY"
        tint.inputs["Fac"].default_value = 1.0
        tint.inputs["Color2"].default_value = tuple(srgb_to_linear(e["rgb"])) + (1.0,)
        links.new(coord.outputs["UV"], mapping.inputs["Vector"])
        links.new(mapping.outputs["Vector"], img.inputs["Vector"])
        links.new(img.outputs["Color"], tint.inputs["Color1"])
        links.new(tint.outputs["Color"], bsdf.inputs["Base Color"])


def setup_render(samples):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.eevee.taa_render_samples = samples
    if hasattr(scene.eevee, "use_gtao"):
        scene.eevee.use_gtao = True
    if hasattr(scene.eevee, "use_soft_shadows"):
        scene.eevee.use_soft_shadows = True
    scene.view_settings.view_transform = "Standard"
    world = scene.world or bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.75, 0.85, 1.0, 1.0)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.7
    sun = bpy.data.objects.new("PreviewSun", bpy.data.lights.new("PreviewSun", "SUN"))
    sun.data.energy = 2.2
    sun.rotation_euler = Euler((math.radians(50), math.radians(10), math.radians(200)))
    scene.collection.objects.link(sun)
    for k, lp in enumerate(json.load(open(LAYOUT_PATH, encoding="utf-8"))["lampsLocal"]):
        pt = bpy.data.objects.new("PreviewLamp%d" % k, bpy.data.lights.new("PL%d" % k, "POINT"))
        pt.data.energy = 220
        pt.data.shadow_soft_size = 0.6
        pt.location = u2b(lp)
        scene.collection.objects.link(pt)


# ---------------------------------------------------------------------------- checks ---

def check_handedness():
    """Pins the Unity -> Blender mapping documented on u2b. The import half (that Unity really
    places Anchor_Board_* where hub_layout.json says) is checked by HubAssembler on the imported
    model, which reports any anchor more than 2 cm off."""
    b = u2b((1.0, 2.0, 3.0))
    assert (b.x, b.y, b.z) == (-1.0, -3.0, 2.0), b


def join_by_material():
    """One mesh object per material: the hub draws in ~15 calls instead of ~230. Anchors stay."""
    groups = {}
    for ob in list(COLL.all_objects):
        if ob.type != "MESH" or not ob.data.materials:
            continue
        groups.setdefault(ob.data.materials[0].name, []).append(ob)
    for name, obs in groups.items():
        bpy.ops.object.select_all(action="DESELECT")
        for ob in obs:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = obs[0]
        if len(obs) > 1:
            bpy.ops.object.join()
        joined = bpy.context.view_layer.objects.active
        joined.name = "Art_" + name
        joined.data.name = "Art_" + name


def export_fbx(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for ob in COLL.all_objects:
        ob.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        object_types={"MESH", "EMPTY"}, mesh_smooth_type="FACE", add_leaf_bones=False,
        use_mesh_modifiers=True, use_custom_props=False)
    print("[hubart] fbx " + path + " bytes=" + str(os.path.getsize(path)))


def main():
    a = args()
    with open(LAYOUT_PATH, encoding="utf-8") as fh:
        layout = json.load(fh)
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    check_handedness()
    boards = build(layout)
    metre_uvs()
    meshes = sum(1 for o in COLL.all_objects if o.type == "MESH")
    anchors = sum(1 for o in COLL.all_objects if o.name.startswith("Anchor_"))
    tris = 0
    for o in COLL.all_objects:
        if o.type == "MESH":
            tris += sum(len(p.vertices) - 2 for p in o.data.polygons)
    print("[hubart] built meshes=%d anchors=%d tris=%d materials=%d" % (
        meshes, anchors, tris, len(MATERIALS)))
    if a["fbx_on"]:
        join_by_material()
        meshes = sum(1 for o in COLL.all_objects if o.type == "MESH")
        print("[hubart] joined into %d mesh objects" % meshes)
        export_fbx(a["fbx"])
    if a["preview_on"]:
        preview_textures(layout)
        setup_render(a["samples"])
        preview_panels(layout, boards)
        d = a["preview_dir"]
        sp = layout["spawnLocal"]
        render(os.path.join(d, "hub_art_overview.png"), [0, 13.5, -9.5], [0, 0.6, 0.6], 22,
               a["samples"])
        # Eye height 1.6 m at HubSpawn; lens 14 mm on Blender's 36 mm sensor = 104 deg
        # horizontal, close to a headset's field of view.
        render(os.path.join(d, "hub_art_spawn_view.png"), [sp[0], 1.6, sp[2]], [0, 1.45, 3.8], 14,
               a["samples"])
        render(os.path.join(d, "hub_art_levels_2m.png"), [0, 1.6, 1.8], [0, 1.45, 3.8], 14,
               a["samples"])
        render(os.path.join(d, "hub_art_levels_3m.png"), [0, 1.6, 0.8], [-1.55, 1.6, 3.8], 30,
               a["samples"])
        for bid, name in (("Help", "help"), ("Settings", "settings")):
            b = boards[bid]
            for dist in (2.0, 3.0):
                c, r, u, t = board_matrix(b)
                eye = c + t * dist
                render(os.path.join(d, "hub_art_%s_%dm.png" % (name, int(dist))),
                       [eye.x, 1.6, eye.z], [c.x, c.y, c.z], 14 if dist < 2.5 else 18, a["samples"])

main()
