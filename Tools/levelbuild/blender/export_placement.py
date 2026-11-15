"""Export the current Blender scene back to placement.json (scene -> data).

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/export_placement.py -- \
        --out /tmp/placement.json [--base <existing placement.json>] [--seat]

``--base`` is how the fields that are *not* scene geometry survive the round
trip: ``level{}``, ``cameras[]``, ``renderPresets[]``, and each prop's
``valueUnits`` / ``zone`` / ``kind`` / ``support`` / ``stackedOn`` / ``notes``
(by ``itemId``).  Scene custom properties win over the base file, and the pose
and the AABB always come from the scene and the def's manifest.

Self-checks (design section 5.5, any failure exits non-zero through
``--python-exit-code``): the depsgraph is updated before any matrix is read; a
sample of props is converted JSON -> Blender matrix -> JSON and compared; no
library collection is linked into the scene; every def has a manifest whose
AABB agrees with the scene.

``--seat`` re-seats props on their declared support normal first (the measured
"apple half-buried in the tilted plank" fix).  It is a no-op on data that is
already seated, so a JSON -> scene -> JSON round trip stays byte-identical.
"""

import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))

import blender_common as common  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402

ROUNDTRIP_SAMPLES = 3
ROUNDTRIP_TOLERANCE_M = 1e-4


def _base_doc(path_text):
    if not path_text:
        return None
    path = common.resolve(path_text)
    if not path.is_file():
        raise SystemExit("export_placement: --base %s does not exist" % path)
    return core.load_doc(path)


def _prop_entries(doc_base, exports_dir):
    base_props = {prop["itemId"]: prop for prop in (doc_base or {}).get("props") or []}
    manifests = core.load_manifests(exports_dir)
    entries = []
    problems = []

    for obj in common.scene_props():
        item_id = obj.name
        def_id = common.library_def(obj)
        if def_id is None:
            problems.append("%s: no def (instance_collection is not LIB_<def>)" % item_id)
            continue
        base = base_props.get(item_id, {})
        position, quat = common.pose_of(obj)
        size = manifests.get(def_id, {}).get("aabb")
        if size is None:
            problems.append("%s: def %r has no manifest, so aabb is unknown" % (item_id, def_id))
            continue

        entry = {"itemId": item_id, "def": def_id}
        for scene_key, json_key in (("kind", "kind"), ("zone", "zone"),
                                    ("group", "group"), ("notes", "notes")):
            if scene_key in obj.keys():
                entry[json_key] = str(obj[scene_key])
            elif json_key in base:
                entry[json_key] = base[json_key]
        position = tuple(core.rounded(v, core.POSITION_DECIMALS) for v in position)
        quat = tuple(core.rounded(v, core.QUAT_DECIMALS) for v in quat)
        entry["position"] = list(position)
        entry["rotationQuat"] = list(quat)

        for scene_key, json_key in (("support", "support"), ("stacked_on", "stackedOn")):
            if scene_key in obj.keys():
                entry[json_key] = str(obj[scene_key])
            elif json_key in base:
                entry[json_key] = base[json_key]
        entry["aabb"] = list(size)

        if "value_units" in obj.keys():
            entry["valueUnits"] = int(obj["value_units"])
        elif "valueUnits" in base:
            entry["valueUnits"] = int(base["valueUnits"])
        else:
            problems.append("%s: no valueUnits (set the object's 'value_units' custom "
                            "property or pass --base)" % item_id)
        entries.append(entry)

    return entries, problems


def _structure_entries(doc_base):
    base_structures = {structure["id"]: structure for structure in
                       (doc_base or {}).get("structures") or []}
    entries = []
    for obj in common.scene_structures():
        structure_id = str(obj.get("structure_id") or obj.name)
        base = base_structures.get(structure_id, {})
        center, quat = common.pose_of(obj)
        size = common.structure_size_u(obj)
        entry = {"id": structure_id}
        entry["kind"] = str(obj["struct_kind"]) if "struct_kind" in obj.keys() else base.get("kind")
        entry["role"] = str(obj["role"])
        entry["shape"] = str(obj["shape"]) if "shape" in obj.keys() else base.get("shape", "box")
        entry["center"] = list(core.rounded(v, core.POSITION_DECIMALS) for v in center)
        entry["size"] = list(core.rounded(v, core.SIZE_DECIMALS) for v in size)
        entry["rotationQuat"] = list(core.rounded(v, core.QUAT_DECIMALS) for v in quat)
        entry["collision"] = (str(obj["struct_collision"]) if "struct_collision" in obj.keys()
                              else base.get("collision", "box"))
        entry["absorbable"] = bool(obj.get("absorbable", base.get("absorbable", False)))
        entry["support"] = bool(obj.get("support", base.get("support", True)))
        if "struct_meets" in obj.keys():
            entry["meets"] = str(obj["struct_meets"])
        elif "meets" in base:
            entry["meets"] = base["meets"]
        if "struct_material" in obj.keys():
            entry["material"] = str(obj["struct_material"])
        elif "material" in base:
            entry["material"] = base["material"]
        if "struct_notes" in obj.keys():
            entry["notes"] = str(obj["struct_notes"])
        elif "notes" in base:
            entry["notes"] = base["notes"]
        if entry.get("kind") is None:
            del entry["kind"]
        entries.append(entry)
    return entries


