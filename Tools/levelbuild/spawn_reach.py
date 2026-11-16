"""Where a level's ball can roll, and what it can eat on the way: spawn choice and growth preview.

The reference stages (``Docs/Design/Levels/_ref_*``) carry no spawn point, so a spawn has to be
*chosen*, and a good choice depends on what the ball can actually reach at the start diameter.
A prop the ball cannot absorb is a wall to it, so reachability has to treat the level's
heavy furniture as obstacles, not just the terrain.  This module is that model.  It reads a
``katamari.placement/2`` document plus its ``.katerrain`` sidecar and answers three questions:

* :class:`Reach` -- for one ball diameter, which ground points (0.1 m grid, several layers per
  cell) the ball can stand on and which it can roll between;
* :func:`score_candidates` -- for every standing point, how many absorbable props the ball
  reaches within 3 m / 8 m of path, and how much ground it can reach at all;
* :func:`simulate_growth` -- a greedy eat-the-nearest run that regrows the ball after every
  absorb and recomputes what is absorbable, what is no longer in the way and how high it can
  climb.

Rules and where they come from (every number is a constant below):

``absorb gate``     a prop is absorbable by a ball of diameter ``D`` iff
                    ``absorbDiameterReqMm <= floor_mm(D)`` (``placement_core.floor_mm``, the data
                    twin of ``PickupAbsorbMath.IsBeyondAbsorbLimit`` / ``MmFloorEpsilon``).
``contact``         the ball touches a prop when the 3-D distance between the ball centre and the
                    prop centre is ``<= r + contactRadius + halfDiagonal(aabb)``
                    (``PickupAbsorbMath.TryComputeReach``; ``contactRadius`` is 0 in the
                    scenes).  ``aabb`` is the model's local size, so the half diagonal does not
                    depend on the rotation.
``growth``          ``V = V_start + sum(growthMm3)``, ``D = (6 V / pi) ** (1/3)``.
``ground``          a triangle is ground when its normal has ``y >= WALK_NORMAL_Y`` (the
                    "flat or gentle slope" rule); every other triangle is a wall or a ceiling.
                    A ball sphere rests on a plane with its centre ``r / n.y`` above it.
``step``            the ball climbs a vertical rise of up to ``CLIMB_DIAMETERS * D`` (1.35 D, the
                    working value under the 1.5 D design ceiling); anything taller is a wall.
                    Any drop is allowed (a one-way edge); the kill plane is not modelled
                    because no walkable cell lies below it.
``buried``          a ground layer with another ground layer less than one diameter above it in
                    the same cell is not standable (the upper surface would cut the ball);
``clearance``       the ball centre stays ``r - WALL_SLACK_M`` away from every obstacle: wall and
                    ceiling triangles taller than the step, solids' convex hulls, and the oriented
                    boxes of every prop the ball cannot absorb yet.  A prop whose top is within
                    the step height of the ground it stands on is a bump the ball rolls over, not
                    an obstacle.
``activity area``   the ball centre stays ``r + ACTIVITY_WALL_HALF_M`` inside the rectangle.

What it does not model (state these wherever a number is quoted): the ball's momentum and the
real contact solver, moving props (positions are the placed ones), occlusion between a ball
and a prop it is within reach of, props standing on top of other props, and the physical size
of absorbed props (a rolled-up ball is wider than its sphere).  Results are estimates.
"""

from __future__ import annotations

import argparse
import heapq
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import placement_v2  # noqa: E402

CELL_M = 0.1
START_DIAMETER_M = 0.222
WALK_NORMAL_Y = 0.707
CLIMB_DIAMETERS = 1.35
OLD_STEP_DIAMETERS = 0.25          # the retired TERRAIN_CLIMB rule, kept for the self-check
WALL_SLACK_M = 0.01
ACTIVITY_WALL_HALF_M = 0.1         # activityArea wall thickness 0.2 m, centred on the polygon
BUCKET_RATIO = 1.01                # growth preview: geometry is cached per 1 % of diameter
CRUISE_SPEED_MPS = 1.5
LAYER_MERGE_M = 0.01
GATE_SLACK_UNITS = 1e-9

_DIRS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
_INF = float("inf")


# --------------------------------------------------------------------------- #
# geometry
# --------------------------------------------------------------------------- #
def quat_matrix(q):
    """Row-major 3x3 rotation (local -> world) of a unit quaternion ``(x, y, z, w)``."""
    x, y, z, w = (float(v) for v in q)
    norm = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    return ((1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)))


def is_unit_quaternion(q, tolerance=1e-4):
    """True when ``q`` is the identity rotation (either sign)."""
    return (abs(abs(q[3]) - 1.0) <= tolerance and abs(q[0]) <= tolerance
            and abs(q[1]) <= tolerance and abs(q[2]) <= tolerance)


def tri_dist(p, a, b, c):
    """Distance from point ``p`` to triangle ``abc`` (closest-point-on-triangle)."""
    abx, aby, abz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    acx, acy, acz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    apx, apy, apz = p[0] - a[0], p[1] - a[1], p[2] - a[2]
    d1 = abx * apx + aby * apy + abz * apz
    d2 = acx * apx + acy * apy + acz * apz
    if d1 <= 0.0 and d2 <= 0.0:
        return math.sqrt(apx * apx + apy * apy + apz * apz)
    bpx, bpy, bpz = p[0] - b[0], p[1] - b[1], p[2] - b[2]
    d3 = abx * bpx + aby * bpy + abz * bpz
    d4 = acx * bpx + acy * bpy + acz * bpz
    if d3 >= 0.0 and d4 <= d3:
        return math.sqrt(bpx * bpx + bpy * bpy + bpz * bpz)
    vc = d1 * d4 - d3 * d2
    if vc <= 0.0 and d1 >= 0.0 and d3 <= 0.0:
        v = d1 / (d1 - d3)
        qx, qy, qz = apx - v * abx, apy - v * aby, apz - v * abz
        return math.sqrt(qx * qx + qy * qy + qz * qz)
    cpx, cpy, cpz = p[0] - c[0], p[1] - c[1], p[2] - c[2]
    d5 = abx * cpx + aby * cpy + abz * cpz
    d6 = acx * cpx + acy * cpy + acz * cpz
    if d6 >= 0.0 and d5 <= d6:
        return math.sqrt(cpx * cpx + cpy * cpy + cpz * cpz)
    vb = d5 * d2 - d1 * d6
    if vb <= 0.0 and d2 >= 0.0 and d6 <= 0.0:
        w = d2 / (d2 - d6)
        qx, qy, qz = apx - w * acx, apy - w * acy, apz - w * acz
        return math.sqrt(qx * qx + qy * qy + qz * qz)
    va = d3 * d6 - d5 * d4
    if va <= 0.0 and (d4 - d3) >= 0.0 and (d5 - d6) >= 0.0:
        w = (d4 - d3) / ((d4 - d3) + (d5 - d6))
        qx = bpx - w * (c[0] - b[0])
        qy = bpy - w * (c[1] - b[1])
        qz = bpz - w * (c[2] - b[2])
        return math.sqrt(qx * qx + qy * qy + qz * qz)
    denom = 1.0 / (va + vb + vc)
    v = vb * denom
    w = vc * denom
    qx = apx - abx * v - acx * w
    qy = apy - aby * v - acy * w
    qz = apz - abz * v - acz * w
    return math.sqrt(qx * qx + qy * qy + qz * qz)


def _cross(u, v):
    return (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])


