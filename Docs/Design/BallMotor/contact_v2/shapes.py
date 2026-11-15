"""Three shape layers for ball contact v2; standard-library reference only.

Companion to ``contact_v2.py`` and to ``CONTACT_MODEL_V2.md`` §4A.  The original
game separates three models (US7402104B2): the display model ``Mp`` (a low-poly
"clutter" body, 21 size tiers in the REROLL assets), the collision model ``Mh``
(the bounding box of the equal-volume sphere, used for collision and pickup), and
the rolling model ``Mr`` (16 fixed pins + 4 optional pins whose tips are the
ground points).  Our v2 keeps three *explicit* objects with separate names,
data and consumers:

* ``VisualShape``  — core mesh + attachment mount; never physics.
* ``RollingShape`` — core sphere + needle support lengths; the contact solver's
  shape.  Needles are ball-local, so they rotate with the visual roll.
* ``PickupShape``  — the stick/trigger volume; larger than the rolling core and
  never a blocker.

Numbers are OUR design/provisional.  The original only anchors the *ordering*
(display body, equal-volume collision box, pin rolling) and the estimate
``visual core ~= 0.95 r_phys`` (Docs/Design research, /tmp/orig_physics §10.3).
"""
from dataclasses import dataclass, field
import math
from typing import List, Optional, Sequence, Tuple

Vec3 = Tuple[float, float, float]

# OUR design/provisional.
VISUAL_CORE_TO_PHYSICAL = 0.95     # /tmp/orig_physics §10.3: 0.9-1.0, best 0.95
NEEDLE_PROTRUSION_PER_RADIUS = 0.35
NEEDLE_PROTRUSION_WORLD_M = 0.30
NEEDLE_TIP_RADIUS_M = 0.01
NEEDLE_DIRECTION_COUNT = 16        # patent: 16 fixed + 4 optional
OPTIONAL_NEEDLE_COUNT = 4
COMPACT_NEEDLE_CAP = 8             # only the 8 longest protrusions enter contacts
NEEDLE_STEEPER_GRIP = 0.5          # k_grip: a half-radius protrusion halves v_req


def _dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _length(a: Vec3) -> float:
    return math.sqrt(_dot(a, a))


def _unit(a: Vec3, name: str = "vector") -> Vec3:
    length = _length(a)
    if not math.isfinite(length) or length < 1e-12:
        raise ValueError(name + " is degenerate")
    return (a[0] / length, a[1] / length, a[2] / length)


def _clamp(value: float, low: float, high: float) -> float:
    return low if value < low else (high if value > high else value)


def visual_core_radius(physical_radius: float,
                       scale: float = VISUAL_CORE_TO_PHYSICAL) -> float:
    """The mesh radius: slightly inside the physical sphere (original estimate)."""
    if physical_radius <= 0.0:
        raise ValueError("physical_radius must be positive")
    return physical_radius * scale


def needle_protrusion_cap(physical_radius: float) -> float:
    """Physics clamp on how far a pin may stick out (matches the proxy cap)."""
    return min(NEEDLE_PROTRUSION_PER_RADIUS * physical_radius,
               NEEDLE_PROTRUSION_WORLD_M)


def pickup_box_reach(direction: Vec3, physical_radius: float) -> float:
    """Support function of the cube that bounds the equal-volume sphere.

    The patent's collision/pickup model is the *bounding box* of the equivalent
    sphere, so its reach along a direction is ``r * (|x|+|y|+|z|)``: 1.0 r on an
    axis, 1.41 r on a face diagonal, 1.73 r at a corner.  Direction dependent by
    construction, no extra constant.
    """
    unit = _unit(direction, "direction")
    return physical_radius * (abs(unit[0]) + abs(unit[1]) + abs(unit[2]))


# --------------------------------------------------------------- rotations ---
Mat3 = Tuple[Vec3, Vec3, Vec3]  # rows; identity is ((1,0,0),(0,1,0),(0,0,1))


def mat3_identity() -> Mat3:
    return ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))


