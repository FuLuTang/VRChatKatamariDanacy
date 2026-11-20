"""Skinned reference GLB -> FBX with one take per clip (runs INSIDE Blender 4.0.2 / 5.x).

    blender -b --python-exit-code 1 --python skinned_glb_to_fbx.py -- \
        --src glb --out fbx --motion-table motion_table.json --original-defs original_defs.json

在参考数据的工作目录运行。--only 用于试转，不能覆盖装配清单。

Input: ``type_<objectId>.glb`` from the (out-of-repo) skeletal exporter, plus its
``report.json`` (clip names and fps).  Output per type: ``type_<id>/type_<id>.fbx`` (armature +
skinned meshes + one Action per clip, baked per frame, no leaf bones) and
``skinned_defs.json`` mapping defs to the types.  Everything stays outside the repo (the files
are unlicensed reference data); the Unity side copies them under Assets/_RefUnlicensed/Animation/
(see Docs/Design/PickupAbsorb/MOTION_HOST.md, skeletal section).

Conventions follow the static pipeline (``convert_models_cancer_native.py``): metres,
FBX Forward -Z / Up Y, FBX_SCALE_UNITS.  The static models have their AABB centre at the origin;
this script does NOT move the vertices (an armature export would shear the bind pose).  The Unity
assembler aligns the skin to the static model by bounds instead and logs ``[anim_fit]``.
"""
import argparse
import json
import os
import struct
import sys
import tempfile
from pathlib import Path


def attach_detached_joints(document):
    """将旧参考 GLB 漏挂的根骨放回已有坐标根，不改骨骼、矩阵或曲线。"""
    nodes = document["nodes"]
    scene_roots = document["scenes"][document.get("scene", 0)]["nodes"]
    if len(scene_roots) != 1 or len(document["skins"]) != 1:
        raise ValueError("expected one reference coordinate root and one skin")
    root = scene_roots[0]
    skin = document["skins"][0]
    parents = {}
    for parent, node in enumerate(nodes):
        for child in node.get("children", []):
            if child in parents:
                raise ValueError(f"joint hierarchy contains more than one parent for node {child}")
            parents[child] = parent
    joints = set(skin["joints"])
    detached = set()
    for node in joints:
        visited = set()
        while node != root and node in parents:
            if node in visited:
                raise ValueError("joint hierarchy contains a cycle")
            visited.add(node)
            node = parents[node]
        if node == root:
            continue
        # Only the known orphan-joint case is repairable. Never move another asset root.
        if node not in joints:
            raise ValueError(f"joint hierarchy uses unexpected disconnected root {node}")
        detached.add(node)
    if not detached:
        return False
    if root in joints or "matrix" not in nodes[root]:
        raise ValueError("cannot identify the existing reference coordinate root")
    nodes[root].setdefault("children", []).extend(sorted(detached))
    skin["skeleton"] = root
    return True