def convex_hull_facets(points, tolerance=2e-4):
    """``(triangles, planes)`` of the convex hull of ``points`` (brute force, <= ~30 points).

    ``planes`` are ``(nx, ny, nz, d)`` with outward normals (inside: ``n . p <= d``); a face
    with more than three vertices is fan-triangulated, so ``triangles`` stays short.
    """
    pts = [tuple(float(v) for v in p) for p in points]
    count = len(pts)
    faces = {}
    for i in range(count):
        for j in range(i + 1, count):
            for k in range(j + 1, count):
                a, b, c = pts[i], pts[j], pts[k]
                n = _cross((b[0] - a[0], b[1] - a[1], b[2] - a[2]),
                           (c[0] - a[0], c[1] - a[1], c[2] - a[2]))
                length = math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
                if length < 1e-9:
                    continue
                n = (n[0] / length, n[1] / length, n[2] / length)
                d = n[0] * a[0] + n[1] * a[1] + n[2] * a[2]
                above = below = 0
                for q in pts:
                    s = n[0] * q[0] + n[1] * q[1] + n[2] * q[2] - d
                    if s > tolerance:
                        above += 1
                    elif s < -tolerance:
                        below += 1
                    if above and below:
                        break
                if above and below:
                    continue
                if above:
                    n, d = (-n[0], -n[1], -n[2]), -d
                key = (round(n[0], 2), round(n[1], 2), round(n[2], 2), round(d, 2))
                face = faces.setdefault(key, {"n": n, "d": d, "idx": set()})
                face["idx"].update((i, j, k))
    triangles = []
    planes = []
    for face in faces.values():
        n, d = face["n"], face["d"]
        members = [pts[i] for i in face["idx"]
                   if abs(n[0] * pts[i][0] + n[1] * pts[i][1] + n[2] * pts[i][2] - d) <= 2 * tolerance]
        if len(members) < 3:
            continue
        planes.append((n[0], n[1], n[2], d))
        centre = [sum(m[t] for m in members) / len(members) for t in range(3)]
        axis_u = (1.0, 0.0, 0.0) if abs(n[0]) < 0.9 else (0.0, 1.0, 0.0)
        u = _cross(n, axis_u)
        ul = math.sqrt(u[0] ** 2 + u[1] ** 2 + u[2] ** 2)
        u = (u[0] / ul, u[1] / ul, u[2] / ul)
        v = _cross(n, u)

        def angle(m, u=u, v=v, centre=centre):
            rel = (m[0] - centre[0], m[1] - centre[1], m[2] - centre[2])
            return math.atan2(rel[0] * v[0] + rel[1] * v[1] + rel[2] * v[2],
                              rel[0] * u[0] + rel[1] * u[1] + rel[2] * u[2])

        members.sort(key=angle)
        for t in range(1, len(members) - 1):
            triangles.append((members[0], members[t], members[t + 1]))
    return triangles, planes


class Solid:
    """A convex obstacle in world space: planes + a surface triangulation + bounds.

    Built either from an oriented box (``from_box``) or from a convex hull
    (``from_hull``).  ``dist`` is the distance from a point to the solid (0 inside).
    """

    __slots__ = ("name", "planes", "tris", "lo", "hi", "top")

    def __init__(self, name, planes, tris):
        self.name = name
        self.planes = planes
        self.tris = tris
        xs = [v[0] for t in tris for v in t]
        ys = [v[1] for t in tris for v in t]
        zs = [v[2] for t in tris for v in t]
        self.lo = (min(xs), min(ys), min(zs))
        self.hi = (max(xs), max(ys), max(zs))
        self.top = self.hi[1]

    @classmethod
    def from_hull(cls, name, centre, rotation, local_points):
        world = []
        for p in local_points:
            world.append(tuple(centre[r] + rotation[r][0] * p[0] + rotation[r][1] * p[1]
                               + rotation[r][2] * p[2] for r in range(3)))
        tris, planes = convex_hull_facets(world)
        return cls(name, planes, tris)

    @classmethod
    def from_box(cls, name, centre, rotation, size):
        hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0
        corners = [(sx * hx, sy * hy, sz * hz) for sx in (-1, 1) for sy in (-1, 1)
                   for sz in (-1, 1)]
        return cls.from_hull(name, centre, rotation, corners)

    def dist(self, p):
        inside = True
        for nx, ny, nz, d in self.planes:
            if nx * p[0] + ny * p[1] + nz * p[2] > d:
                inside = False
                break
        if inside:
            return 0.0
        best = _INF
        for a, b, c in self.tris:
            value = tri_dist(p, a, b, c)
            if value < best:
                best = value
        return best

    def first_hit_above(self, x, z, y0, y1):
        """Lowest ``y`` in ``[y0, y1]`` where the vertical line ``(x, z)`` is inside, else None."""
        lo, hi = y0, y1
        for nx, ny, nz, d in self.planes:
            base = nx * x + nz * z
            if abs(ny) < 1e-12:
                if base > d:
                    return None
                continue
            bound = (d - base) / ny
            if ny > 0:
                if bound < hi:
                    hi = bound
            elif bound > lo:
                lo = bound
            if lo > hi:
                return None
        return lo


# --------------------------------------------------------------------------- #
# the level
# --------------------------------------------------------------------------- #
class Prop:
    """One placed prop with the numbers the model needs."""

    __slots__ = ("id", "def_name", "pos", "half_diagonal", "req_mm", "growth_mm3",
                 "absorbable", "rotation", "quat", "aabb", "solid")

    def __init__(self, entry):
        self.id = entry["itemId"]
        self.def_name = entry.get("def")
        self.pos = tuple(float(v) for v in entry["position"])
        self.aabb = tuple(float(v) for v in entry["aabb"])
        self.half_diagonal = 0.5 * math.sqrt(sum(v * v for v in self.aabb))
        self.req_mm = int(entry["absorbDiameterReqMm"])
        self.growth_mm3 = int(entry["growthMm3"])
        self.absorbable = bool(entry["absorbable"])
        self.quat = tuple(float(v) for v in entry["rotationQuat"])
        self.rotation = quat_matrix(self.quat)
        self.solid = None

    def gate_open(self, diameter_m):
        """The absorb gate: whole-millimetre comparison, as the runtime makes it."""
        return self.absorbable and self.req_mm <= core.floor_mm(diameter_m + GATE_SLACK_UNITS)


