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

Measured on the 2-core Linux box: WORKBENCH 5-10 s, EEVEE 35-49 s,
CYCLES CPU 48 spp ~104 s per 1280x720 frame.
"""

import sys
import time
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))

import blender_common as common  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402


def fit_ortho_to_frame(camera, res):
    """Make ``coverage_m`` the extent guaranteed to be in frame on the *shorter* side.

    Blender's ``ortho_scale`` is the extent along the sensor's longer side, so a 16:9 plan
    view of a square arena would silently crop the top and bottom (measured: a 13.4 m
    arena showed only 7.9 m of depth).  The camera data keeps the authored coverage; the
    per-preset resolution decides the scale.
    """
    data = camera.data
    if data.type != "ORTHO":
        return
    authored = float(camera.get("cam_coverage_m", data.ortho_scale))
    aspect = float(res[0]) / float(res[1]) if res[1] else 1.0
    data.ortho_scale = authored * max(1.0, aspect)


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
    common.build_from_doc(doc, common.resolve(exports) if exports else None)

    written = []
    for preset in presets:
        engine = (engine_override or preset.get("engine") or "WORKBENCH").upper()
        res = res_override.split("x") if res_override else preset.get("res") or [1280, 720]
        res = [int(res[0]), int(res[1])]

        engine_id = common.select_engine(scene, engine)
        common.configure_shading(scene, engine, samples or int(preset.get("samples") or 0))
        common.configure_output(scene, res)

        camera = common.camera_for(doc, preset["camera"])
        scene.camera = camera
        fit_ortho_to_frame(camera, res)

        target = out_dir / ("%s_%s_%s.png" % (level_id, preset["id"], engine.lower()))
        scene.render.filepath = str(target)
        started = time.time()
        bpy.ops.render.render(write_still=True)
        elapsed = time.time() - started
        if not target.is_file() or target.stat().st_size == 0:
            raise SystemExit("render_views: %s was not written" % target)
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
