"""Ball-contact v2 reference model; standard-library host reference only.

DRAFT reference for ``Docs/Design/BallMotor/CONTACT_MODEL_V2.md``.  It mirrors,
name for name and in the same order, the C# that the design proposes in a new
``Runtime/BallLayers/ContactV2/ContactSolveMath.cs`` plus the small state machine
in the motor.  Nothing here is a Unity, UdonSharp or world-scale claim: the
purpose is to pin the arithmetic (closest point, contact classification, the
climb equations, the substepping bound, the near window and the curved-ramp
tessellation) so the C# can be reviewed against running code.

Runs with ``python3 -m unittest discover -s Docs/tests``.

Conventions
-----------
* One primitive for every solid: an oriented box (OBB).  A static box, a ramp
  slab, an AABB prop and a blocking prop are all OBBs; props simply use the
  world axes.  Planes stay half-spaces (``dot(n, x) >= offset`` is free space).
* Contact normal ``n`` points from the solid towards the ball centre.
* ``theta`` = angle between ``n`` and +Y: 0 deg floor, 90 deg vertical wall.
* A contact affects the ball only by (a) position push-out along ``n`` and
  (b) a velocity response that depends on its class.  There is no per-surface
  special case anywhere in this file.
"""
from dataclasses import dataclass, field
from typing import List, Optional, Sequence, Tuple
import math

Vec3 = Tuple[float, float, float]

EPS = 1e-9

# Feature of the closest point on a box (diagnostics and tests only).
FEATURE_INSIDE = 0
FEATURE_FACE = 1
FEATURE_EDGE = 2
FEATURE_CORNER = 3

# Solid role.  Both are the same primitive; the role only feeds diagnostics.
ROLE_BOX = 0
ROLE_SLAB = 1
ROLE_PROP = 2
ROLE_BLOCKING = 3

# Contact class, derived from theta.  It selects the velocity response.
CLASS_NONE = 0
CLASS_RIDE = 1   # the ball rolls on it (support); no speed is removed
CLASS_CLIMB = 2  # the ball pushes against it; inward speed is removed, ascent injected

# ---------------------------------------------------------------- OUR design ---
# Every number below is OUR design/provisional.  k_h is the only one anchored on
# a source: We Love Katamari's manual, "climb over walls that are about the same
# size as your katamari" (printed p.23 / PDF p.25); qualitative sequel evidence,
# not an exact first-game cap or centre-displacement measurement.
THETA_SLOPE_MAX_DEG = 60.0
SLOPE_REFILL_MAX_DEG = 2.0
SLOPE_STAMINA_MAX = 100.0
SLOPE_DRAIN_SATURATION_DEG = 66.6
SLOPE_DRAIN_PER_SECOND = 22.8
SLOPE_REFERENCE_DIAMETER_M = 0.30
SLOPE_FATIGUE_PER_SECOND = 0.15
SLOPE_MINIMUM_SPEED_FACTOR = 0.35
SLOPE_REST_SECONDS = 0.3
SLOPE_REST_SPEED_MPS = 0.05
DASH_SPEED_SCALE = 1.6
THETA_RIDE_DEG = 45.0          # <= this: rideable surface (v1 ramp feel)
THETA_RIDE_TOLERANCE_DEG = 0.5  # equality discipline: 45.5 deg is still a ride
CLIMB_STEEPER_MARGIN_DEG = 15.0  # a climb must be clearly steeper than the support
KAPPA_SUBSTEP = 0.5            # max travel per substep, in radii
MAX_SUBSTEPS = 8               # cost cap; the fall displacement is clamped to it
SOLVER_ITERATIONS = 3
CONTACT_SLOP_M = 0.0005
CONTACT_SKIN_M = 0.005         # keep a resting contact registered; never pushes
P_MIN = 0.2                    # push-in below this never climbs
V_MIN_GATE = 1.5               # m/s, required drive at 90 deg
GATE_ALPHA = 1.0               # v_req = v_min * sin(theta)^alpha
K_INTENT = 0.5                 # the motion layer's target speed counts half
CLIMB_TIME_S = 2.8125         # time to climb 98% of H_cap at full push (OUR, PROVISIONAL; owner 2026-11-17 +60% speed, was 4.5)
CLIMB_TIME_NORM = 1.718        # sqrt-fade normalisation: t(0.98H) = 2H/v0 * 0.859
CLIMB_V_GATE_REF = 2.0         # the gate speed that gives rate factor 1.0
CLIMB_RATE_MIN = 0.75
CLIMB_RATE_MAX = 1.5
M0 = 0.15                      # creep floor shape: m(theta) = cos + M0*sin
K_H = 1.5                      # H_cap = k_h * (2r + lambda*L); OUR PROVISIONAL
                               # (no original-game cap is published, see
                               # ORIGINAL_CLIMB_OBSERVATIONS.md; owner 2026-11-16)
