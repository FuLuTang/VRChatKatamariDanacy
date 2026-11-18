"""Builds the hub room's art (floor, walls, the three wall boards, card plates, decor and the
Anchor_* empties the Unity assembler hangs text and buttons on) from hub_layout.json, exports
the FBX into the Unity project and renders review previews.

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

Everything is generated here from primitive shapes and flat colours - no reference-game
asset, no texture file. Materials are named Hub_<colour>; the FBX carries the names only and
the Unity assembler builds the matching materials.
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
PREVIEW_FONT = "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc"
# Blender sets this collection's glyphs at about 0.344 x size per CJK character (measured:
# "选择关卡" at size 1 is 1.376 wide), while Unity's fontSize x scale is the em, ~1 per CJK
# character. Multiplying keeps the preview's glyphs the size Unity will draw.
PREVIEW_EM_TO_SIZE = 1.0 / 0.344

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
    local -Y = towards the reader (rounded_panel and triangle are built in that frame).
    right x away = up in a right-handed frame, so this is a proper rotation."""
    c, r, u, t = board_matrix(board)
    rb, ub, tb = u2b(r), u2b(u), u2b(t)
    m = Matrix((rb, -tb, ub)).transposed()  # columns: local X, local Y, local Z
    assert abs(m.determinant() - 1.0) < 1e-4, m.determinant()
    return m.to_euler()