def mat3_from_axis_angle(axis: Vec3, angle_rad: float) -> Mat3:
    """Rodrigues rotation; keeps the reference model dependency-free."""
    x, y, z = _unit(axis, "axis")
    c = math.cos(angle_rad)
    s = math.sin(angle_rad)
    t = 1.0 - c
    return (
        (t * x * x + c, t * x * y - s * z, t * x * z + s * y),
        (t * x * y + s * z, t * y * y + c, t * y * z - s * x),
        (t * x * z - s * y, t * y * z + s * x, t * z * z + c),
    )


def mat3_apply(matrix: Mat3, vector: Vec3) -> Vec3:
    return (_dot(matrix[0], vector), _dot(matrix[1], vector), _dot(matrix[2], vector))


# ------------------------------------------------------------- rolling shape ---
@dataclass
class RollingShape:
    """Core sphere plus ball-local needle support lengths.

    ``directions`` are unit, ball-local, fixed (the patent's 16 fixed pins).  A
    support length is ``>= core_radius``; anything above the core is a protruding
    object.  ``optional_count`` marks the 4 extra pins as separate in reports.
    """

    core_radius: float
    directions: List[Vec3] = field(default_factory=list)
    supports: List[float] = field(default_factory=list)
    optional_count: int = OPTIONAL_NEEDLE_COUNT
    tip_radius: float = NEEDLE_TIP_RADIUS_M

    def __post_init__(self) -> None:
        if self.core_radius <= 0.0:
            raise ValueError("core_radius must be positive")
        if len(self.directions) != len(self.supports):
            raise ValueError("directions and supports must have the same length")
        if len(self.directions) not in (NEEDLE_DIRECTION_COUNT,
                                        NEEDLE_DIRECTION_COUNT + OPTIONAL_NEEDLE_COUNT):
            raise ValueError("needle count must be 16 or 20")
        cap = needle_protrusion_cap(self.core_radius)
        self.supports = [_clamp(float(value), self.core_radius, self.core_radius + cap)
                         for value in self.supports]

    def protruding(self) -> List[int]:
        """Indices with a real protrusion, longest first, capped for contacts."""
        indices = [i for i, value in enumerate(self.supports)
                   if value > self.core_radius + 0.001]
        indices.sort(key=lambda i: (-self.supports[i], i))
        return indices[:COMPACT_NEEDLE_CAP]

    def tip_offsets_world(self, rotation: Mat3) -> List[Vec3]:
        """Tip offsets from the ball centre, world space, only protruding pins."""
        return [mat3_apply(rotation, tuple(component * self.supports[i] for component in self.directions[i]))
                for i in self.protruding()]

    def reach_along(self, direction: Vec3) -> float:
        """How far the rolling shape reaches along ``direction`` (world unit)."""
        unit = _unit(direction, "direction")
        best = self.core_radius
        for index, local in enumerate(self.directions):
            best = max(best, self.supports[index] * _dot(local, unit))
        return best


