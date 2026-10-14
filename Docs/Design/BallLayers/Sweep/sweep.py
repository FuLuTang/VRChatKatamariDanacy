"""DRAFT offline sphere/environment sweep reference; stdlib only, not runtime code."""
from dataclasses import dataclass
import math
from typing import Sequence, Tuple, Union

Vec3 = Tuple[float, float, float]
_EPS = 1e-10


def _finite(value: float, name: str) -> float:
    try:
        value = float(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ValueError(f"{name} must be finite") from exc
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


def _add(a: Vec3, b: Vec3) -> Vec3:
    result = (a[0] + b[0], a[1] + b[1], a[2] + b[2])
    if not all(math.isfinite(value) for value in result):
        raise ValueError("position addition overflowed")
    return result


def _sub(a: Vec3, b: Vec3) -> Vec3:
    result = (a[0] - b[0], a[1] - b[1], a[2] - b[2])
    if not all(math.isfinite(value) for value in result):
        raise ValueError("vector subtraction overflowed")
    return result


def _mul(a: Vec3, scalar: float) -> Vec3:
    result = (a[0] * scalar, a[1] * scalar, a[2] * scalar)
    if not all(math.isfinite(value) for value in result):
        raise ValueError("vector scaling overflowed")
    return result


def _dot(a: Vec3, b: Vec3) -> float:
    value = a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    if not math.isfinite(value):
        raise ValueError("dot product overflowed")
    return value


def _length(vector: Vec3) -> float:
    value = math.hypot(*vector)
    if not math.isfinite(value):
        raise ValueError("vector length is non-finite")
    return value


def _unit(vector: Vec3, name: str) -> tuple[Vec3, float]:
    length = _length(vector)
    if length <= _EPS:
        raise ValueError(f"{name} must be non-zero")
    unit = tuple(component / length for component in vector)
    return unit, length  # type: ignore[return-value]


@dataclass(frozen=True)
class Plane:
    """One-sided half-space obstacle; normal points into allowed free space."""
    normal: Vec3
    offset: float = 0.0  # raw equation dot(normal, point) >= offset is free space


@dataclass(frozen=True)
class Box:
    """Axis-aligned solid box; bounds are its min/max center-space coordinates."""
    minimum: Vec3
    maximum: Vec3


Obstacle = Union[Plane, Box]


@dataclass(frozen=True)
class Contact:
    time: float  # normalized fraction of the original step displacement
    center: Vec3
    point: Vec3  # approximate feature point; box corner is conservative
    normal: Vec3
    obstacle_index: int
    penetration_depth: float


@dataclass(frozen=True)
class SweepResult:
    position: Vec3
    remaining_displacement: Vec3
    slide_direction: Vec3
    contacts: Tuple[Contact, ...]
    stuck: bool  # solver failed to validate/finish; not synonymous with blocked
    blocked: bool  # contacts consumed all requested motion without solver failure


def _validated_plane(plane: Plane) -> tuple[Vec3, float]:
    normal, raw_length = _unit(_vec3(plane.normal, "plane.normal"), "plane.normal")
    offset = _finite(plane.offset, "plane.offset") / raw_length
    if not math.isfinite(offset):
        raise ValueError("normalized plane offset is non-finite")
    return normal, offset


def _validated_box(box: Box, radius: float) -> tuple[Vec3, Vec3]:
    minimum = _vec3(box.minimum, "box.minimum")
    maximum = _vec3(box.maximum, "box.maximum")
    if any(minimum[i] > maximum[i] for i in range(3)):
        raise ValueError("box minimum must not exceed maximum")
    lo = tuple(minimum[i] - radius for i in range(3))
    hi = tuple(maximum[i] + radius for i in range(3))
    if not all(math.isfinite(value) for value in lo + hi):
        raise ValueError("expanded box bounds are non-finite")
    return lo, hi  # type: ignore[return-value]


def _plane_hit(start: Vec3, delta: Vec3, radius: float, p: Plane):
    normal, offset = _validated_plane(p)
    distance = _dot(start, normal) - offset - radius
    normal_motion = _dot(delta, normal)
    if distance < -_EPS:
        return 0.0, normal, -distance
    if normal_motion >= -_EPS:
        return None
    time = -distance / normal_motion
    if not math.isfinite(time):
        raise ValueError("plane time-of-impact is non-finite")
    if -_EPS <= time <= 1.0 + _EPS:
        return max(0.0, min(1.0, time)), normal, 0.0
    return None


def _box_hit(start: Vec3, delta: Vec3, radius: float, box: Box):
    lo, hi = _validated_box(box, radius)
    inside = all(lo[i] - _EPS <= start[i] <= hi[i] + _EPS for i in range(3))
    if inside:
        # Distance to each free face of the expanded AABB.  Ties choose the
        # lowest axis, then the negative face, so recovery is deterministic.
        choices = []
        for axis in range(3):
            choices.append((start[axis] - lo[axis], axis, -1.0))
            choices.append((hi[axis] - start[axis], axis, 1.0))
        depth, axis, sign = min(choices, key=lambda item: (item[0], item[1], item[2]))
        normal = tuple(sign if i == axis else 0.0 for i in range(3))
        if depth > _EPS:
            return 0.0, normal, depth
        # At a boundary only inward movement is a collision; tangent/outward
        # motion is free and must not be mislabeled as stuck.
        if _dot(delta, normal) < -_EPS:
            return 0.0, normal, 0.0
        return None

    enter, leave, normal = -math.inf, math.inf, None
    for axis in range(3):
        if abs(delta[axis]) <= _EPS:
            if start[axis] < lo[axis] or start[axis] > hi[axis]:
                return None
            continue
        first = (lo[axis] - start[axis]) / delta[axis]
        second = (hi[axis] - start[axis]) / delta[axis]
        if not math.isfinite(first) or not math.isfinite(second):
            raise ValueError("box time-of-impact intermediate is non-finite")
        sign = -1.0 if delta[axis] > 0.0 else 1.0
        if first > second:
            first, second = second, first
            sign = -sign
        if first > enter:
            enter = first
            normal = tuple(sign if i == axis else 0.0 for i in range(3))
        leave = min(leave, second)
    if normal is None or enter > leave + _EPS or leave < 0.0 or enter > 1.0:
        return None
    return max(0.0, enter), normal, 0.0


def _penetrates(position: Vec3, radius: float, obstacle: Obstacle) -> bool:
    if isinstance(obstacle, Plane):
        normal, offset = _validated_plane(obstacle)
        return _dot(position, normal) - offset < radius - _EPS
    lo, hi = _validated_box(obstacle, radius)
    return all(lo[i] + _EPS < position[i] < hi[i] - _EPS for i in range(3))


def sweep_sphere(start: Sequence[float], displacement: Sequence[float], radius: float,
                 obstacles: Tuple[Obstacle, ...], max_contacts: int = 8,
                 separation_epsilon: float = 1e-8) -> SweepResult:
    """Sweep a sphere and project residual motion in stable obstacle order.

    Plane ``normal`` and ``offset`` are normalized as a pair.  A box is expanded
    by the radius and tested as an AABB: this is conservative at box corners,
    not an exact sphere-vs-rounded-box calculation.  Initial penetration is
    examined even with zero requested displacement.  ``stuck`` means unresolved
    geometry or an unfinished budget/no-progress failure; a clean complete stop
    is reported as ``blocked=True, stuck=False``.
    """
    start_point = _vec3(start, "start")
    requested = _vec3(displacement, "displacement")
    sweep_radius = _finite(radius, "radius")
    epsilon = _finite(separation_epsilon, "separation_epsilon")
    if sweep_radius < 0.0 or epsilon < 0.0:
        raise ValueError("radius and separation_epsilon must be nonnegative")
    if (isinstance(max_contacts, bool) or not isinstance(max_contacts, int)
            or max_contacts < 1):
        raise ValueError("max_contacts must be a positive integer")
    if not isinstance(obstacles, tuple):
        obstacles = tuple(obstacles)
    for obstacle in obstacles:
        if isinstance(obstacle, Plane):
            _validated_plane(obstacle)
        elif isinstance(obstacle, Box):
            _validated_box(obstacle, sweep_radius)
        else:
            raise ValueError("obstacles must contain only Plane or Box values")

    position = start_point
    remaining = requested
    time_cursor, time_scale = 0.0, 1.0
    contacts = []
    unresolved = False
    exhausted = False
    motion_blocked_by_contact = False

    for _ in range(max_contacts):
        hits = []
        for index, obstacle in enumerate(obstacles):
            hit = (_plane_hit(position, remaining, sweep_radius, obstacle)
                   if isinstance(obstacle, Plane)
                   else _box_hit(position, remaining, sweep_radius, obstacle))
            if hit is not None:
                local_time, normal, penetration = hit
                hits.append((local_time, index, normal, penetration))
        if not hits:
            position = _add(position, remaining)
            remaining = (0.0, 0.0, 0.0)
            break

        hits.sort(key=lambda hit: (hit[0], hit[1]))
        local_time = hits[0][0]
        simultaneous = [hit for hit in hits if abs(hit[0] - local_time) <= 1e-8]
        contact_time = time_cursor + time_scale * local_time
        position = _add(position, _mul(remaining, local_time))
        residual = _mul(remaining, 1.0 - local_time)
        normals = []
        prior_position = position
        for _, index, normal, penetration in simultaneous:
            if not any(_dot(normal, existing) > 1.0 - 1e-8 for existing in normals):
                normals.append(normal)
            position = _add(position, _mul(normal, penetration + epsilon))
            point = _sub(position, _mul(normal, sweep_radius))
            contacts.append(Contact(contact_time, position, point, normal, index, penetration))

        # Several deterministic passes project against the active contact cone.
        for _pass in range(4):
            for normal in normals:
                inward = _dot(residual, normal)
                if inward < 0.0:
                    residual = _sub(residual, _mul(normal, inward))

        no_position_progress = _length(_sub(position, prior_position)) <= _EPS
        prior_motion_length = _length(remaining)
        residual_motion_length = _length(residual)
        if prior_motion_length > _EPS and residual_motion_length <= _EPS:
            motion_blocked_by_contact = True
        no_motion_progress = _length(_sub(residual, remaining)) <= _EPS
        if local_time <= _EPS and no_position_progress and no_motion_progress:
            remaining = residual
            unresolved = True
            break

        time_cursor, time_scale = contact_time, time_scale * (1.0 - local_time)
        remaining = residual
    else:
        exhausted = _length(remaining) > _EPS

    if exhausted:
        unresolved = True
    for obstacle in obstacles:
        if _penetrates(position, sweep_radius, obstacle):
            unresolved = True

    residual_length = _length(remaining)
    slide = _mul(remaining, 1.0 / residual_length) if residual_length > _EPS else (0.0, 0.0, 0.0)
    requested_length = _length(requested)
    blocked = motion_blocked_by_contact and not unresolved
    return SweepResult(position, remaining, slide, tuple(contacts), unresolved, blocked)
