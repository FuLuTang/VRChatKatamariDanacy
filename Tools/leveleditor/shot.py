#!/usr/bin/env python3
"""Look at any level cheaply: one JSON line, one or more PNGs.

    python3 Tools/leveleditor/shot.py story_01_bench_size
    python3 Tools/leveleditor/shot.py _ref_cancer --view start --layers props,terrain,grid
    python3 Tools/leveleditor/shot.py --status
    python3 Tools/leveleditor/shot.py --stop

Why a daemon?
-------------
A fresh Chromium + a fresh page load costs several seconds.  An agent that wants to
*look* at a level usually does it repeatedly (move a prop, look again), so the first
call starts a small persistent render daemon and every later call reuses the same
browser, page and level.  The daemon is a normal process; `--stop` ends it cleanly and
it also exits by itself after `--idle-timeout` seconds without a request.

What is rendered
----------------
The *real* web editor page (``Tools/leveleditor/web/index.html``) in a headless
Chromium, served by the *real* editor server (``server.py``) with its own port and
token.  That page draws the same scene a human sees; ``web/js/shot.js`` only sets a
camera, the visibility layers and the export.  There is no second renderer.

Cameras are planned here, in Python, from the level document: a view is a
(position, target, fov) triple in three.js space (the page's x, y, -z frame) plus the
world-space box it framed.  Keeping the math here makes it testable without a browser
and keeps the page a dumb executor.

Reference levels
----------------
``_ref_*`` levels are UNLICENSED local references.  Shots of them may only be written
under ``~/KatamariRef/viewer_evidence/shots`` (or ``$VRC_KATAMARI_REF_SHOTS``), never
inside the repository, and every image carries an ``UNLICENSED REFERENCE`` watermark
plus a PNG text chunk.  This module refuses the write before a browser is started.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import secrets
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import ref_models  # noqa: E402

SERVICE = "vrc-katamari-leveleditor-shot"
PROTOCOL = 1
DEFAULT_PORT = 8793
DEFAULT_STATE_DIR = Path(os.environ.get("XDG_STATE_HOME")
                         or (Path.home() / ".local/state")) / "vrc-katamari-leveleditor"
DEFAULT_W, DEFAULT_H = 1280, 720
DEFAULT_MAX_PX = 4_000_000
DEFAULT_TIMEOUT = 240.0
DEFAULT_IDLE = 900.0
TILE_MIN = 240
#: Daemons this process started, so `--stop` (and tests) can reap the child instead of
#: leaving a ResourceWarning and a zombie for the shell to collect.
_STARTED_PROCESSES = []
TILE_MAX = 4096
MAX_FRAMES = 32
EYE_HEIGHT_PER_RADIUS = 8.0
START_FOV = 60.0
FIT_FOV = 45.0
SHEET_ORDER = ("top", "iso", "start", "focus:auto")

LAYER_NAMES = ("props", "structures", "terrain", "collision", "parents", "grid",
               "wire", "labels", "tier", "ball", "select")
BASE_LAYERS = ("props", "structures")


class ShotError(Exception):
    """A refusal or failure with a stable machine-readable code."""

    def __init__(self, message, code="shot_error", details=None, status=400):
        super().__init__(message)
        self.message = message
        self.code = code
        self.details = details or []
        self.status = status


# --------------------------------------------------------------------------- #
# state directory / token (the same files server.py uses)
# --------------------------------------------------------------------------- #
def state_dir(explicit=None):
    path = Path(explicit).expanduser() if explicit else DEFAULT_STATE_DIR
    path.mkdir(parents=True, exist_ok=True)
    return path


def state_file(directory):
    return Path(directory) / "shot-daemon.json"


def load_token(directory, explicit=None):
    """Read the editor token, or create the same one server.py would create."""
    if explicit:
        return explicit
    path = Path(directory) / "token"
    if path.is_file():
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    token = secrets.token_urlsafe(24)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(token + "\n")
    return token


# --------------------------------------------------------------------------- #
# levels and geometry
# --------------------------------------------------------------------------- #
def is_ref_level(level_id):
    return str(level_id or "").startswith("_ref_")


def ref_shots_root():
    explicit = os.environ.get("VRC_KATAMARI_REF_SHOTS")
    if explicit:
        return Path(explicit).expanduser()
    return Path.home() / "KatamariRef" / "viewer_evidence" / "shots"


def level_folder(level_id, levels_dir=None):
    return core.level_folder(level_id, Path(levels_dir) if levels_dir else None)


def placement_path(level_id, levels_dir=None):
    """The file the editor would open for this level (same rule as WebEditor.resolve)."""
    folder = level_folder(level_id, levels_dir)
    if is_ref_level(level_id):
        model = folder / "placement.model.json"
        if model.is_file():
            return model
    return folder / "placement.json"


def load_level(level_id, levels_dir=None):
    path = placement_path(level_id, levels_dir)
    if not path.is_file():
        raise ShotError("no placement file for level %r (%s)"
                        % (level_id, core.display_path(path)), "level_not_found", status=404)
    doc = core.load_doc(path)
    return path, doc


def level_meta(level_id, path, doc):
    data = Path(path).read_bytes()
    level = doc.get("level") or {}
    bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
    return {
        "level": level_id,
        "ref": is_ref_level(level_id),
        "placement": core.display_path(Path(path)),
        "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data),
        "propCount": len(doc.get("props") or []),
        "defCount": len(doc.get("defs") or []),
        "structureCount": len(doc.get("structures") or []),
        "ballDiameterStart": float(level.get("ballDiameterStart") or 0.3),
        "ballDiameterMax": float(level.get("ballDiameterMax") or 2.2),
        "bounds": [[float(bounds[0][0]), float(bounds[0][1])],
                   [float(bounds[1][0]), float(bounds[1][1])]],
    }


def _finite(value, label):
    try:
        number = float(value)
    except (TypeError, ValueError):
        raise ShotError("%s must be a number" % label, "bad_argument")
    if not math.isfinite(number):
        raise ShotError("%s must be finite" % label, "bad_argument")
    return number


def _vec3(values, label):
    if not isinstance(values, (list, tuple)) or len(values) != 3:
        raise ShotError("%s must be [x,y,z]" % label, "bad_argument")
    return tuple(_finite(value, label) for value in values)


def box_union(boxes):
    boxes = [box for box in boxes if box is not None]
    if not boxes:
        return None
    return (min(box[0] for box in boxes), min(box[1] for box in boxes),
            min(box[2] for box in boxes), max(box[3] for box in boxes),
            max(box[4] for box in boxes), max(box[5] for box in boxes))


def world_to_three_box(low, high):
    """Unity world AABB (x, y, z) -> three.js box (x, y, -z)."""
    return (min(low[0], high[0]), min(low[1], high[1]), -max(low[2], high[2]),
            max(low[0], high[0]), max(low[1], high[1]), -min(low[2], high[2]))


def three_to_world_box(box):
    return {"min": [round(box[0], 4), round(box[1], 4), round(-box[5], 4)],
            "max": [round(box[3], 4), round(box[4], 4), round(-box[2], 4)]}


def three_to_world(point):
    return [round(point[0], 4), round(point[1], 4), round(-point[2], 4)]


def prop_box(prop):
    try:
        position = tuple(float(value) for value in prop["position"])
        size = tuple(float(value) for value in prop["aabb"])
        quat = tuple(float(value) for value in prop.get("rotationQuat") or (0, 0, 0, 1))
        low, high = core.world_aabb(position, size, quat)
    except (KeyError, TypeError, ValueError):
        return None
    return world_to_three_box(low, high)


def structure_box(structure):
    try:
        low, high = core.structure_world_aabb(structure)
    except (KeyError, TypeError, ValueError):
        return None
    return world_to_three_box(low, high)


def level_bounds_box(doc):
    """The level's authored footprint, shallow in y: what a plan view should frame.

    A top-down perspective camera must not back off because a tree at the edge is 9 m
    tall; the top view is about where things are on the map, so only the bounds
    rectangle and a shallow height band are used.
    """
    level = doc.get("level") or {}
    bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
    ground = float(level.get("groundY") or 0.0)
    return (float(bounds[0][0]) - 0.5, ground - 0.1, -float(bounds[1][1]) - 0.5,
            float(bounds[1][0]) + 0.5, ground + 0.6, -float(bounds[0][1]) + 0.5)


def level_box(doc):
    """Everything a normal level has: the bounds rectangle plus the real prop height."""
    level = doc.get("level") or {}
    bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
    boxes = [prop_box(prop) for prop in doc.get("props") or []]
    boxes += [structure_box(structure) for structure in doc.get("structures") or []]
    boxes = [box for box in boxes if box]
    if boxes:
        low, high = box_union(boxes)[:3], box_union(boxes)[3:]
        low_y = min(low[1], float(level.get("groundY") or 0.0))
        high_y = max(high[1], low_y + 1.0)
    else:
        low_y, high_y = 0.0, 2.0
    return (min(bounds[0][0], bounds[0][0]) - 1.0,
            low_y - 0.25,
            -float(bounds[1][1]) - 1.0,
            float(bounds[1][0]) + 1.0,
            high_y + 0.5,
            -float(bounds[0][1]) + 1.0)


# --------------------------------------------------------------------------- #
# camera math (three.js space; testable without a browser)
# --------------------------------------------------------------------------- #
def _normalize(vector):
    length = math.sqrt(sum(component * component for component in vector))
    if length < 1e-12:
        return (0.0, 0.0, 1.0)
    return tuple(component / length for component in vector)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return sum(left * right for left, right in zip(a, b))


def camera_basis(direction, up_hint=(0.0, 1.0, 0.0)):
    """Right/up for a camera looking from ``center + direction * d`` at ``center``.

    ``direction`` points from the target to the camera (three.js +y is up).  A
    straight-down view has no right vector from the world up hint, so it uses -z up,
    which makes a plan view read like a map (north up).
    """
    direction = _normalize(direction)
    forward = (-direction[0], -direction[1], -direction[2])
    right = _cross(forward, up_hint)
    if math.sqrt(_dot(right, right)) < 1e-6:
        right = (1.0, 0.0, 0.0)
    right = _normalize(right)
    up = _normalize(_cross(right, forward))
    return direction, right, up


def fit_distance(box, direction, fov_deg, aspect, margin=1.06):
    """Smallest camera distance from the box centre that keeps all 8 corners inside.

    Perspective fit, not a bounding sphere: a wide, flat level should not be framed by
    its diagonal.  ``aspect`` is width / height.
    """
    if aspect <= 0 or not math.isfinite(aspect):
        raise ShotError("aspect must be positive", "bad_argument")
    fov = math.radians(max(1.0, min(179.0, float(fov_deg))))
    tan_v = math.tan(fov / 2.0)
    tan_h = tan_v * aspect
    direction = _normalize(direction)
    _, right, up = camera_basis(direction)
    center = ((box[0] + box[3]) / 2.0, (box[1] + box[4]) / 2.0, (box[2] + box[5]) / 2.0)
    forward = (-direction[0], -direction[1], -direction[2])
    distance = 0.0
    for x in (box[0], box[3]):
        for y in (box[1], box[4]):
            for z in (box[2], box[5]):
                relative = (x - center[0], y - center[1], z - center[2])
                px, py, pz = _dot(relative, right), _dot(relative, up), _dot(relative, forward)
                distance = max(distance,
                               abs(px) * margin / tan_h - pz,
                               abs(py) * margin / tan_v - pz)
    radius = math.sqrt(sum((box[3 + index] - box[index]) ** 2 for index in range(3))) / 2.0
    return max(distance, radius * 1.02, 0.05)


def fit_view(box, direction, fov_deg, aspect, margin=1.06):
    direction, right, up = camera_basis(direction)
    center = ((box[0] + box[3]) / 2.0, (box[1] + box[4]) / 2.0, (box[2] + box[5]) / 2.0)
    distance = fit_distance(box, direction, fov_deg, aspect, margin)
    position = tuple(center[index] + direction[index] * distance for index in range(3))
    return {"position": position, "target": center, "up": up,
            "fov": float(fov_deg), "distance": distance, "box": box}


def look_view(position, target, fov_deg):
    position = _vec3(position, "camera position")
    target = _vec3(target, "camera target")
    if math.dist(position, target) < 1e-6:
        raise ShotError("camera position and target are the same point", "bad_argument")
    return {"position": position, "target": target, "up": (0.0, 1.0, 0.0),
            "fov": float(fov_deg), "distance": math.dist(position, target), "box": None}


def fov_from_lens(lens):
    """Vertical FOV from a 35 mm focal length (24 mm sensor height), Unity-like."""
    try:
        lens = float(lens)
    except (TypeError, ValueError):
        return FIT_FOV
    if lens <= 0 or not math.isfinite(lens):
        return FIT_FOV
    return math.degrees(2.0 * math.atan(24.0 / (2.0 * lens)))


def start_view(doc, aspect):
    """Eye at the spawn, human-eye height scaled by the start ball radius."""
    level = doc.get("level") or {}
    spawn = level.get("ballStart") or level.get("playerSpawn") or [0.0, 0.15, 0.0]
    spawn = tuple(float(value) for value in spawn)
    diameter = float(level.get("ballDiameterStart") or 0.3)
    radius = max(0.01, diameter / 2.0)
    ground = float(level.get("groundY") or 0.0)
    eye_y = ground + max(0.35, EYE_HEIGHT_PER_RADIUS * radius)
    position = (spawn[0], eye_y, -spawn[2])

    target = None
    source = "level centre"
    cameras = doc.get("cameras") or []
    for camera in cameras:
        if not isinstance(camera, dict):
            continue
        camera_id = str(camera.get("id") or "")
        if camera_id in ("ball_eye", "start", "start_view") or "ball_eye" in camera_id:
            try:
                location = tuple(float(value) for value in camera["location"])
                camera_target = tuple(float(value) for value in camera["target"])
            except (KeyError, TypeError, ValueError):
                continue
            direction = (camera_target[0] - location[0], 0.0,
                         -(camera_target[2] - location[2]))
            if math.hypot(direction[0], direction[2]) > 1e-6:
                target = (position[0] + direction[0], eye_y - 0.12,
                          position[2] + direction[2])
                source = "camera %r" % camera_id
                break
    if target is None:
        bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
        center_x = (float(bounds[0][0]) + float(bounds[1][0])) / 2.0
        center_z = (float(bounds[0][1]) + float(bounds[1][1])) / 2.0
        direction = (center_x - spawn[0], 0.0, -(center_z - spawn[2]))
        if math.hypot(direction[0], direction[2]) < 1e-6:
            direction = (0.0, 0.0, -1.0)
        length = math.hypot(direction[0], direction[2])
        reach = max(4.0, min(25.0, length))
        target = (position[0] + direction[0] / length * reach,
                  eye_y - 0.12,
                  position[2] + direction[2] / length * reach)
    view = look_view(position, target, START_FOV)
    view.update({"eyeHeight": round(eye_y, 4), "ballRadius": round(radius, 4),
                 "directionSource": source, "spawn": [round(v, 4) for v in spawn]})
    return view


def focus_box(doc, selector):
    """The AABB of one itemId (exact) or every prop of a def."""
    selector = str(selector or "").strip()
    if not selector:
        raise ShotError("focus: needs an itemId or defId", "bad_argument")
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    matches = [prop for prop in props if prop.get("itemId") == selector]
    if not matches:
        matches = [prop for prop in props if prop.get("def") == selector]
    boxes = [prop_box(prop) for prop in matches]
    boxes = [box for box in boxes if box]
    if not boxes:
        available = sorted({str(prop.get("def")) for prop in props})[:12]
        raise ShotError("focus: no prop with itemId or def %r (defs include %s)"
                        % (selector, ", ".join(available)), "not_found", status=404)
    box = box_union(boxes)
    if len(matches) > 1:
        return box, {"kind": "def", "selector": selector, "count": len(matches),
                     "ids": [prop.get("itemId") for prop in matches[:20]]}
    return box, {"kind": "prop", "selector": selector, "count": 1,
                 "ids": [matches[0].get("itemId")]}


def auto_focus(doc):
    """The most useful thing to look at: the level's highest-value landmark.

    "Most relevant" has to be deterministic and explainable to an agent.  The original
    game is about absorbing value, so the first key is ``valueUnits``; ties (common in
    reference levels, where every original prop is 1) go to the physically largest
    prop, which is the landmark a human would call the thing in the room.  The item id
    is the final tie-break, so the answer never changes between runs.
    """
    level = doc.get("level") or {}
    spawn = level.get("ballStart") or [0.0, 0.0, 0.0]
    spawn = tuple(float(value) for value in spawn)
    best = None
    for prop in doc.get("props") or []:
        if not isinstance(prop, dict):
            continue
        box = prop_box(prop)
        if box is None:
            continue
        volume = ((box[3] - box[0]) * (box[4] - box[1]) * (box[5] - box[2]))
        units = float(prop.get("valueUnits") or 0.0)
        key = (units, volume, str(prop.get("itemId") or ""))
        if best is None or key > best[0]:
            best = (key, prop)
    if best is None:
        return None
    prop = best[1]
    try:
        position = tuple(float(value) for value in prop["position"])
        distance = math.dist(spawn, position)
    except (KeyError, TypeError, ValueError):
        distance = None
    return {"kind": "prop", "selector": prop.get("itemId"), "count": 1,
            "ids": [prop.get("itemId")], "def": prop.get("def"),
            "valueUnits": best[0][0], "volumeM3": round(best[0][1], 6),
            "distanceToSpawn": round(distance, 4) if distance is not None else None,
            "reason": "highest valueUnits; ties by largest AABB volume"}


def regions_path(level_id, levels_dir=None):
    return level_folder(level_id, levels_dir) / "regions.json"


def load_regions(level_id, levels_dir=None):
    path = regions_path(level_id, levels_dir)
    if not path.is_file():
        return []
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ShotError("cannot read %s: %s" % (core.display_path(path), error),
                        "bad_regions")
    return payload.get("regions") or []


def region_box(level_id, name, levels_dir=None):
    regions = load_regions(level_id, levels_dir)
    for region in regions:
        if region.get("name") != name:
            continue
        try:
            low = [float(value) for value in region["min"]]
            high = [float(value) for value in region["max"]]
        except (KeyError, TypeError, ValueError):
            raise ShotError("region %r has no usable min/max" % name, "bad_regions")
        return world_to_three_box(low, high)
    names = ", ".join(str(region.get("name")) for region in regions) or "(none saved)"
    raise ShotError("region %r is not in regions.json (regions: %s)" % (name, names),
                    "not_found", status=404)


def bookmark_view(doc, name, aspect):
    cameras = doc.get("cameras") or []
    for camera in cameras:
        if not isinstance(camera, dict) or camera.get("id") != name:
            continue
        kind = str(camera.get("type") or "perspective")
        try:
            position = tuple(float(value) for value in camera["location"])
        except (KeyError, TypeError, ValueError):
            raise ShotError("camera %r has no location" % name, "bad_bookmark")
        if kind == "ortho_top" and camera.get("coverage_m"):
            # An ortho plan camera has no useful target/lens; frame the same square in
            # perspective at 45 degrees so the bookmark still renders in this viewer.
            coverage = float(camera["coverage_m"])
            half = coverage / 2.0
            box = (position[0] - half, float(position[1]) - coverage,
                   position[2] - half, position[0] + half, float(position[1]),
                   position[2] + half)
            return fit_view(box, (0.0, 1.0, 0.0001), FIT_FOV, aspect)
        try:
            target = tuple(float(value) for value in camera["target"])
        except (KeyError, TypeError, ValueError):
            raise ShotError("camera %r has no target" % name, "bad_bookmark")
        fov = fov_from_lens(camera.get("lens")) if kind == "perspective" else FIT_FOV
        location = (position[0], position[1], -position[2])
        aim = (target[0], target[1], -target[2])
        view = look_view(location, aim, fov)
        view["bookmark"] = {"id": name, "type": kind, "lens": camera.get("lens")}
        return view
    names = ", ".join(str(camera.get("id")) for camera in cameras
                      if isinstance(camera, dict)) or "(none)"
    raise ShotError("bookmark %r is not in the level cameras (bookmarks: %s)"
                    % (name, names), "not_found", status=404)


def parse_raw_camera(text):
    """``x,y,z>tx,ty,tz[,fov]`` in Unity world metres."""
    body = str(text or "")
    if body.startswith("cam="):
        body = body[4:]
    if ">" not in body:
        raise ShotError("cam view must be x,y,z>tx,ty,tz[,fov]", "bad_argument")
    eye_text, target_text = body.split(">", 1)
    try:
        eye = [float(value) for value in eye_text.split(",")]
        target_values = [float(value) for value in target_text.split(",")]
    except ValueError:
        raise ShotError("cam view coordinates must be numbers", "bad_argument")
    if len(eye) != 3 or len(target_values) not in (3, 4):
        raise ShotError("cam view must be x,y,z>tx,ty,tz[,fov]", "bad_argument")
    fov = target_values[3] if len(target_values) == 4 else START_FOV
    if not (1.0 < float(fov) < 179.0):
        raise ShotError("cam view fov must be between 1 and 179", "bad_argument")
    return look_view((eye[0], eye[1], -eye[2]),
                     (target_values[0], target_values[1], -target_values[2]), fov)


def view_specs(params, doc, aspect):
    """Expand the requested view names into concrete camera plans.

    Returns a list of dicts, each with name/kind/position/target/up/fov and an optional
    ``mark`` (prop id to highlight) and ``box`` (three-space box that was framed).
    """
    requested = params.get("views") or ["sheet"]
    expanded = []
    for name in requested:
        if name == "sheet":
            expanded.extend(SHEET_ORDER)
        else:
            expanded.append(name)
    plans = []
    for index, name in enumerate(expanded):
        if name == "top":
            box = level_bounds_box(doc)
            plan = fit_view(box, (0.0, 1.0, 0.0001), FIT_FOV, aspect)
            plan.update({"name": "top", "kind": "top"})
        elif name == "iso":
            box = level_box(doc)
            plan = fit_view(box, (1.0, 0.82, 1.0), FIT_FOV, aspect)
            plan.update({"name": "iso", "kind": "iso"})
        elif name == "side":
            box = level_box(doc)
            plan = fit_view(box, (1.0, 0.02, 0.0001), FIT_FOV, aspect)
            plan.update({"name": "side", "kind": "side"})
        elif name == "start":
            plan = start_view(doc, aspect)
            plan.update({"name": "start", "kind": "start"})
        elif name.startswith("focus:"):
            selector = name[len("focus:"):]
            if selector.strip().lower() == "auto":
                focus = auto_focus(doc)
                if focus is None:
                    raise ShotError("focus:auto found no props in this level", "not_found")
                selector = focus["selector"]
                plan = fit_view(focus_box(doc, selector)[0], (1.0, 0.7, 1.0),
                                FIT_FOV, aspect, margin=1.38)
                plan.update({"name": "focus:%s" % selector, "kind": "focus",
                             "mark": none_or(selector), "focus": focus})
            else:
                box, focus = focus_box(doc, selector)
                plan = fit_view(box, (1.0, 0.7, 1.0), FIT_FOV, aspect, margin=1.38)
                plan.update({"name": "focus:%s" % selector, "kind": "focus",
                             "mark": focus["ids"][0], "focus": focus})
        elif name.startswith("region:"):
            region_name = name[len("region:"):]
            box = region_box(params["level"], region_name, params.get("levelsDir"))
            plan = fit_view(box, (1.0, 0.9, 1.0), FIT_FOV, aspect, margin=1.1)
            plan.update({"name": name, "kind": "region", "region": region_name})
        elif name.startswith("bookmark:"):
            bookmark_name = name[len("bookmark:"):]
            plan = bookmark_view(doc, bookmark_name, aspect)
            plan.update({"name": name, "kind": "bookmark", "bookmarkName": bookmark_name})
        elif name.startswith("cam=") or name.startswith("cam:"):
            text = name[4:] if name.startswith("cam=") else name[4:]
            plan = parse_raw_camera(name)
            plan.update({"name": "cam", "kind": "raw"})
        else:
            raise ShotError(
                "unknown view %r; use top, iso, side, start, focus:<itemId|defId>, "
                "focus:auto, region:<name>, bookmark:<name>, cam=x,y,z>tx,ty,tz[,fov]"
                % name, "bad_view")
        plans.append(plan)
    if not plans:
        raise ShotError("no views requested", "bad_view")
    return plans


def none_or(value):
    return value if value else None


def layout_for(count):
    if count <= 1:
        return {"cols": 1, "rows": 1}
    if count == 2:
        return {"cols": 2, "rows": 1}
    if count <= 4:
        return {"cols": 2, "rows": 2}
    if count <= 6:
        return {"cols": 3, "rows": 2}
    return {"cols": 3, "rows": int(math.ceil(count / 3.0))}


def world_view_rows(plans, actual_views=None):
    """The camera rows to print: Python's plan, overlaid with what the page used.

    The page is the final authority on the camera (a reference-level start view may be
    lifted from the placement's flat y onto the extracted terrain), but the plan owns the
    framed box and the focus metadata.
    """
    rows = []
    actual_views = actual_views or []
    for index, plan in enumerate(plans):
        row = {
            "name": plan["name"], "kind": plan["kind"],
            "fov": round(float(plan["fov"]), 3),
            "position": three_to_world(plan["position"]),
            "target": three_to_world(plan["target"]),
            "box": three_to_world_box(plan["box"]) if plan.get("box") else None,
            "mark": plan.get("mark"),
            "focus": plan.get("focus"),
        }
        actual = actual_views[index] if index < len(actual_views) else None
        if isinstance(actual, dict) and len(actual.get("position") or []) == 3:
            row["fov"] = round(float(actual.get("fov") or plan["fov"]), 3)
            row["position"] = three_to_world(actual["position"])
            row["target"] = three_to_world(actual["target"])
            row["renderMs"] = actual.get("renderMs")
            row["startSurfaceY"] = actual.get("startSurfaceY")
            row["startLiftedM"] = actual.get("startLiftedM")
        rows.append(row)
    return rows


def tile_size(width, height, count, max_px):
    layout = layout_for(count)
    final_w, final_h = layout["cols"] * width, layout["rows"] * height
    if final_w * final_h > max_px:
        scale = math.sqrt(max_px / float(final_w * final_h))
        width = max(TILE_MIN, int(math.floor(width * scale)))
        height = max(TILE_MIN, int(math.floor(height * scale)))
        final_w, final_h = layout["cols"] * width, layout["rows"] * height
    return width, height, final_w, final_h


# --------------------------------------------------------------------------- #
# argument parsing and the request payload
# --------------------------------------------------------------------------- #
def build_parser():
    parser = argparse.ArgumentParser(
        prog="shot.py",
        description="Render a level to PNG(s) with a headless browser; one JSON line out.")
    parser.add_argument("level", nargs="?", help="level id (story_01_bench_size, _ref_cancer, ...)")
    parser.add_argument("--view", action="append", default=None,
                        help="named view (repeatable): top, iso, side, start, "
                             "focus:<itemId|defId>, focus:auto, region:<name>, "
                             "bookmark:<name>, cam=x,y,z>tx,ty,tz[,fov]; default sheet")
    parser.add_argument("--views", default=None,
                        help="comma-separated views (same names as --view)")
    parser.add_argument("--frames", type=int, default=1,
                        help="capture N frames of the same camera (default 1)")
    parser.add_argument("--interval", type=float, default=0.5,
                        help="seconds between frames (default 0.5)")
    parser.add_argument("--no-diff", action="store_true",
                        help="do not make the diff heatmap for multi-frame shots")
    parser.add_argument("--shadows", action="store_true",
                        help="enable shadow maps (slower; off by default for shots)")
    parser.add_argument("--layers", default=None,
                        help="comma list: props, structures, terrain, collision, parents, "
                             "grid, wire, labels[:N], tier:<D>, ball:<D>, select:<ids>")
    parser.add_argument("--w", type=int, default=DEFAULT_W, help="tile width (default 1280)")
    parser.add_argument("--h", type=int, default=DEFAULT_H, help="tile height (default 720)")
    parser.add_argument("--max-px", type=int, default=DEFAULT_MAX_PX,
                        help="cap on the final image's pixels (default 4000000)")
    parser.add_argument("--out", default=None,
                        help="output directory (or .png file for a single image)")
    parser.add_argument("--save-bookmark", default=None, metavar="NAME",
                        help="store the single view's camera into the level cameras[]")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help="render daemon port (default 8793)")
    parser.add_argument("--state-dir", default=None,
                        help="state/token directory (default: XDG state dir)")
    parser.add_argument("--token", default=None, help="editor/render token override")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT,
                        help="render timeout in seconds (default 240)")
    parser.add_argument("--idle-timeout", type=float, default=DEFAULT_IDLE,
                        help="daemon idle exit in seconds (default 900)")
    parser.add_argument("--levels-dir", default=None, help="override Docs/Design/Levels")
    parser.add_argument("--chrome", default=None, help="Chromium/Chrome binary override")
    parser.add_argument("--status", action="store_true", help="print the daemon status")
    parser.add_argument("--stop", action="store_true", help="stop the render daemon")
    parser.add_argument("--quiet", action="store_true", help="no diagnostics on stderr")
    return parser


def parse_layers(text, level_start_diameter):
    layers = {name: False for name in BASE_LAYERS}
    layers.update({"terrain": False, "collision": False, "parents": False,
                   "grid": False, "wire": False, "labels": 0,
                   "tier": None, "ball": None, "select": [], "requested": []})
    raw_items = [item.strip() for item in str(text or "").split(",") if item.strip()]
    # `select:a,b` is one layer whose value is a comma list, so a bare token that does
    # not start a new layer belongs to the previous item.
    items = []
    for part in raw_items:
        head = part.partition(":")[0].strip().lower()
        if items and head not in LAYER_NAMES:
            items[-1] = items[-1] + "," + part
        else:
            items.append(part)
    if not items:
        items = list(BASE_LAYERS)
    for item in items:
        name, _, value = item.partition(":")
        name = name.strip().lower()
        value = value.strip()
        if name not in LAYER_NAMES:
            raise ShotError("unknown layer %r; allowed: %s"
                            % (name, ", ".join(LAYER_NAMES)), "bad_layer")
        layers["requested"].append(item)
        if name in BASE_LAYERS or name in ("terrain", "collision", "parents",
                                           "grid", "wire"):
            layers[name] = True
        elif name == "labels":
            count = 10 if not value else int(_finite(value, "labels"))
            if count < 0 or count > 200:
                raise ShotError("labels count must be 0..200", "bad_layer")
            layers["labels"] = count
        elif name == "tier":
            layers["tier"] = _finite(value or level_start_diameter, "tier diameter")
            if layers["tier"] <= 0:
                raise ShotError("tier diameter must be positive", "bad_layer")
        elif name == "ball":
            layers["ball"] = _finite(value or level_start_diameter, "ball diameter")
            if layers["ball"] <= 0:
                raise ShotError("ball diameter must be positive", "bad_layer")
        elif name == "select":
            layers["select"] = [entry.strip() for entry in value.split(",")
                                if entry.strip()]
    return layers


def normalize_request(args):
    """argparse namespace -> the JSON request the daemon and /api/render share."""
    if not args.level:
        raise ShotError("a level id is required (or use --status / --stop)", "bad_argument")
    if not core.is_valid_level_id(args.level):
        raise ShotError("invalid level id %r" % args.level, "bad_level")
    views = []
    for value in args.view or []:
        views.extend(entry.strip() for entry in str(value).split(",") if entry.strip())
    if args.views:
        views.extend(entry.strip() for entry in str(args.views).split(",") if entry.strip())
    if not views:
        views = ["sheet"]
    frames = int(args.frames)
    if not (1 <= frames <= MAX_FRAMES):
        raise ShotError("--frames must be 1..%d" % MAX_FRAMES, "bad_argument")
    view_count = len(SHEET_ORDER) if "sheet" in views else len(views)
    if frames > 1 and view_count != 1:
        raise ShotError("--frames captures one camera; pass exactly one --view",
                        "bad_argument")
    interval = float(args.interval)
    if not (0.0 <= interval <= 60.0):
        raise ShotError("--interval must be 0..60 seconds", "bad_argument")
    width, height = int(args.w), int(args.h)
    for label, value in (("--w", width), ("--h", height)):
        if not (TILE_MIN <= value <= TILE_MAX):
            raise ShotError("%s must be %d..%d" % (label, TILE_MIN, TILE_MAX),
                            "bad_argument")
    if args.max_px < 100_000 or args.max_px > 40_000_000:
        raise ShotError("--max-px must be 100000..40000000", "bad_argument")
    if args.save_bookmark:
        if len(views) != 1 or views[0] == "sheet":
            raise ShotError("--save-bookmark needs exactly one named view (not sheet)",
                            "bad_argument")
        if not all(character.isalnum() or character in "_.-" for character in
                   args.save_bookmark):
            raise ShotError("bookmark name must be letters/digits/_.-", "bad_argument")
    directory = state_dir(args.state_dir)
    level = args.level
    path, doc = load_level(level, args.levels_dir)
    meta = level_meta(level, path, doc)
    layers = parse_layers(args.layers, meta["ballDiameterStart"])
    for key in ("tier", "ball"):
        if layers[key] is not None and not (0.01 <= float(layers[key]) <= 100.0):
            raise ShotError("%s diameter must be 0.01..100 m" % key, "bad_layer")
    out = output_target(level, args.out, state_dir=directory)
    if out.get("outFile") and (len(views) != 1 or frames > 1):
        raise ShotError("--out may name a .png file only for a single view and frame",
                        "bad_argument")
    if is_ref_level(level):
        # The watermark is the page's job; the daemon passes this flag through.
        out["watermark"] = True
    request = {
        "level": level,
        "views": views,
        "frames": frames,
        "interval": interval,
        "diff": not args.no_diff and frames > 1,
        "shadows": bool(getattr(args, "shadows", False)),
        "layers": layers,
        "w": width,
        "h": height,
        "maxPx": int(args.max_px),
        "out": str(out["out"]),
        "outFile": out["outFile"],
        "watermark": bool(out.get("watermark")),
        "saveBookmark": args.save_bookmark,
        "levelsDir": str(Path(args.levels_dir).expanduser()) if args.levels_dir else None,
        "meta": meta,
        "timeout": float(args.timeout),
    }
    # Validate view names, focus targets, regions and bookmarks before a browser is
    # started; the daemon plans the same list again (same function, no drift).
    view_specs(request, doc, width / float(height))
    return request


def output_target(level, out, state_dir=None):
    """Where the PNGs go, and the reference-level guard.

    A reference level's images may only live under the reference evidence root; a
    normal level's default is the editor state directory.  The guard runs before a
    browser starts, so a mistake costs nothing.
    """
    directory = Path(state_dir).expanduser() if state_dir else DEFAULT_STATE_DIR
    if out:
        raw = Path(out).expanduser()
        out_file = raw.name if raw.suffix.lower() == ".png" else None
        directory = raw.parent if out_file else raw
    elif is_ref_level(level):
        directory = ref_shots_root() / str(level)
        out_file = None
    else:
        directory = directory / "shots" / str(level)
        out_file = None
    directory = directory.resolve()
    if is_ref_level(level):
        allowed = ref_shots_root().resolve()
        try:
            directory.relative_to(allowed)
        except ValueError:
            raise ShotError(
                "reference-level shots may only be written under %s; %s is refused"
                % (allowed, directory), "ref_output_forbidden")
    repo = ROOT.resolve()
    try:
        directory.relative_to(repo)
        inside_repo = True
    except ValueError:
        inside_repo = False
    if inside_repo:
        relative = directory.relative_to(repo)
        if any(part.startswith("_ref_") for part in relative.parts):
            raise ShotError("refusing to write inside a _ref_* directory: %s" % directory,
                            "ref_output_forbidden")
        if relative.parts[:1] == ("Docs",) and len(relative.parts) > 1 \
                and relative.parts[1] == "Source":
            raise ShotError("refusing to write into Docs/Source: %s" % directory,
                            "ref_output_forbidden")
    return {"out": directory, "outFile": out_file,
            "watermark": is_ref_level(level)}


# --------------------------------------------------------------------------- #
# daemon client
# --------------------------------------------------------------------------- #
def read_state(directory):
    path = state_file(directory)
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def http_json(url, token=None, payload=None, timeout=10.0):
    headers = {"Accept": "application/json"}
    data = None
    if token:
        headers["X-Editor-Token"] = token
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers,
                                     method="POST" if payload is not None else "GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read()
    except urllib.error.HTTPError as error:
        body = error.read()
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ShotError("daemon HTTP %d: %s" % (error.code, body[:300]),
                            "daemon_error", status=502)
        raise ShotError(payload.get("error") or "daemon HTTP %d" % error.code,
                        payload.get("code") or "daemon_error", payload.get("details"),
                        status=502)
    except (urllib.error.URLError, OSError) as error:
        raise ShotError("cannot reach the render daemon: %s" % error, "daemon_down",
                        status=503)
    try:
        return json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ShotError("the daemon returned non-JSON: %s" % body[:300], "daemon_error",
                        status=502)


def daemon_alive(state):
    if not state:
        return False
    port = state.get("port")
    if not isinstance(port, int):
        return False
    try:
        payload = http_json("http://127.0.0.1:%d/healthz" % port, timeout=1.5)
    except ShotError:
        return False
    return bool(payload.get("ok")) and payload.get("pid") == state.get("pid")


def wait_for_daemon(directory, timeout=25.0, process=None):
    deadline = time.time() + timeout
    while time.time() < deadline:
        state = read_state(directory)
        if state and daemon_alive(state):
            return state
        if process is not None and process.poll() is not None:
            break
        time.sleep(0.1)
    return None


def start_daemon(directory, token, port=DEFAULT_PORT, idle=DEFAULT_IDLE, quiet=False,
                 levels_dir=None, chrome=None):
    directory = Path(directory)
    log_dir = directory / "shot"
    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / "daemon.log"
    command = [sys.executable, str(Path(__file__).resolve().with_name("shot_daemon.py")),
               "--state-dir", str(directory), "--token", token, "--port", str(port),
               "--idle-timeout", str(idle)]
    if quiet:
        command.append("--quiet")
    if levels_dir:
        command += ["--levels-dir", str(levels_dir)]
    if chrome:
        command += ["--chrome", str(chrome)]
    with open(log_path, "ab") as handle:
        process = subprocess.Popen(command, stdout=handle, stderr=handle,
                                   stdin=subprocess.DEVNULL, start_new_session=True,
                                   close_fds=True)
    state = wait_for_daemon(directory, timeout=25.0, process=process)
    if state is not None:
        _STARTED_PROCESSES.append(process)
    if state is None:
        tail = ""
        try:
            tail = log_path.read_text(encoding="utf-8", errors="replace")[-1200:]
        except OSError:
            pass
        raise ShotError("the render daemon did not start (see %s): %s"
                        % (log_path, tail.strip() or "no log"), "daemon_start_failed",
                        status=503)
    return state


def ensure_daemon(directory, token, port=DEFAULT_PORT, idle=DEFAULT_IDLE, quiet=False,
                  levels_dir=None, chrome=None):
    state = read_state(directory)
    if daemon_alive(state):
        return state
    return start_daemon(directory, token, port=port, idle=idle, quiet=quiet,
                        levels_dir=levels_dir, chrome=chrome)


def stop_daemon(directory):
    state = read_state(directory)
    if not state:
        return {"ok": True, "stopped": False, "reason": "no daemon state file"}
    port = state.get("port")
    token = load_token(directory)
    result = {"ok": True, "stopped": False, "pid": state.get("pid")}
    if isinstance(port, int):
        try:
            answer = http_json("http://127.0.0.1:%d/stop" % port, token=token,
                               payload={}, timeout=5.0)
            result.update({"stopped": bool(answer.get("ok")), "answer": answer})
        except ShotError as error:
            result["reason"] = str(error)
    deadline = time.time() + 8.0
    while time.time() < deadline:
        if read_state(directory) is None:
            result["stopped"] = True
            break
        time.sleep(0.1)
    if read_state(directory) is not None:
        # A stale state file, or a daemon whose token changed (so /stop answered 403).
        # If the recorded pid is still alive, ask it to leave; never kill a reused pid.
        pid = state.get("pid") if isinstance(state, dict) else None
        if isinstance(pid, int) and not result.get("stopped"):
            try:
                os.kill(pid, 0)
                os.kill(pid, signal.SIGTERM)
                deadline = time.time() + 8.0
                while time.time() < deadline:
                    try:
                        os.kill(pid, 0)
                    except OSError:
                        break
                    time.sleep(0.1)
            except OSError:
                pass
        if read_state(directory) is not None:
            try:
                state_file(directory).unlink()
            except OSError:
                pass
    for process in list(_STARTED_PROCESSES):
        if state and process.pid == state.get("pid"):
            try:
                process.wait(timeout=8.0)
            except (OSError, subprocess.TimeoutExpired):
                pass
            try:
                _STARTED_PROCESSES.remove(process)
            except ValueError:
                pass
    return result


def render_request(params, directory=None, token=None, port=None, idle=None,
                   quiet=False):
    """Run one render through the daemon, starting it if needed."""
    directory = Path(directory) if directory else state_dir(params.get("stateDir"))
    token = token or load_token(directory, params.get("token"))
    state = ensure_daemon(directory, token, port=port or params.get("port") or DEFAULT_PORT,
                          idle=idle or params.get("idleTimeout") or DEFAULT_IDLE,
                          quiet=quiet, levels_dir=params.get("levelsDir"),
                          chrome=params.get("chrome"))
    payload = dict(params)
    for key in ("token", "stateDir", "port", "idleTimeout", "chrome", "meta"):
        payload.pop(key, None)
    answer = http_json("http://127.0.0.1:%d/render" % state["port"], token=token,
                       payload=payload, timeout=float(params.get("timeout") or DEFAULT_TIMEOUT))
    return answer


# --------------------------------------------------------------------------- #
# the editor server's GET /api/render (same implementation as the CLI)
# --------------------------------------------------------------------------- #
def request_from_query(query, level, levels_dir=None, state_dir=None):
    """Build a render request from URL query params (GET /api/render)."""
    def one(name, default=None):
        values = query.get(name)
        if not values:
            return default
        return values[-1]

    def many(name):
        values = query.get(name) or []
        return [entry.strip() for value in values for entry in str(value).split(",")
                if entry.strip()]

    args = argparse.Namespace(
        level=one("level") or level,
        view=many("view") or None,
        views=one("views"),
        frames=int(one("frames") or 1),
        interval=float(one("interval") or 0.5),
        no_diff=one("diff", "1") in ("0", "false", "no"),
        shadows=one("shadows", "0") in ("1", "true", "yes"),
        layers=one("layers"),
        w=int(one("w") or DEFAULT_W),
        h=int(one("h") or DEFAULT_H),
        max_px=int(one("max-px") or one("maxPx") or DEFAULT_MAX_PX),
        out=one("out"),
        save_bookmark=one("save-bookmark"),
        port=int(one("port") or DEFAULT_PORT),
        state_dir=one("state-dir") or state_dir,
        token=None,
        timeout=float(one("timeout") or DEFAULT_TIMEOUT),
        idle_timeout=float(one("idle-timeout") or DEFAULT_IDLE),
        levels_dir=one("levels-dir") or levels_dir,
        chrome=one("chrome"),
        status=False,
        stop=False,
        quiet=True,
    )
    params = normalize_request(args)
    params["stateDir"] = str(state_dir) if state_dir else None
    params["port"] = args.port
    params["idleTimeout"] = args.idle_timeout
    params["token"] = None
    params["chrome"] = args.chrome
    return params


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def _print(payload):
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), flush=True)


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    started = time.time()
    directory = state_dir(args.state_dir)
    try:
        if args.stop:
            _print(stop_daemon(directory))
            return 0
        if args.status:
            state = read_state(directory)
            payload = {"ok": True, "running": daemon_alive(state), "daemon": state,
                       "stateFile": str(state_file(directory))}
            _print(payload)
            return 0
        params = normalize_request(args)
        params["stateDir"] = str(directory)
        params["port"] = args.port
        params["idleTimeout"] = args.idle_timeout
        params["chrome"] = args.chrome
        answer = render_request(params, directory=directory,
                                token=load_token(directory, args.token),
                                port=args.port, idle=args.idle_timeout,
                                quiet=args.quiet)
        answer["cliSeconds"] = round(time.time() - started, 3)
        _print(answer)
        return 0 if answer.get("ok") else 1
    except ShotError as error:
        _print({"ok": False, "error": error.message, "code": error.code,
                "details": error.details, "cliSeconds": round(time.time() - started, 3)})
        if not args.quiet:
            print("shot: %s" % error.message, file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main())
