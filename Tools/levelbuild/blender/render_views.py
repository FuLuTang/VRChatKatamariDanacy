"""Render the level's camera presets to PNG (the pictures the author looks at).

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
        --placement Docs/Design/Levels/story_01_bench_size/placement.json \
        --preset plan,overview [--engine WORKBENCH|EEVEE|CYCLES] \
        [--out-dir Docs/Evidence/levelbuild] [--res 1280x720] [--samples 32]

Camera and resolution come from the document's ``cameras[]`` / ``renderPresets[]``,
so the same data renders the same picture on 4.0.2 (Linux, headless, llvmpipe)
and 5.3.0 (Windows, GPU).  ``--engine`` is an author convenience for a quick
plan view; it does not change the file.

This is the **one-shot** path: process start, whole-scene build, render, exit.  While
iterating, keep the resident editor open instead (``levelserve.py start``) and ask it
for one frame - the build is paid once, not per render.  Both paths share
``blender_common.render_frame``, so the picture is identical.

Measured on the 2-core Linux box: WORKBENCH 5-10 s, EEVEE 35-49 s,
CYCLES CPU 48 spp ~104 s per 1280x720 frame.
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
        raise SystemExit("render_views: --placement <placement.json> is required")
    path = common.resolve(placement)
    doc = core.load_doc(path)
    level_id = str((doc.get("level") or {}).get("id") or "level")

    presets = common.select_presets(doc, common.option("--preset"))
    out_dir = common.resolve(common.option("--out-dir") or "/tmp/levelbuild-render")
    out_dir.mkdir(parents=True, exist_ok=True)
    engine_override = common.option("--engine")
    res_override = common.option("--res")
    samples = int(common.option("--samples") or 0)

    common.reset_scene()
    scene = bpy.context.scene
    exports = common.option("--exports")
    built = common.build_from_doc(doc, common.resolve(exports) if exports else None)

    written = []
    for preset in presets:
        engine = (engine_override or preset.get("engine") or "WORKBENCH").upper()
        res = res_override.split("x") if res_override else preset.get("res") or [1280, 720]
        res = [int(res[0]), int(res[1])]

        camera = common.camera_for(doc, preset["camera"])
        target = out_dir / ("%s_%s_%s.png" % (level_id, preset["id"], engine.lower()))
        engine_id, elapsed = common.render_frame(
            scene, camera, target, engine, res, samples or int(preset.get("samples") or 0))
        written.append((preset["id"], engine_id, target, elapsed))
        print("rendered %-10s %-18s %dx%d %.1fs -> %s"
              % (preset["id"], engine_id, res[0], res[1], elapsed, target))

    print("done: %d image(s), %.1f s total"
          % (len(written), sum(item[3] for item in written)))
    # A machine-readable tail line so an agent can pick the paths up without parsing prose.
    for _, _, target, _ in written:
        print("IMAGE %s" % target)


if __name__ == "__main__":
    main()