class Level:
    """The static scene: terrain triangles, solids, props, activity rectangle."""

    def __init__(self, document, terrain, *, ball_diameter_m=START_DIAMETER_M):
        self.start_diameter_m = ball_diameter_m
        self.props = [Prop(entry) for entry in document["props"]]
        self.by_id = {prop.id: prop for prop in self.props}
        walls = document.get("activityArea", {}).get("walls") or []
        points = [pt for wall in walls for pt in wall["points"]]
        if points:
            self.rect = (min(p[0] for p in points), min(p[1] for p in points),
                         max(p[0] for p in points), max(p[1] for p in points))
        else:
            self.rect = (-1e9, -1e9, 1e9, 1e9)
        self._build_solids(document)
        self.prop_index = _GridIndex(0.5)
        for prop in self.props:
            if prop.solid is not None:
                self.prop_index.add(prop.solid, prop.solid.lo[0], prop.solid.lo[2],
                                    prop.solid.hi[0], prop.solid.hi[2])
        self._build_terrain(terrain)
        self.start_volume_mm3 = math.pi / 6.0 * (ball_diameter_m * 1000.0) ** 3

    # -- solids --------------------------------------------------------------
    def _build_solids(self, document):
        declared = {}
        for solid in (document.get("structures") or {}).get("solids") or []:
            collider = solid.get("collider") or {}
            rotation = quat_matrix(solid.get("rotationQuat") or (0, 0, 0, 1))
            centre = solid["center"]
            if collider.get("type") == "convex":
                flat = collider["hull"]
                if flat and not isinstance(flat[0], (list, tuple)):
                    flat = [flat[i:i + 3] for i in range(0, len(flat), 3)]
                offset = collider.get("center") or (0, 0, 0)
                local = [(p[0] + offset[0], p[1] + offset[1], p[2] + offset[2]) for p in flat]
                declared[solid["id"]] = Solid.from_hull(solid["id"], centre, rotation, local)
            else:
                size = collider.get("size") or solid["size"]
                offset = collider.get("center") or (0, 0, 0)
                world_centre = [centre[r] + rotation[r][0] * offset[0]
                                + rotation[r][1] * offset[1] + rotation[r][2] * offset[2]
                                for r in range(3)]
                declared[solid["id"]] = Solid.from_box(solid["id"], world_centre, rotation, size)
        self.declared_solids = declared
        # every prop's own obstacle volume: the declared solid when it has one, else its
        # oriented box (local aabb x rotationQuat, centred on ``position``)
        for prop in self.props:
            if prop.id in declared:
                prop.solid = declared[prop.id]
            elif min(prop.aabb) > 1e-6:
                prop.solid = Solid.from_box(prop.id, prop.pos, prop.rotation, prop.aabb)

    # -- terrain -------------------------------------------------------------
    def _build_terrain(self, terrain):
        vertices = terrain["vertices"]
        indices = terrain["indices"]
        self.walk = []      # walkable triangles (a, b, c, ny)
        self.steep = []     # wall / ceiling triangles (a, b, c, minY, maxY, is_ceiling)
        self.all_tris = []
        for k in range(0, len(indices), 3):
            a, b, c = (vertices[indices[k]], vertices[indices[k + 1]], vertices[indices[k + 2]])
            n = _cross((b[0] - a[0], b[1] - a[1], b[2] - a[2]),
                       (c[0] - a[0], c[1] - a[1], c[2] - a[2]))
            length = math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
            if length < 1e-12:
                continue
            ny = n[1] / length
            self.all_tris.append((a, b, c))
            if ny >= WALK_NORMAL_Y:
                self.walk.append((a, b, c, ny))
            else:
                self.steep.append((a, b, c, min(a[1], b[1], c[1]), max(a[1], b[1], c[1]),
                                   ny <= -WALK_NORMAL_Y))
        self.steep_index = _GridIndex(0.5)
        for tri in self.steep:
            self.steep_index.add(tri, min(tri[0][0], tri[1][0], tri[2][0]),
                                 min(tri[0][2], tri[1][2], tri[2][2]),
                                 max(tri[0][0], tri[1][0], tri[2][0]),
                                 max(tri[0][2], tri[1][2], tri[2][2]))
        self.all_index = _GridIndex(0.5)
        for tri in self.all_tris:
            self.all_index.add(tri, min(tri[0][0], tri[1][0], tri[2][0]),
                               min(tri[0][2], tri[1][2], tri[2][2]),
                               max(tri[0][0], tri[1][0], tri[2][0]),
                               max(tri[0][2], tri[1][2], tri[2][2]))
        self.layers = {}
        x0, z0, x1, z1 = self.rect
        for a, b, c, ny in self.walk:
            lo_x, hi_x = min(a[0], b[0], c[0]), max(a[0], b[0], c[0])
            lo_z, hi_z = min(a[2], b[2], c[2]), max(a[2], b[2], c[2])
            if hi_x < x0 or lo_x > x1 or hi_z < z0 or lo_z > z1:
                continue
            det = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])
            if abs(det) < 1e-12:
                continue
            for i in range(math.ceil(lo_x / CELL_M - 1e-9), math.floor(hi_x / CELL_M + 1e-9) + 1):
                x = i * CELL_M
                for j in range(math.ceil(lo_z / CELL_M - 1e-9),
                               math.floor(hi_z / CELL_M + 1e-9) + 1):
                    z = j * CELL_M
                    l1 = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / det
                    l2 = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / det
                    l3 = 1.0 - l1 - l2
                    if l1 < -1e-7 or l2 < -1e-7 or l3 < -1e-7:
                        continue
                    height = l1 * a[1] + l2 * b[1] + l3 * c[1]
                    bucket = self.layers.setdefault((i, j), [])
                    for slot, (existing, _ny) in enumerate(bucket):
                        if abs(existing - height) <= LAYER_MERGE_M:
                            if ny > bucket[slot][1]:
                                bucket[slot] = (height, ny)
                            break
                    else:
                        bucket.append((height, ny))
        for bucket in self.layers.values():
            bucket.sort()

    def terrain_heights_at(self, x, z):
        """Every walkable surface height under ``(x, z)``, ascending."""
        found = []
        for a, b, c, _ny in self.walk:
            if (x < min(a[0], b[0], c[0]) or x > max(a[0], b[0], c[0])
                    or z < min(a[2], b[2], c[2]) or z > max(a[2], b[2], c[2])):
                continue
            det = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])
            if abs(det) < 1e-12:
                continue
            l1 = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / det
            l2 = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / det
            l3 = 1.0 - l1 - l2
            if l1 < -1e-7 or l2 < -1e-7 or l3 < -1e-7:
                continue
            found.append(l1 * a[1] + l2 * b[1] + l3 * c[1])
        return sorted(found)

    def terrain_height_at(self, x, z, ceiling=_INF):
        """Highest walkable surface at or below ``ceiling`` (None when there is none)."""
        below = [h for h in self.terrain_heights_at(x, z) if h <= ceiling]
        return below[-1] if below else None

    def terrain_height_near(self, x, z, reference, tolerance):
        """The walkable surface closest to ``reference`` within ``tolerance`` (None when none)."""
        close = [h for h in self.terrain_heights_at(x, z) if abs(h - reference) <= tolerance]
        return min(close, key=lambda h: abs(h - reference)) if close else None

    def nearest_prop_aabb_distance(self, p, within=1.0):
        """Distance from ``p`` to the nearest prop's world AABB (the validator's clearance test).

        The world AABB is the box of the rotated local ``aabb`` around ``position`` -- what
        ``placement_core.aabb_distance_to_point`` measures for ``meta.spawnClearanceM``.
        """
        best = within
        for prop in self.props:
            if abs(prop.pos[0] - p[0]) > best + 3.0 or abs(prop.pos[2] - p[2]) > best + 3.0:
                continue
            extent = [sum(abs(prop.rotation[r][c]) * prop.aabb[c] / 2.0 for c in range(3))
                      for r in range(3)]
            gap = [max(abs(p[r] - prop.pos[r]) - extent[r], 0.0) for r in range(3)]
            best = min(best, math.sqrt(gap[0] ** 2 + gap[1] ** 2 + gap[2] ** 2))
        return best

    def nearest_prop_distance(self, p, within=1.0):
        """Distance from ``p`` to the nearest prop volume (``within`` when none is closer)."""
        best = within
        for solid in self.prop_index.near(p[0], p[2], within):
            if (p[0] < solid.lo[0] - best or p[0] > solid.hi[0] + best
                    or p[1] < solid.lo[1] - best or p[1] > solid.hi[1] + best
                    or p[2] < solid.lo[2] - best or p[2] > solid.hi[2] + best):
                continue
            best = min(best, solid.dist(p))
        return best

    def volume_after(self, absorbed_ids):
        return self.start_volume_mm3 + sum(self.by_id[i].growth_mm3 for i in absorbed_ids)


def diameter_from_volume_mm3(volume):
    """``D`` in metres of the sphere that holds ``volume`` mm^3."""
    return (6.0 * volume / math.pi) ** (1.0 / 3.0) / 1000.0


class _GridIndex:
    """A uniform XZ hash of objects with a bounding rectangle."""

    def __init__(self, cell):
        self.cell = cell
        self.cells = {}

    def add(self, obj, x0, z0, x1, z1):
        c = self.cell
        for gx in range(math.floor(x0 / c), math.floor(x1 / c) + 1):
            for gz in range(math.floor(z0 / c), math.floor(z1 / c) + 1):
                self.cells.setdefault((gx, gz), []).append(obj)

    def near(self, x, z, radius):
        c = self.cell
        seen = []
        marks = set()
        for gx in range(math.floor((x - radius) / c), math.floor((x + radius) / c) + 1):
            for gz in range(math.floor((z - radius) / c), math.floor((z + radius) / c) + 1):
                for obj in self.cells.get((gx, gz), ()):
                    key = id(obj)
                    if key not in marks:
                        marks.add(key)
                        seen.append(obj)
        return seen


