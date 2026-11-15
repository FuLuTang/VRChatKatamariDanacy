#!/usr/bin/env python3
"""Level authoring CLI: the gate every ``placement.json`` passes before it ships.

    python3 Tools/levelbuild/levelbuild.py check <placement.json> [--report] [--json]
    python3 Tools/levelbuild/levelbuild.py format <placement.json> [--check]
    python3 Tools/levelbuild/levelbuild.py seat <placement.json> [--write] [--item ID ...]

Exit codes: ``0`` clean, ``1`` hard error (the gate is red), ``2`` the input
could not be read at all.

All geometry lives in :mod:`placement_core`; this file is argument handling and
report formatting only, so the Blender scripts and the validator cannot drift.
See ``Tools/levelbuild/README.md`` for the human/agent workflows and
``Docs/Design/Tools/LEVEL_AUTHORING.md`` for the design.
"""

import argparse
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402


def _print_banner(doc):
    level = doc.get("level") or {}
    print("quatOrder=%s  schema=%s  level=%s  convention=%s"
          % (core.QUAT_ORDER, doc.get("schema"), level.get("id"), level.get("convention")))


def _summary(doc):
    level = doc.get("level") or {}
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    structures = [structure for structure in doc.get("structures") or []
                  if isinstance(structure, dict)]
    absorbable = sum(1 for prop in props if prop.get("absorbable") is True)
    kinds = sorted({prop.get("def") for prop in props if prop.get("def")})
    # A curve_ramp is one logical ramp; its slab segments are what the motor bakes, so both
    # numbers are printed (one ramp, N segments - CONTACT_MODEL_V2 5.7).
    curves = [structure for structure in structures if structure.get("role") == "curve_ramp"]
    curve_radius = core.curve_ball_radius_m(level)
    curve_segments = sum(core.curve_segment_count(structure, curve_radius)
                         for structure in curves)
    return ("props=%d units=%d absorbable=%d defIds=%d defs[]=%d structures=%d "
            "curves=%d/%d ballDiameterMax=%s"
            % (len(props), core.value_units_total(doc), absorbable, len(kinds),
               len(doc.get("defs") or []), len(structures), len(curves), curve_segments,
               level.get("ballDiameterMax")))


def _report_text(report, doc):
    lines = []
    if report.errors:
        lines.append("ERRORS (%d):" % len(report.errors))
        lines.extend("  - " + error for error in report.errors)
    if report.warnings:
        lines.append("WARNINGS (%d):" % len(report.warnings))
        lines.extend("  - " + warning for warning in report.warnings)
    if report.notes:
        lines.append("NOTES (%d):" % len(report.notes))
        lines.extend("  - " + note for note in report.notes)
    return "\n".join(lines)


def cmd_check(args):
    if not args.level and not args.placement:
        print("levelbuild: check needs a placement path or --level", file=sys.stderr)
        return 2
    if args.level and args.placement:
        print("levelbuild: give either a placement path or --level, not both", file=sys.stderr)
        return 2
    if args.level:
        try:
            path = core.placement_path(args.level)
        except ValueError as error:
            print("levelbuild: %s" % error, file=sys.stderr)
            return 2
    else:
        path = Path(args.placement)
    if not path.is_file():
        print("levelbuild: cannot read %s" % path, file=sys.stderr)
        return 2
    try:
        doc = core.load_doc(path)
    except (OSError, json.JSONDecodeError) as error:
        print("levelbuild: %s is not readable JSON: %s" % (path, error), file=sys.stderr)
        return 2

    manifests = None
    if args.manifests:
        directory = Path(args.manifests)
        if not directory.is_dir():
            print("levelbuild: --manifests %s is not a directory" % directory, file=sys.stderr)
            return 2
        manifests = core.load_manifests(directory)

    report = core.validate(doc, path=path, manifests=manifests)
    if args.strict:
        report.errors.extend(report.warnings)
        report.warnings = []

    if args.json:
        # Machine-readable only: a caller (the Unity operator's script, a CI job) can pipe
        # this straight into json.load without stripping a banner first.
        print(json.dumps({
            "path": core.display_path(path),
            "level": (doc.get("level") or {}).get("id"),
            "schema": doc.get("schema"),
            "quatOrder": core.QUAT_ORDER,
            "props": len([p for p in doc.get("props") or [] if isinstance(p, dict)]),
            "valueUnits": core.value_units_total(doc),
            "errors": report.errors,
            "warnings": report.warnings,
            "notes": report.notes,
            "tables": report.tables,
        }, ensure_ascii=False, indent=2))
        return 0 if report.ok else 1

    _print_banner(doc)
    print(_summary(doc))
    print("")

    if args.report:
        print(core.format_report_tables(report.tables,
                                        expected=(doc.get("level") or {}).get("expected")))
        print("")

    text = _report_text(report, doc)
    if text:
        print(text)
        print("")

    print("%s %s (%d error(s), %d warning(s))"
          % ("PASS" if report.ok else "FAIL", core.display_path(path),
             len(report.errors), len(report.warnings)))
    return 0 if report.ok else 1


