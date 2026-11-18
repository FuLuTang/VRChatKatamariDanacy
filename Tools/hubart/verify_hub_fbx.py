"""Independent check of HubRoomArt.fbx: reads the FBX nodes directly (no Blender import),
composes each Anchor_* node's world transform the way the FBX SDK does, converts it to Unity's
frame the way Unity's importer does, and compares it with hub_layout.json.

    source ~/tools/blender-env.sh
    $BLENDER $BLENDER_FLAGS --python Tools/hubart/verify_hub_fbx.py -- [path/to/HubRoomArt.fbx]

Only Blender's bundled FBX *parser* (io_scene_fbx.parse_fbx, a plain binary reader) is used,
so this does not share code with the exporter it checks. Conversions, stated so a reader can
check them:
  * FBX node world = parent world @ T(Lcl Translation) @ R(Lcl Rotation, XYZ order: Rz Ry Rx)
    @ S(Lcl Scaling); no Pre/Post rotation or pivots are written by the exporter (checked).
  * GlobalSettings must say Up=+Y, Front=+Z, Coord=+X and UnitScaleFactor=100 (1 unit = 1 m);
    Unity then imports a point (x, y, z) as (-x, y, z) metres (right- to left-handed).
Also reports the bounds of all mesh vertices in Unity space (expect the 16 x 3 x 8 m room).
Prints "[hub-fbx] anchors N of M within 2 cm" and exits 1 on any mismatch.
"""

import json
import math
import os
import sys

sys.path.insert(0, os.path.join(os.environ.get("BLENDER_SYSTEM_SCRIPTS", ""), "addons"))
from io_scene_fbx import parse_fbx  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_FBX = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Art",
                           "Hub", "HubRoomArt.fbx")
TOL = 0.02


def child(e, name):
    for c in e.elems:
        if c.id == name:
            return c
    return None


def props70(e):
    out = {}
    p = child(e, b"Properties70")
    if p is None:
        return out
    for q in p.elems:
        key = q.props[0].decode()
        out[key] = q.props[4:]
    return out


def matmul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def trs(t, r, s):
    rx, ry, rz = (math.radians(v) for v in r)
    cx, sx, cy, sy, cz, sz = math.cos(rx), math.sin(rx), math.cos(ry), math.sin(ry), math.cos(rz), math.sin(rz)
    Rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    Ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    Rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    m3 = lambda a, b: [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    R = m3(Rz, m3(Ry, Rx))
    return [[R[0][0] * s[0], R[0][1] * s[1], R[0][2] * s[2], t[0]],
            [R[1][0] * s[0], R[1][1] * s[1], R[1][2] * s[2], t[1]],
            [R[2][0] * s[0], R[2][1] * s[1], R[2][2] * s[2], t[2]],
            [0, 0, 0, 1]]


def apply(m, p):
    return [m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3)]


def board_point(board, x, y, depth):
    yaw = math.radians(board["yawDeg"])
    right = (math.cos(yaw), 0.0, -math.sin(yaw))
    toward = (-math.sin(yaw), 0.0, -math.cos(yaw))
    c = board["center"]
    return [c[i] + right[i] * x + (1.0 if i == 1 else 0.0) * y + toward[i] * depth for i in range(3)]


