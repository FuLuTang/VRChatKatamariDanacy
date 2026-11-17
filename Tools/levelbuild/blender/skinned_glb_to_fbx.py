"""Skinned reference GLB -> FBX with one take per clip (runs INSIDE Blender 4.0.2 / 5.x).

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/skinned_glb_to_fbx.py -- \
        --src ~/KatamariRef/props/skinned --out ~/KatamariRef/props/skinned_fbx [--only type_480]

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
import sys

# Which defs are which skeletal type (objectId) - from ORIGINAL_SKELETAL_ANIMATION.md section 5.
DEF_TYPES = {
    "small_fiddler_crab": 480, "fiddler_crab": 481, "coconut_crab": 482,
    "horseshoe_crab": 483, "spider_crab": 484, "blue_crab": 485,
}


ACTION_SUFFIX = "_root"
DURATION_TOLERANCE_S = 1e-3
# Scene frame rates tried in order; the first at which every clip lasts a whole number of frames
# wins.  The importer turns glTF seconds into frames at the scene rate and the FBX exporter
# writes that rate as the file's TimeMode, so a rate that cuts a clip mid-frame makes Unity round
# the take to whole frames at that rate (the default 24 read 0.9667 s as 23 frames = 0.9583 s).
SCENE_FPS_CANDIDATES = (24, 25, 30, 48, 50, 60, 90, 120, 180)
WHOLE_FRAME_TOLERANCE = 0.01


def clip_name_of(action_name):
    """The report clip name a glTF-imported Action carries (the importer appends ``_root``)."""
    if action_name.endswith(ACTION_SUFFIX):
        return action_name[:-len(ACTION_SUFFIX)]
    return action_name


def take_names(action_names, clips):
    """Map each Action name to its clip's name, looking the clip up by name (never by index).

    Raises when an Action has no clip, two Actions claim one clip, or a clip has no Action.
    """
    by_name = {c["name"]: c for c in clips}
    mapping = {}
    for action in action_names:
        clip = clip_name_of(action)
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
        if abs(got - seconds[name]) > DURATION_TOLERANCE_S:
            raise RuntimeError("take %r lasts %.4f s, the report says %.4f s" % (name, got, seconds[name]))


def scene_fps(clips):
    """The lowest candidate frame rate at which every clip spans a whole number of frames."""
    for fps in SCENE_FPS_CANDIDATES:
        if all(abs(c["seconds"] * fps - round(c["seconds"] * fps)) <= WHOLE_FRAME_TOLERANCE for c in clips):
            return fps
    raise RuntimeError("no frame rate in %s gives whole-frame clips for %s"
                       % (SCENE_FPS_CANDIDATES, [(c["name"], c["seconds"]) for c in clips]))


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--src", required=True)
    p.add_argument("--out", required=True)
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
    bpy.ops.import_scene.gltf(filepath=glb)
    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("no armature in " + glb)
    armature = armatures[0]
    # glTF import leaves one Action per clip, named "<clip>_root": rename each to its clip's
    # name (looked up by name) so Unity's takes read walk / idle / ...
    actions = list(bpy.data.actions)
    mapping = take_names([a.name for a in actions], clips)
    spans = {mapping[a.name]: tuple(a.frame_range) for a in actions}
    check_durations(spans, clips, fps)
    renamed = []
    idle = None
    for action in actions:
        action.name = mapping[action.name]
        action.use_fake_user = True
        renamed.append(action.name)
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
    return path, renamed, fps


def main():
    args = parse()
    only = set(x for x in args.only.split(",") if x)
    report = json.load(open(os.path.join(args.src, "report.json")))
    written = []
    for entry in report:
        name = "type_%d" % entry["objectId"]
        if only and name not in only:
            continue
        glb = os.path.join(args.src, name + ".glb")
        if not os.path.isfile(glb):
            continue
        path, takes, fps = convert(glb, os.path.join(args.out, name), entry["clips"])
        print("OUT", path, takes, "fps", fps)
        written.append({"type": name, "objectId": entry["objectId"], "fbx": name + "/" + name + ".fbx",
                        "fps": fps,
                        "clips": [{"name": c["name"], "seconds": c["seconds"], "speed": c["speed"],
                                   "slot": c["slot"]} for c in entry["clips"]]})
    defs = {d: "type_%d" % t for d, t in DEF_TYPES.items()
            if any(w["objectId"] == t for w in written)}
    os.makedirs(args.out, exist_ok=True)
    json.dump({"defs": defs, "types": written}, open(os.path.join(args.out, "skinned_defs.json"), "w"),
              indent=1)


if __name__ == "__main__":
    main()