# --------------------------------------------------------------------------- #
# reachability for one ball
# --------------------------------------------------------------------------- #
class Reach:
    """Standing points and rolling edges of one ball diameter on one level.

    ``blockers`` picks which props are walls: ``"props"`` = every prop whose gate is closed at
    this diameter (the rule), ``"solids"`` = only ``structures.solids``, ``"none"``.
    ``max_drop`` (limit on the height lost in one 0.1 m edge, None = any) and
    ``step_diameters`` exist so the retired rule can be replayed for the self-check.
    """

    def __init__(self, level, diameter_m, *, blockers="props", step_diameters=CLIMB_DIAMETERS,
                 max_drop=None, wall_slack=WALL_SLACK_M, rect_margin=None):
        self.level = level
        self.diameter = diameter_m
        self.radius = diameter_m / 2.0
        self.climb = step_diameters * diameter_m
        self.wall_clear = self.radius - wall_slack
        self.max_drop = max_drop
        margin = self.radius + ACTIVITY_WALL_HALF_M if rect_margin is None else rect_margin
        x0, z0, x1, z1 = level.rect
        self.rect = (x0 + margin, z0 + margin, x1 - margin, z1 - margin)
        self.obstacles = []
        self.low_props = 0
        if blockers not in ("none", "solids", "props"):
            raise ValueError("blockers must be none, solids or props, got %r" % (blockers,))
        for prop in level.props:
            if prop.solid is None or blockers == "none" or prop.gate_open(diameter_m):
                continue
            if blockers == "props" or prop.id in level.declared_solids:
                self.obstacles.append(prop.solid)
        self.index = _GridIndex(0.5)
        for solid in self.obstacles:
            self.index.add(solid, solid.lo[0], solid.lo[2], solid.hi[0], solid.hi[2])
        self.jump_cells = int(math.ceil((self.wall_clear + 0.05) / CELL_M)) + 1
        self._valid = {}
        self._edges = {}
        self._components = None

    # -- nodes ---------------------------------------------------------------
    def centre(self, node):
        i, j, k = node
        height, ny = self.level.layers[(i, j)][k]
        return (i * CELL_M, height + self.radius / ny, j * CELL_M)

    def height(self, node):
        return self.level.layers[(node[0], node[1])][node[2]][0]

    def valid(self, node):
        cached = self._valid.get(node)
        if cached is not None:
            return cached
        result = self._compute_valid(node)
        self._valid[node] = result
        return result

    def _compute_valid(self, node):
        x, y, z = self.centre(node)
        x0, z0, x1, z1 = self.rect
        if x < x0 or x > x1 or z < z0 or z > z1:
            return False
        h = self.height(node)
        # another ground layer inside the ball's own height: the ball would sit inside it
        # (overlapping floor patches, or a floor slab right above this one)
        for other, _ny in self.level.layers[(node[0], node[1])]:
            if h + 1e-9 < other < h + self.diameter - WALL_SLACK_M:
                return False
        return self._free((x, y, z), h + self.climb)

    def _free(self, p, limit):
        """True when a ball centred at ``p`` touches no obstacle taller than ``limit``."""
        x, y, z = p
        wc = self.wall_clear
        for a, b, c, lo, hi, ceiling in self.level.steep_index.near(x, z, wc):
            if (hi <= limit and not ceiling) or lo > y + wc or hi < y - wc:
                continue
            if tri_dist(p, a, b, c) < wc:
                return False
        for solid in self.index.near(x, z, wc):
            if solid.top <= limit:
                continue
            if (x < solid.lo[0] - wc or x > solid.hi[0] + wc or z < solid.lo[2] - wc
                    or z > solid.hi[2] + wc or y < solid.lo[1] - wc or y > solid.hi[1] + wc):
                continue
            if solid.dist(p) < wc:
                return False
        return True

    def cell_nodes(self, i, j):
        layers = self.level.layers.get((i, j))
        if not layers:
            return []
        return [(i, j, k) for k in range(len(layers))]

    def all_nodes(self):
        out = []
        for (i, j), layers in self.level.layers.items():
            for k in range(len(layers)):
                node = (i, j, k)
                if self.valid(node):
                    out.append(node)
        return out

    # -- edges ---------------------------------------------------------------
    def edges(self, node):
        """``[(neighbour, cost_m)]``: where the ball can roll (or fall) from ``node``.

        In each of the eight directions the neighbour cell offers its ground layers at most one
        step above this node, highest first.  The first one the ball fits on, and can reach
        without passing through a slab (the segment between the two ball centres must be
        free), is the edge: a rise, a level move, or a drop.  A drop of more than a step with
        no standable ground right at the foot is a ledge: the ball goes over the edge and
        lands on the first standable cell further out (up to ``jump_cells`` cells, which clears
        the clearance band the cliff foot is blocked by); it never lands on ground at its own
        level, so a thin wall or a gap cannot be crossed that way.
        """
        cached = self._edges.get(node)
        if cached is not None:
            return cached
        i, j, _k = node
        h = self.height(node)
        source = self.centre(node)
        limit = h + self.climb
        out = []
        for di, dj in _DIRS:
            step = math.hypot(di, dj) * CELL_M
            layers = self.level.layers.get((i + di, j + dj))
            if not layers:
                continue
            candidates = [k for k, (height, _ny) in enumerate(layers) if height <= limit + 1e-6]
            candidates.sort(key=lambda k: -layers[k][0])
            chosen = None
            highest_drop = None
            for k in candidates:
                target = (i + di, j + dj, k)
                th = layers[k][0]
                if th < h - self.climb:
                    if highest_drop is None:
                        highest_drop = target
                    break
                if not self.valid(target):
                    continue
                if th > h + 0.02:
                    centre = self.centre(target)
                    middle = ((source[0] + centre[0]) / 2.0, (source[1] + centre[1]) / 2.0,
                              (source[2] + centre[2]) / 2.0)
                    if not self._free(middle, limit):
                        continue
                chosen = target
                break
            if chosen is not None:
                th = self.height(chosen)
                if self.max_drop is None or h - th <= self.max_drop:
                    out.append((chosen, math.hypot(step, th - h)))
                continue
            if highest_drop is None:
                continue
            th = self.height(highest_drop)
            if self.valid(highest_drop):
                if self.max_drop is None or h - th <= self.max_drop:
                    out.append((highest_drop, math.hypot(step, th - h)))
                continue
            for m in range(2, self.jump_cells + 1):
                landing = self._pick(i + m * di, j + m * dj, h)
                if landing is None:
                    break
                lh = self.height(landing)
                if lh >= h - self.climb:
                    break
                if not self._free((source[0] + m * di * CELL_M, source[1],
                                   source[2] + m * dj * CELL_M), limit):
                    break
                if self.valid(landing):
                    if self.max_drop is None or h - lh <= self.max_drop:
                        out.append((landing, math.hypot(m * step, lh - h)))
                    break
        self._edges[node] = out
        return out

    def _pick(self, i, j, h):
        layers = self.level.layers.get((i, j))
        if not layers:
            return None
        limit = h + self.climb + 1e-6
        best = None
        for k, (height, _ny) in enumerate(layers):
            if height <= limit:
                best = k
        return None if best is None else (i, j, best)

    # -- searches ------------------------------------------------------------
    def snap(self, x, z, y=None):
        """The node under ``(x, z)`` (the highest layer at or below ``y`` when given)."""
        i, j = round(x / CELL_M), round(z / CELL_M)
        layers = self.level.layers.get((i, j))
        if not layers:
            return None
        best = None
        for k, (height, _ny) in enumerate(layers):
            if y is None or height <= y + 1e-6:
                best = k
        return None if best is None else (i, j, best)

    def dijkstra(self, sources, limit=_INF, reverse_graph=None):
        """Shortest path cost from ``sources`` (node -> start cost) to every node within ``limit``."""
        dist = dict(sources)
        heap = [(cost, node) for node, cost in dist.items()]
        heapq.heapify(heap)
        done = set()
        while heap:
            cost, node = heapq.heappop(heap)
            if node in done:
                continue
            done.add(node)
            if cost > limit:
                break
            neighbours = (reverse_graph.get(node, ()) if reverse_graph is not None
                          else self.edges(node))
            for other, weight in neighbours:
                total = cost + weight
                if total < dist.get(other, _INF) and total <= limit:
                    dist[other] = total
                    heapq.heappush(heap, (total, other))
        return dist

    def reachable_area_m2(self, start, limit=_INF):
        """Ground area (m^2) reachable from ``start`` (one cell = ``CELL_M`` squared)."""
        if start is None or not self.valid(start):
            return 0.0
        return len(self.dijkstra({start: 0.0}, limit)) * CELL_M * CELL_M

    def components(self):
        """Strongly connected components of the standable graph: ``{node: component id}``.

        A one-way drop (a ledge higher than a step) joins two components in one direction
        only; the ball that falls into the lower one cannot come back.
        """
        if getattr(self, "_components", None) is not None:
            return self._components
        nodes = self.all_nodes()
        in_set = set(nodes)
        index = {}
        low = {}
        on_stack = set()
        stack = []
        comp = {}
        counter = 0
        number = 0
        for root in nodes:
            if root in index:
                continue
            work = [(root, iter(self.edges(root)))]
            index[root] = low[root] = counter
            counter += 1
            stack.append(root)
            on_stack.add(root)
            while work:
                node, children = work[-1]
                advanced = False
                for other, _w in children:
                    if other not in in_set:
                        continue
                    if other not in index:
                        index[other] = low[other] = counter
                        counter += 1
                        stack.append(other)
                        on_stack.add(other)
                        work.append((other, iter(self.edges(other))))
                        advanced = True
                        break
                    if other in on_stack:
                        low[node] = min(low[node], index[other])
                if advanced:
                    continue
                work.pop()
                if work:
                    parent = work[-1][0]
                    low[parent] = min(low[parent], low[node])
                if low[node] == index[node]:
                    while True:
                        member = stack.pop()
                        on_stack.discard(member)
                        comp[member] = number
                        if member == node:
                            break
                    number += 1
        self._components = comp
        return comp

    def contact_nodes(self, prop):
        """Standable nodes whose ball centre touches ``prop`` (``TryComputeReach``)."""
        reach = self.radius + prop.half_diagonal
        span = int(math.ceil(reach / CELL_M)) + 1
        ci, cj = round(prop.pos[0] / CELL_M), round(prop.pos[2] / CELL_M)
        out = []
        limit2 = reach * reach
        for i in range(ci - span, ci + span + 1):
            for j in range(cj - span, cj + span + 1):
                layers = self.level.layers.get((i, j))
                if not layers:
                    continue
                for k, (height, ny) in enumerate(layers):
                    dx = i * CELL_M - prop.pos[0]
                    dy = height + self.radius / ny - prop.pos[1]
                    dz = j * CELL_M - prop.pos[2]
                    if dx * dx + dy * dy + dz * dz <= limit2:
                        node = (i, j, k)
                        if self.valid(node):
                            out.append(node)
        return out

    def touching(self, node, props):
        """The props (of an iterable) whose reach contains the ball at ``node``."""
        x, y, z = self.centre(node)
        found = []
        for prop in props:
            reach = self.radius + prop.half_diagonal
            dx, dy, dz = x - prop.pos[0], y - prop.pos[1], z - prop.pos[2]
            if dx * dx + dy * dy + dz * dz <= reach * reach:
                found.append(prop)
        return found

    def nearest_obstacle(self, node):
        """Distance from the ball centre to the nearest obstacle (taller than a step)."""
        x, y, z = self.centre(node)
        p = (x, y, z)
        limit = self.height(node) + self.climb
        best = _INF
        for a, b, c, _lo, hi, ceiling in self.level.steep_index.near(x, z, 3.0):
            if hi > limit or ceiling:
                best = min(best, tri_dist(p, a, b, c))
        for solid in self.index.near(x, z, 3.0):
            if solid.top > limit:
                best = min(best, solid.dist(p))
        return best

    def headroom(self, node, need=1.5, spread=0.2):
        """Free height above the ball top at ``node`` (capped at ``need``).

        Five vertical probes (centre and four ``spread`` m away) hit terrain triangles of any
        orientation and the obstacle solids; the answer is the lowest hit.
        """
        x, y, z = self.centre(node)
        base = y + self.radius
        lowest = need
        for ox, oz in ((0.0, 0.0), (spread, 0.0), (-spread, 0.0), (0.0, spread), (0.0, -spread)):
            px, pz = x + ox, z + oz
            for a, b, c in self.level.all_index.near(px, pz, 0.0):
                det = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])
                if abs(det) < 1e-12:
                    continue
                l1 = ((b[2] - c[2]) * (px - c[0]) + (c[0] - b[0]) * (pz - c[2])) / det
                l2 = ((c[2] - a[2]) * (px - c[0]) + (a[0] - c[0]) * (pz - c[2])) / det
                l3 = 1.0 - l1 - l2
                if l1 < 0 or l2 < 0 or l3 < 0:
                    continue
                gap = l1 * a[1] + l2 * b[1] + l3 * c[1] - base
                if 0.0 <= gap < lowest:
                    lowest = gap
            for solid in self.index.near(px, pz, 0.0):
                hit = solid.first_hit_above(px, pz, base, base + lowest)
                if hit is not None and hit - base < lowest:
                    lowest = max(0.0, hit - base)
        return lowest


