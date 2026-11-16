"""Procedural v2 terrain meshes for PhysicsTestField cells C21-C24.

Single source for the four terrain test cells: run this module to rewrite
``Docs/Design/Levels/physics_test_field/C2{1,2,3,4}/placement.json``. The criteria rows
live in ``Docs/Design/BallMotor/physics_test_field.json`` (the field's single source);
this module only writes geometry documents. It does not touch any Unity scene.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEVELS = ROOT / "Docs/Design/Levels/physics_test_field"
FIELD = ROOT / "Docs/Design/BallMotor/physics_test_field.json"
MATERIAL = "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundGround.mat"
COS45 = math.cos(math.radians(45.0))

# Cell centres are a second row below the existing nine cells (z = -22).
CELLS = {
    "C21": {"slug": "terrain_stairs", "center": (-10.5, -22.0), "size": (6.0, 9.0),
            "name_zh": "地形台阶 踢面 0.30/0.60/0.95、台面 1.85 m（T2 爬升）"},
    "C22": {"slug": "terrain_gully", "center": (-3.5, -22.0), "size": (6.0, 9.0),
            "name_zh": "地形沟 6×3 m、深 1.0 m（T2 爬升）"},
    "C23": {"slug": "terrain_tunnel", "center": (3.5, -22.0), "size": (6.0, 9.0),
            "name_zh": "地形隧道/悬垂、净高 1.2 m（T0/T1）"},
    "C24": {"slug": "terrain_curb", "center": (10.5, -22.0), "size": (6.0, 9.0),
            "name_zh": "地形路缘 0.60 m（T2 爬升）"},
}


class Mesh:
    def __init__(self):
        self.vertices: list[list[float]] = []
        self.indices: list[int] = []
        self.expected: list[tuple[float, float, float]] = []

    def quad(self, a, b, c, d, normal):
        """Append one outward quad; a/b/c/d are counter-clockwise on the front side."""
        n = _cross(_sub(b, a), _sub(c, a))
        if _dot(n, normal) < 0:
            a, b, c, d = a, d, c, b
            n = _cross(_sub(b, a), _sub(c, a))
        if _length(n) <= 1e-9:
            raise ValueError("degenerate quad")
        base = len(self.vertices)
        for point in (a, b, c, d):
            self.vertices.append([round(float(v), 4) for v in point])
        self.indices.extend([base, base + 1, base + 2, base, base + 2, base + 3])
        normal_value = _unit(n)
        self.expected.extend((normal_value, normal_value))

    def triangle(self, a, b, c, normal):
        n = _cross(_sub(b, a), _sub(c, a))
        if _dot(n, normal) < 0:
            b, c = c, b
            n = _cross(_sub(b, a), _sub(c, a))
        if _length(n) <= 1e-9:
            raise ValueError("degenerate triangle")
        base = len(self.vertices)
        for point in (a, b, c):
            self.vertices.append([round(float(v), 4) for v in point])
        self.indices.extend([base, base + 1, base + 2])
        self.expected.append(_unit(n))

    def translate(self, dx, dz):
        for point in self.vertices:
            point[0] += dx
            point[2] += dz


def _sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def _cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def _dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def _length(a): return math.sqrt(_dot(a, a))
def _unit(a): return (a[0] / _length(a), a[1] / _length(a), a[2] / _length(a))


def _flat(mesh, x0, x1, z0, z1, y):
    mesh.quad((x0, y, z0), (x1, y, z0), (x1, y, z1), (x0, y, z1), (0.0, 1.0, 0.0))


def _riser(mesh, x0, x1, z, y0, y1, normal):
    mesh.quad((x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z), normal)


def _wall_x(mesh, x, z0, z1, y0, y1, normal):
    mesh.quad((x, y0, z0), (x, y1, z0), (x, y1, z1), (x, y0, z1), normal)


def _wall_z(mesh, z, x0, x1, y0, y1, normal):
    mesh.quad((x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z), normal)


def build_stairs():
    """Three graded risers 0.30 / 0.60 / 0.95 m with 2 m treads, plateau 1.85 m.

    ``TERRAIN_CLIMB.md`` 6.3 (2026-11-16): a riser is climbable when its height is below
    ``H_cap = 1.5 * D``, i.e. 0.45 / 0.90 / 1.65 / 3.30 m at the four preset diameters.
    The ladder therefore stops the 0.30 ball at the first riser and the 0.60 ball at the
    second, and only the 1.10 / 2.20 balls reach the plateau. The plateau runs to the cell
    edge: a ball that clears it keeps rolling onto the arena plane, which is why the cell's
    ``climbed`` row is a progress threshold (a run-off is not a stall).
    """
    mesh = Mesh()
    _flat(mesh, -1.5, 1.5, -4.4, -2.5, 0.0)
    _riser(mesh, -1.5, 1.5, -2.5, 0.0, 0.30, (0.0, 0.0, -1.0))
    _flat(mesh, -1.5, 1.5, -2.5, -0.5, 0.30)
    _riser(mesh, -1.5, 1.5, -0.5, 0.30, 0.90, (0.0, 0.0, -1.0))
    _flat(mesh, -1.5, 1.5, -0.5, 1.5, 0.90)
    _riser(mesh, -1.5, 1.5, 1.5, 0.90, 1.85, (0.0, 0.0, -1.0))
    _flat(mesh, -1.5, 1.5, 1.5, 4.4, 1.85)
    return mesh


def build_gully():
    mesh = Mesh()
    # 6 x 3 m gully: 1.0 m deep, vertical walls, approach/exit lips at y=0.
    # The lips run to the cell edge so the exit has room to roll out (TERRAIN_CLIMB 7:
    # the 1.10 ball climbs out in ~2 s and needs a place to land).
    _flat(mesh, -3.0, 3.0, -4.4, -1.5, 0.0)
    _flat(mesh, -3.0, 3.0, 1.5, 4.4, 0.0)
    _flat(mesh, -3.0, 3.0, -1.5, 1.5, -1.0)
    _wall_z(mesh, -1.5, -3.0, 3.0, 0.0, -1.0, (0.0, 0.0, 1.0))
    _wall_z(mesh, 1.5, -3.0, 3.0, 0.0, -1.0, (0.0, 0.0, -1.0))
    _wall_x(mesh, -3.0, -1.5, 1.5, 0.0, -1.0, (1.0, 0.0, 0.0))
    _wall_x(mesh, 3.0, -1.5, 1.5, 0.0, -1.0, (-1.0, 0.0, 0.0))
    return mesh


def build_tunnel():
    mesh = Mesh()
    _flat(mesh, -3.0, 3.0, -3.0, 3.0, 0.0)
    # Roof underside at 1.2 m, normal down; ball below is on its front side.
    mesh.quad((-1.5, 1.2, -1.5), (1.5, 1.2, -1.5), (1.5, 1.2, 1.5), (-1.5, 1.2, 1.5),
              (0.0, -1.0, 0.0))
    _wall_x(mesh, -1.5, -1.5, 1.5, 0.0, 1.2, (1.0, 0.0, 0.0))
    _wall_x(mesh, 1.5, -1.5, 1.5, 0.0, 1.2, (-1.0, 0.0, 0.0))
    return mesh


def build_curb():
    """0.60 m curb, 0.90 m wide, on ground that runs to the cell edge.

    ``TERRAIN_CLIMB.md`` 6.3: the 0.30 m ball (H_cap 0.45) is stopped, the 0.60 (0.90) and
    1.10 (1.65) balls climb it, and the 2.20 ball rolls over it (h < r).
    """
    mesh = Mesh()
    _flat(mesh, -3.0, 3.0, -4.4, 4.4, 0.0)
    _flat(mesh, -3.0, 3.0, -0.45, 0.45, 0.60)
    _wall_z(mesh, -0.45, -3.0, 3.0, 0.0, 0.60, (0.0, 0.0, -1.0))
    _wall_z(mesh, 0.45, -3.0, 3.0, 0.0, 0.60, (0.0, 0.0, 1.0))
    _wall_x(mesh, -3.0, -0.45, 0.45, 0.0, 0.60, (-1.0, 0.0, 0.0))
    _wall_x(mesh, 3.0, -0.45, 0.45, 0.0, 0.60, (1.0, 0.0, 0.0))
    return mesh


BUILDERS = {"C21": build_stairs, "C22": build_gully, "C23": build_tunnel,
            "C24": build_curb}


def _classes(mesh):
    classes = []
    for normal in mesh.expected:
        value = 1 if normal[1] >= COS45 else (2 if normal[1] > 1e-6 else 3)
        classes.append(value)
    return classes


def _doc(cell_id, mesh):
    spec = CELLS[cell_id]
    mesh.translate(spec["center"][0], spec["center"][1])
    count = len(mesh.indices) // 3
    minimum = [min(p[i] for p in mesh.vertices) for i in range(3)]
    maximum = [max(p[i] for p in mesh.vertices) for i in range(3)]
    # The sampling grid must COVER the broad-phase box (placement_v2 [terrain_grid]): its
    # origin is the box's inflated minimum, not the cell footprint corner - the two differ
    # by the 1 mm inflation and by any mesh that does not reach the footprint edge.
    grid_cell = 4.0
    grid_origin = [round(minimum[0] - 0.001, 4), round(minimum[2] - 0.001, 4)]
    grid_cells_x = max(1, int(math.ceil((maximum[0] + 0.001 - grid_origin[0]) / grid_cell)))
    grid_cells_z = max(1, int(math.ceil((maximum[2] + 0.001 - grid_origin[1]) / grid_cell)))
    boxes = [{"id": cell_id + "_0", "min": [round(minimum[0] - 0.001, 4), round(minimum[1] - 0.001, 4), round(minimum[2] - 0.001, 4)],
              "max": [round(maximum[0] + 0.001, 4), round(maximum[1] + 0.001, 4), round(maximum[2] + 0.001, 4)],
              "triangleRange": [0, count]}]
    doc = {
        "schema": "katamari.placement/2",
        "meta": {
            "id": cell_id,
            "scene": "PhysicsTestField",
            "units": "m",
            "convention": "unity-y-up-left-handed-quat-xyzw",
            "ball": {"startDiameterM": 0.30, "targetDiameterM": 0.60,
                     "maxDiameterRef": "level", "maxDiameterM": 2.20,
                     "start": [spec["center"][0], 0.15, spec["center"][1] - 3.0],
                     "spawnSource": "authored"},
            "timeLimitS": 0,
            "playerSpawn": [spec["center"][0], 0.5, spec["center"][1] - 3.0],
            "groundY": -2.0,
            "bounds": [[spec["center"][0] - 3.0, spec["center"][1] - 4.5],
                       [spec["center"][0] + 3.0, spec["center"][1] + 4.5]],
            "spawnClearanceM": 0.2,
            "zones": [{"id": cell_id, "code": 0, "kind": "ground"}],
            "arena": {"id": cell_id},
            "caps": {"props": 0, "terrainTriangles": 512, "terrainVertices": 1024,
                     "staticSolids": 0, "activityWalls": 0, "movingProps": 0, "animators": 0},
            "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
            "expected": {"propCount": 0, "valueUnits": 0, "terrainTriangles": count},
            "source": {"kind": "authored"},
        },
        "terrain": {
            "id": cell_id,
            "kind": "mesh",
            "winding": "ccw",
            "vertices": mesh.vertices,
            "indices": mesh.indices,
            "triangles": {"count": count, "zoneIds": [0] * count, "classes": _classes(mesh)},
            "broadPhase": {"kind": "aabb", "boxes": boxes},
            "chunks": [{"id": cell_id + "_chunk_0", "boxRange": [0, 1],
                        "triangleRange": [0, count]}],
            "grid": {"cellM": grid_cell, "origin": grid_origin,
                     "cellsX": grid_cells_x, "cellsZ": grid_cells_z},
            "materialSegments": [{"id": "terrain", "firstIndex": 0,
                                  "indexCount": len(mesh.indices), "material": MATERIAL}],
            "source": {"kind": "authored"},
        },
        "structures": {"solids": []},
        "activityArea": {
            "walls": [],
            "killPlane": {"y": -4.0, "respawn": "ballStart", "zoneId": 0},
            "softMarginM": 0.0,
            "source": "authored",
        },
        "props": [],
        "defs": [],
        "motions": [],
        "paths": [],
        "cameras": [],
        "renderPresets": [],
        "regions": [],
    }
    return doc


def build_docs():
    return {cell_id: _doc(cell_id, BUILDERS[cell_id]()) for cell_id in CELLS}


def write_docs():
    for cell_id, doc in build_docs().items():
        directory = LEVELS / cell_id
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "placement.json").write_text(
            json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    write_docs()
    for cell_id, doc in build_docs().items():
        print(cell_id, doc["terrain"]["triangles"]["count"], len(doc["terrain"]["vertices"]))
