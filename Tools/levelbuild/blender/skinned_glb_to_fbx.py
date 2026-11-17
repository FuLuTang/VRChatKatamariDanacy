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

import bpy

# Which defs are which skeletal type (objectId) - from ORIGINAL_SKELETAL_ANIMATION.md section 5.
DEF_TYPES = {
    "small_fiddler_crab": 480, "fiddler_crab": 481, "coconut_crab": 482,
    "horseshoe_crab": 483, "spider_crab": 484, "blue_crab": 485,
}


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--src", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--only", default="")
    return p.parse_args(argv)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def convert(glb, out_dir, clips):
    reset()
    bpy.ops.import_scene.gltf(filepath=glb)
    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("no armature in " + glb)
    armature = armatures[0]
    # glTF import leaves one Action per clip, named by the glTF animation name: keep them all
    # and give them the report's clip names so Unity's takes read walk / idle / ...
    actions = sorted(bpy.data.actions, key=lambda a: a.name)
    renamed = []
    for index, action in enumerate(actions):
        if index < len(clips):
            action.name = clips[index]["name"]
        action.use_fake_user = True
        renamed.append(action.name)
    if armature.animation_data is None:
        armature.animation_data_create()
    armature.animation_data.action = actions[0] if actions else None

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
    return path, renamed


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
        path, takes = convert(glb, os.path.join(args.out, name), entry["clips"])
        print("OUT", path, takes)
        written.append({"type": name, "objectId": entry["objectId"], "fbx": name + "/" + name + ".fbx",
                        "clips": [{"name": c["name"], "seconds": c["seconds"], "speed": c["speed"],
                                   "slot": c["slot"]} for c in entry["clips"]]})
    defs = {d: "type_%d" % t for d, t in DEF_TYPES.items()
            if any(w["objectId"] == t for w in written)}
    os.makedirs(args.out, exist_ok=True)
    json.dump({"defs": defs, "types": written}, open(os.path.join(args.out, "skinned_defs.json"), "w"),
              indent=1)


main()