# --------------------------------------------------------------------------- #
# candidates
# --------------------------------------------------------------------------- #
def flat_ground(reach, node, radius_m=0.3, max_range_m=0.05):
    """True when the 0.6 m disc around ``node`` is one flat layer (range <= 0.05 m)."""
    i, j, _k = node
    h = reach.height(node)
    span = int(round(radius_m / CELL_M))
    for di in range(-span, span + 1):
        for dj in range(-span, span + 1):
            if di * di + dj * dj > span * span:
                continue
            layers = reach.level.layers.get((i + di, j + dj))
            if not layers:
                return False
            near = [height for height, _ny in layers if abs(height - h) <= max_range_m]
            if not near:
                return False
    return True


def main_component(reach, targets=None):
    """``(nodes, touchable_prop_ids)`` of the strongly connected region that touches most props.

    Standing there the ball can always come back, so a spawn outside it risks a one-way trap
    (``Reach.components``); the regions that touch no absorbable prop are plateaus and pits.
    """
    comp = reach.components()
    if targets is None:
        targets = [p for p in reach.level.props if p.gate_open(reach.diameter)]
    touch = {}
    for prop in targets:
        for node in reach.contact_nodes(prop):
            touch.setdefault(comp[node], set()).add(prop.id)
    if not touch:
        return set(), set()
    best = max(touch, key=lambda c: len(touch[c]))
    return {n for n, c in comp.items() if c == best}, touch[best]