def normalized_glb(raw):
    """只替换需要修正的 GLB JSON 块；BIN 及后续块原样保留。"""
    magic, version, length, json_length, tag = struct.unpack_from("<4sIII4s", raw)
    if (magic, version, length, tag) != (b"glTF", 2, len(raw), b"JSON") or json_length > len(raw) - 20:
        raise ValueError("expected a glTF 2.0 binary with a JSON first chunk")
    doc = json.loads(raw[20:20 + json_length])
    if not attach_detached_joints(doc):
        return raw
    payload = json.dumps(doc, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    payload += b" " * (-len(payload) % 4)
    rest = raw[20 + json_length:]
    return struct.pack("<4sIII4s", magic, version, 20 + len(payload) + len(rest), len(payload), tag) + payload + rest


def import_reference_glb(glb, scratch_parent):
    import bpy
    source = Path(glb).read_bytes()
    fixed = normalized_glb(source)
    if fixed is source:
        bpy.ops.import_scene.gltf(filepath=glb)
        return
    with tempfile.TemporaryDirectory(dir=scratch_parent) as directory:
        normalized = Path(directory) / Path(glb).name
        normalized.write_bytes(fixed)
        bpy.ops.import_scene.gltf(filepath=str(normalized))

def skeletal_defs(instances, definitions):
    """按原作 nameIdx/摆放 mono 对接，不从英文重名推断 def。"""
    counts = {}
    for item in instances:
        if item["inLevelProps"] and item["animated"]:
            object_id = item["nameIdx"]
            counts[object_id] = counts.get(object_id, 0) + 1
    result = {}
    for entry in definitions:
        object_id = entry["mono"]
        if object_id not in counts:
            continue
        if object_id in result or any(v["id"] == entry["id"] for v in result.values()):
            raise ValueError(f"duplicate skeletal definition: {entry['id']} / {object_id}")
        if entry["instances"] != counts[object_id]:
            raise ValueError(f"skeletal definition count differs: {entry['id']} / {counts[object_id]}")
        result[object_id] = entry
    missing = sorted(counts.keys() - result.keys())
    if not counts or missing:
        raise ValueError(f"missing skeletal definitions: {missing}")
    return result


def export_inputs(specs, report, src, only):
    """在启动 Blender 转换前拒绝重复、未知或缺失的骨骼源。"""
    entries = {}
    for entry in report:
        object_id = entry["objectId"]
        if object_id in entries:
            raise ValueError(f"duplicate source report: {object_id}")
        entries[object_id] = entry
    wanted = {f"type_{i}": i for i in specs}
    if only - wanted.keys():
        raise ValueError(f"unknown selected types: {sorted(only - wanted.keys())}")
    result = []
    for name in sorted(only or wanted.keys(), key=lambda x: wanted[x]):
        object_id = wanted[name]
        glb = os.path.join(src, name + ".glb")
        if object_id not in entries or not os.path.isfile(glb):
            raise ValueError(f"missing source report or GLB: {name}")
        result.append((name, glb, entries[object_id]))
    return result


ACTION_SUFFIX = "_root"
DURATION_TOLERANCE_S = 1e-3
# Scene frame rates tried in order; the first at which every clip lasts a whole number of frames
# wins.  The importer turns glTF seconds into frames at the scene rate and the FBX exporter
# writes that rate as the file's TimeMode, so a rate that cuts a clip mid-frame makes Unity round
# the take to whole frames at that rate (the default 24 read 0.9667 s as 23 frames = 0.9583 s).
SCENE_FPS_CANDIDATES = (24, 25, 30, 48, 50, 60, 90, 120, 180, 240, 300, 360, 480)
WHOLE_FRAME_TOLERANCE = 0.01
# 对不能整数对齐的原作 Q8 速率，端点最多挪 1 毫秒（Unity 核对门为 2 毫秒）。
FRAME_ROUNDING_TOLERANCE_S = 1e-3


def clip_name_of(action_name, armature_names=()):
    """glTF 导入器可能在动作名末尾附上 armature 对象名或数据名。"""
    suffixes = {ACTION_SUFFIX} | {"_" + n for n in armature_names if n}
    for suffix in sorted(suffixes, key=len, reverse=True):
        if action_name.endswith(suffix):
            return action_name[:-len(suffix)]
    return action_name


def take_names(action_names, clips, armature_names=()):
    """Map each Action name to its clip's name, looking the clip up by name (never by index).

    Raises when an Action has no clip, two Actions claim one clip, or a clip has no Action.
    """
    by_name = {c["name"]: c for c in clips}
    mapping = {}
    for action in action_names:
        clip = action if action in by_name else clip_name_of(action, armature_names)
        if clip not in by_name:
            raise RuntimeError("action %r has no clip %r in the report (clips: %s)"
                               % (action, clip, sorted(by_name)))
        if clip in mapping.values():
            raise RuntimeError("clip %r is claimed by two actions" % clip)
        mapping[action] = clip
    missing = sorted(set(by_name) - set(mapping.values()))
    if missing:
        raise RuntimeError("clips without an action: %s" % missing)
    return mapping


def check_durations(takes, clips, fps):
    """``takes`` is {clip name: (frame_start, frame_end)}; each span must equal the report's seconds."""
    seconds = {c["name"]: c["seconds"] for c in clips}
    for name, (start, end) in takes.items():
        got = (end - start) / fps
        expected = seconds[name] or 0.0
        # Blender 3.6 给单关键帧动作补至少 1 帧范围；对定格姿态两种输入都有效。
        if seconds[name] is None and 0 <= got <= 1 / fps + DURATION_TOLERANCE_S:
            continue
        if abs(got - expected) > DURATION_TOLERANCE_S:
            raise RuntimeError("take %r lasts %.4f s, the report says %.4f s" % (name, got, expected))


def scene_fps(clips):
    """The lowest candidate frame rate at which every clip spans a whole number of frames."""
    seconds = [c["seconds"] for c in clips if c["seconds"] is not None]
    for fps in SCENE_FPS_CANDIDATES:
        if all(round(s * fps) > 0 and abs(s * fps - round(s * fps)) <= WHOLE_FRAME_TOLERANCE
               for s in seconds):
            return fps
    for fps in SCENE_FPS_CANDIDATES:
        if all(round(s * fps) > 0 and abs(s - round(s * fps) / fps) <= FRAME_ROUNDING_TOLERANCE_S
               for s in seconds):
            return fps
    raise RuntimeError("no frame rate in %s gives whole-frame clips within %.3f ms for %s"
                       % (SCENE_FPS_CANDIDATES, FRAME_ROUNDING_TOLERANCE_S * 1000,
                          [(c["name"], c["seconds"]) for c in clips]))


def take_frames(seconds, fps):
    # FBX/Unity 不能可靠保留零长度 take；导出两相同关键帧，保留原作定格姿态。
    return 1 if seconds is None else round(seconds * fps)


def align_action(action, frames):
    start, end = action.frame_range
    for curve in action.fcurves:
        if end > start:
            for key in curve.keyframe_points:
                for coord in (key.co, key.handle_left, key.handle_right):
                    coord.x = (coord.x - start) * frames / (end - start)
        else:
            value = curve.evaluate(start)
            for key in curve.keyframe_points:
                key.co.x = 0.0
            curve.keyframe_points.insert(frames, value)
        curve.update()


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--src", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--motion-table", required=True)
    p.add_argument("--original-defs", required=True)
    p.add_argument("--only", default="")
    return p.parse_args(argv)