def needle_tip_contact(obb_center: Vec3, axis_x: Vec3, axis_y: Vec3, axis_z: Vec3,
                       half_extents: Vec3, tip: Vec3, tip_radius: float
                       ) -> Optional[Tuple[Vec3, float, Vec3]]:
    """Point-vs-OBB contact for one needle tip (the reference model's form).

    Production C# must sweep the tip from its previous position to this one
    (segment vs OBB) because the pin also moves by ``omega x r``; this point
    test is the static half of that and is what the model's tests exercise.
    """
    delta = (tip[0] - obb_center[0], tip[1] - obb_center[1], tip[2] - obb_center[2])
    local = (_dot(delta, axis_x), _dot(delta, axis_y), _dot(delta, axis_z))
    axes = (axis_x, axis_y, axis_z)
    clamped = [_clamp(local[i], -half_extents[i], half_extents[i]) for i in range(3)]
    outside = 0
    for i in range(3):
        if abs(local[i] - clamped[i]) > 1e-9:
            outside += 1
    offset = (local[0] - clamped[0], local[1] - clamped[1], local[2] - clamped[2])
    distance = _length(offset)
    if distance > tip_radius:
        return None
    if distance > 1e-9:
        normal_local = (offset[0] / distance, offset[1] / distance, offset[2] / distance)
        normal = (normal_local[0] * axes[0][0] + normal_local[1] * axes[1][0] + normal_local[2] * axes[2][0],
                  normal_local[0] * axes[0][1] + normal_local[1] * axes[1][1] + normal_local[2] * axes[2][1],
                  normal_local[0] * axes[0][2] + normal_local[1] * axes[1][2] + normal_local[2] * axes[2][2])
        point = (obb_center[0] + clamped[0] * axis_x[0] + clamped[1] * axis_y[0] + clamped[2] * axis_z[0],
                 obb_center[1] + clamped[0] * axis_x[1] + clamped[1] * axis_y[1] + clamped[2] * axis_z[1],
                 obb_center[2] + clamped[0] * axis_x[2] + clamped[1] * axis_y[2] + clamped[2] * axis_z[2])
        return normal, tip_radius - distance, point
    # Tip inside the box: push out through the nearest face.
    best_axis = 0
    best_gap = half_extents[0] - abs(local[0])
    for i in (1, 2):
        gap = half_extents[i] - abs(local[i])
        if gap < best_gap:
            best_gap = gap
            best_axis = i
    sign = -1.0 if local[best_axis] < 0.0 else 1.0
    normal = tuple(sign * axes[best_axis][i] for i in range(3))
    point = (tip[0] + normal[0] * best_gap,
             tip[1] + normal[1] * best_gap,
             tip[2] + normal[2] * best_gap)
    return normal, tip_radius + best_gap, point


def _closest_point_on_obb(center: Vec3, axis_x: Vec3, axis_y: Vec3, axis_z: Vec3,
                          half_extents: Vec3, point: Vec3) -> Tuple[float, Vec3, Vec3]:
    """(distance, normal, closest point) of an exact sphere-vs-box query.

    ``distance`` is negative when the point is inside the solid: minus the gap to
    the nearest face, so the contact depth is ``tip_radius + gap``.
    """
    delta = (point[0] - center[0], point[1] - center[1], point[2] - center[2])
    local = (_dot(delta, axis_x), _dot(delta, axis_y), _dot(delta, axis_z))
    axes = (axis_x, axis_y, axis_z)
    clamped = [_clamp(local[i], -half_extents[i], half_extents[i]) for i in range(3)]
    offset = (local[0] - clamped[0], local[1] - clamped[1], local[2] - clamped[2])
    distance = _length(offset)
    if distance > 1e-9:
        normal_local = (offset[0] / distance, offset[1] / distance, offset[2] / distance)
        normal = (normal_local[0] * axes[0][0] + normal_local[1] * axes[1][0] + normal_local[2] * axes[2][0],
                  normal_local[0] * axes[0][1] + normal_local[1] * axes[1][1] + normal_local[2] * axes[2][1],
                  normal_local[0] * axes[0][2] + normal_local[1] * axes[1][2] + normal_local[2] * axes[2][2])
        closest = (center[0] + clamped[0] * axis_x[0] + clamped[1] * axis_y[0] + clamped[2] * axis_z[0],
                   center[1] + clamped[0] * axis_x[1] + clamped[1] * axis_y[1] + clamped[2] * axis_z[1],
                   center[2] + clamped[0] * axis_x[2] + clamped[1] * axis_y[2] + clamped[2] * axis_z[2])
        return distance, normal, closest
    best_axis = 0
    best_gap = half_extents[0] - abs(local[0])
    for i in (1, 2):
        gap = half_extents[i] - abs(local[i])
        if gap < best_gap:
            best_gap = gap
            best_axis = i
    sign = -1.0 if local[best_axis] < 0.0 else 1.0
    normal = tuple(sign * axes[best_axis][i] for i in range(3))
    # Inside: walking along the outward face normal by the gap lands on that face.
    closest = (point[0] + normal[0] * best_gap, point[1] + normal[1] * best_gap,
               point[2] + normal[2] * best_gap)
    return -best_gap, normal, closest