def score_candidates(reach, *, allowed=None, near_m=3.0, far_m=8.0, min_ny=0.94, boundary_m=2.0,
                     headroom_m=1.5, spawn_clearance_m=0.5, keep=1500, progress=None):
    """One record per eligible standing point, with the counts the spawn choice ranks on.

    Eligible: standable, on ground with ``n.y >= min_ny``, flat disc, ``>= boundary_m`` from the
    activity rectangle, inside ``allowed`` (a node set, e.g. :func:`main_component`), no prop AABB
    within ``spawn_clearance_m`` of the ball centre
    (``meta.spawnClearanceM``, the validator's test), ``>= headroom_m`` free above the ball top.  Returns
    ``(records, graph_size)``; each record has ``node, x, y, z, ground, near, far, nearest_m``.
    """
    level = reach.level
    nodes = reach.all_nodes()
    node_set = set(nodes)
    reverse = {}
    for node in nodes:
        for other, weight in reach.edges(node):
            if other in node_set:
                reverse.setdefault(other, []).append((node, weight))
    targets = [p for p in level.props if p.gate_open(reach.diameter)]
    near_count = {}
    far_count = {}
    nearest = {}
    for count, prop in enumerate(targets):
        sources = {node: 0.0 for node in reach.contact_nodes(prop)}
        if not sources:
            continue
        dist = reach.dijkstra(sources, limit=far_m, reverse_graph=reverse)
        for node, cost in dist.items():
            far_count[node] = far_count.get(node, 0) + 1
            if cost <= near_m:
                near_count[node] = near_count.get(node, 0) + 1
            if cost < nearest.get(node, _INF):
                nearest[node] = cost
        if progress:
            progress(count + 1, len(targets))
    x0, z0, x1, z1 = level.rect
    records = []
    for node in nodes:
        if allowed is not None and node not in allowed:
            continue
        i, j, k = node
        _x, y, _z = reach.centre(node)
        ground, ny = level.layers[(i, j)][k]
        x, z = i * CELL_M, j * CELL_M
        if ny < min_ny:
            continue
        if x < x0 + boundary_m or x > x1 - boundary_m or z < z0 + boundary_m \
                or z > z1 - boundary_m:
            continue
        if not flat_ground(reach, node):
            continue
        records.append({"node": node, "x": x, "y": y, "z": z, "ground": ground,
                        "near": near_count.get(node, 0), "far": far_count.get(node, 0),
                        "nearest_m": nearest.get(node)})
    # headroom last: it is the expensive probe, so it only runs for points that matter
    records.sort(key=lambda r: (-r["near"], -r["far"], r["nearest_m"] or _INF))
    kept = []
    for record in records:
        if level.nearest_prop_aabb_distance((record["x"], record["y"], record["z"]),
                                            spawn_clearance_m) < spawn_clearance_m - 1e-9:
            continue
        record["headroom_m"] = reach.headroom(record["node"], need=headroom_m)
        if record["headroom_m"] >= headroom_m - 1e-9:
            kept.append(record)
        if len(kept) >= keep:
            break
    return kept, len(nodes)


def distinct_top(records, count=5, separation_m=1.0):
    """The best ``count`` records, no two closer than ``separation_m`` on the ground plane."""
    picked = []
    for record in records:
        if all(math.hypot(record["x"] - p["x"], record["z"] - p["z"]) >= separation_m
               for p in picked):
            picked.append(record)
        if len(picked) >= count:
            break
    return picked


def describe_point(reach, start_node, near_m=3.0, far_m=8.0):
    """The report row for one standing point (forward search from it)."""
    level = reach.level
    dist = {}
    parent = {}
    heap = [(0.0, start_node)]
    dist[start_node] = 0.0
    done = set()
    while heap:
        cost, node = heapq.heappop(heap)
        if node in done:
            continue
        done.add(node)
        for other, weight in reach.edges(node):
            total = cost + weight
            if total < dist.get(other, _INF):
                dist[other] = total
                parent[other] = node
                heapq.heappush(heap, (total, other))
    targets = [p for p in level.props if p.gate_open(reach.diameter)]
    best = {}
    for prop in targets:
        low = _INF
        low_node = None
        for node in reach.contact_nodes(prop):
            if node in dist and dist[node] < low:
                low, low_node = dist[node], node
        if low_node is not None:
            best[prop.id] = (low, low_node)
    within_near = [pid for pid, (cost, _n) in best.items() if cost <= near_m]
    within_far = [pid for pid, (cost, _n) in best.items() if cost <= far_m]
    nearest = min((cost for cost, _n in best.values()), default=None)

    def max_rise(target):
        rise = 0.0
        node = target
        while node in parent:
            previous = parent[node]
            rise = max(rise, reach.height(node) - reach.height(previous))
            node = previous
        return rise

    highest = max((max_rise(best[pid][1]) for pid in within_near), default=0.0)
    return {"area_m2": len(dist) * CELL_M * CELL_M, "near_ids": within_near,
            "far_ids": within_far, "nearest_m": nearest, "highest_step_m": highest,
            "dist": dist, "parent": parent, "best": best}


MAX_STEP_RATIO = 2.0 / 3.0   # a spawn route may need at most this share of the climb limit
MIN_OBSTACLE_M = 0.5          # and the ball starts at least this far from a wall or solid


def rank_spawns(level, *, count=5, diameter_m=START_DIAMETER_M, refine=400, separation_m=0.6,
                near_m=3.0, far_m=8.0):
    """The best ``count`` spawn points, one row each (``describe_point`` + the scan fields).

    Pipeline: build the start-diameter graph, keep only its main strongly connected region
    (:func:`main_component`), score every eligible standing point (:func:`score_candidates`),
    refine the best ``refine`` with a forward search from the point, drop points whose route
    to the props within ``near_m`` needs a step over ``MAX_STEP_RATIO`` of the climb limit or
    that start within ``MIN_OBSTACLE_M`` of a wall/solid or have no feasible
    :func:`player_start`, then rank by (props within ``near_m``,
    props within ``far_m``, reachable area) with no two rows closer than ``separation_m``.
    """
    reach = Reach(level, diameter_m)
    region, _ids = main_component(reach)
    records, graph = score_candidates(reach, allowed=region, near_m=near_m, far_m=far_m)
    rows = []
    for record in records[:refine]:
        info = describe_point(reach, record["node"], near_m, far_m)
        row = dict(record)
        row.update(near=len(info["near_ids"]), far=len(info["far_ids"]),
                   nearest_m=info["nearest_m"], area_m2=info["area_m2"],
                   highest_step_m=info["highest_step_m"], near_ids=info["near_ids"],
                   obstacle_m=reach.nearest_obstacle(record["node"]),
                   prop_m=level.nearest_prop_distance((record["x"], record["y"], record["z"]), 5.0))
        rows.append(row)
    rows.sort(key=lambda r: (-r["near"], -r["far"], -r["area_m2"]))
    limit = MAX_STEP_RATIO * reach.climb
    eligible = [r for r in rows if r["highest_step_m"] <= limit and r["obstacle_m"] >= MIN_OBSTACLE_M
                and r["nearest_m"] is not None]
    ranked = []
    no_player = []
    for row in eligible:
        if any(math.hypot(row["x"] - p["x"], row["z"] - p["z"]) < separation_m for p in ranked):
            continue
        row["player"] = player_start(level, (row["x"], row["ground"] + reach.radius, row["z"]),
                                     diameter_m=diameter_m)
        if row["player"] is None:
            no_player.append(row)
            continue
        ranked.append(row)
        if len(ranked) >= count:
            break
    return {"reach": reach, "graph_nodes": graph, "main_nodes": len(region),
            "ranked": ranked, "noPlayerStart": no_player,
            "excluded": [r for r in distinct_top(rows, count + 3, separation_m)
                         if r not in eligible],
            "step_limit_m": limit}


# --------------------------------------------------------------------------- #
# the player's own spawn (VRChat descriptor spawn, ours: not the original's)
# --------------------------------------------------------------------------- #
PLAYER_BACK_M = 2.5
PLAYER_BACK_RANGE_M = (1.8, 3.5)
PLAYER_ANGLE_RANGE_DEG = 30.0
PLAYER_CLEAR_M = 0.4
PLAYER_STEP_M = 0.3
SEAT_TRIGGER_OFFSET = (0.0, 0.966, -0.994)   # from the ball centre, ball-local (heading 0: z back)


