#!/usr/bin/env python3
"""Blender helper: turn prop FBX exports into collider-proxy point sidecars.

Run headless (the usual Blender environment):

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/fit_collider_proxy.py -- \
        --exports Docs/Art/OriginalModels/props/exports \
        --out /home/fulutang/KatamariRef/meta/collider_proxy/points

Each ``<def>.fbx`` becomes ``<out>/<def>.json``:

    {"def": "...", "space": "unity_local_y_up", "vertices": [[x,y,z], ...],
     "faces": [[i,j,k], ...], "bounds": {"min": [...], "max": [...]}}

Why the raw ``v.co`` and not ``matrix_world @ v.co``: Blender's FBX importer
rotates the *object* by +90 degrees about X to present the file's Y-up data in
Blender's Z-up world.  The mesh-local coordinates are therefore already the frame
Unity uses (X right, Y up, Z forward), which is exactly the frame the placement
``collider`` block is written in.  For the exported props the object transform is
that conversion only (translation 0, scale 1); the helper asserts that and stops
if a file ever carries another local transform, instead of silently fitting a
different frame.

``--fit`` additionally runs ``Tools/levelbuild/collider_proxy.py`` in this Blender
Python (standard library only) and writes ``<out>/../fits/<def>.json``.  The
normal batch run keeps fitting in plain Python so the report can be regenerated
without Blender; the option exists for a one-prop check.
"""

import argparse
import json
import math
import sys
from pathlib import Path

import bpy  # noqa: E402  (Blender-only module)

ROOT = Path(__file__).resolve().parents[3]


def _clean(value):
    value = round(float(value), 6)
    return 0.0 if value == 0.0 else value


def _collect_mesh():
    objects = [ob for ob in bpy.context.scene.objects if ob.type == "MESH"]
    if not objects:
        return None
    if len(objects) > 1:
        names = ", ".join(ob.name for ob in objects)
        raise SystemExit("more than one mesh object in the FBX: " + names)

    ob = objects[0]
    matrix = ob.matrix_world
    # The importer's conversion: rotation about X by +/-90 degrees, no translation,
    # unit scale.  Anything else means the mesh data is not the Unity-local frame.
    translation = matrix.translation
    if max(abs(translation.x), abs(translation.y), abs(translation.z)) > 1e-5:
        raise SystemExit("%s: object has a translation (%r); expected the FBX axis "
                         "conversion only" % (ob.name, tuple(translation)))
    scale = matrix.to_scale()
    if max(abs(scale.x - 1.0), abs(scale.y - 1.0), abs(scale.z - 1.0)) > 1e-5:
        raise SystemExit("%s: object has a non-unit scale (%r)" % (ob.name, tuple(scale)))
    rotation = matrix.to_quaternion()
    if (abs(abs(rotation.x) - math.sqrt(0.5)) > 1e-3
            or abs(rotation.y) > 1e-3 or abs(rotation.z) > 1e-3):
        raise SystemExit("%s: object rotation %r is not the FBX Y-up -> Z-up "
                         "conversion (+/-90 deg about X); the mesh data would not be "
                         "the Unity-local frame" % (ob.name, tuple(rotation)))

    vertices = [[_clean(c) for c in vertex.co] for vertex in ob.data.vertices]
    ob.data.calc_loop_triangles()
    faces = [[int(triangle.vertices[0]), int(triangle.vertices[1]),
              int(triangle.vertices[2])] for triangle in ob.data.loop_triangles]
    if not vertices or not faces:
        raise SystemExit("%s: the mesh has no vertices or no triangles" % ob.name)
    return {"name": ob.name, "vertices": vertices, "faces": faces}


def _bounds(points):
    return ([min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)])


