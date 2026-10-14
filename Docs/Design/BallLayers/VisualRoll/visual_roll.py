"""T53 host-only standard-library reference math; not Unity/Udon code.

The host convention here is an explicitly declared right-handed vector/quaternion
algebra. The Unity adapter must pass the independent axis probe before use.
"""
from dataclasses import dataclass, field
import math
from typing import Optional, Tuple

Vector3 = Tuple[float, float, float]
Quaternion = Tuple[float, float, float, float]  # x, y, z, w
_UNIT_Q: Quaternion = (0.0, 0.0, 0.0, 1.0)
_UNIT_SCALE: Vector3 = (1.0, 1.0, 1.0)
_EPS = 1e-7  # host/candidate C# near-zero tangent cutoff in world metres
_MIN_Q_NORM = 1e-12


def _finite_vector(value, name: str) -> Vector3:
    if not isinstance(value, (tuple, list)) or len(value) != 3:
        raise ValueError(f"{name} must be a 3-vector")
    out = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            raise ValueError(f"{name} must contain real numbers")
        converted = float(component)
        if not math.isfinite(converted):
            raise ValueError(f"{name} must be finite")
        out.append(converted)
    return (out[0], out[1], out[2])


def _finite_scalar(value, name: str, *, positive: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a real number")
    converted = float(value)
    if not math.isfinite(converted) or (positive and converted <= 0.0):
        raise ValueError(f"{name} must be finite" + (" and positive" if positive else ""))
    return converted


def _length(vector: Vector3, name: str) -> float:
    scale = max(abs(component) for component in vector)
    if scale == 0.0:
        return 0.0
    scaled_length = math.sqrt(sum((component / scale) ** 2 for component in vector))
    length = scale * scaled_length
    if not math.isfinite(length):
        raise OverflowError(f"{name} magnitude overflow")
    return length


def _unit_vector(vector: Vector3, name: str) -> Vector3:
    scale = max(abs(component) for component in vector)
    if scale <= 0.0:
        raise ValueError(f"{name} must be non-zero")
    scaled = (vector[0] / scale, vector[1] / scale, vector[2] / scale)
    norm = math.sqrt(sum(component * component for component in scaled))
    if not math.isfinite(norm) or norm <= 0.0:
        raise ValueError(f"{name} must be non-zero and finite")
    return (scaled[0] / norm, scaled[1] / norm, scaled[2] / norm)


def _dot(a: Vector3, b: Vector3, name: str = "dot product") -> float:
    try:
        value = math.fsum((a[0] * b[0], a[1] * b[1], a[2] * b[2]))
    except (OverflowError, ValueError) as exc:
        raise OverflowError(f"{name} overflow") from exc
    if not math.isfinite(value):
        raise OverflowError(f"{name} overflow")
    return value


def _cross_rhs(a: Vector3, b: Vector3) -> Vector3:
    """Conventional right-handed cross product for this host reference only."""
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _normalize_quaternion(value) -> Quaternion:
    if not isinstance(value, (tuple, list)) or len(value) != 4:
        raise ValueError("local_rotation must be an xyzw quaternion")
    parts = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            raise ValueError("local_rotation must contain real numbers")
        converted = float(component)
        if not math.isfinite(converted):
            raise ValueError("local_rotation must be finite")
        parts.append(converted)
    scale = max(abs(component) for component in parts)
    if scale == 0.0:
        raise ValueError("local_rotation must have non-zero norm")
    scaled = tuple(component / scale for component in parts)
    scaled_norm = math.sqrt(sum(component * component for component in scaled))
    if scale <= _MIN_Q_NORM / scaled_norm:
        raise ValueError("local_rotation norm is below the supported threshold")
    return (scaled[0] / scaled_norm, scaled[1] / scaled_norm,
            scaled[2] / scaled_norm, scaled[3] / scaled_norm)


def _multiply(a: Quaternion, b: Quaternion) -> Quaternion:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    result = (aw * bx + ax * bw + ay * bz - az * by,
              aw * by - ax * bz + ay * bw + az * bx,
              aw * bz + ax * by - ay * bx + az * bw,
              aw * bw - ax * bx - ay * by - az * bz)
    if not all(math.isfinite(component) for component in result):
        raise OverflowError("quaternion multiplication overflow")
    return result


@dataclass(frozen=True)
class VisualRollState:
    """Visual-only rotation; gameplay heading and scale are not writable here."""
    local_rotation: Quaternion = _UNIT_Q
    local_scale: Vector3 = field(default=_UNIT_SCALE, init=False)

    def __post_init__(self) -> None:
        object.__setattr__(self, "local_rotation", _normalize_quaternion(self.local_rotation))

    def advance(self, displacement: Vector3, ground_normal: Vector3, radius: float,
                *, discontinuous: bool = False,
                max_displacement_m: Optional[float] = None) -> "VisualRollState":
        """Apply one confirmed displacement in the caller's parent-local frame.

        Only the component tangent to the support plane rolls. ``discontinuous``
        is an explicit teleport/respawn marker; an optional caller-set maximum
        displacement is a second discontinuity guard, not a product constant.
        A skipped step preserves rotation; callers must start the next segment
        from the new position and provide its displacement, never the teleport
        chord.
        """
        delta_p = _finite_vector(displacement, "displacement")
        normal = _unit_vector(_finite_vector(ground_normal, "ground_normal"), "ground_normal")
        roll_radius = _finite_scalar(radius, "radius", positive=True)
        if not isinstance(discontinuous, bool):
            raise ValueError("discontinuous must be a bool")
        threshold = None
        if max_displacement_m is not None:
            threshold = _finite_scalar(max_displacement_m, "max_displacement_m", positive=True)

        # Revalidate in case an external caller bypassed frozen-dataclass rules.
        current = _normalize_quaternion(self.local_rotation)
        total_distance = _length(delta_p, "displacement")
        if discontinuous or (threshold is not None and total_distance > threshold):
            return self

        normal_distance = _dot(delta_p, normal, "normal projection")
        tangent = (delta_p[0] - normal[0] * normal_distance,
                   delta_p[1] - normal[1] * normal_distance,
                   delta_p[2] - normal[2] * normal_distance)
        if not all(math.isfinite(component) for component in tangent):
            raise OverflowError("tangent projection overflow")
        distance = _length(tangent, "tangent displacement")
        if distance <= _EPS:
            return self

        # From v + omega x rho = 0, rho = -r*n, so omega is parallel
        # to n x v in the declared right-handed host convention. This sign is
        # tested independently from contact-point kinematics in the unit tests.
        axis = _unit_vector(_cross_rhs(normal, tangent), "rolling axis")
        angle = distance / roll_radius
        if not math.isfinite(angle):
            raise OverflowError("rolling angle overflow")
        half = math.remainder(angle * 0.5, math.tau)
        sine = math.sin(half)
        delta_q: Quaternion = (axis[0] * sine, axis[1] * sine,
                               axis[2] * sine, math.cos(half))
        # The increment is expressed in the same parent-local frame and is
        # left-composed, matching q*v*q^-1 in this host convention.
        rotation = _normalize_quaternion(_multiply(delta_q, current))
        return VisualRollState(rotation)

    @staticmethod
    def identity() -> "VisualRollState":
        """Return an explicit pose reset; ordinary invalid updates never reset."""
        return VisualRollState(_UNIT_Q)