LAMBDA_PROTRUSION = 1.0
CLIMB_RATE_LIMIT = 3.0          # m/s^2, v_y approaches the target no faster
CLIMB_COOLDOWN_S = 0.35
CLIMB_CAP_MARGIN_M = 0.02
LEDGE_DRIVE_PER_RADIUS = 10.0  # 1/s: v_exit = 10 * r (m/s), NOT 10 / r
LEDGE_DRIVE_MIN_MPS = 0.5
V_SLIDE_MAX = 1.0              # m/s, slow slide-back while a wall is touched
GRAVITY = 9.81
MIN_UPWARD_NORMAL_Y = 0.05      # n.y < -this is a ceiling (CLASS_NONE)


# ------------------------------------------------------------------ vector ---
def _dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _sub(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _add(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _mul(a: Vec3, s: float) -> Vec3:
    return (a[0] * s, a[1] * s, a[2] * s)


def _length(a: Vec3) -> float:
    return math.sqrt(_dot(a, a))


def _unit(a: Vec3, name: str = "vector") -> Vec3:
    length = _length(a)
    if not math.isfinite(length) or length < EPS:
        raise ValueError(name + " is degenerate")
    return (a[0] / length, a[1] / length, a[2] / length)


def _clamp(value: float, low: float, high: float) -> float:
    if value < low:
        return low
    if value > high:
        return high
    return value


def _finite(value: float, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(name + " must be a number")
    if not math.isfinite(value):
        raise ValueError(name + " must be finite")
    return float(value)


# -------------------------------------------------------------------- data ---
@dataclass(frozen=True)
class Obb:
    """One oriented solid.  Axes are orthonormal, half extents are local."""

    center: Vec3
    axis_x: Vec3
    axis_y: Vec3
    axis_z: Vec3
    half_extents: Vec3
    surface_id: int
    role: int = ROLE_BOX

    def bounding_radius(self) -> float:
        return _length(self.half_extents)


@dataclass(frozen=True)
class Plane:
    """One-sided half-space; ``dot(normal, point) >= offset`` is free space."""

    normal: Vec3
    offset: float = 0.0


@dataclass(frozen=True)
class Contact:
    surface_id: int
    role: int
    normal: Vec3
    point: Vec3
    depth: float
    feature: int
    angle_deg: float
    cls: int


def _validate_obb(obb: Obb) -> None:
    for name, axis in (("axis_x", obb.axis_x), ("axis_y", obb.axis_y), ("axis_z", obb.axis_z)):
        if abs(_length(axis) - 1.0) > 1e-6:
            raise ValueError("obb." + name + " is not unit length")
    if abs(_dot(obb.axis_x, obb.axis_y)) > 1e-6 or abs(_dot(obb.axis_x, obb.axis_z)) > 1e-6:
        raise ValueError("obb axes are not orthogonal")
    if abs(_dot(obb.axis_y, obb.axis_z)) > 1e-6:
        raise ValueError("obb axes are not orthogonal")
    for i in range(3):
        if _finite(obb.half_extents[i], "obb.half_extents") < 0.0:
            raise ValueError("obb half extents must be nonnegative")


def to_local(obb: Obb, point: Vec3) -> Vec3:
    delta = _sub(point, obb.center)
    return (_dot(delta, obb.axis_x), _dot(delta, obb.axis_y), _dot(delta, obb.axis_z))


def to_world(obb: Obb, local: Vec3) -> Vec3:
    return _add(obb.center, _add(_mul(obb.axis_x, local[0]),
                                _add(_mul(obb.axis_y, local[1]), _mul(obb.axis_z, local[2]))))


def closest_point_on_obb(obb: Obb, point: Vec3) -> Tuple[Vec3, float, Vec3, int]:
    """Exact closest point, distance, outward normal and feature of an OBB.

    This is the rounded box: the closest point is the clamped local point, so
    faces, edges and corners all have their true normal.  The old motor inflated
    the AABB by the radius, which is the source of the 45 deg seam deadlock and
    of the "a 0.6 m deck is a wall for a 2.2 m ball" failure.
    """
    _validate_obb(obb)
    local = to_local(obb, point)
    axes = (obb.axis_x, obb.axis_y, obb.axis_z)
    clamped = [0.0, 0.0, 0.0]
    outside = 0
    for i in range(3):
        half = obb.half_extents[i]
        clamped[i] = _clamp(local[i], -half, half)
        if abs(local[i] - clamped[i]) > EPS:
            outside += 1
    delta = (local[0] - clamped[0], local[1] - clamped[1], local[2] - clamped[2])
    distance = _length(delta)
    if distance > EPS:
        local_normal = (delta[0] / distance, delta[1] / distance, delta[2] / distance)
        normal = _add(_mul(axes[0], local_normal[0]),
                      _add(_mul(axes[1], local_normal[1]), _mul(axes[2], local_normal[2])))
        feature = (FEATURE_FACE, FEATURE_EDGE, FEATURE_CORNER)[outside - 1]
        return to_world(obb, (clamped[0], clamped[1], clamped[2])), distance, normal, feature

    # Centre inside the box: push out through the nearest face.  The normal is
    # the face axis, and the penetration is measured from that face.
    best_axis = 0
    best_gap = obb.half_extents[0] - abs(local[0])
    for i in (1, 2):
        gap = obb.half_extents[i] - abs(local[i])
        if gap < best_gap:
            best_gap = gap
            best_axis = i
    sign = 1.0
    if local[best_axis] < 0.0:
        sign = -1.0
    normal = _mul(axes[best_axis], sign)
    surface_local = [local[0], local[1], local[2]]
    surface_local[best_axis] = obb.half_extents[best_axis] * sign
    return to_world(obb, (surface_local[0], surface_local[1], surface_local[2])), -best_gap, normal, FEATURE_INSIDE


def classify_contact(normal: Vec3, theta_ride_deg: float = THETA_RIDE_DEG) -> int:
    """One classification, continuous in theta; only the boundaries are numeric."""
    upward = normal[1]
    if upward < -MIN_UPWARD_NORMAL_Y:
        return CLASS_NONE
    # 0.5 deg of tolerance on the ride boundary: a 45 deg board is exactly on
    # the line, and float noise must not flip it between ride and climb.
    if upward >= math.cos(math.radians(theta_ride_deg + THETA_RIDE_TOLERANCE_DEG)):
        return CLASS_RIDE
    return CLASS_CLIMB


def contact_angle_deg(normal: Vec3) -> float:
    return math.degrees(math.acos(_clamp(normal[1], -1.0, 1.0)))


def sphere_obb_contact(obb: Obb, center: Vec3, radius: float,
                       skin: float = CONTACT_SKIN_M) -> Optional[Contact]:
    point, distance, normal, feature = closest_point_on_obb(obb, center)
    if distance > radius + skin:
        return None
    # depth is negative while the ball is resting just outside the skin radius;
    # only a positive depth (beyond CONTACT_SLOP_M) is pushed out.
    depth = radius - distance
    cls = classify_contact(normal)
    return Contact(obb.surface_id, obb.role, normal, point, depth, feature,
                   contact_angle_deg(normal), cls)


def plane_contact(plane: Plane, center: Vec3, radius: float, surface_id: int,
                  skin: float = CONTACT_SKIN_M) -> Optional[Contact]:
    normal = _unit(plane.normal, "plane.normal")
    signed = _dot(normal, center) - plane.offset
    if signed > radius + skin:
        return None
    depth = radius - signed
    cls = classify_contact(normal)
    point = _sub(center, _mul(normal, signed))
    return Contact(surface_id, ROLE_BLOCKING, normal, point, depth, FEATURE_FACE,
                   contact_angle_deg(normal), cls)


# ------------------------------------------------------------ substepping ---
def max_substeps(displacement: Vec3, radius: float,
                 kappa: float = KAPPA_SUBSTEP, cap: int = MAX_SUBSTEPS) -> int:
    """N = clamp(ceil(|d| / (kappa*r)), 1, cap).

    Sampling every kappa*r along the path cannot tunnel through a convex solid:
    dist(centre, solid) is 1-Lipschitz and zero inside, so the set where it is
    <= r is at least 2r long along any straight path that enters the solid.  A
    sample spacing <= r therefore always lands inside it; kappa = 0.5 gives a
    4x margin.  The cap is the cost bound (see the design: the fall
    displacement is clamped to cap*kappa*r).
    """
    if not math.isfinite(radius) or radius <= 0.0:
        return 1
    length = _length(displacement)
    needed = int(math.ceil(length / (kappa * radius) - 1e-9))
    return max(1, min(cap, needed))


# ------------------------------------------------------------------ climb ---
def climb_height_cap(radius: float, protrusion_m: float = 0.0,
                     k_h: float = K_H, lam: float = LAMBDA_PROTRUSION) -> float:
    """Centre-rise cap, H_cap = k_h * (2r + lambda*L).  k_h=1.5 is OUR PROVISIONAL value."""
    return k_h * (2.0 * radius + lam * max(0.0, protrusion_m))


def ledge_exit_speed(radius: float, drive_speed: float,
                     per_radius: float = LEDGE_DRIVE_PER_RADIUS,
                     minimum: float = LEDGE_DRIVE_MIN_MPS) -> float:
    """Ledge exit speed, m/s: clamp(per_radius * radius, minimum, drive_speed).

    per_radius is 10 1/s, so the expression is 10 * r (1/s * m = m/s), NOT 10 / r:
    t = r / v and drop = g t^2 / 2, so a 5 cm drop budget gives v = r * 9.9.
    """
    speed = per_radius * radius
    if speed < minimum:
        speed = minimum
    if speed > drive_speed:
        speed = drive_speed
    return speed


def required_speed(theta_deg: float, v_min: float = V_MIN_GATE,
                   alpha: float = GATE_ALPHA) -> float:
    """Continuous velocity gate; no angle whitelist."""
    return v_min * (math.sin(math.radians(theta_deg)) ** alpha)


def climb_shape(theta_deg: float, m0: float = M0) -> float:
    """m(theta) = cos(theta) + m0*sin(theta): 1 at 0 deg, m0 at 90 deg."""
    rad = math.radians(theta_deg)
    return math.cos(rad) + m0 * math.sin(rad)


def climb_fade(height_m: float, h_cap_m: float) -> float:
    """sqrt(1 - h/H_cap): speed reaches zero only at the cap."""
    if h_cap_m <= 0.0:
        return 0.0
    return math.sqrt(_clamp(1.0 - height_m / h_cap_m, 0.0, 1.0))


def climb_speed(push_in: float, v_gate: float, theta_deg: float, height_m: float,
                h_cap_m: float, climb_time_s: float = CLIMB_TIME_S) -> float:
    """Vertical climb rate, parameterised by the time to reach H_cap.

    v_up0 = 1.718 * H_cap / T_climb  (the 1.718 is the sqrt-fade normalisation)
    v_up  = p * rate(v_gate) * (m(theta)/m(90)) * v_up0 * fade(h)

    Parameterising by TIME rather than a speed keeps the feel constant across
    ball sizes: H_cap is proportional to r, so a big ball climbs faster in m/s
    but takes the same seconds to reach its cap.  OUR design, provisional.
    """
    if push_in <= 0.0 or v_gate <= 0.0 or h_cap_m <= 0.0:
        return 0.0
    rate_factor = _clamp(v_gate / CLIMB_V_GATE_REF, CLIMB_RATE_MIN, CLIMB_RATE_MAX)
    v_up0 = CLIMB_TIME_NORM * h_cap_m / climb_time_s
    return (_clamp(push_in, 0.0, 1.0) * rate_factor
            * (climb_shape(theta_deg) / climb_shape(90.0))
            * v_up0 * climb_fade(height_m, h_cap_m))


def climb_eligible(push_in: float, v_gate: float, theta_deg: float, height_m: float,
                   h_cap_m: float, cooldown_s: float) -> bool:
    return (push_in >= P_MIN and cooldown_s <= 0.0 and height_m < h_cap_m
            and v_gate >= required_speed(theta_deg))


# Slope-only formulas mirror SlopeClimbMath (all project PROVISIONAL).
def is_slope(theta):
    return THETA_RIDE_DEG + THETA_RIDE_TOLERANCE_DEG < theta <= THETA_SLOPE_MAX_DEG


def slope_drain_rate(theta, diameter, uphill_seconds, gate_speed):
    size = math.sqrt(max(diameter / SLOPE_REFERENCE_DIAMETER_M, 0.01))
    steepness = _clamp(theta / SLOPE_DRAIN_SATURATION_DEG, 0.0, 1.0)
    fatigue = 1.0 + max(0.0, uphill_seconds) * SLOPE_FATIGUE_PER_SECOND
    effort = _clamp(CLIMB_V_GATE_REF / max(gate_speed, CLIMB_V_GATE_REF),
                    1.0 / DASH_SPEED_SCALE, 1.0)
    return SLOPE_DRAIN_PER_SECOND * steepness * fatigue * effort / size


def slope_uphill_vertical_speed(push_in, gate_speed, theta, diameter, uphill_seconds):
    fatigue = max(SLOPE_MINIMUM_SPEED_FACTOR,
                  1.0 / (1.0 + max(0.0, uphill_seconds) * SLOPE_FATIGUE_PER_SECOND))
    # Wall base rate v_up0 = 0.917 * D at 90 deg (m(90)/m(90) = 1): no angle factor, no
    # height fade -- a steeper slope is not faster. theta only picks the along-slope
    # projection in slope_motion.
    return climb_speed(push_in, gate_speed, 90.0, 0.0, K_H * diameter) * fatigue


def slope_motion(theta, diameter, push_in, gate_speed, dt, stamina, uphill_seconds,
                 rest_seconds, sliding, along, rest_hold=SLOPE_REST_SECONDS,
                 rest_speed=SLOPE_REST_SPEED_MPS):
    pushing = push_in >= P_MIN and gate_speed >= required_speed(theta)
    sin_theta = math.sin(math.radians(theta))
    if pushing and stamina > 0.0:
        drain = slope_drain_rate(theta, diameter, uphill_seconds + dt * 0.5, gate_speed) * dt
        uphill_seconds += dt
        stamina = max(0.0, stamina - drain)
        rest_seconds, sliding = 0.0, False
        if stamina > 0.0:
            target = slope_uphill_vertical_speed(push_in, gate_speed, theta, diameter,
                                                  uphill_seconds) / max(sin_theta, 0.2)
            rate = CLIMB_RATE_LIMIT * dt
            along = min(along + rate, target) if target > along else max(along - rate, target)
            return stamina, uphill_seconds, rest_seconds, sliding, along
        along = 0.0
    if along > 0.0:
        along = max(0.0, along - CLIMB_RATE_LIMIT * dt)
    # RampSlideMath.TryStepRestLatch with effective drive=0 and onSlope=True.
    rest_seconds = rest_seconds + dt if abs(along) < rest_speed else 0.0
    sliding = sliding or rest_seconds >= rest_hold
    if sliding:
        along = max(along - GRAVITY * sin_theta * dt, -V_SLIDE_MAX)
    return stamina, uphill_seconds, rest_seconds, sliding, along


# ------------------------------------------------------------------ solver ---
@dataclass
class World:
    obbs: List[Obb] = field(default_factory=list)
    planes: List[Plane] = field(default_factory=list)
    ground: Optional[Plane] = None

    def solids(self, center: Vec3, radius: float, skin: float = CONTACT_SKIN_M) -> List[Contact]:
        contacts: List[Contact] = []
        if self.ground is not None:
            hit = plane_contact(self.ground, center, radius, 0, skin)
            if hit is not None:
                contacts.append(hit)
        for index, plane in enumerate(self.planes):
            hit = plane_contact(plane, center, radius, 1000 + index, skin)
            if hit is not None:
                contacts.append(hit)
        for obb in self.obbs:
            hit = sphere_obb_contact(obb, center, radius, skin)
            if hit is not None:
                contacts.append(hit)
        return contacts


def _push_out(world: World, center: Vec3, radius: float,
              iterations: int = SOLVER_ITERATIONS) -> Tuple[Vec3, List[Contact], float]:
    """Sequential push-out of the deepest contact, then re-query.

    Returns the corrected centre, the contacts at that centre, and the largest
    remaining penetration (the "unresolved" signal; the motor refuses to move
    the ball into geometry and reports it instead of teleporting).
    """
    position = center
    contacts = world.solids(position, radius)
    for _ in range(iterations):
        deepest = None
        for contact in contacts:
            if contact.depth > CONTACT_SLOP_M and (deepest is None or contact.depth > deepest.depth):
                deepest = contact
        if deepest is None:
            break
        position = _add(position, _mul(deepest.normal, deepest.depth))
        contacts = world.solids(position, radius)
    residual = 0.0
    for contact in contacts:
        if contact.depth > residual:
            residual = contact.depth
    return position, contacts, residual


@dataclass
class State:
    center: Vec3
    radius: float
    v_planar: Vec3 = (0.0, 0.0, 0.0)
    v_y: float = 0.0
    slope_stamina: float = SLOPE_STAMINA_MAX
    slope_uphill_seconds: float = 0.0
    slope_rest_seconds: float = 0.0
    slope_sliding: bool = False
    climb_normal: Vec3 = (0.0, 1.0, 0.0)
    climb_active: bool = False
    climb_anchor_y: float = 0.0
    climb_surface_id: int = -1
    climb_height: float = 0.0
    climb_cooldown: float = 0.0
    climb_locked: bool = False   # one attempt per push: set at the cap, cleared on release
    climb_along_speed: float = 0.0  # rate along the uphill tangent while climbing
    climbing: bool = False


@dataclass
class StepReport:
    center: Vec3
    contacts: int
    dominant_id: int
    dominant_class: int
    climb_height: float
    climb_speed: float
    substeps: int
    residual: float
    airborne: bool
    normal: Vec3


def _dominant(contacts: Sequence[Contact]) -> Optional[Contact]:
    best = None
    for contact in contacts:
        if best is None or contact.normal[1] > best.normal[1]:
            best = contact
        elif abs(contact.normal[1] - best.normal[1]) < 1e-9 and contact.depth > best.depth:
            best = contact
    return best


def _support_contact(contacts: Sequence[Contact]) -> Optional[Contact]:
    """The support is the rideable contact with the highest n.y (ties: deepest)."""
    best = None
    for contact in contacts:
        if contact.cls != CLASS_RIDE:
            continue
        if best is None or contact.normal[1] > best.normal[1]:
            best = contact
        elif abs(contact.normal[1] - best.normal[1]) < 1e-9 and contact.depth > best.depth:
            best = contact
    return best


def _climb_contact(contacts: Sequence[Contact], drive_dir: Vec3) -> Optional[Contact]:
    """Among climb-class contacts, prefer the one the drive pushes into most."""
    best = None
    best_push = -1.0
    for contact in contacts:
        if contact.cls != CLASS_CLIMB:
            continue
        horizontal = _unit((contact.normal[0], 0.0, contact.normal[2]), "contact horizontal")
        push = max(0.0, -_dot(_unit(drive_dir, "drive_dir"), horizontal))
        if push > best_push + 1e-9 or (abs(push - best_push) <= 1e-9 and best is not None
                                       and contact.normal[1] > best.normal[1]):
            best = contact
            best_push = push
    return best


def step(world: World, state: State, drive_dir: Vec3, drive_speed: float,
         dt: float, protrusion_m: float = 0.0, theta_ride_deg: float = THETA_RIDE_DEG,
         kappa: float = KAPPA_SUBSTEP, max_substeps_cap: int = MAX_SUBSTEPS,
         drive_accel: float = 0.0) -> StepReport:
    """One fixed step: move, resolve, classify, respond, integrate.

    ``drive_dir`` is the unit planar direction the motion layer is pushing in;
    ``drive_speed`` is its target speed (not the speed the contacts left over).

    Support and climb are chosen independently: being supported on the floor
    while pushing a wall is the normal start of a climb, so a "dominant contact"
    would never let one begin.
    """
    radius = state.radius
    displacement = (state.v_planar[0] * dt, state.v_y * dt, state.v_planar[2] * dt)
    # The substep count is fixed from the pre-step velocity plus the largest
    # velocity a contact can add inside one step (gravity or the climb ramp),
    # so the no-tunnel bound covers the whole step, not just its first substep.
    margin = (GRAVITY + CLIMB_RATE_LIMIT) * dt * dt
    substeps = max_substeps(_add(displacement, (margin, margin, margin)), radius,
                            kappa, max_substeps_cap)
    position = state.center
    v_planar = state.v_planar
    v_y = state.v_y

    climb_active = state.climb_active
    climb_anchor_y = state.climb_anchor_y
    climb_surface_id = state.climb_surface_id
    cooldown = max(0.0, state.climb_cooldown - dt)
    locked = state.climb_locked
    h_cap = climb_height_cap(radius, protrusion_m)
    support = None
    contact_count = 0
    residual = 0.0
    climb_speed_value = 0.0
    along = state.climb_along_speed
    drive = _unit(drive_dir, "drive_dir")
    want = _mul(drive, drive_speed)

    for _ in range(substeps):
        previous_slope = is_slope(contact_angle_deg(state.climb_normal))
        if climb_active and previous_slope:
            tangent = _unit(_sub((0.0, 1.0, 0.0), _mul(state.climb_normal, state.climb_normal[1])))
            v_planar = (tangent[0] * along, 0.0, tangent[2] * along)
            v_y = tangent[1] * along
        # The substep uses the CURRENT velocity, so a velocity the contact adds
        # (climb, gravity) acts in the same step instead of one step later.
        position = _add(position, (v_planar[0] * dt / substeps,
                                   v_y * dt / substeps,
                                   v_planar[2] * dt / substeps))
        position, contacts, residual = _push_out(world, position, radius)
        contact_count = len(contacts)
        support = _support_contact(contacts)
        wall = _climb_contact(contacts, drive)
        # A contact only a little steeper than the surface the ball is rolling
        # on is that surface curving over (a ramp meeting a deck, a rounded
        # lip); the push-out rolls the ball over it and the climb channel must
        # not steal the velocity.  Only a clearly steeper surface is a climb.
        # The same rule must also exempt that contact from the velocity clip:
        # otherwise "not climbing" would still brake the ball at every convex
        # transition, which is exactly the seam the design removes.
        support_theta = contact_angle_deg(support.normal) if support is not None else 0.0
        if wall is not None and support is not None:
            if wall.angle_deg < support_theta + CLIMB_STEEPER_MARGIN_DEG:
                wall = None

        if support is not None and support_theta <= SLOPE_REFILL_MAX_DEG:
            state.slope_stamina, state.slope_uphill_seconds = SLOPE_STAMINA_MAX, 0.0
            state.slope_rest_seconds, state.slope_sliding = 0.0, False
        slope = wall is not None and is_slope(wall.angle_deg)
        if climb_surface_id >= 0 and wall is not None and previous_slope != slope:
            climb_anchor_y, locked, cooldown = position[1], False, 0.0
        if wall is not None:
            state.climb_normal = wall.normal
            theta = wall.angle_deg
            horizontal = _unit((wall.normal[0], 0.0, wall.normal[2]), "contact horizontal")
            push_in = max(0.0, -_dot(drive, horizontal))
            v_gate = max(_length((v_planar[0], 0.0, v_planar[2])), K_INTENT * drive_speed)
            if push_in < P_MIN:
                # Releasing the push is the only way to clear a cap lock: holding
                # into a wall taller than H_cap must not produce a silent
                # climb/fall loop.  The same release leaves the climb state.
                locked = False
            # The climb key is the CONTACT CLASS, not a surface id: a ramp that
            # meets a deck hand the ball from one surface to another with the
            # same normal, and treating that as a new surface would abort the
            # climb at every seam.  The id is recorded for diagnostics only.
            if climb_active and not slope and push_in < P_MIN:
                climb_active = False
                cooldown = CLIMB_COOLDOWN_S
                along = 0.0
            if (not climb_active and (slope or (not locked and cooldown <= 0.0)) and push_in >= P_MIN
                    and v_gate >= required_speed(theta) and h_cap > 0.0):
                climb_active = True
                climb_anchor_y = position[1]
                climb_surface_id = wall.surface_id
                along = 0.0
        else:
            climb_active = False
            along = 0.0

        if climb_active and wall is not None:
            # The climb channel OWNS the velocity: it drives the ball along the
            # uphill tangent (which is +Y for a wall, and up-the-hill for a
            # steep slope).  Setting the tangent instead of clipping the inward
            # component is what lets a 70 deg slope be climbed at all: the small
            # into-the-surface component is what keeps the contact alive, and the
            # push-out turns it into ascent.
            up = (0.0, 1.0, 0.0)
            cos_theta = _clamp(wall.normal[1], -1.0, 1.0)
            tangent = _sub(up, _mul(wall.normal, cos_theta))
            tangent = _unit(tangent, "climb tangent")
            height = position[1] - climb_anchor_y
            if slope:
                (state.slope_stamina, state.slope_uphill_seconds, state.slope_rest_seconds,
                 state.slope_sliding, along) = slope_motion(theta, 2 * radius,
                    push_in if drive_speed > 0.0 else 0.0, v_gate, dt / substeps,
                    state.slope_stamina, state.slope_uphill_seconds, state.slope_rest_seconds,
                    state.slope_sliding, along)
            else:
                vertical_target = climb_speed(push_in, v_gate, theta, height, h_cap)
                along_target = vertical_target / max(math.sin(math.radians(theta)), 0.2)
                rate = CLIMB_RATE_LIMIT * dt / substeps
                along = min(along + rate, along_target) if along_target > along else max(along - rate, along_target)
            v_planar = (tangent[0] * along, 0.0, tangent[2] * along)
            v_y = tangent[1] * along
            climb_speed_value = v_y
            if not slope and height >= h_cap - CLIMB_CAP_MARGIN_M:
                climb_active = False
                cooldown = CLIMB_COOLDOWN_S
                locked = True
                along = 0.0
            elif not slope and push_in < P_MIN:
                climb_active = False
                cooldown = CLIMB_COOLDOWN_S
                along = 0.0
        else:
            # Solid response for the surfaces the ball cannot roll on.  A ride
            # contact is a rolling contact: the push-out follows it and the
            # speed is preserved (that is what keeps a ramp exit from dumping
            # 30% of the speed).  A skin-only contact (depth <= 0) is "near",
            # not "touching", and must not brake a ball that is leaving.
            for contact in contacts:
                if contact.cls == CLASS_RIDE or contact.depth <= 0.0 or contact is support:
                    continue
                if (support is not None and contact.cls == CLASS_CLIMB
                        and contact.angle_deg < support_theta + CLIMB_STEEPER_MARGIN_DEG):
                    continue  # the surface turning over: rolled, not collided with
                if contact.cls == CLASS_CLIMB:
                    normal_h = _unit((contact.normal[0], 0.0, contact.normal[2]),
                                     "contact horizontal")
                    inward = _dot(v_planar, normal_h)
                    if inward < 0.0:
                        v_planar = _sub(v_planar, _mul(normal_h, inward))
                else:
                    inward = _dot((v_planar[0], v_y, v_planar[2]), contact.normal)
                    if inward < 0.0:
                        v_planar = (v_planar[0] - contact.normal[0] * inward,
                                    0.0,
                                    v_planar[2] - contact.normal[2] * inward)
                        v_y = v_y - contact.normal[1] * inward
            if support is not None:
                # Rolling on a surface: keep the velocity in its tangent plane when that
                # is downhill (the ball follows the slope instead of walking off it);
                # flat or uphill motion keeps v_y = 0.
                ny = support.normal[1]
                v_y = 0.0
                if ny > 1e-3:
                    tangent = -(support.normal[0] * v_planar[0]
                                + support.normal[2] * v_planar[2]) / ny
                    if tangent < 0.0:
                        v_y = tangent
            elif wall is not None:
                # Touched but not climbing: gravity, with the slide clamped so
                # the ball slides back slowly instead of free-falling.
                v_y = max(v_y - GRAVITY * dt / substeps, -V_SLIDE_MAX)
            else:
                v_y = max(v_y - GRAVITY * dt / substeps, -40.0)
            if drive_accel > 0.0:
                # Stand-in for BallMotionMath: accelerate the planar speed
                # toward the drive target (used by the reference tests only).
                current = _dot(v_planar, drive)
                delta = _clamp(drive_speed - current, -drive_accel * dt / substeps,
                               drive_accel * dt / substeps)
                v_planar = _add(v_planar, _mul(drive, delta))

    state.center = position
    state.v_planar = v_planar
    state.v_y = v_y
    state.climb_active = climb_active
    state.climb_anchor_y = climb_anchor_y
    state.climb_surface_id = climb_surface_id
    state.climb_cooldown = cooldown
    state.climb_locked = locked
    state.climb_along_speed = along
    state.climbing = climb_active
    state.climb_height = 0.0 if not climb_active else max(0.0, position[1] - climb_anchor_y)

    if support is not None:
        dominant_id = support.surface_id
        dominant_class = CLASS_RIDE
        normal = support.normal
        airborne = False
    elif state.climb_surface_id >= 0:
        dominant_id = state.climb_surface_id
        dominant_class = CLASS_CLIMB
        normal = (0.0, 1.0, 0.0)
        airborne = False
    else:
        dominant_id = -1
        dominant_class = CLASS_NONE
        normal = (0.0, 1.0, 0.0)
        airborne = True
    return StepReport(position, contact_count, dominant_id, dominant_class,
                      state.climb_height, climb_speed_value, substeps, residual,
                      airborne, normal)


# ------------------------------------------------------------ curved ramps ---
def tessellate_vertical_curve(origin: Vec3, forward: Vec3, up: Vec3, radius_m: float,
                              arc_deg: float, segments: int, width_m: float,
                              thickness_m: float, surface_id0: int = 200,
                              role: int = ROLE_SLAB) -> Tuple[List[Obb], dict]:
    """Expand one vertical arc into segment OBBs (midpoint tangent placement).

    ``forward`` is horizontal, ``up`` is world up; the arc starts at ``origin``
    going along ``forward`` and bends upwards by ``arc_deg``.  Each segment is
    lengthened by one sagitta so the convex side has no crack; the leftover
    bump on the convex side is the chord error the design budgets.
    """
    if segments < 1:
        raise ValueError("segments must be positive")
    if radius_m <= 0.0:
        raise ValueError("radius_m must be positive")
    forward = _unit(forward, "forward")
    up = _unit(up, "up")
    side = _unit((forward[1] * up[2] - forward[2] * up[1],
                  forward[2] * up[0] - forward[0] * up[2],
                  forward[0] * up[1] - forward[1] * up[0]), "side")
    arc = math.radians(arc_deg)
    delta = arc / segments
    seg_len = 2.0 * radius_m * math.sin(delta / 2.0)
    sagitta = radius_m * (1.0 - math.cos(delta / 2.0))
    overlap = 1.0 + (2.0 * sagitta / seg_len if seg_len > EPS else 0.0)
    obbs: List[Obb] = []
    for i in range(segments):
        phi = (i + 0.5) * delta
        # Point on the arc, tangent and surface normal at the segment midpoint.
        point = _add(origin, _add(_mul(forward, radius_m * math.sin(phi)),
                                  _mul(up, radius_m * (1.0 - math.cos(phi)))))
        tangent = _add(_mul(forward, math.cos(phi)), _mul(up, math.sin(phi)))
        normal = _add(_mul(forward, -math.sin(phi)), _mul(up, math.cos(phi)))
        half = (width_m / 2.0, thickness_m / 2.0, seg_len * overlap / 2.0)
        obbs.append(Obb(point, side, normal, tangent, half, surface_id0 + i, role))
    metrics = {
        "segments": segments,
        "delta_deg": math.degrees(delta),
        "sagitta_m": sagitta,
        "normal_jump_deg": math.degrees(delta) / 2.0,
        "segment_length_m": seg_len,
        "overlap": overlap,
    }
    return obbs, metrics


def curve_chord_budget(radius_m: float, segments: int, arc_deg: float,
                       ball_radius_m: float, max_error_per_radius: float = 0.05) -> bool:
    """True when the chord error is within max_error_per_radius of the ball radius."""
    delta = math.radians(arc_deg) / max(1, segments)
    sagitta = radius_m * (1.0 - math.cos(delta / 2.0))
    return sagitta <= max_error_per_radius * ball_radius_m


# --------------------------------------------------------------- broadphase ---
@dataclass
class NearWindow:
    items: List[Obb]
    ids: List[int]
    dropped: int
    complete: bool
    rebuilt: bool
    radius_in: float
    radius_out: float


def _approx_distance(obb: Obb, point: Vec3) -> float:
    """Bounding-sphere lower bound used by the window scan (6 flops)."""
    return _length(_sub(point, obb.center)) - obb.bounding_radius()


def select_window(obbs: Sequence[Obb], center: Vec3, radius: float, max_travel: float,
                  previous_ids: Sequence[int] = (), capacity: int = 24,
                  enter_margin: float = 0.05, exit_margin: float = 0.5) -> NearWindow:
    """Scan-and-copy near set with a two-radius hysteresis.

    Only OBBs within ``radius_in = r + travel + margin`` (or already selected
    and within ``radius_out = radius_in + exit_margin``) enter the per-step
    fixed array.  The window is recomputed every step from the position, so it
    cannot drift; the hysteresis only stops a boundary item from flapping the
    working set (and therefore its rebuild cost) every step.

    Overflow is explicit: the nearest ``capacity`` items are kept, ``dropped``
    counts the rest, and ``complete`` is false only when a dropped item could
    have been within contact reach.  The motor must surface both to the test
    field instead of silently losing geometry.
    """
    radius_in = radius + max_travel + enter_margin
    radius_out = radius_in + exit_margin
    candidates: List[Tuple[float, Obb]] = []
    for obb in obbs:
        approximate = _approx_distance(obb, center)
        if approximate > radius_out:
            continue
        # The exact distance is computed in C# only for the survivors of the
        # bounding-sphere test; the reference model always takes the exact path.
        _, distance, _, _ = closest_point_on_obb(obb, center)
        if distance <= radius_in or (obb.surface_id in previous_ids and distance <= radius_out):
            candidates.append((distance, obb))
    candidates.sort(key=lambda pair: (pair[0], pair[1].surface_id))
    dropped = max(0, len(candidates) - capacity)
    kept = candidates[:capacity]
    nearest_dropped = candidates[capacity][0] if dropped > 0 else float("inf")
    complete = dropped == 0 or nearest_dropped > radius + 0.02
    ids = [obb.surface_id for _, obb in kept]
    return NearWindow([obb for _, obb in kept], ids, dropped, complete,
                      ids != list(previous_ids), radius_in, radius_out)