def _depth(tip_radius: float, signed_distance: float) -> float:
    """Contact depth of a tip: r - signed distance, never negative."""
    depth = tip_radius - signed_distance
    return 0.0 if depth < 0.0 else depth


def _clip_slab(origin: float, direction: float, half: float,
               enter: float, leave: float) -> Optional[Tuple[float, float]]:
    """One-axis clip of a segment against the inflated slab ``|x| <= half``."""
    if abs(direction) < 1e-9:
        if abs(origin) > half:
            return None
        return enter, leave
    t1 = (-half - origin) / direction
    t2 = (half - origin) / direction
    low = t1 if t1 < t2 else t2
    high = t2 if t1 < t2 else t1
    if low > enter:
        enter = low
    if high < leave:
        leave = high
    if enter > leave:
        return None
    return enter, leave


def needle_segment_contact(obb_center: Vec3, axis_x: Vec3, axis_y: Vec3, axis_z: Vec3,
                           half_extents: Vec3, tip_previous: Vec3, tip_now: Vec3,
                           tip_radius: float
                           ) -> Optional[Tuple[Vec3, float, Vec3, float]]:
    """Swept tip vs OBB: (normal, depth, point, entry fraction), or None.

    A pin tip moves by ``v_center + omega x r``, up to ~16 m/s at 4 m/s of rolling,
    so a 20 ms substep can sweep 0.32 m - farther than a thin wall.  A point test
    tunnels through that; this function sweeps the segment instead.

    The sweep is exact in two stages.  A slab clip against the box inflated by
    ``tip_radius`` gives the entry/exit interval, which is a conservative superset
    of the true swept volume (box (+) sphere is the box with rounded edges and
    corners).  Inside that interval the exact closest-point distance is sampled
    (entry + 4 interior points) and the first crossing is bisected 12 times, so a
    face contact is found at the entry itself (one query) and an edge/corner pass
    is located to 1/4096 of the interval (~0.1 mm of a 0.32 m sweep).  A graze that only admits an interval
    shorter than a quarter of the entry chord can be missed; that is a
    sub-millimetre corner contact on a 1 cm tip, and the C# port documents the
    same caveat.
    """
    if tip_radius < 0.0:
        raise ValueError("tip_radius must be non-negative")
    delta_prev = (tip_previous[0] - obb_center[0], tip_previous[1] - obb_center[1],
                  tip_previous[2] - obb_center[2])
    delta_now = (tip_now[0] - obb_center[0], tip_now[1] - obb_center[1],
                 tip_now[2] - obb_center[2])
    local_prev = (_dot(delta_prev, axis_x), _dot(delta_prev, axis_y), _dot(delta_prev, axis_z))
    local_now = (_dot(delta_now, axis_x), _dot(delta_now, axis_y), _dot(delta_now, axis_z))
    direction = (local_now[0] - local_prev[0], local_now[1] - local_prev[1],
                 local_now[2] - local_prev[2])

    enter, leave = 0.0, 1.0
    for i in range(3):
        clipped = _clip_slab(local_prev[i], direction[i], half_extents[i] + tip_radius,
                             enter, leave)
        if clipped is None:
            return None
        enter, leave = clipped
    if enter > leave:
        return None

    # A point sitting exactly on the inflated face is a contact with depth 0; the
    # 1e-5 m margin absorbs the float error of the entry parameter and is 1/1000 of
    # the tip radius, so it cannot create a contact that is not there.
    margin = 1e-5
    span = leave - enter
    previous_fraction = enter
    for sample in range(5):
        fraction = enter if sample == 0 else enter + span * (sample * 0.25)
        point = (tip_previous[0] + (tip_now[0] - tip_previous[0]) * fraction,
                 tip_previous[1] + (tip_now[1] - tip_previous[1]) * fraction,
                 tip_previous[2] + (tip_now[2] - tip_previous[2]) * fraction)
        distance, normal, closest = _closest_point_on_obb(
            obb_center, axis_x, axis_y, axis_z, half_extents, point)
        if distance <= tip_radius + margin:
            if sample == 0:
                return normal, _depth(tip_radius, distance), closest, fraction
            low, high = previous_fraction, fraction
            for _ in range(12):
                middle = 0.5 * (low + high)
                middle_point = (tip_previous[0] + (tip_now[0] - tip_previous[0]) * middle,
                                tip_previous[1] + (tip_now[1] - tip_previous[1]) * middle,
                                tip_previous[2] + (tip_now[2] - tip_previous[2]) * middle)
                middle_distance, middle_normal, middle_closest = _closest_point_on_obb(
                    obb_center, axis_x, axis_y, axis_z, half_extents, middle_point)
                if middle_distance <= tip_radius + margin:
                    high = middle
                    distance, normal, closest = middle_distance, middle_normal, middle_closest
                else:
                    low = middle
            return normal, _depth(tip_radius, distance), closest, high
        previous_fraction = fraction
    return None