def cmd_format(args):
    path = Path(args.placement)
    if not path.is_file():
        print("levelbuild: cannot read %s" % path, file=sys.stderr)
        return 2
    try:
        doc = core.load_doc(path)
    except (OSError, json.JSONDecodeError) as error:
        print("levelbuild: %s is not readable JSON: %s" % (path, error), file=sys.stderr)
        return 2

    canonical = core.dump_doc(doc)
    current = path.read_text(encoding="utf-8")
    if canonical == current:
        print("already canonical: %s" % core.display_path(path))
        return 0
    if args.check:
        print("not canonical: %s" % core.display_path(path))
        return 1
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(canonical)
    print("rewrote %s (%d -> %d bytes)"
          % (core.display_path(path), len(current.encode("utf-8")), len(canonical.encode("utf-8"))))
    return 0


def cmd_seat(args):
    """``seat``: one implementation, shared with the resident editor's ``seat`` op."""
    path = Path(args.placement)
    if not path.is_file():
        print("levelbuild: cannot read %s" % path, file=sys.stderr)
        return 2
    doc = core.load_doc(path)
    result = editor_core.seat_props(doc, set(args.item) if args.item else None)

    for detail in result["details"]:
        print("%-24s pos %s -> %s  quat %s -> %s"
              % (detail["itemId"],
                 ["%.4f" % v for v in detail["previous"]],
                 ["%.4f" % v for v in detail["position"]],
                 ["%.5f" % v for v in detail["previousQuat"]],
                 ["%.5f" % v for v in detail["rotationQuat"]]))

    moved = len(result["moved"])
    skipped = len(result["skipped"])
    print("")
    print("%d prop(s) re-seated, %d skipped, %d untouched"
          % (moved, skipped, result["props"] - moved - skipped))
    if moved and args.write:
        core.write_doc(doc, path)
        print("wrote %s" % core.display_path(path))
    elif moved:
        print("dry run; pass --write to apply")
    return 0


# --------------------------------------------------------------------------- #
# picnic_corner_layout.json -> placement.json
# --------------------------------------------------------------------------- #
#: ``Docs/Design/StoryMode/picnic_corner_layout.json`` is the level designer's own
#: format (x/y/z + yaw, labels in prose); the design's section 7.2 defines the
#: mapping. Only the *shapes* differ - the numbers are the author's.
LAYOUT_SUPPORT_ALIASES = {
    "ground": None,
    "bed": "flower_bed",
}

#: The ``overview_3q`` camera stands outside the **south-east** corner: the corner tree is
#: a 9 m pillar at the south-west one, so a south-west view has the trunk between the
#: camera and the arena - the old 45 mm, 4 m-out pose cropped the whole south-west corner
#: (tree and stroller), and simply widening it put the trunk across the middle of the
#: frame.  Distance, height and lens scale with the field so every corner stays in frame
#: (at the 13.4 m first level the four corners land at 90 % of the half-frame).
_OVERVIEW_MARGIN = 0.47
_OVERVIEW_HEIGHT = 0.37
_OVERVIEW_LENS = 32


def _yaw_quat(yaw_deg):
    return core.quat_axis_angle((0.0, 1.0, 0.0), math.radians(float(yaw_deg or 0.0)))


#: A boundary is drawn as a 1.2 m high, 4 cm thick wall along the arena edge.
BOUNDARY_HEIGHT_M = 1.2
BOUNDARY_THICKNESS_M = 0.04


