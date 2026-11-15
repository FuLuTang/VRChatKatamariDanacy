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