def needle_segment_plane_contact(plane_normal: Vec3, plane_offset: float,
                                 tip_previous: Vec3, tip_now: Vec3, tip_radius: float
                                 ) -> Optional[Tuple[Vec3, float, Vec3, float]]:
    """Swept tip vs a one-sided plane: (normal, depth, point, entry fraction)."""
    if tip_radius < 0.0:
        raise ValueError("tip_radius must be non-negative")
    unit = _unit(plane_normal, "plane_normal")
    start = _dot(unit, tip_previous) - plane_offset
    end = _dot(unit, tip_now) - plane_offset
    if start <= tip_radius + 1e-5:
        fraction = 0.0
    elif end <= tip_radius:
        denominator = start - end
        if abs(denominator) < 1e-12:
            return None
        fraction = (start - tip_radius) / denominator
        if fraction < 0.0:
            fraction = 0.0
        if fraction > 1.0:
            fraction = 1.0
    else:
        return None
    entry = (tip_previous[0] + (tip_now[0] - tip_previous[0]) * fraction,
             tip_previous[1] + (tip_now[1] - tip_previous[1]) * fraction,
             tip_previous[2] + (tip_now[2] - tip_previous[2]) * fraction)
    signed = _dot(unit, entry) - plane_offset
    point = (entry[0] - unit[0] * signed, entry[1] - unit[1] * signed,
             entry[2] - unit[2] * signed)
    return unit, _depth(tip_radius, signed), point, fraction


# -------------------------------------------------------------- pickup shape ---
def pickup_reach(direction: Vec3, physical_radius: float,
                 shape: Optional[RollingShape] = None) -> float:
    """The pickup/trigger reach: the box support function, unioned with the pins.

    The union is what keeps the user-facing ordering "pickup >= rolling" true
    even when a long object protrudes further than the box corner.
    """
    reach = pickup_box_reach(direction, physical_radius)
    if shape is not None:
        reach = max(reach, shape.reach_along(direction))
    return reach


def rolling_reach(direction: Vec3, shape: RollingShape) -> float:
    return shape.reach_along(direction)


def grip_from_protrusion(protrusion_m: float, core_radius: float,
                         k_grip: float = NEEDLE_STEEPER_GRIP) -> float:
    """0..1 grip: a protrusion of half a radius already gives 1.0."""
    if core_radius <= 0.0:
        return 0.0
    return _clamp(protrusion_m / (0.5 * core_radius), 0.0, 1.0) * k_grip


def required_speed_with_grip(theta_deg: float, protrusion_m: float,
                             core_radius: float, v_min: float = 1.5,
                             alpha: float = 1.0, k_grip: float = NEEDLE_STEEPER_GRIP) -> float:
    """The climb speed gate with a needle grip discount."""
    base = v_min * (math.sin(math.radians(theta_deg)) ** alpha)
    return base * (1.0 - grip_from_protrusion(protrusion_m, core_radius, k_grip))