def _boundary_size(inward, length):
    """The wall's local size for an inward normal, keeping local +Y on ``inward``.

    ``align_up_to`` only pins local +Y; the two in-plane axes are whatever the
    shortest-arc rotation produces, and they differ between the four sides.  For an
    inward normal of +-X it sends local +X onto -+Y (so local +X *is* the height) and
    leaves local +Z along the wall; for +-Z it is local +Z that turns vertical and
    local +X that stays along the wall.  Handing every wall ``[length, thickness,
    height]`` therefore made the x walls ``length`` metres tall - 13.4 m grey pillars
    in the overview render - while the z walls looked right.

    So the desired *world* dimensions are mapped back onto whichever local axis
    carries them instead of assuming one fixed order.  A normal that is not aligned
    with a world axis has no such frame under ``align_up_to`` (both in-plane axes end
    up oblique), so it raises rather than silently drawing a slanted, wrong-sized box;
    the layout format only has the four side walls.
    """
    quat = core.align_up_to(inward)
    wanted = {"y": BOUNDARY_HEIGHT_M}
    if abs(inward[0]) >= abs(inward[2]):
        wanted["x"] = BOUNDARY_THICKNESS_M     # normal along X: the thickness is world X
        wanted["z"] = length                   # the wall runs along world Z
    else:
        wanted["z"] = BOUNDARY_THICKNESS_M
        wanted["x"] = length

    size = []
    for local_axis, local in enumerate(((1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
                                        (0.0, 0.0, 1.0))):
        turned = core.quat_rotate(quat, local)
        world_axis = max(range(3), key=lambda index: abs(turned[index]))
        if abs(turned[world_axis]) < 0.999:
            raise ValueError(
                "boundary inward normal %r is not axis-aligned: local axis %d lands on "
                "no world axis (%.4f), so a 1.2 m high wall cannot be drawn without "
                "changing the orientation" % (tuple(inward), local_axis,
                                              abs(turned[world_axis])))
        size.append(wanted["xyz"[world_axis]])
    return size


def _structure_from_shell(shell, bounds):
    structures = []
    x0, x1 = float(bounds["x0"]), float(bounds["x1"])
    z0, z1 = float(bounds["z0"]), float(bounds["z1"])
    width, depth = x1 - x0, z1 - z0
    centre_x, centre_z = (x0 + x1) / 2.0, (z0 + z1) / 2.0
    floor = float(bounds.get("ground_y", 0.0))
    by_id = {entry["id"]: entry for entry in shell}

    if "ground" in by_id:
        structures.append({
            "id": "ground", "kind": "lawn", "role": "ground", "shape": "plane",
            "center": [centre_x, floor, centre_z], "size": [width, 0.04, depth],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "none",
            "absorbable": False, "support": True,
            "notes": "arena floor: this is the level's ground plane",
        })

    walls = {
        "boundary_x0": ((1.0, 0.0, 0.0), (x0, centre_z), depth),
        "boundary_x1": ((-1.0, 0.0, 0.0), (x1, centre_z), depth),
        "boundary_z0": ((0.0, 0.0, 1.0), (centre_x, z0), width),
        "boundary_z1": ((0.0, 0.0, -1.0), (centre_x, z1), width),
    }
    for wall_id, (inward, (cx, cz), length) in walls.items():
        if wall_id not in by_id:
            continue
        # The JSON states a wall as a plane whose local +Y is the inward normal.  Its
        # local size is chosen per normal so the *world* box is always side x 1.2 x 0.04.
        quat = core.align_up_to(inward)
        structures.append({
            "id": wall_id, "kind": "boundary", "role": "boundary", "shape": "plane",
            "center": [cx, floor + BOUNDARY_HEIGHT_M / 2.0, cz],
            "size": [round(v, 4) for v in _boundary_size(inward, length)],
            "rotationQuat": list(core.rounded_seq(quat, core.QUAT_DECIMALS)),
            "collision": "none", "absorbable": False, "support": False,
        })

    for entry in shell:
        shell_id = entry["id"]
        if shell_id.startswith("boundary") or shell_id == "ground":
            continue
        if "x0" in entry:                      # an axis-aligned slab given by its footprint
            size = [float(entry["x1"]) - float(entry["x0"]), float(entry["top"]),
                    float(entry["z1"]) - float(entry["z0"])]
            centre = [(float(entry["x0"]) + float(entry["x1"])) / 2.0,
                      float(entry["top"]) / 2.0,
                      (float(entry["z0"]) + float(entry["z1"])) / 2.0]
        else:
            size = [float(entry["size"][0]), float(entry["size"][1]), float(entry["size"][2])]
            centre = [float(entry["x"]), float(entry["top"]) - size[1] / 2.0, float(entry["z"])]
        structures.append({
            "id": shell_id, "kind": "shell", "role": "box", "shape": "box",
            "center": [round(v, 4) for v in centre], "size": [round(v, 4) for v in size],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
            "absorbable": False, "support": True,
        })
    return structures


def _structure_from_ramp(ramp):
    """A ramp is a box whose local +Y is the surface normal and whose +Z runs up the slope.

    **The author's line is the walking surface, not the board's underside.**  ``bottom``
    and ``top`` are points on the plane the ball rolls on (``PICNIC_CORNER_LAYOUT`` 4.4
    authors them as ``底 → 顶`` and demands the top edge flush with the table, delta = 0).
    The walking surface is the box's +Y face - what ``structure_top_plane`` returns and
    what P3's ``AddRamp(center, n, right, halfWidth, halfRun)`` has to take its ``center``
    from - so that face is what has to land on those points.  The 4 cm board therefore hangs *below* the authored line: the centre
    sits half a thickness under it (``- normal * t/2``), not above it.  At the foot the
    board's lower corner is up to one thickness under the ground, which is intended; the
    validator allows exactly that for ``role=ramp`` (``RAMP_UNDERSIDE_ALLOWANCE_M``).

    The centre is rebuilt from the author's *foot point* rather than copied from the
    layout's ``center``: copying ``center`` leaves the boards either floating or 2 cm sunk
    (the validator catches both).  Local +Z is the slope because ``align_up_to`` turns the
    model's +Y onto the normal and leaves +X alone, which is the layout's ``right`` axis.
    """
    normal = tuple(float(v) for v in ramp["normal"])
    quat = core.align_up_to(normal)
    thickness = core.RAMP_THICKNESS_M
    bottom = tuple(float(v) for v in ramp["bottom"])
    top = tuple(float(v) for v in ramp["top"])
    tangent = core.normalized(core.sub(top, bottom))
    length = float(ramp["slope_length"])
    centre = core.add(bottom, core.add(core.scale(tangent, length / 2.0),
                                       core.scale(normal, -thickness / 2.0)))
    return {
        "id": ramp["id"], "kind": "ramp", "role": "ramp", "shape": "box",
        "center": [round(v, 4) for v in centre],
        "size": [round(float(ramp["width"]), 4), thickness, round(length, 4)],
        "rotationQuat": list(core.rounded_seq(quat, core.QUAT_DECIMALS)),
        "collision": "box", "absorbable": False, "support": True,
        "notes": ramp.get("note", ""),
    }


def _layout_items(layout):
    """Every absorbable piece of the layout, in one list (design section 7.2)."""
    items = []
    for prop in layout.get("props") or []:
        items.append(("prop", prop))
    for gate_entry in layout.get("gate") or []:
        items.append(("gate", gate_entry))
    for structure in layout.get("structures") or []:
        items.append(("furniture", structure))
    return items


def _prop_from_layout(kind, entry, manifests, catalog, drop_missing, skipped):
    def_id = entry.get("def")
    if not def_id:
        skipped.append("%s: no def" % entry.get("itemId") or entry.get("id"))
        return None
    if def_id not in manifests:
        if drop_missing:
            skipped.append("%s (def %s has no model)" % (entry.get("itemId") or entry.get("id"), def_id))
            return None
        aabb = [float(v) for v in entry.get("size", (0.1, 0.1, 0.1))]
    else:
        aabb = list(manifests[def_id]["aabb"])
    item_id = entry.get("itemId") or entry.get("id")
    prop_kind = entry.get("kind") or catalog.get(def_id) or manifests.get(def_id, {}).get("category")
    prop = {
        "itemId": str(item_id),
        "def": def_id,
        "kind": prop_kind,
        "zone": entry.get("zone") or kind,
        "position": [round(float(entry["x"]), 4), round(float(entry["y"]), 4),
                     round(float(entry["z"]), 4)],
        "rotationQuat": list(core.rounded_seq(_yaw_quat(entry.get("yaw", 0.0)),
                                              core.QUAT_DECIMALS)),
        "aabb": list(aabb),
        "valueUnits": int(entry.get("units", 1)) if int(entry.get("units", 1)) > 0 else 1,
    }
    support = entry.get("support")
    if support and support not in ("ground",):
        prop["_layout_support"] = support
    return prop


def _infer_support(prop, structures_by_id, prop_ids, ids_by_def):
    """Turn the layout's support label into this schema's ``support`` / ``stackedOn``.

    The layout names zones and furniture in prose ("bed", "folding_table"); the
    schema wants a structure id or another prop's itemId, checked geometrically.
    A label this function cannot place is left undeclared and the validator's
    "floats" rule reports it - the gate, not a guess, decides.
    """
    label = prop.pop("_layout_support", None)
    if not label:
        return
    if label == "bed" and "flower_bed" in structures_by_id:
        prop["support"] = "flower_bed"
        return
    if label in prop_ids:
        prop["stackedOn"] = label
        return
    # A furniture label ("folding_table") names a *def* in this layout, so it maps to the
    # one piece of that def (more than one would be ambiguous, and guessing would put a
    # prop on the wrong table).
    matches = [item_id for item_id in ids_by_def.get(label, []) if item_id != prop["itemId"]]
    if len(matches) == 1:
        prop["stackedOn"] = matches[0]
        return
    prop["_unmapped_support"] = label


def _seat_converted_props(props, structures_by_id, layout):
    """Seat every prop on its support surface using the *real* (manifest) size.

    The layout's own ``y`` column is ``support_top + height / 2`` with the size table it
    was built from, and that table is not the model AABB (the design records the same
    gap: design size vs real size). Re-seating here is the one-rule fix - the surface
    stays where the author put it, the prop is lifted by its true half height - and it is
    exactly what ``levelbuild.py seat`` does to a hand-edited file.
    """
    floor = float(layout["bounds"].get("ground_y", 0.0))
    by_id = {prop["itemId"]: prop for prop in props}

    def seat(prop, normal, point):
        position = tuple(prop["position"])
        size = tuple(prop["aabb"])
        quat = tuple(prop["rotationQuat"])
        new_position, new_quat = core.reseat(position, size, quat, normal, point)
        prop["position"] = [core.rounded(v, core.POSITION_DECIMALS) for v in new_position]
        prop["rotationQuat"] = [core.rounded(v, core.QUAT_DECIMALS) for v in new_quat]

    for prop in props:
        if prop.get("stackedOn"):
            continue
        support = prop.get("support")
        if support and support in structures_by_id:
            normal, point = core.structure_top_plane(structures_by_id[support])
        else:
            normal, point = (0.0, 1.0, 0.0), (0.0, floor, 0.0)
        seat(prop, normal, point)

    for prop in props:
        other_id = prop.get("stackedOn")
        if not other_id or other_id not in by_id:
            continue
        other = by_id[other_id]
        try:
            top = core.world_aabb(tuple(other["position"]), tuple(other["aabb"]),
                                 tuple(other["rotationQuat"]))[1][1]
        except (KeyError, TypeError, ValueError):
            continue
        seat(prop, (0.0, 1.0, 0.0), (0.0, top, 0.0))


def cmd_from_layout(args):
    path = Path(args.layout)
    if not path.is_file():
        print("levelbuild: cannot read %s" % path, file=sys.stderr)
        return 2
    layout = json.loads(path.read_text(encoding="utf-8"))
    manifests = core.load_manifests()
    level_id = args.level_id or path.stem.replace("_layout", "")
    bounds = layout["bounds"]
    ball = layout["ball_start"]
    spawn = layout["spawn"]
    rules = layout.get("rules") or {}

    structures = _structure_from_shell(layout.get("shell") or [], bounds)
    structures += [_structure_from_ramp(ramp) for ramp in layout.get("ramps") or []]
    structures_by_id = {structure["id"]: structure for structure in structures}

    # A ramp "meets" the platform its top edge arrives on; naming it lets the validator
    # check the step (design section 8.10). Found geometrically, not by trusting a label.
    for ramp in layout.get("ramps") or []:
        structure = structures_by_id.get(ramp["id"])
        if structure is None:
            continue
        top = tuple(float(v) for v in ramp["top"])
        best = None
        for candidate in structures:
            if candidate["role"] not in ("box",):
                continue
            normal, point = core.structure_top_plane(candidate)
            height = core.plane_height_at(normal, point, top[0], top[2])
            if height is None:
                continue
            if abs(top[1] - height) <= 0.02:
                best = candidate["id"]
                break
        if best is not None:
            structure["meets"] = best

    catalog = {}
    for def_id, manifest in sorted(manifests.items()):
        catalog[def_id] = manifest.get("category")

    skipped = []
    props = []
    for kind, entry in _layout_items(layout):
        prop = _prop_from_layout(kind, entry, manifests, catalog, not args.keep_missing, skipped)
        if prop is not None:
            props.append(prop)

    # The layout's "folding_table" support points at a furniture *prop*, so the prop
    # ids must exist before the labels can be resolved.
    prop_ids = {prop["itemId"] for prop in props}
    ids_by_def = {}
    for prop in props:
        ids_by_def.setdefault(prop["def"], []).append(prop["itemId"])
    for prop in props:
        _infer_support(prop, structures_by_id, prop_ids, ids_by_def)

    if not args.no_seat:
        _seat_converted_props(props, structures_by_id, layout)

    defs = {}
    for prop in props:
        entry = defs.setdefault(prop["def"], {"id": prop["def"], "kind": prop["kind"],
                                             "valueUnits": prop["valueUnits"]})
        if entry["valueUnits"] != prop["valueUnits"]:
            entry.pop("valueUnits", None)     # per-prop values disagree: keep them per prop

    total_units = sum(prop["valueUnits"] for prop in props)
    centre_x = (float(bounds["x0"]) + float(bounds["x1"])) / 2.0
    centre_z = (float(bounds["z0"]) + float(bounds["z1"])) / 2.0
    span = max(float(bounds["x1"]) - float(bounds["x0"]), float(bounds["z1"]) - float(bounds["z0"]))

    doc = {
        "schema": core.SCHEMA,
        "level": {
            "id": level_id,
            "scene": args.scene,
            "units": "m",
            "convention": core.CONVENTION,
            "ballStart": [round(float(ball["x"]), 4), round(float(ball["y"]), 4),
                          round(float(ball["z"]), 4)],
            "playerSpawn": [round(float(spawn["x"]), 4), round(float(spawn["y"]), 4),
                            round(float(spawn["z"]), 4)],
            "ballDiameterStart": round(float(ball.get("start_diameter_m", 0.30)), 4),
            "ballDiameterMax": round(float(rules.get("ball_cap_m", 2.20)), 4),
            "groundY": float(bounds.get("ground_y", 0.0)),
            "bounds": [[float(bounds["x0"]), float(bounds["z0"])],
                       [float(bounds["x1"]), float(bounds["z1"])]],
            "spawnClearanceM": 1.0,
            "budget": {"planes": 8, "boxes": 8, "ramps": 4, "propSupports": 6, "blocking": 6},
            "expected": {"propCount": len(props), "valueUnits": total_units},
            "zones": [{"id": zone} for zone in sorted({prop.get("zone") for prop in props if prop.get("zone")})],
        },
        "defs": [defs[key] for key in sorted(defs)],
        "props": props,
        "structures": structures,
        "cameras": [
            {"id": "overview_3q", "type": "perspective",
             "location": [round(float(bounds["x1"]) + _OVERVIEW_MARGIN * span, 3),
                          round(_OVERVIEW_HEIGHT * span, 3),
                          round(float(bounds["z0"]) - _OVERVIEW_MARGIN * span, 3)],
             "target": [round(centre_x, 3), 0.35, round(centre_z, 3)], "lens": _OVERVIEW_LENS},
            {"id": "top", "type": "ortho_top",
             "location": [round(centre_x, 3), 14.0, round(centre_z, 3)],
             "target": [round(centre_x, 3), 0.0, round(centre_z, 3)],
             "coverage_m": round(span * 1.05, 3)},
            {"id": "ball_eye", "type": "perspective",
             "location": [round(float(ball["x"]), 3), 0.35, round(float(ball["z"]) - 1.6, 3)],
             "target": [round(float(ball["x"]) + 2.0, 3), 0.35, round(float(ball["z"]) + 1.0, 3)],
             "lens": 30},
        ],
        "renderPresets": [
            {"id": "plan", "camera": "top", "engine": "WORKBENCH", "res": [1280, 720]},
            {"id": "overview", "camera": "overview_3q", "engine": "EEVEE", "res": [1280, 720]},
            {"id": "ball_eye", "camera": "ball_eye", "engine": "EEVEE", "res": [1280, 720]},
        ],
    }

    if args.expected:
        wanted = [int(part) for part in args.expected.replace(" ", "").split(",")]
        doc["level"]["expected"] = {"propCount": wanted[0], "valueUnits": wanted[1]}

    out = Path(args.out)
    core.write_doc(doc, out)
    print("wrote %s" % core.display_path(out))
    print("  level=%s props=%d units=%d structures=%d defs=%d"
          % (level_id, len(props), total_units, len(structures), len(defs)))
    by_role = {}
    for structure in structures:
        by_role[structure["role"]] = by_role.get(structure["role"], 0) + 1
    print("  structures by role: %s" % json.dumps(by_role, sort_keys=True))
    unmapped = sorted(prop["itemId"] for prop in props if prop.pop("_unmapped_support", None))
    if unmapped:
        print("  UNMAPPED support label on: %s" % ", ".join(unmapped))
    if skipped:
        print("  dropped %d piece(s) with no model:" % len(skipped))
        for line in skipped[:12]:
            print("    - %s" % line)
        if len(skipped) > 12:
            print("    ... %d more" % (len(skipped) - 12))
    print("  next: python3 Tools/levelbuild/levelbuild.py check %s --report" % core.display_path(out))
    return 0


def build_parser():
    parser = argparse.ArgumentParser(
        prog="levelbuild.py",
        description="Validate / normalise / re-seat a placement.json (the level authoring gate).")
    sub = parser.add_subparsers(dest="command")

    check = sub.add_parser("check", help="validate one placement.json (exit 1 on a hard error)")
    check.add_argument("placement", nargs="?",
                       help="the placement.json (or use --level to resolve it)")
    check.add_argument("--level", default=None, metavar="LEVEL_ID",
                       help="resolve Docs/Design/Levels/<LEVEL_ID>/placement.json")
    check.add_argument("--manifests", default=None, metavar="DIR",
                       help="load def manifests from DIR instead of "
                            "Docs/Art/OriginalModels/props/exports")
    check.add_argument("--report", action="store_true",
                       help="print the zone / def / tier / three-state tables")
    check.add_argument("--json", action="store_true", help="print the report as JSON")
    check.add_argument("--strict", action="store_true", help="treat warnings as errors")
    check.set_defaults(func=cmd_check)

    fmt = sub.add_parser("format", help="rewrite the file in canonical (deterministic) form")
    fmt.add_argument("placement")
    fmt.add_argument("--check", action="store_true", help="only report whether it is canonical")
    fmt.set_defaults(func=cmd_format)

    seat = sub.add_parser("seat", help="re-seat props on their declared support surface")
    seat.add_argument("placement")
    seat.add_argument("--write", action="store_true", help="write the file (default is a dry run)")
    seat.add_argument("--item", action="append", metavar="ITEM_ID",
                      help="restrict to these itemIds (repeatable)")
    seat.set_defaults(func=cmd_seat)

    convert = sub.add_parser(
        "from-layout",
        help="convert Docs/Design/StoryMode/picnic_corner_layout.json into a placement.json")
    convert.add_argument("layout")
    convert.add_argument("--out", required=True)
    convert.add_argument("--level-id", default=None)
    convert.add_argument("--scene", default="StoryMode")
    convert.add_argument("--no-seat", action="store_true",
                         help="keep the layout's y values instead of seating on the support")
    convert.add_argument("--expected", default=None, metavar="COUNT,UNITS",
                         help="override level.expected (e.g. the design table's 127,1500)")
    convert.add_argument("--keep-missing", action="store_true",
                         help="keep defs without a model (the check will then be red)")
    convert.set_defaults(func=cmd_from_layout)
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "func", None):
        parser.print_help()
        return 2
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