def _player_free(level, obstacles, x, z, ground, radius):
    """No wall or non-absorbable prop within ``radius`` of a standing player at ``(x, z)``."""
    probes = ((x, ground + 0.5, z), (x, ground + 1.2, z))
    for probe in probes:
        for a, b, c, lo, hi, _ceiling in level.steep_index.near(x, z, radius):
            if hi <= ground + PLAYER_STEP_M or lo > probe[1] + radius or hi < probe[1] - radius:
                continue
            if tri_dist(probe, a, b, c) < radius:
                return False
        for solid in obstacles.near(x, z, radius):
            if solid.top <= ground + PLAYER_STEP_M:
                continue
            if solid.dist(probe) < radius:
                return False
    return True


def player_start(level, ball_start, *, heading_deg=0.0, diameter_m=START_DIAMETER_M):
    """The player's spawn: 2.5 m behind the ball along its heading, on the terrain, facing it.

    ``heading_deg`` 0 is +Z.  The point must be on a terrain face (the one closest to the
    ball's ground, within a step), keep ``PLAYER_CLEAR_M`` free of walls and of props the start
    diameter cannot absorb, and have a straight walkable line (no step over
    ``PLAYER_STEP_M``, the same clearance all the way) to the ground under the seat trigger
    (``SEAT_TRIGGER_OFFSET`` from the ball centre).  When 2.5 m behind fails, the nearest
    feasible point 1.8-3.5 m back within +-30 degrees is taken.  Returns a dict
    (``position``, ``distanceM``, ``angleDeg``, ``facingDeg`` = the bearing from the player to
    the ball = ``heading_deg + angleDeg``,
    ``seatGround``) or ``None``.
    """
    ground0 = ball_start[1] - diameter_m / 2.0
    obstacles = _GridIndex(0.5)
    for prop in level.props:
        if prop.solid is not None and not prop.gate_open(diameter_m):
            obstacles.add(prop.solid, prop.solid.lo[0], prop.solid.lo[2], prop.solid.hi[0],
                          prop.solid.hi[2])
    heading = math.radians(heading_deg)
    seat = (ball_start[0] + SEAT_TRIGGER_OFFSET[2] * math.sin(heading),
            ball_start[2] + SEAT_TRIGGER_OFFSET[2] * math.cos(heading))
    x0, z0, x1, z1 = level.rect
    options = []
    steps = int(round((PLAYER_BACK_RANGE_M[1] - PLAYER_BACK_RANGE_M[0]) / 0.1))
    for i in range(steps + 1):
        distance = PLAYER_BACK_RANGE_M[0] + 0.1 * i
        for a in range(0, int(PLAYER_ANGLE_RANGE_DEG / 5.0) + 1):
            for sign in ((1, -1) if a else (1,)):
                options.append((abs(distance - PLAYER_BACK_M), a * 5.0, distance, sign * a * 5.0))
    options.sort()
    for _d, _a, distance, angle in options:
        direction = heading + math.radians(angle)
        x = ball_start[0] - distance * math.sin(direction)
        z = ball_start[2] - distance * math.cos(direction)
        if x < x0 + PLAYER_CLEAR_M or x > x1 - PLAYER_CLEAR_M or z < z0 + PLAYER_CLEAR_M \
                or z > z1 - PLAYER_CLEAR_M:
            continue
        ground = level.terrain_height_near(x, z, ground0, PLAYER_STEP_M)
        if ground is None or not _player_free(level, obstacles, x, z, ground, PLAYER_CLEAR_M):
            continue
        # the straight line to the ground under the seat trigger
        length = math.hypot(seat[0] - x, seat[1] - z)
        count = max(1, int(math.ceil(length / 0.1)))
        previous = ground
        ok = True
        for k in range(1, count + 1):
            px = x + (seat[0] - x) * k / count
            pz = z + (seat[1] - z) * k / count
            height = level.terrain_height_near(px, pz, previous, PLAYER_STEP_M)
            if height is None or not _player_free(level, obstacles, px, pz, height, 0.3):
                ok = False
                break
            previous = height
        if not ok:
            continue
        return {"position": [round(x, 4), round(ground, 4), round(z, 4)],
                "distanceM": round(distance, 2), "angleDeg": angle,
                "facingDeg": round(heading_deg + angle, 2),
                "seatGround": [round(seat[0], 4), round(previous, 4), round(seat[1], 4)]}
    return None


# --------------------------------------------------------------------------- #
# growth preview
# --------------------------------------------------------------------------- #
def bucket_diameter(diameter_m, start_m=START_DIAMETER_M):
    """The cached diameter: ``start * 1.01 ** k``, never larger than ``diameter_m``."""
    k = max(0, int(math.floor(math.log(diameter_m / start_m) / math.log(BUCKET_RATIO) + 1e-9)))
    return start_m * BUCKET_RATIO ** k


def simplify_polyline(points, tolerance=0.15):
    """Ramer-Douglas-Peucker on ``[(x, z)]``; keeps the end points."""
    if len(points) < 3:
        return list(points)
    (x0, z0), (x1, z1) = points[0], points[-1]
    length = math.hypot(x1 - x0, z1 - z0)
    best, index = -1.0, 0
    for k in range(1, len(points) - 1):
        px, pz = points[k]
        if length < 1e-9:
            dist = math.hypot(px - x0, pz - z0)
        else:
            dist = abs((x1 - x0) * (z0 - pz) - (x0 - px) * (z1 - z0)) / length
        if dist > best:
            best, index = dist, k
    if best <= tolerance:
        return [points[0], points[-1]]
    left = simplify_polyline(points[:index + 1], tolerance)
    return left[:-1] + simplify_polyline(points[index:], tolerance)


def simulate_growth(level, start_xz, *, target_m=0.80, max_steps=600, speed_mps=CRUISE_SPEED_MPS,
                    safe=False, lookahead=1.04):
    """Greedy preview: roll to the nearest absorbable prop, absorb, regrow, repeat.

    ``safe`` keeps the ball inside the strongly connected region that touches most remaining
    absorbable props, at the current diameter and ``lookahead`` times it, so a plateau or pit
    that only closes after a few more absorbs is never entered (the plain greedy run walks
    into those; see the report).  Returns a dict with ``events`` (one per absorb, with the
    ``segment`` polyline of the move that reached it), ``final`` diameter, ``stuck`` details
    and ``timeline`` ``[(seconds, diameter)]``.  Geometry is cached per 1 % of diameter, and
    the bucket's (slightly smaller) diameter also decides what is absorbable, so the preview
    errs on the small side.
    """
    absorbed = []
    absorbed_set = set()
    volume = level.start_volume_mm3
    caches = {}
    safe_regions = {}

    def reach_for(diameter):
        bucket = bucket_diameter(diameter, level.start_diameter_m)
        key = round(bucket, 6)
        if key not in caches:
            caches[key] = Reach(level, bucket)
        return caches[key]

    def safe_region(current, remaining):
        """The main region at this ``lookahead`` step and the next, cached per step."""
        k = int(math.floor(math.log(current / level.start_diameter_m) / math.log(lookahead)
                           + 1e-9))
        if k not in safe_regions:
            regions = []
            for index in (k, k + 1):
                size = level.start_diameter_m * lookahead ** index
                key = ("safe", round(size, 6))
                if key not in caches:
                    caches[key] = Reach(level, size)
                regions.append(main_component(
                    caches[key], [p for p in remaining if p.gate_open(size)])[0])
            safe_regions[k] = regions[0] & regions[1]
        return safe_regions[k]

    diameter = level.start_diameter_m
    reach = reach_for(diameter)
    node = reach.snap(start_xz[0], start_xz[1])
    if node is None:
        raise ValueError("no ground under the start %r" % (start_xz,))
    travelled = 0.0
    events = []
    timeline = [(0.0, diameter)]
    stuck = None
    while len(events) < max_steps and diameter < target_m:
        reach = reach_for(diameter)
        node = _relocate(reach, node)
        if node is None:
            stuck = {"reason": "the ball no longer fits where it stands"}
            break
        targets = [p for p in level.props
                   if p.id not in absorbed_set and p.gate_open(reach.diameter)]
        allowed = None
        if safe and targets:
            allowed = safe_region(diameter, targets)
            if node not in allowed:
                allowed = None
        dist = {node: 0.0}
        heap = [(0.0, node)]
        done = set()
        parent = {}
        found = None
        while heap:
            cost, current = heapq.heappop(heap)
            if current in done:
                continue
            done.add(current)
            touched = reach.touching(current, targets)
            if touched:
                found = (cost, current, touched)
                break
            for other, weight in reach.edges(current):
                if allowed is not None and other not in allowed:
                    continue
                total = cost + weight
                if total < dist.get(other, _INF):
                    dist[other] = total
                    parent[other] = current
                    heapq.heappush(heap, (total, other))
        if found is None:
            stuck = _stuck_report(level, reach, done, absorbed_set, diameter)
            break
        cost, current, touched = found
        travelled += cost
        centre = reach.centre(current)
        touched.sort(key=lambda p: math.dist(p.pos, centre))
        chain = [current]
        while chain[-1] in parent:
            chain.append(parent[chain[-1]])
        chain.reverse()
        segment = simplify_polyline([(reach.centre(n)[0], reach.centre(n)[2]) for n in chain])
        for prop in touched:
            absorbed.append(prop.id)
            absorbed_set.add(prop.id)
            volume += prop.growth_mm3
            diameter = diameter_from_volume_mm3(volume)
            events.append({"order": len(events) + 1, "id": prop.id, "def": prop.def_name,
                           "position": prop.pos, "diameterAfterM": diameter,
                           "pathM": travelled, "timeS": travelled / speed_mps,
                           "ballXZ": (centre[0], centre[2]), "reqMm": prop.req_mm,
                           "node": current, "segment": segment if prop is touched[0] else []})
            timeline.append((travelled / speed_mps, diameter))
        node = current
    return {"events": events, "final_diameter_m": diameter, "stuck": stuck,
            "path_m": travelled, "absorbed": absorbed, "timeline": timeline}


