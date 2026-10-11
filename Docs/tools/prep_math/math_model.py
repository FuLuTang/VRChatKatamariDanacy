"""Offline pure-math reference helpers; not production physics or Udon code."""
from __future__ import annotations

import math
from typing import Iterable, Mapping, Sequence

Vec3 = tuple[float, float, float]
Mat4 = tuple[tuple[float, float, float, float], ...]


def finite(value: float, name: str = "value") -> float:
    value = float(value)
    if not math.isfinite(value):
        raise ValueError(f"{name} must be finite")
    return value


def finite_sum(values: Iterable[float], name: str = "result") -> float:
    try:
        result = math.fsum(values)
    except (OverflowError, ValueError) as exc:
        raise ValueError(f"{name} must be finite") from exc
    return finite(result, name)


def positive_scale(scale: Sequence[float]) -> Vec3:
    if len(scale) != 3:
        raise ValueError("scale must have three components")
    result = tuple(finite(v, "scale") for v in scale)
    if any(v <= 0.0 for v in result):
        raise ValueError("scale components must be positive")
    return result  # type: ignore[return-value]


def sphere_radius_from_volume(volume: float) -> float:
    """Return equivalent sphere radius for nonnegative game-volume units."""
    volume = finite(volume, "volume")
    if volume < 0.0:
        raise ValueError("volume must be nonnegative")
    # Avoid overflowing the intermediate 3*volume when the true radius is finite.
    result = math.cbrt(volume) * math.cbrt(3.0 / (4.0 * math.pi))
    return finite(result, "computed radius")


def sphere_volume_from_radius(radius: float) -> float:
    radius = finite(radius, "radius")
    if radius < 0.0:
        raise ValueError("radius must be nonnegative")
    try:
        result = (4.0 / 3.0) * math.pi * radius ** 3
    except OverflowError as exc:
        raise ValueError("computed volume is not finite") from exc
    return finite(result, "computed volume")


def add_contribution(ledger: Mapping[str, float], item_id: str, amount: float) -> dict[str, float]:
    """Return a new ledger; duplicate item id is idempotent, conflicting repeat rejects."""
    if not item_id:
        raise ValueError("item_id must be nonempty")
    amount = finite(amount, "amount")
    if amount < 0.0:
        raise ValueError("amount must be nonnegative")
    result = dict(ledger)
    if item_id in result:
        if not math.isclose(result[item_id], amount, rel_tol=0.0, abs_tol=1e-12):
            raise ValueError("duplicate item id with conflicting contribution")
        return result
    result[item_id] = amount
    return result


def remove_contribution(ledger: Mapping[str, float], item_id: str) -> dict[str, float]:
    """Remove exact item's ledger entry; unknown item is a no-op."""
    result = dict(ledger)
    result.pop(item_id, None)
    return result


def total_volume(ledger: Mapping[str, float], core_volume: float = 0.0) -> float:
    core_volume = finite(core_volume, "core_volume")
    if core_volume < 0.0:
        raise ValueError("core_volume must be nonnegative")
    values = [finite(v, "contribution") for v in ledger.values()]
    if any(v < 0.0 for v in values):
        raise ValueError("contributions must be nonnegative")
    return finite(core_volume + finite_sum(values, "contribution total"), "total volume")


def normalize(v: Sequence[float]) -> Vec3:
    if len(v) != 3:
        raise ValueError("vector must have three components")
    xyz = tuple(finite(x, "vector component") for x in v)
    scale = max(abs(x) for x in xyz)
    if scale <= 1e-15:
        raise ValueError("cannot normalize zero vector")
    scaled = tuple(x / scale for x in xyz)
    magnitude = math.hypot(*scaled)
    return tuple(x / magnitude for x in scaled)  # type: ignore[return-value]


def dot(a: Sequence[float], b: Sequence[float]) -> float:
    if len(a) != 3 or len(b) != 3:
        raise ValueError("dot product requires 3D vectors")
    return finite_sum((finite(x) * finite(y) for x, y in zip(a, b)), "dot product")


def support_distance(direction: Sequence[float], points: Iterable[Sequence[float]], core_radius: float) -> float:
    """Max support distance along unit direction, recomputed from current candidates."""
    n = normalize(direction)
    core_radius = finite(core_radius, "core_radius")
    if core_radius < 0.0:
        raise ValueError("core_radius must be nonnegative")
    distances = [dot(n, p) for p in points]
    return max([core_radius, *distances])