def text_rotation_b(board):
    """Rotation of a preview text curve (glyphs in local XY, read from local +Z)."""
    c, r, u, t = board_matrix(board)
    m = Matrix((u2b(r), u2b(u), u2b(t))).transposed()
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
    global ROOT, COLL
    pal = {e["name"]: srgb_to_linear(e["rgb"]) for e in layout["palette"]}
    M = {k: material(k, v) for k, v in pal.items()}
    COLL = bpy.data.collections.new("HubRoomArt")
    bpy.context.scene.collection.children.link(COLL)
    ROOT = bpy.data.objects.new(layout["model"]["rootName"], None)
    COLL.objects.link(ROOT)

    # Floor: cream base with a checker of big pastel tiles, inset 1 cm so the imported
    # placement floor (hub_floor, 4 cm slab at y=0) stays the walkable collider.
    box("Floor", [ROOM_W, 0.02, ROOM_D], [0, 0.021, 0], M["Hub_Cream"])
    tile = 1.6
    colours = ["Hub_Peach", "Hub_Sky", "Hub_Lemon", "Hub_Mint"]
    nx, nz = int(ROOM_W / tile), int(ROOM_D / tile)
    k = 0
    for ix in range(nx):
        for iz in range(nz):
            if (ix + iz) % 2:
                continue
            cx = -ROOM_W / 2 + tile * (ix + 0.5)
            cz = -ROOM_D / 2 + tile * (iz + 0.5)
            box("FloorTile_%02d" % k, [tile - 0.04, 0.004, tile - 0.04], [cx, 0.033, cz],
                M[colours[(ix // 2 + iz) % 4]])
            k += 1

    # Walls: two-tone (pink lower band, sky upper), wood skirting, white cornice.
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
        box("Wall%s_Lower" % name, [sx, 1.1, sz], [c[0], 0.55, c[2]], M["Hub_Pink"])
        box("Wall%s_Upper" % name, [sx, WALL_H - 1.1, sz], [c[0], 1.1 + (WALL_H - 1.1) / 2, c[2]],
            M["Hub_Sky"])
        box("Wall%s_Skirt" % name, [sx + 0.04, 0.14, sz + 0.04], [c[0], 0.07, c[2]], M["Hub_Wood"])
        box("Wall%s_Rail" % name, [sx + 0.06, 0.07, sz + 0.06], [c[0], 1.12, c[2]], M["Hub_White"])
        box("Wall%s_Cornice" % name, [sx + 0.08, 0.12, sz + 0.08], [c[0], WALL_H - 0.06, c[2]],
            M["Hub_White"])
    # Polka dots on the upper walls of the south side, for the behind-you view.
    dot_cols = ["Hub_Lemon", "Hub_White", "Hub_Pink", "Hub_Mint"]
    for i in range(14):
        x = -7.0 + i * 1.07
        y = 1.6 + (0.45 if i % 2 else 0.0)
        d = cylinder("WallSouth_Dot_%02d" % i, 0.16, 0.02, [x, y, -ROOM_D / 2 + 0.06],
                     M[dot_cols[i % 4]], 20)
        d.rotation_euler = Euler((math.radians(90), 0, 0))

    # ---- boards ----
    # Anchors are direct children of the root, never grandchildren: with
    # bake_space_transform the 4.0 exporter converts a node's transform by its parent's
    # *converted* local matrix, so the axis change is applied once for a root child and lost
    # one level deeper (R53: every anchor under an "Anchors" group came out as Blender
    # (x, y, z) unconverted). verify_hub_fbx.py checks the exported file.
    anchors = ROOT
    boards = {b["id"]: b for b in layout["boards"]}
    for b in layout["boards"]:
        w, h = b["size"]
        frame = rounded_panel("Board%s_Frame" % b["id"], w + 0.24, h + 0.24, 0.10, 0.22,
                              M[b["frame"]], ROOT)
        place_on_board(frame, b, 0, 0, 0.0)
        face = rounded_panel("Board%s_Face" % b["id"], w, h, 0.02, 0.16, M[b["face"]], ROOT)
        place_on_board(face, b, 0, 0, 0.06)
        band = rounded_panel("Board%s_TitleBand" % b["id"], w * 0.62, 0.36, 0.02, 0.15,
                             M[b["frame"]], ROOT)
        place_on_board(band, b, 0, h / 2 - 0.2, 0.08)
        # Feet / brackets so the boards stand, not float.
        for side in (-1, 1):
            leg = box("Board%s_Leg%s" % (b["id"], "L" if side < 0 else "R"),
                      [0.12, b["center"][1] - h / 2, 0.12],
                      [0, 0, 0], M["Hub_Wood"], bevel=0.02)
            p = board_point(b, side * (w / 2 - 0.4), 0, -0.02)
            leg.location = u2b([p.x, (b["center"][1] - h / 2) / 2, p.z])
        # Sun-burst bolts in the frame corners.
        for i, (sx, sy) in enumerate([(-1, 1), (1, 1), (-1, -1), (1, -1)]):
            bolt = sphere("Board%s_Bolt%d" % (b["id"], i), 0.06, [0, 0, 0], M["Hub_Lemon"])
            bolt.location = u2b(board_point(b, sx * (w / 2 + 0.04), sy * (h / 2 + 0.04), 0.07))

    for t in layout["texts"]:
        b = boards[t["board"]]
        empty("Anchor_Text_%s" % t["id"], b, t["x"], t["y"], t["depth"], t["w"], t["h"], anchors)

    for c in layout["levelCards"]:
        b = boards[c["board"]]
        i = c["index"]
        plate = rounded_panel("LevelCard%d_Plate" % i, c["w"], c["h"], 0.05, 0.14, M[c["plate"]],
                              ROOT)
        place_on_board(plate, b, c["x"], c["y"], c["depth"] - 0.025)
        inner = rounded_panel("LevelCard%d_Inner" % i, c["w"] - 0.08, c["h"] - 0.08, 0.01, 0.1,
                              M["Hub_White"], ROOT)
        place_on_board(inner, b, c["x"], c["y"], c["depth"] + 0.002)
        pv = c["preview"]
        frame = rounded_panel("LevelCard%d_PreviewFrame" % i, pv["w"] + 0.06, pv["h"] + 0.06, 0.02,
                              0.06, M[c["plate"]], ROOT)
        place_on_board(frame, b, c["x"] + pv["x"], c["y"] + pv["y"], pv["depth"] - 0.012)
        swatch = pv["swatch"]  # dark enough for the white level number
        sw = rounded_panel("LevelCard%d_Preview" % i, pv["w"], pv["h"], 0.005, 0.04, M[swatch],
                           ROOT)
        place_on_board(sw, b, c["x"] + pv["x"], c["y"] + pv["y"], pv["depth"] + 0.004)
        st = c["start"]
        pill = rounded_panel("LevelCard%d_StartPill" % i, st["w"], st["h"], 0.03, st["h"] / 2,
                             M["Hub_Tomato"], ROOT)
        place_on_board(pill, b, c["x"] + st["x"], c["y"] + st["y"], st["depth"] - 0.016)
        empty("Anchor_Level%d_Button" % i, b, c["x"], c["y"], c["depth"] + 0.03, c["w"], c["h"],
              anchors)
        for key in ("preview", "name", "goal", "record", "start"):
            e = c[key]
            empty("Anchor_Level%d_%s" % (i, key.capitalize()), b, c["x"] + e["x"], c["y"] + e["y"],
                  e["depth"], e["w"], e["h"] if "h" in e else 0.2, anchors)

    for r in layout["settingRows"]:
        b = boards[r["board"]]
        i = r["id"]
        y = r["y"]
        if i % 2 == 0:
            stripe = rounded_panel("SettingRow%02d_Stripe" % i, 2.95, r["h"], 0.005, 0.05,
                                   M["Hub_Peach"], ROOT)
            place_on_board(stripe, b, 0, y, 0.072)
        for key, sign in (("less", -1), ("more", 1)):
            e = r[key]
            pad = rounded_panel("SettingRow%02d_%s" % (i, key.capitalize()), e["w"], e["h"], 0.03,
                                e["h"] / 2, M["Hub_Grape"], ROOT)
            place_on_board(pad, b, e["x"], y, e["depth"] - 0.015)
            arrow = triangle("SettingRow%02d_%sArrow" % (i, key.capitalize()), e["h"] * 0.32,
                             sign, M["Hub_White"])
            place_on_board(arrow, b, e["x"], y, e["depth"] + 0.003)
            empty("Anchor_Setting%02d_%s" % (i, key.capitalize()), b, e["x"], y, e["depth"],
                  e["w"], e["h"], anchors)
        for key in ("label", "value"):
            e = r[key]
            empty("Anchor_Setting%02d_%s" % (i, key.capitalize()), b, e["x"], y, e["depth"], e["w"],
                  r["h"], anchors)

    # ---- decor: bunting along the side and back walls, a katamari, plants, lamps ----
    flag_cols = ["Hub_Tomato", "Hub_Orange", "Hub_Lemon", "Hub_Mint", "Hub_Sky", "Hub_Grape"]
    runs = [([-7.9, 2.62, -3.9], [7.9, 2.62, -3.9]), ([-7.9, 2.62, -3.6], [-7.9, 2.62, 1.0]),
            ([7.9, 2.62, -3.6], [7.9, 2.62, 1.0])]
    k = 0
    for p0, p1 in runs:
        a0, a1 = Vector(p0), Vector(p1)
        n = max(2, int((a1 - a0).length / 0.55))
        for j in range(n):
            q0 = a0.lerp(a1, (j + 0.1) / n)
            q1 = a0.lerp(a1, (j + 0.9) / n)
            sag = 0.10 * math.sin(math.pi * (j + 0.5) / n)
            tip = (q0 + q1) / 2 + Vector((0, -0.34 - sag, 0))
            q0 = q0 + Vector((0, -sag, 0))
            q1 = q1 + Vector((0, -sag, 0))
            flat_triangle("Bunting_%02d" % k, q0, q1, tip, M[flag_cols[k % 6]])
            k += 1
    ball_cols = ["Hub_Pink", "Hub_Lemon", "Hub_Sky", "Hub_Mint", "Hub_Grape", "Hub_Orange"]
    core = sphere("DecorBall_Core", 0.42, [-6.6, 0.47, -2.6], M["Hub_Mint"])
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
        cylinder("Plant%d_Pot" % k, 0.22, 0.4, [x, 0.2, z], M["Hub_Tomato"])
        sphere("Plant%d_Leaves" % k, 0.36, [x, 0.72, z], M["Hub_Leaf"])
        sphere("Plant%d_Leaves2" % k, 0.24, [x + 0.12, 1.02, z - 0.05], M["Hub_Leaf"])
    # Ceiling lamps (emissive in Blender only; Unity lighting is the assembler's).
    lamp_mat = material("Hub_LampGlow", srgb_to_linear([1.0, 0.95, 0.8]), emit=2.0)
    for k, lp in enumerate(layout["lampsLocal"]):
        cylinder("Lamp%d_Shade" % k, 0.35, 0.18, [lp[0], 2.85, lp[2]], M["Hub_Lemon"])
        sphere("Lamp%d_Bulb" % k, 0.14, [lp[0], 2.72, lp[2]], lamp_mat)
    # Spawn mark: a welcome star on the floor where the player appears.
    sp = layout["spawnLocal"]
    star = star_mesh("SpawnStar", 0.42, 0.2, M["Hub_Lemon"])
    star.location = u2b([sp[0], 0.04, sp[2]])
    return boards


def flat_triangle(name, p0_u, p1_u, p2_u, mat):
    """One-sided triangle (Unity points) whose front faces the room centre."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    vs = [bm.verts.new(u2b(p)) for p in (p0_u, p1_u, p2_u)]
    f = bm.faces.new(vs)
    bm.normal_update()
    centre = (vs[0].co + vs[1].co + vs[2].co) / 3
    if f.normal.dot(u2b([0, 1.5, 0]) - centre) < 0:
        f.normal_flip()
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    return link(bpy.data.objects.new(name, me))


def triangle(name, size, sign, mat):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    a = bm.verts.new((sign * size, 0, 0))
    b = bm.verts.new((-sign * size * 0.6, 0, size * 0.9))
    c = bm.verts.new((-sign * size * 0.6, 0, -size * 0.9))
    f = bm.faces.new((a, b, c))
    bmesh.ops.recalc_face_normals(bm, faces=[f])
    if f.normal.y > 0:
        f.normal_flip()
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    return link(ob)


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

def preview_text(layout, boards):
    """Sample text in the anchors' places, preview only (Unity draws the real text)."""
    font = bpy.data.fonts.load(PREVIEW_FONT) if os.path.exists(PREVIEW_FONT) else None
    pal = {e["name"]: srgb_to_linear(e["rgb"]) for e in layout["palette"]}
    navy = material("Preview_Navy", pal["Hub_Navy"])
    white = material("Preview_White", pal["Hub_White"])
    sample = {
        "LevelsTitle": "选择关卡", "Prompt": "点墙上的关卡卡片开始 · 桌面：对准后左键 · VR：指向后扳机",
        "Credits": "VRC塊魂 · 粉丝制作的 VRChat 世界，与官方无关", "HelpTitle": "怎么玩",
        "HowTo": "· 滚动块魂，碰到比球小的东西就会粘上来\n· 粘得越多球越大，能吸的东西也越大\n· 限时内把球滚到目标大小；时间到自动结算\n· 结果显示 8 秒后自动回大厅\n· 菜单里的 Respawn = 放弃本局并回大厅",
        "ControlsTitle": "操作",
        "ControlsVr": "VR\n移动：两根摇杆一起推＝前进\n冲刺：长按左扳机蓄力，松手冲出\n道具：Jump 键\n下车：按住 Jump 1.5 秒",
        "ControlsDesktop": "桌面\n移动：W/S 前后，A/D 转向\n冲刺：冲刺键\n道具：空格（Jump）\n下车：按住空格 1.5 秒",
        "SettingsTitle": "设置", "SettingsHint": "点左右箭头修改，自动保存"}
    names = ["怀旧关：蟹座小院", "野餐一角", "地图 0：物理测试场", "地形实验室"]
    goals = ["限时 7:00 · 目标 80 cm", "限时 2:00 · 目标 1.25 m", "自由练习 · 不计时", "自由练习 · 不计时"]
    recs = ["最好 82 cm · 135 件\n上次 61 cm · 90 件  ·  已玩 3", "还没有记录", "", ""]
    labels = ["VR 操作方式", "左右手互换", "摇杆死区", "转向灵敏度", "音乐", "音效", "提示音", "振动",
              "低特效", "减弱自动转向", "语言 / Language / 言語"]
    values = ["原作双摇杆", "关", "0.20", "×1.0", "80%", "80%", "80%", "标准", "关", "关", "简体中文"]

    def put(text, board, x, y, depth, w, em, align, mat):
        if not text:
            return
        cu = bpy.data.curves.new("txt", "FONT")
        if font:
            cu.font = font
        cu.body = text
        cu.size = em * PREVIEW_EM_TO_SIZE
        cu.space_line = 0.42
        cu.align_x = {"left": "LEFT", "center": "CENTER", "right": "RIGHT"}[align]
        cu.align_y = "CENTER"
        ob = bpy.data.objects.new("PreviewText", cu)
        ob.data.materials.append(mat)
        COLL.objects.link(ob)
        ox = x - w / 2 if align == "left" else x
        ob.location = u2b(board_point(board, ox, y, depth))
        ob.rotation_euler = text_rotation_b(board)

    for t in layout["texts"]:
        mat = white if t["color"] == "Hub_White" else navy
        put(sample.get(t["id"], ""), boards[t["board"]], t["x"], t["y"], t["depth"] + 0.02, t["w"],
            t["em"], t["align"], mat)
    for c in layout["levelCards"]:
        b = boards[c["board"]]
        i = c["index"]
        for key, txt in (("name", names[i]), ("goal", goals[i]), ("record", recs[i]),
                         ("start", "开始 →")):
            e = c[key]
            put(txt, b, c["x"] + e["x"], c["y"] + e["y"], e["depth"] + 0.02, e["w"], e["em"],
                e["align"], white if key == "start" else navy)
        pv = c["preview"]
        put(str(i + 1), b, c["x"] + pv["x"], c["y"] + pv["y"], pv["depth"] + 0.02, pv["w"],
            pv["em"], "center", white)
    for r in layout["settingRows"]:
        b = boards[r["board"]]
        put(labels[r["id"]], b, r["label"]["x"], r["y"], r["label"]["depth"] + 0.02, r["label"]["w"],
            r["label"]["em"], "left", navy)
        put(values[r["id"]], b, r["value"]["x"], r["y"], r["value"]["depth"] + 0.02, r["value"]["w"],
            r["value"]["em"], "center", navy)


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
    places Anchor_* where hub_layout.json says) is checked by HubAssembler on the imported
    model, which reports any anchor more than 1 cm off."""
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
        setup_render(a["samples"])
        preview_text(layout, boards)
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
