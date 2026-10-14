"""T49 ground-support approximation; standard-library host reference only.

DRAFT: no Unity/Udon, dynamics, or world-scale claim.  The input rotation is an
active local-to-world 3x3 matrix acting on column vectors; its convention must
be supplied by a caller-side Unity direction probe before direct integration.
"""
from dataclasses import dataclass
import math
from typing import Iterable, Sequence, Tuple

Vec3 = Tuple[float, float, float]
Mat3 = Tuple[Vec3, Vec3, Vec3]
_ROTATION_TOLERANCE = 1e-6


@dataclass(frozen=True)
class GroundContact:
    support_height: float
    center_position: Vec3
    center_normal_coordinate: float
    plane_anchor: Vec3
    applied_normal_correction: float
    normal_speed_perturbation: float
    lift_clamped: bool
    drop_clamped: bool


def _finite(value: float, name: str) -> float:
    try:
        value = float(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ValueError(f"{name} must be a finite scalar") from exc
    if not math.isfinite(value):
        raise ValueError(f"{name} must be finite")
    return value


def _vec3(value: Sequence[float], name: str) -> Vec3:
    try:
        if len(value) != 3:
            raise ValueError
        result = tuple(_finite(value[i], f"{name}[{i}]") for i in range(3))
    except (TypeError, IndexError, ValueError) as exc:
        raise ValueError(f"{name} must be a finite 3-vector") from exc
    return result  # type: ignore[return-value]


def _dot(a: Vec3, b: Vec3) -> float:
    result = a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    if not math.isfinite(result):
        raise ValueError("non-finite intermediate vector dot product")
    return result


def _normalize(value: Vec3, name: str) -> tuple[Vec3, float]:
    length = math.hypot(value[0], value[1], value[2])
    if not math.isfinite(length) or length == 0.0:
        raise ValueError(f"{name} must be finite and non-zero")
    result = tuple(component / length for component in value)
    return result, length  # type: ignore[return-value]


def _mat_vec(matrix: Mat3, point: Vec3) -> Vec3:
    result = tuple(_dot(row, point) for row in matrix)
    if not all(math.isfinite(value) for value in result):
        raise ValueError("rotation produced non-finite coordinates")
    return result  # type: ignore[return-value]


def validate_rotation_matrix(value: Sequence[Sequence[float]]) -> Mat3:
    """Validate a proper orthonormal 3x3 local-to-world rotation matrix.

    Rows act on a column vector.  Scaling, shear, reflection and malformed
    dimensions are rejected instead of being silently interpreted as rotation.
    """
    try:
        if len(value) != 3 or any(len(row) != 3 for row in value):
            raise ValueError
        matrix = tuple(tuple(_finite(value[i][j], f"rotation[{i}][{j}]")
                             for j in range(3)) for i in range(3))
    except (TypeError, IndexError, ValueError) as exc:
        raise ValueError("visual_rotation must be a finite 3x3 matrix") from exc
    rows: Mat3 = matrix  # type: ignore[assignment]
    for i in range(3):
        for j in range(3):
            expected = 1.0 if i == j else 0.0
            if abs(_dot(rows[i], rows[j]) - expected) > _ROTATION_TOLERANCE:
                raise ValueError("visual_rotation rows must be orthonormal")
    determinant = (
        rows[0][0] * (rows[1][1] * rows[2][2] - rows[1][2] * rows[2][1])
        - rows[0][1] * (rows[1][0] * rows[2][2] - rows[1][2] * rows[2][0])
        + rows[0][2] * (rows[1][0] * rows[2][1] - rows[1][1] * rows[2][0])
    )
    if not math.isfinite(determinant) or abs(determinant - 1.0) > _ROTATION_TOLERANCE:
        raise ValueError("visual_rotation must be a proper rotation (determinant +1)")
    return rows


def rotation_matrix_from_quaternion(quaternion: Sequence[float]) -> Mat3:
    """Convert a host-reference Hamilton (x,y,z,w) active quaternion to a matrix.

    This adapter is deliberately not asserted to match Unity Quaternion behavior;
    compare its +Y/90° direction probe with the target Unity 2022.3 editor first.
    """
    try:
        if len(quaternion) != 4:
            raise ValueError
        q = tuple(_finite(quaternion[i], f"quaternion[{i}]") for i in range(4))
    except (TypeError, IndexError, ValueError) as exc:
        raise ValueError("quaternion must contain four finite components") from exc
    norm = math.hypot(*q)
    if not math.isfinite(norm) or norm <= 1e-15:
        raise ValueError("quaternion must be non-zero")
    x, y, z, w = (component / norm for component in q)
    matrix: Mat3 = (
        (1.0 - 2.0 * (y*y + z*z), 2.0 * (x*y - z*w), 2.0 * (x*z + y*w)),
        (2.0 * (x*y + z*w), 1.0 - 2.0 * (x*x + z*z), 2.0 * (y*z - x*w)),
        (2.0 * (x*z - y*w), 2.0 * (y*z + x*w), 1.0 - 2.0 * (x*x + y*y)),
    )
    return validate_rotation_matrix(matrix)


def solve_ground_contact(
    core_radius: float,
    visual_rotation: Sequence[Sequence[float]],
    proxy_points: Iterable[Sequence[float]],
    ground_normal: Sequence[float],
    plane_height: float,
    *,
    current_center: Sequence[float],
    max_lift_per_step: float,
    max_drop_per_step: float,
    requested_normal_speed_perturbation: float,
    max_normal_speed_perturbation: float,
) -> GroundContact:
    """Recompute current support and apply bounded normal-only correction.

    Plane convention is raw ``dot(ground_normal, x) = plane_height``.  If the
    supplied normal is not unit length, both it and its offset are normalized
    together.  The center correction preserves its tangential world position;
    ``center_normal_coordinate`` is a coordinate along the unit normal, not a
    world-Y height.  ``plane_anchor`` is the closest point on the plane to the
    origin, not a unique proxy/terrain contact feature.
    """
    radius = _finite(core_radius, "core_radius")
    raw_offset = _finite(plane_height, "plane_height")
    lift_limit = _finite(max_lift_per_step, "max_lift_per_step")
    drop_limit = _finite(max_drop_per_step, "max_drop_per_step")
    requested_speed = _finite(requested_normal_speed_perturbation,
                              "requested_normal_speed_perturbation")
    speed_limit = _finite(max_normal_speed_perturbation,
                          "max_normal_speed_perturbation")
    if radius < 0.0 or lift_limit < 0.0 or drop_limit < 0.0 or speed_limit < 0.0:
        raise ValueError("radius, correction caps, and speed cap must be nonnegative")

    matrix = validate_rotation_matrix(visual_rotation)
    raw_normal = _vec3(ground_normal, "ground_normal")
    normal, raw_normal_length = _normalize(raw_normal, "ground_normal")
    offset = raw_offset / raw_normal_length
    if not math.isfinite(offset):
        raise ValueError("normalized plane offset is non-finite")
    center = _vec3(current_center, "current_center")

    support = radius
    try:
        points = tuple(_vec3(point, "proxy point") for point in proxy_points)
    except TypeError as exc:
        raise ValueError("proxy_points must be an iterable of finite 3-vectors") from exc
    for point in points:
        rotated = _mat_vec(matrix, point)
        support = max(support, -_dot(normal, rotated))
    if not math.isfinite(support):
        raise ValueError("support calculation is non-finite")

    current_coordinate = _dot(normal, center)
    target_coordinate = offset + support
    if not math.isfinite(target_coordinate):
        raise ValueError("target center coordinate is non-finite")
    requested_correction = target_coordinate - current_coordinate
    correction = max(-drop_limit, min(lift_limit, requested_correction))
    lift_clamped = requested_correction > lift_limit
    drop_clamped = requested_correction < -drop_limit
    new_center = tuple(center[i] + normal[i] * correction for i in range(3))
    if not all(math.isfinite(value) for value in new_center):
        raise ValueError("corrected center position is non-finite")
    new_coordinate = _dot(normal, new_center)
    anchor = tuple(offset * component for component in normal)
    if not all(math.isfinite(value) for value in anchor):
        raise ValueError("plane anchor is non-finite")
    perturbation = max(-speed_limit, min(speed_limit, requested_speed))

    return GroundContact(
        support_height=support,
        center_position=new_center,  # type: ignore[arg-type]
        center_normal_coordinate=new_coordinate,
        plane_anchor=anchor,  # type: ignore[arg-type]
        applied_normal_correction=correction,
        normal_speed_perturbation=perturbation,
        lift_clamped=lift_clamped,
        drop_clamped=drop_clamped,
    )