def vec3(x: float, y: float, z: float) -> Vec3:
    return (finite(x), finite(y), finite(z))


def identity() -> Mat4:
    return ((1., 0., 0., 0.), (0., 1., 0., 0.), (0., 0., 1., 0.), (0., 0., 0., 1.))


def validate_matrix(m: Sequence[Sequence[float]]) -> Mat4:
    if len(m) != 4 or any(len(row) != 4 for row in m):
        raise ValueError("transform matrix must be 4x4")
    rows = tuple(tuple(finite(value, "matrix component") for value in row) for row in m)
    return rows  # type: ignore[return-value]


def matmul(a: Mat4, b: Mat4) -> Mat4:
    """Column-vector convention: result transforms by b, then a."""
    a, b = validate_matrix(a), validate_matrix(b)
    result = tuple(tuple(finite_sum((a[r][k] * b[k][c] for k in range(4)), "matrix product") for c in range(4)) for r in range(4))
    return validate_matrix(result)


def transform_direction(m: Mat4, d: Sequence[float]) -> Vec3:
    """Transform a direction with w=0; translation is intentionally ignored."""
    m = validate_matrix(m)
    if len(d) != 3:
        raise ValueError("direction must have three components")
    xyz = tuple(finite(x) for x in d)
    result = tuple(finite_sum((m[r][c] * xyz[c] for c in range(3)), "transformed direction") for r in range(3))
    return tuple(finite(x, "transformed direction") for x in result)  # type: ignore[return-value]


def transform_point(m: Mat4, p: Sequence[float]) -> Vec3:
    m = validate_matrix(m)
    if len(p) != 3:
        raise ValueError("point must have three components")
    xyz = tuple(finite(x) for x in p)
    q = [finite_sum((m[r][c] * (xyz[c] if c < 3 else 1.0) for c in range(4)), "transformed homogeneous component") for r in range(4)]
    q = [finite(x, "transformed homogeneous component") for x in q]
    w = q[3]
    if abs(w) <= 1e-15:
        raise ValueError("invalid homogeneous point")
    return tuple(finite(q[i] / w, "transformed point component") for i in range(3))  # type: ignore[return-value]


def inverse4(m: Mat4) -> Mat4:
    """Gauss-Jordan inverse for a finite nonsingular 4x4 matrix."""
    m = validate_matrix(m)
    aug = [[m[r][c] for c in range(4)] + [1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]
    for col in range(4):
        pivot = max(range(col, 4), key=lambda r: abs(aug[r][col]))
        if abs(aug[pivot][col]) <= 1e-15:
            raise ValueError("singular transform")
        aug[col], aug[pivot] = aug[pivot], aug[col]
        divisor = aug[col][col]
        aug[col] = [x / divisor for x in aug[col]]
        for row in range(4):
            if row == col:
                continue
            factor = aug[row][col]
            aug[row] = [aug[row][i] - factor * aug[col][i] for i in range(8)]
    return validate_matrix(tuple(tuple(aug[r][c + 4] for c in range(4)) for r in range(4)))


def translation(x: float, y: float, z: float) -> Mat4:
    return ((1., 0., 0., finite(x)), (0., 1., 0., finite(y)), (0., 0., 1., finite(z)), (0., 0., 0., 1.))


def scale_matrix(scale: Sequence[float]) -> Mat4:
    x, y, z = positive_scale(scale)
    return ((x, 0., 0., 0.), (0., y, 0., 0.), (0., 0., z, 0.), (0., 0., 0., 1.))


def rotation_z(radians: float) -> Mat4:
    angle = finite(radians, "radians")
    c, s = math.cos(angle), math.sin(angle)
    return ((c, -s, 0., 0.), (s, c, 0., 0.), (0., 0., 1., 0.), (0., 0., 0., 1.))


def transform_chain(*local_to_parent: Mat4) -> Mat4:
    """Compose local→parent matrices in root-to-leaf order into local→world."""
    result = identity()
    for matrix in local_to_parent:
        result = matmul(result, matrix)
    return result


def attachment_local_pose(attachment_world: Sequence[float], visual_root_local_to_world: Mat4) -> Vec3:
    return transform_point(inverse4(visual_root_local_to_world), attachment_world)


def attachment_world_pose(attachment_local: Sequence[float], visual_root_local_to_world: Mat4) -> Vec3:
    return transform_point(visual_root_local_to_world, attachment_local)