def main():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--exports", default=str(ROOT / "Docs/Art/OriginalModels/props/exports"))
    parser.add_argument("--out", required=True)
    parser.add_argument("--only", help="comma-separated def names")
    parser.add_argument("--fit", action="store_true",
                        help="also run collider_proxy.py and write the fitted block")
    args = parser.parse_args(argv)

    exports = Path(args.exports)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    wanted = set(args.only.split(",")) if args.only else None
    files = sorted(exports.glob("*.fbx"))
    if wanted is not None:
        files = [path for path in files if path.stem in wanted]
    if not files:
        raise SystemExit("no .fbx files matched under %s" % exports)

    fits_dir = out.parent / "fits"
    if args.fit:
        fits_dir.mkdir(parents=True, exist_ok=True)
        if str(ROOT) not in sys.path:
            sys.path.insert(0, str(ROOT))
        from Tools.levelbuild import collider_proxy

    for index, path in enumerate(files):
        name = path.stem
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.fbx(filepath=str(path))
        mesh = _collect_mesh()
        if mesh is None:
            raise SystemExit("%s: no mesh object" % path)
        low, high = _bounds(mesh["vertices"])
        # The manifest's dimensions are Blender axes (x, y, z); the mesh-local frame
        # is Unity's (x, z, y).  `pivot: center` makes the visual AABB symmetric, so
        # dimensions are enough and no sign question enters.  This is the bound the
        # collider must stay inside (COLLIDER_LAYER 4.1).
        visual_bounds = None
        manifest_path = path.with_suffix(".manifest.json")
        if manifest_path.exists():
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            dimensions = manifest.get("dimensions_m") or {}
            if all(key in dimensions for key in ("x", "y", "z")):
                unity = (float(dimensions["x"]), float(dimensions["z"]),
                         float(dimensions["y"]))
                visual_bounds = [[-_clean(0.5 * value) for value in unity],
                                 [_clean(0.5 * value) for value in unity]]
                mesh_size = [high[i] - low[i] for i in range(3)]
                if max(abs(mesh_size[i] - unity[i]) for i in range(3)) > 1e-3:
                    print("WARNING %s: mesh AABB %.4f/%.4f/%.4f differs from manifest "
                          "dimensions %.4f/%.4f/%.4f" % (name, mesh_size[0], mesh_size[1],
                                                          mesh_size[2], unity[0], unity[1],
                                                          unity[2]))
        document = {
            "def": name,
            "space": "unity_local_y_up",
            "source_fbx": str(path.relative_to(ROOT)) if path.is_relative_to(ROOT)
                          else str(path),
            "vertices": mesh["vertices"],
            "faces": mesh["faces"],
            "bounds": {"min": [_clean(c) for c in low],
                       "max": [_clean(c) for c in high]},
            "visual_bounds": visual_bounds,
            "vertex_count": len(mesh["vertices"]),
            "triangle_count": len(mesh["faces"]),
        }
        target = out / (name + ".json")
        target.write_text(json.dumps(document, separators=(",", ":")) + "\n",
                          encoding="utf-8")
        line = ("[%3d/%3d] %-22s v=%4d t=%4d bounds=%.3f x %.3f x %.3f"
                % (index + 1, len(files), name, len(mesh["vertices"]),
                   len(mesh["faces"]), high[0] - low[0], high[1] - low[1],
                   high[2] - low[2]))
        print(line)
        if args.fit:
            fit = collider_proxy.fit_proxy(mesh["vertices"], mesh["faces"],
                                           bounds=(tuple(visual_bounds[0]),
                                                   tuple(visual_bounds[1]))
                                           if visual_bounds else None)
            result = {
                "block": fit.block("auto")["collider"],
                "accepted": fit.accepted,
                "reason": fit.reason,
                "notes": fit.notes,
                "volume_ratio": fit.volume_ratio,
                "mesh_volume_ratio": fit.mesh_volume_ratio,
                "outer_fraction": fit.outer_fraction,
            }
            (fits_dir / (name + ".json")).write_text(
                json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            print("      -> %s fit_error=%.4f volume_ratio=%.3f"
                  % (fit.kind, fit.fit_error, fit.volume_ratio))

    print("wrote %d sidecar(s) to %s" % (len(files), out))


if __name__ == "__main__":
    main()