def reset():
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def convert(glb, out_dir, clips):
    import bpy
    reset()
    fps = scene_fps(clips)
    bpy.context.scene.render.fps = fps
    bpy.context.scene.render.fps_base = 1.0
    os.makedirs(out_dir, exist_ok=True)
    import_reference_glb(glb, out_dir)
    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("no armature in " + glb)
    armature = armatures[0]
    # glTF import leaves one Action per clip, named "<clip>_root": rename each to its clip's
    # name (looked up by name) so Unity's takes read walk / idle / ...
    actions = list(bpy.data.actions)
    mapping = take_names([a.name for a in actions], clips, (armature.name, armature.data.name))
    spans = {mapping[a.name]: tuple(a.frame_range) for a in actions}
    check_durations(spans, clips, fps)
    by_name = {c["name"]: c for c in clips}
    takes = {}
    idle = None
    for action in actions:
        action.name = mapping[action.name]
        action.use_fake_user = True
        frames = take_frames(by_name[action.name]["seconds"], fps)
        align_action(action, frames)
        if tuple(action.frame_range) != (0.0, float(frames)):
            raise RuntimeError(f"take {action.name}: Blender frame range is {tuple(action.frame_range)}, expected 0..{frames}")
        takes[action.name] = frames
        if action.name == "idle":
            idle = action
    if armature.animation_data is None:
        armature.animation_data_create()
    armature.animation_data.action = idle or actions[0]

    bpy.ops.object.select_all(action="SELECT")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, os.path.basename(out_dir) + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"},
        apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
        bake_space_transform=False, add_leaf_bones=False, mesh_smooth_type="FACE",
        bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
        bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
        bake_anim_step=1.0, path_mode="COPY", embed_textures=False)
    if not os.path.isfile(path):
        raise RuntimeError(f"missing FBX output: {path}")
    return path, takes, fps


def read_json(path):
    with open(path, encoding="utf-8-sig") as stream:
        return json.load(stream)


def main():
    args = parse()
    only = {x.strip() for x in args.only.split(",") if x.strip()}
    specs = skeletal_defs(read_json(args.motion_table)["instances"], read_json(args.original_defs)["defs"])
    report = read_json(os.path.join(args.src, "report.json"))
    wanted = export_inputs(specs, report, args.src, only)
    manifest = os.path.join(args.out, "skinned_defs.json")
    if only and os.path.exists(manifest):
        raise ValueError("--only must write to a separate scratch directory, not an assembled batch")
    written = []
    defs = {}
    for name, glb, entry in wanted:
        object_id = entry["objectId"]
        path, takes, fps = convert(glb, os.path.join(args.out, name), entry["clips"])
        print("OUT", path, takes, "fps", fps)
        written.append({"type": name, "objectId": object_id, "fbx": name + "/" + name + ".fbx",
                        "fps": fps,
                        "clips": [{"name": c["name"], "seconds": takes[c["name"]] / fps,
                                   "sourceSeconds": c["seconds"], "speed": c["speed"], "slot": c["slot"]}
                                  for c in entry["clips"]]})
        defs[specs[object_id]["id"]] = name
    if not only:
        temporary = manifest + ".tmp"
        with open(temporary, "w", encoding="utf-8") as stream:
            json.dump({"defs": defs, "types": written}, stream, indent=1)
        os.replace(temporary, manifest)
        print(f"{len(defs)} types, {sum(d['instances'] for d in specs.values())} instances")


if __name__ == "__main__":
    main()