def expected_anchors(layout):
    boards = {b["id"]: b for b in layout["boards"]}
    out = {}
    for t in layout["texts"]:
        out["Anchor_Text_" + t["id"]] = board_point(boards[t["board"]], t["x"], t["y"], t["depth"])
    for c in layout["levelCards"]:
        b = boards[c["board"]]
        i = c["index"]
        out["Anchor_Level%d_Button" % i] = board_point(b, c["x"], c["y"], c["depth"] + 0.03)
        for key in ("preview", "name", "goal", "record", "start"):
            e = c[key]
            out["Anchor_Level%d_%s" % (i, key.capitalize())] = board_point(
                b, c["x"] + e["x"], c["y"] + e["y"], e["depth"])
    for r in layout["settingRows"]:
        b = boards[r["board"]]
        for key in ("label", "value", "less", "more"):
            e = r[key]
            out["Anchor_Setting%02d_%s" % (r["id"], key.capitalize())] = board_point(
                b, e["x"], r["y"], e["depth"])
    return out


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    path = argv[0] if argv else DEFAULT_FBX
    with open(os.path.join(HERE, "hub_layout.json"), encoding="utf-8") as fh:
        layout = json.load(fh)
    root, _ = parse_fbx.parse(path)

    gs = props70(child(root, b"GlobalSettings"))
    axes = tuple(int(gs[k][0]) for k in ("UpAxis", "UpAxisSign", "FrontAxis", "FrontAxisSign",
                                         "CoordAxis", "CoordAxisSign"))
    unit = float(gs["UnitScaleFactor"][0])
    print("[hub-fbx] GlobalSettings up/front/coord=%s unitScale=%s" % (axes, unit))
    ok = axes == (1, 1, 2, 1, 0, 1) and abs(unit - 100.0) < 1e-6

    models = {}
    geoms = {}
    for e in child(root, b"Objects").elems:
        name = e.props[1].split(b"\x00")[0].decode()
        if e.id == b"Model":
            p = props70(e)
            for bad in ("PreRotation", "PostRotation", "RotationPivot", "ScalingPivot",
                        "RotationOffset", "ScalingOffset", "GeometricTranslation",
                        "GeometricRotation", "GeometricScaling"):
                if bad in p and any(abs(v) > 1e-9 for v in p[bad][:3]):
                    if not (bad == "GeometricScaling" and all(abs(v - 1) < 1e-9 for v in p[bad][:3])):
                        print("[hub-fbx] unexpected %s on %s: %s" % (bad, name, p[bad]))
                        ok = False
            ro = p.get("RotationOrder", [0])[0]
            if ro not in (0,):
                print("[hub-fbx] rotation order %s on %s" % (ro, name))
                ok = False
            models[e.props[0]] = {
                "name": name,
                "m": trs(p.get("Lcl Translation", (0, 0, 0))[:3], p.get("Lcl Rotation", (0, 0, 0))[:3],
                         p.get("Lcl Scaling", (1, 1, 1))[:3]),
                "parent": 0}
        elif e.id == b"Geometry":
            v = child(e, b"Vertices")
            geoms[e.props[0]] = list(v.props[0]) if v is not None else []

    mesh_of = {}
    for c in child(root, b"Connections").elems:
        if c.props[0] != b"OO":
            continue
        src, dst = c.props[1], c.props[2]
        if src in models and dst in models:
            models[src]["parent"] = dst
        elif src in geoms and dst in models:
            mesh_of[dst] = src

    def world(uid):
        m = models[uid]["m"]
        p = models[uid]["parent"]
        while p in models:
            m = matmul(models[p]["m"], m)
            p = models[p]["parent"]
        return m

    to_unity = lambda q: [-q[0], q[1], q[2]]

    lo = [1e9] * 3
    hi = [-1e9] * 3
    for uid, gid in mesh_of.items():
        w = world(uid)
        v = geoms[gid]
        for k in range(0, len(v), 3):
            q = to_unity(apply(w, v[k:k + 3]))
            for i in range(3):
                lo[i] = min(lo[i], q[i])
                hi[i] = max(hi[i], q[i])
    print("[hub-fbx] meshes=%d unity bounds min=(%.2f, %.2f, %.2f) max=(%.2f, %.2f, %.2f)" % (
        len(mesh_of), lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]))
    if not (abs(hi[0] - lo[0] - 16.0) < 0.3 and abs(hi[2] - lo[2] - 8.0) < 0.3 and lo[1] > -0.1
            and abs(hi[1] - 3.0) < 0.2):
        print("[hub-fbx] room bounds are not the 16 x 3 x 8 m room")
        ok = False

    want = expected_anchors(layout)
    by_name = {m["name"]: uid for uid, m in models.items()}
    good = 0
    first = None
    for name, p in sorted(want.items()):
        uid = by_name.get(name)
        if uid is None:
            first = first or (name + " missing")
            continue
        got = to_unity(apply(world(uid), [0, 0, 0]))
        d = math.dist(got, p)
        if d <= TOL:
            good += 1
        elif first is None:
            first = "%s at (%.3f, %.3f, %.3f), layout (%.3f, %.3f, %.3f)" % (name, *got, *p)
    extra = sorted(n for n in by_name if n.startswith("Anchor_") and n not in want)
    print("[hub-fbx] anchors %d of %d within %d cm%s" % (good, len(want), int(TOL * 100),
                                                         "" if first is None else "; first bad: " + first))
    if extra:
        print("[hub-fbx] anchors not in the layout: %s" % ", ".join(extra))
    ok = ok and good == len(want) and not extra
    sample = "Anchor_Text_LevelsTitle"
    if sample in by_name:
        print("[hub-fbx] %s unity=(%.3f, %.3f, %.3f) layout=(%.3f, %.3f, %.3f)" % (
            sample, *to_unity(apply(world(by_name[sample]), [0, 0, 0])), *want[sample]))
    print("[hub-fbx] RESULT " + ("PASS" if ok else "FAIL"))
    sys.exit(0 if ok else 1)


main()
