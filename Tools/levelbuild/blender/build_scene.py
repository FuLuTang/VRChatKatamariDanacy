"""Build the Blender scene for a placement.json (library instances + structure boxes).

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/build_scene.py -- \
        --placement Docs/Design/Levels/story_01_bench_size/placement.json \
        [--save-blend /tmp/story_01.blend]

The JSON is the truth: this script never re-seats, re-orders or re-derives a
pose.  Use ``levelbuild.py seat --write`` to fix a pose in the data, then rebuild.
Run ``render_views.py`` instead when the only goal is a picture - it builds the
same scene through ``blender_common`` and skips saving.
"""

import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))

import blender_common as common  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402


def main():
    placement = common.option("--placement")
    if not placement:
        raise SystemExit("build_scene: --placement <placement.json> is required")
    path = common.resolve(placement)
    doc = core.load_doc(path)
    print("levelbuild: building %s (schema %s)" % (core.display_path(path), doc.get("schema")))
    print("confidence: quatOrder=%s convention=%s"
          % (core.QUAT_ORDER, (doc.get("level") or {}).get("convention")))

    exports = common.option("--exports")
    common.reset_scene()
    built = common.build_from_doc(doc, common.resolve(exports) if exports else None)
    bpy.context.view_layer.update()

    print("built props=%d structures=%d cameras=%d defs=%d"
          % (len(built["props"]), len(built["structures"]), len(built["cameras"]),
             len(built["library"])))

    for structure_id, collection in built["library"].items():
        if common.library_is_linked(collection):
            raise SystemExit("library collection %r is linked to the scene; instances would "
                             "inherit its visibility (see LEVEL_AUTHORING section 1.5)"
                             % collection.name)

    save = common.option("--save-blend")
    if save:
        target = common.resolve(save)
        target.parent.mkdir(parents=True, exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=str(target))
        print("saved %s" % target)
    print("ok")


if __name__ == "__main__":
    main()