def _relocate(reach, node):
    """``node`` if the ball still fits there, else the nearest standable node within 0.6 m."""
    if reach.valid(node):
        return node
    i, j, k = node
    h = reach.level.layers[(i, j)][k][0]
    best = None
    span = int(round(0.6 / CELL_M))
    for di in range(-span, span + 1):
        for dj in range(-span, span + 1):
            layers = reach.level.layers.get((i + di, j + dj))
            if not layers:
                continue
            for kk, (height, _ny) in enumerate(layers):
                if abs(height - h) > reach.climb:
                    continue
                other = (i + di, j + dj, kk)
                score = di * di + dj * dj
                if (best is None or score < best[0]) and reach.valid(other):
                    best = (score, other)
    return None if best is None else best[1]


def _stuck_report(level, reach, region, absorbed_set, diameter):
    """What the ball can reach when nothing absorbable is left, and what it would need."""
    remaining = [p for p in level.props if p.id not in absorbed_set and p.absorbable]
    needed = {}
    for prop in remaining:
        if prop.gate_open(reach.diameter):
            continue
        for node in region:
            if reach.touching(node, [prop]):
                needed[prop.id] = prop.req_mm
                break
    near_miss = []
    for prop in remaining:
        if not prop.gate_open(reach.diameter):
            continue
        best = _INF
        for node in region:
            x, y, z = reach.centre(node)
            best = min(best, math.dist((x, y, z), prop.pos) - reach.radius - prop.half_diagonal)
        near_miss.append((best, prop.id))
    near_miss.sort()
    return {"diameterM": diameter, "reachableAreaM2": len(region) * CELL_M * CELL_M,
            "sizeLockedInReach": sorted(needed.items(), key=lambda kv: kv[1])[:10],
            "minReqMmToProgress": min(needed.values()) if needed else None,
            "absorbableButUnreachable": len([1 for gap, _ in near_miss]),
            "closestUnreachable": [(round(gap, 3), pid) for gap, pid in near_miss[:5]]}


# --------------------------------------------------------------------------- #
# loading and CLI
# --------------------------------------------------------------------------- #
def load_level(level_dir):
    """A :class:`Level` from ``<level_dir>/placement.v2.json`` and its terrain sidecar."""
    folder = Path(level_dir)
    document = json.loads((folder / "placement.v2.json").read_text(encoding="utf-8"))
    mesh = document["terrain"]["meshRef"]["path"]
    terrain = placement_v2.read_sidecar((folder / mesh).read_bytes())
    return Level(document, terrain)


def route_waypoints(level, reach, start_node, events, *, spacing_m=0.5):
    """Waypoints ``[{x, z, items}]`` for the growth events: one per absorb stop.

    Consecutive events at the same ball node are merged; the ``items`` of a stop are the
    prop ids absorbed there.  Positions are ground-plane ball positions (heading 0 = +Z).
    """
    del reach, start_node, spacing_m
    stops = []
    for event in events:
        xz = [round(event["ballXZ"][0], 2), round(event["ballXZ"][1], 2)]
        if stops and stops[-1]["node"] == event["node"]:
            stops[-1]["items"].append(event["id"])
        else:
            stops.append({"x": xz[0], "z": xz[1], "items": [event["id"]], "node": event["node"]})
    for stop in stops:
        del stop["node"]
    return stops


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("level_dir", help="a reference level directory with placement.v2.json")
    sub = parser.add_subparsers(dest="command", required=True)
    area = sub.add_parser("area", help="reachable ground area from a point")
    area.add_argument("x", type=float)
    area.add_argument("z", type=float)
    area.add_argument("--old-rules", action="store_true",
                      help="step and drop 0.25 D per edge, only structures.solids block (the retired rule)")
    scan = sub.add_parser("scan", help="rank the spawn points of the level")
    scan.add_argument("--count", type=int, default=5)
    grow = sub.add_parser("grow", help="greedy growth preview from a point")
    grow.add_argument("x", type=float)
    grow.add_argument("z", type=float)
    grow.add_argument("--safe", action="store_true",
                      help="stay inside the main strongly connected region (no plateau traps)")
    args = parser.parse_args(argv)
    level = load_level(args.level_dir)
    if args.command == "area":
        if args.old_rules:
            reach = Reach(level, START_DIAMETER_M, blockers="solids",
                          step_diameters=OLD_STEP_DIAMETERS,
                          max_drop=OLD_STEP_DIAMETERS * START_DIAMETER_M)
        else:
            reach = Reach(level, START_DIAMETER_M)
        node = reach.snap(args.x, args.z)
        print("start node %r valid=%s area=%.1f m2"
              % (node, reach.valid(node) if node else None,
                 reach.reachable_area_m2(node) if node else 0.0))
    elif args.command == "scan":
        report = rank_spawns(level, count=args.count)
        print("graph %d nodes, main region %d; step limit %.3f m"
              % (report["graph_nodes"], report["main_nodes"], report["step_limit_m"]))
        for row in report["ranked"]:
            print("(%.2f, %.2f) ground %.4f  3 m: %d  8 m: %d  nearest %.2f m  obstacle %.2f m  "
                  "area %.1f m2  step %.3f m" % (row["x"], row["z"], row["ground"], row["near"],
                                                row["far"], row["nearest_m"], row["obstacle_m"],
                                                row["area_m2"], row["highest_step_m"]))
            print("    player start %r" % (row["player"],))
        for row in report["noPlayerStart"]:
            print("(%.2f, %.2f) has no feasible player start (3 m: %d)"
                  % (row["x"], row["z"], row["near"]))
    else:
        result = simulate_growth(level, (args.x, args.z), safe=args.safe)
        for event in result["events"][:20]:
            print("%3d %-26s D=%.3f  t=%5.1fs" % (event["order"], event["id"],
                                                  event["diameterAfterM"], event["timeS"]))
        print("final D=%.3f m after %d absorbs, %.1f m; stuck=%r"
              % (result["final_diameter_m"], len(result["events"]), result["path_m"],
                 result["stuck"]))


if __name__ == "__main__":
    main()