def _camera_entries(doc_base):
    """Cameras named ``CAM_<id>`` that carry the authoring custom properties."""
    base_cameras = {camera["id"]: camera for camera in (doc_base or {}).get("cameras") or []}
    entries = {}
    for camera in base_cameras.values():
        entries[camera["id"]] = dict(camera)
    for obj in sorted(bpy.data.objects, key=lambda item: item.name):
        if obj.type != "CAMERA" or not obj.name.startswith("CAM_"):
            continue
        camera_id = obj.name[len("CAM_"):]
        kind = str(obj.get("cam_type", "perspective"))
        position, quat = common.pose_of(obj)
        if kind == "ortho_top":
            # A top camera looks straight down; only its XZ placement and the
            # coverage are authored, so keep the base target when there is one.
            target = [round(position[0], core.CAMERA_DECIMALS),
                      round(float((base_cameras.get(camera_id) or {}).get("target", [0, 0, 0])[1]),
                            core.CAMERA_DECIMALS),
                      round(position[2], core.CAMERA_DECIMALS)]
            entries[camera_id] = {"id": camera_id, "type": "ortho_top",
                                  "location": list(position),
                                  "target": target,
                                  "coverage_m": float(obj.get("cam_coverage_m", 10.0))}
        else:
            target = obj.get("cam_target")
            if target is None and camera_id in base_cameras:
                target = base_cameras[camera_id].get("target")
            if target is None:
                print("WARN camera %s has no 'cam_target' custom property; keeping the base "
                      "entry (set it in the Object properties panel)" % camera_id)
                continue
            entries[camera_id] = {"id": camera_id, "type": "perspective",
                                  "location": list(position),
                                  "target": list(core.rounded(float(v), core.CAMERA_DECIMALS)
                                                 for v in target),
                                  "lens": float(obj.get("cam_lens", 45.0))}
    return [entries[key] for key in sorted(entries)]


def _self_check(entries):
    """JSON numbers -> Blender matrix, compared with the object's own matrix.

    Catches the measured failure where ``matrix_world`` was read before the
    depsgraph ran and every exported pose was the origin (LEVEL_AUTHORING 1.5).
    """
    bpy.context.view_layer.update()
    objects = {obj.name: obj for obj in common.scene_props()}
    step = max(1, len(entries) // ROUNDTRIP_SAMPLES)
    sample = entries[::step][:ROUNDTRIP_SAMPLES]
    failures = []
    sampled = []
    for entry in sample:
        obj = objects.get(entry["itemId"])
        if obj is None:
            continue
        sampled.append(entry["itemId"])
        position = Vector(core.unity_to_blender_position(tuple(entry["position"])))
        q = core.unity_to_blender_quat(tuple(entry["rotationQuat"]))
        matrix = Matrix.Translation(position) @ Quaternion((q[0], q[1], q[2], q[3])).to_matrix().to_4x4()
        actual = obj.matrix_world
        error = max(max(abs(matrix[r][c] - actual[r][c]) for c in range(4)) for r in range(3))
        if error > ROUNDTRIP_TOLERANCE_M:
            failures.append("%s: %.6f" % (entry["itemId"], error))
    return failures, sampled


def main():
    out = common.option("--out")
    if not out:
        raise SystemExit("export_placement: --out <path> is required")
    doc_base = _base_doc(common.option("--base"))
    exports = common.option("--exports")
    exports_dir = common.resolve(exports) if exports else None

    bpy.context.view_layer.update()   # matrix_world is stale until the depsgraph runs

    if common.flag("--seat"):
        structure_objects = {str(obj.get("structure_id") or obj.name): obj
                             for obj in common.scene_structures()}
        moved = common.seat_scene_props(structure_objects)
        print("seated %d prop(s)%s" % (len(moved), (": " + ", ".join(moved)) if moved else ""))

    for collection in {obj.instance_collection for obj in common.scene_props()
                       if obj.instance_collection is not None}:
        if common.library_is_linked(collection):
            raise SystemExit("library collection %r is linked to the scene; instances would "
                             "inherit its visibility" % collection.name)

    entries, problems = _prop_entries(doc_base, exports_dir)
    if problems:
        for problem in problems:
            print("ERROR " + problem)
        raise SystemExit("export_placement: %d prop(s) cannot be written" % len(problems))

    doc = {
        "schema": (doc_base or {}).get("schema", core.SCHEMA),
        "level": dict((doc_base or {}).get("level") or {}),
        "props": entries,
        "structures": _structure_entries(doc_base),
        "cameras": _camera_entries(doc_base),
        "renderPresets": list((doc_base or {}).get("renderPresets") or []),
    }
    # ``defs[]`` is level-level data (what each def is worth), not scene geometry, so it
    # comes from the base like ``level`` and the presets.  It used to be dropped, which
    # lost every declared value on a scene -> JSON round trip (measured on story_01:
    # 44 defs vanished while the file still passed the gate, because each prop carried
    # its own valueUnits; a later prop relying on the def's value would have been red).
    if doc_base is not None and "defs" in doc_base:
        doc["defs"] = list(doc_base.get("defs") or [])
    if not doc["level"]:
        print("WARN no --base: level{} is empty and `levelbuild.py check` will reject the file")

    failures, sampled = _self_check(entries)
    if failures:
        for failure in failures:
            print("ERROR roundtrip " + failure)
        raise SystemExit("export_placement: coordinate round trip failed (%d sample(s))"
                         % len(failures))

    target = common.resolve(out)
    core.write_doc(doc, target)
    print("wrote %s: props=%d structures=%d cameras=%d roundtrip=%s"
          % (core.display_path(target), len(entries), len(doc["structures"]), len(doc["cameras"]),
             ",".join(sampled) or "none"))

    if doc_base is not None:
        before = {prop["itemId"] for prop in doc_base.get("props") or []}
        after = {prop["itemId"] for prop in entries}
        added = sorted(after - before)
        removed = sorted(before - after)
        print("diff vs base: +%d ~%d -%d%s%s"
              % (len(added), 0, len(removed),
                 (" added=" + ",".join(added)) if added else "",
                 (" removed=" + ",".join(removed)) if removed else ""))
    print("ok")


if __name__ == "__main__":
    main()
