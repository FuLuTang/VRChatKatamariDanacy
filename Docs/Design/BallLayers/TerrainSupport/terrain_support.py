"""Terrain-support reference model; standard-library host reference only.

DRAFT: no Unity/Udon or world-scale claim.  This mirrors, name for name and in
the same order, the C# in
``VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs``
(P1 + P2 + P3 of Docs/Design/BallMotor/TERRAIN_SUPPORT.md): which local surface
is under the ball centre, whether a static box or an absorb-proof prop is a step
or a wall, a static ramp's walking plane, which absorb-proof prop tops enter the
support table, the optional ramp tangential gravity, and the gravity fall state
once that surface drops away.  Runs with ``python3 -m unittest discover -s
Docs/tests``.

One implementation per language is the repository rule for ball layers; if the
C# changes, this file and the tests change in the same commit.

P3 adds static ramps (an oriented walking plane plus a horizontal footprint) and
the P1 regression fix: the support table reads the motor's step mask for static
boxes instead of re-running the step comparison, and the mask refresh gate is a
function of the predicate's own inputs (reference height and diameter). Ramps
and props have no sweep entry, so they keep the one-predicate band.
"""
from dataclasses import dataclass
import math
from typing import Optional, Sequence, Tuple

Vec3 = Tuple[float, float, float]

NO_SURFACE_ID = -1
GROUND_SURFACE_ID = 0

# P2: support props. One capacity constant and one predicate, mirrored by the C#.
MAX_SUPPORT_PROPS = 6
FIRST_PROP_SURFACE_ID = 100

# P3: static ramps. Ids 10..13, so they never collide with boxes (1..8) or props
# (100..105). Ramps are support-only: the motor never appends them to its sweep.
MAX_RAMPS = 4
FIRST_RAMP_SURFACE_ID = 10
MIN_RAMP_UPWARD_NORMAL_Y = 0.05
MIN_RAMP_HORIZONTAL_NORMAL = 1e-4
DEFAULT_MAX_SUPPORT_SLOPE_DEGREES = 45.0

RAMP_REFUSAL_NONE = 0
RAMP_REFUSAL_INVALID_INPUT = 1
RAMP_REFUSAL_NORMAL_NOT_UPWARD = 2
RAMP_REFUSAL_SLOPE_TOO_STEEP = 3
RAMP_REFUSAL_DEGENERATE_AXIS = 4
RAMP_REFUSAL_SLOPE_LIMIT_INVALID = 5
RAMP_REFUSAL_CAPACITY = 6

# Step-mask refresh gates. The gate is a function of the predicate's own inputs:
# the reference height and the semantic diameter (which becomes the limit).
STEP_MASK_DIAMETER_GATE_M = 0.002
STEP_MASK_REFERENCE_GATE_M = 0.005

PROP_ROLE_NONE = 0
PROP_ROLE_SUPPORT = 1
PROP_ROLE_BLOCKING = 2

_MIN_UPWARD_NORMAL_Y = 1e-3


@dataclass(frozen=True)
class SupportSurface:
    """The selected support under the probe, as the plane pair the motor feeds on."""
    surface_id: int
    point: Vec3
    normal: Vec3
    height: float
    candidate_count: int


@dataclass(frozen=True)
class Ramp:
    """A baked static ramp: a point on the walking plane plus a horizontal footprint."""
    center: Vec3
    normal: Vec3
    right: Vec3
    half_width: float
    half_run: float
    slope_degrees: float


class RampRefusal(ValueError):
    """A geometric ramp refusal; ``code`` is one of the RAMP_REFUSAL_* values."""

    def __init__(self, code: int):
        super().__init__(ramp_refusal_reason(code))
        self.code = code


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
    value = a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    if not math.isfinite(value):
        raise ValueError("non-finite intermediate vector dot product")
    return value


def _cross(a: Vec3, b: Vec3) -> Vec3:
    value = (a[1] * b[2] - a[2] * b[1],
             a[2] * b[0] - a[0] * b[2],
             a[0] * b[1] - a[1] * b[0])
    return _vec3(value, "cross product")


def _unit(value: Vec3, name: str) -> Tuple[Vec3, float]:
    length = math.hypot(value[0], value[1], value[2])
    if not math.isfinite(length) or length == 0.0:
        raise ValueError(f"{name} must be finite and non-zero")
    unit = (value[0] / length, value[1] / length, value[2] / length)
    return unit, length


def try_step_height_limit(semantic_diameter_m: float,
                          step_height_over_ball_diameter: float) -> float:
    """``stepLimit = k * semanticDiameter``; the ratio form, never the radius form."""
    diameter = _finite(semantic_diameter_m, "semantic_diameter_m")
    if diameter < 0.0:
        raise ValueError("semantic_diameter_m must be nonnegative")
    ratio = _finite(step_height_over_ball_diameter, "step_height_over_ball_diameter")
    if ratio < 0.0 or ratio > 1.0:
        raise ValueError("step_height_over_ball_diameter must be in [0, 1]")
    limit = diameter * ratio
    if not math.isfinite(limit):
        raise ValueError("step limit is non-finite")
    return limit


def support_reference_height(support_height_m: float, ball_bottom_m: float) -> float:
    """The ONE reference the sweep mask and the support table compare a top against.

    The higher of the surface the ball was on and the ball's own bottom. Using the
    ball's bottom lets a surface the ball is falling ONTO count, and it makes "the
    ball is already above the top" a consequence of the reference (zero or negative
    rise) rather than a second unconditional rule.  A non-finite ball bottom is
    ignored: the surface height is the conservative answer.
    """
    support = _finite(support_height_m, "support_height_m")
    try:
        bottom = _finite(ball_bottom_m, "ball_bottom_m")
    except (TypeError, ValueError):
        return support
    return bottom if bottom > support else support


def is_step_within_limit(top_y: float, reference_support_height_m: float,
                         step_limit_m: float) -> bool:
    """The ONE static-box step decision: is this top inside the ball's step limit?

    Both sides call this with the same reference: the motor's sweep mask decides
    exclusion, ``try_resolve_support`` decides candidacy.  One subtraction and one
    inclusive bound; a band written as ``top <= reference + limit`` is not
    bit-identical to it and was half of the 2026-11-15 C09 ghost step.  There is
    deliberately no one-way hysteresis: a latch can only widen the exclusion
    beyond the support rule, and a box dropped from the sweep that cannot support
    is exactly the state that let the 0.30 m ball drive through a 0.075 m deck.
    """
    top = _finite(top_y, "top_y")
    reference = _finite(reference_support_height_m, "reference_support_height_m")
    limit = _finite(step_limit_m, "step_limit_m")
    if limit < 0.0:
        raise ValueError("step_limit_m must be nonnegative")
    rise = top - reference
    if not math.isfinite(rise):
        raise ValueError("step rise is non-finite")
    return rise <= limit


def is_step_crossing(rise_m: float, min_step_height_m: float) -> bool:
    """Large enough to grant the one-step lift, above the numeric de-noise floor."""
    rise = _finite(rise_m, "rise_m")
    minimum = _finite(min_step_height_m, "min_step_height_m")
    if minimum < 0.0:
        raise ValueError("min_step_height_m must be nonnegative")
    return rise > minimum


def step_mask_needs_refresh(mask_valid: bool, boxes_dirty: bool,
                            semantic_diameter_m: float, masked_diameter_m: float,
                            reference_height_m: float, masked_reference_height_m: float) -> bool:
    """True when the motor must recompute the static-box step mask.

    The gate is a function of the predicate's OWN inputs (the reference height and
    the diameter, which becomes the step limit), not of the selected support
    height. That distinction is the second half of the 2026-11-15 C09 fix: a ball
    spawning 20 mm above the floor lands, its bottom (the reference) moves to 0,
    and the support height never moves - a support-height gate cannot see it.
    """
    for value, name in ((mask_valid, "mask_valid"), (boxes_dirty, "boxes_dirty")):
        if not isinstance(value, bool):
            raise ValueError(f"{name} must be bool")
    diameter = _finite(semantic_diameter_m, "semantic_diameter_m")
    masked_diameter = _finite(masked_diameter_m, "masked_diameter_m")
    reference = _finite(reference_height_m, "reference_height_m")
    masked_reference = _finite(masked_reference_height_m, "masked_reference_height_m")
    if not mask_valid or boxes_dirty:
        return True
    if abs(diameter - masked_diameter) > STEP_MASK_DIAMETER_GATE_M:
        return True
    return abs(reference - masked_reference) > STEP_MASK_REFERENCE_GATE_M


def is_ramp_count_allowed(count: int) -> bool:
    """True when ``count`` static ramps fit one ball (0..MAX_RAMPS)."""
    if not isinstance(count, int) or isinstance(count, bool):
        raise ValueError("count must be an int")
    return 0 <= count <= MAX_RAMPS


def is_ramp_surface_id(surface_id: int) -> bool:
    if not isinstance(surface_id, int) or isinstance(surface_id, bool):
        raise ValueError("surface_id must be an int")
    return FIRST_RAMP_SURFACE_ID <= surface_id < FIRST_RAMP_SURFACE_ID + MAX_RAMPS


def ramp_surface_index(surface_id: int) -> int:
    if not is_ramp_surface_id(surface_id):
        return -1
    return surface_id - FIRST_RAMP_SURFACE_ID


def ramp_refusal_reason(code: int) -> str:
    if code == RAMP_REFUSAL_NONE:
        return ""
    if code == RAMP_REFUSAL_INVALID_INPUT:
        return "non-finite or negative ramp input"
    if code == RAMP_REFUSAL_NORMAL_NOT_UPWARD:
        return "ramp normal is not upward"
    if code == RAMP_REFUSAL_SLOPE_TOO_STEEP:
        return "ramp slope exceeds the support limit"
    if code == RAMP_REFUSAL_DEGENERATE_AXIS:
        return "ramp axis or extent is degenerate"
    if code == RAMP_REFUSAL_SLOPE_LIMIT_INVALID:
        return "max support slope is not in (0, 90)"
    if code == RAMP_REFUSAL_CAPACITY:
        return "ramp capacity 4 exceeded"
    return "unknown ramp refusal"


def ramp_slope_degrees(normal: Sequence[float]) -> float:
    """Angle between the ramp normal and +Y, degrees."""
    unit, _ = _unit(_vec3(normal, "normal"), "normal")
    upward = max(-1.0, min(1.0, unit[1]))
    return math.degrees(math.acos(upward))


def validate_ramp(center: Sequence[float], normal: Sequence[float], right: Sequence[float],
                  half_width: float, half_run: float,
                  max_slope_degrees: float = DEFAULT_MAX_SUPPORT_SLOPE_DEGREES) -> int:
    """The ONE ramp registration decision; 0 means accepted.

    Mirrors SupportSurfaceMath.TryValidateRamp. Non-finite components raise
    ValueError (the reference model's convention); geometric refusals return a
    RAMP_REFUSAL_* code. The supplied ``right`` is projected onto the horizontal
    plane; a flat plane is refused (it is a box top, not a ramp).
    """
    _vec3(center, "center")
    unit_n, _ = _unit(_vec3(normal, "normal"), "normal")
    _vec3(right, "right")
    width = _finite(half_width, "half_width")
    run = _finite(half_run, "half_run")
    limit = _finite(max_slope_degrees, "max_slope_degrees")
    if width <= 0.0 or run <= 0.0:
        return RAMP_REFUSAL_DEGENERATE_AXIS
    if limit <= 0.0 or limit >= 90.0:
        return RAMP_REFUSAL_SLOPE_LIMIT_INVALID
    if unit_n[1] <= MIN_RAMP_UPWARD_NORMAL_Y:
        return RAMP_REFUSAL_NORMAL_NOT_UPWARD
    horizontal = math.hypot(unit_n[0], unit_n[2])
    if horizontal <= MIN_RAMP_HORIZONTAL_NORMAL:
        return RAMP_REFUSAL_DEGENERATE_AXIS
    slope = math.degrees(math.acos(max(-1.0, min(1.0, unit_n[1]))))
    if slope > limit:
        return RAMP_REFUSAL_SLOPE_TOO_STEEP
    try:
        _unit((right[0], 0.0, right[2]), "right")
    except ValueError:
        return RAMP_REFUSAL_DEGENERATE_AXIS
    return RAMP_REFUSAL_NONE


def bake_ramp(box_center: Sequence[float], box_normal: Sequence[float],
              box_right: Sequence[float], box_size: Sequence[float],
              max_slope_degrees: float = DEFAULT_MAX_SUPPORT_SLOPE_DEGREES) -> Ramp:
    """Bake a rotated box into a ramp whose walking face is the box's +Y face.

    Mirrors SupportSurfaceMath.TryBakeRamp. The reported centre is the walking-face
    centre (box centre + normal * thickness / 2), and the two half-extents are the
    projections of the four face corners onto the horizontal right and downhill
    axes, so ``half_run`` is ``length/2 * cos(slope)`` for a pitched board.
    """
    center = _vec3(box_center, "box_center")
    size = _vec3(box_size, "box_size")
    if size[0] < 0.0 or size[1] < 0.0 or size[2] < 0.0:
        raise RampRefusal(RAMP_REFUSAL_INVALID_INPUT)
    unit_n, _ = _unit(_vec3(box_normal, "box_normal"), "box_normal")
    local_x, _ = _unit(_vec3(box_right, "box_right"), "box_right")
    try:
        local_z, _ = _unit(_cross(unit_n, local_x), "ramp_forward")
        right_flat, _ = _unit((local_x[0], 0.0, local_x[2]), "ramp_right")
        downhill, _ = _unit((unit_n[0], 0.0, unit_n[2]), "ramp_downhill")
    except ValueError as exc:
        raise RampRefusal(RAMP_REFUSAL_DEGENERATE_AXIS) from exc

    face_center = (center[0] + unit_n[0] * size[1] * 0.5,
                   center[1] + unit_n[1] * size[1] * 0.5,
                   center[2] + unit_n[2] * size[1] * 0.5)
    half_x = size[0] * 0.5
    half_z = size[2] * 0.5
    width_extent = 0.0
    run_extent = 0.0
    for sx in (-1.0, 1.0):
        for sz in (-1.0, 1.0):
            offset = (local_x[0] * (sx * half_x) + local_z[0] * (sz * half_z),
                      local_x[1] * (sx * half_x) + local_z[1] * (sz * half_z),
                      local_x[2] * (sx * half_x) + local_z[2] * (sz * half_z))
            width_extent = max(width_extent, abs(_dot(offset, right_flat)))
            run_extent = max(run_extent, abs(_dot(offset, downhill)))
    if width_extent <= 0.0 or run_extent <= 0.0:
        raise RampRefusal(RAMP_REFUSAL_DEGENERATE_AXIS)

    code = validate_ramp(face_center, unit_n, right_flat, width_extent, run_extent,
                         max_slope_degrees)
    if code != RAMP_REFUSAL_NONE:
        raise RampRefusal(code)
    return Ramp(face_center, unit_n, right_flat, width_extent, run_extent,
                ramp_slope_degrees(unit_n))


def ramp_height_at(center: Sequence[float], normal: Sequence[float], x: float, z: float) -> float:
    """Plane height at ``(x, z)``; ``center`` is any point on the walking plane."""
    unit_n, _ = _unit(_vec3(normal, "normal"), "normal")
    point = _vec3(center, "center")
    plane_offset = _dot(unit_n, point)
    height = (plane_offset - unit_n[0] * float(x) - unit_n[2] * float(z)) / unit_n[1]
    if not math.isfinite(height):
        raise ValueError("ramp height is non-finite")
    return height


def ramp_contains(center: Sequence[float], normal: Sequence[float], right: Sequence[float],
                  half_width: float, half_run: float, probe: Sequence[float]) -> bool:
    """True when the probe's XZ is inside the ramp's horizontal footprint (inclusive)."""
    unit_n, _ = _unit(_vec3(normal, "normal"), "normal")
    point = _vec3(center, "center")
    right_flat, _ = _unit((_vec3(right, "right")[0], 0.0, _vec3(right, "right")[2]),
                          "right")
    downhill, _ = _unit((unit_n[0], 0.0, unit_n[2]), "downhill")
    probe_v = _vec3(probe, "probe")
    delta_x = probe_v[0] - point[0]
    delta_z = probe_v[2] - point[2]
    along_right = delta_x * right_flat[0] + delta_z * right_flat[2]
    along_down = delta_x * downhill[0] + delta_z * downhill[2]
    return abs(along_right) <= half_width and abs(along_down) <= half_run


def classify_prop(beyond_absorb_volume_limit: bool, top_y: float, support_height_m: float,
                  step_limit_m: float, already_above: bool) -> int:
    """The only place a prop's role is decided (P2, design §8 P2 rule 1).

    An absorbable prop is food (``PROP_ROLE_NONE``) whatever its geometry. An
    absorb-proof one is a support when its top is inside the step limit
    (:func:`is_step_within_limit`, the same comparison the static-box mask uses;
    the boundary is inclusive) or when the ball is already above its top,
    otherwise a wall.  The already-above branch cannot disagree with the support
    table: the motor's reference is the ball's bottom when it is above the top, so
    the table's rise is zero or negative.  ``beyond_absorb_volume_limit`` is the
    host's ``IsBeyondAbsorbVolumeLimit`` answer, passed in so this layer reads no
    prop and the three identities share one predicate.
    """
    if not isinstance(beyond_absorb_volume_limit, bool) or not isinstance(already_above, bool):
        raise ValueError("beyond_absorb_volume_limit/already_above must be bool")
    top = _finite(top_y, "top_y")
    support = _finite(support_height_m, "support_height_m")
    limit = _finite(step_limit_m, "step_limit_m")
    if limit < 0.0:
        raise ValueError("step_limit_m must be nonnegative")
    if not beyond_absorb_volume_limit:
        return PROP_ROLE_NONE
    if already_above:
        return PROP_ROLE_SUPPORT
    return PROP_ROLE_SUPPORT if is_step_within_limit(top, support, limit) else PROP_ROLE_BLOCKING


def is_support_prop_count_allowed(count: int) -> bool:
    """True when ``count`` support props fit one ball (0..MAX_SUPPORT_PROPS)."""
    if not isinstance(count, int) or isinstance(count, bool):
        raise ValueError("count must be an int")
    return 0 <= count <= MAX_SUPPORT_PROPS


def is_prop_surface_id(surface_id: int) -> bool:
    return surface_id >= FIRST_PROP_SURFACE_ID


def prop_surface_index(surface_id: int) -> int:
    if not is_prop_surface_id(surface_id):
        return -1
    return surface_id - FIRST_PROP_SURFACE_ID


def _validate_box(box: Sequence[Sequence[float]], index: int,
                  label: str = "boxes") -> Tuple[Vec3, Vec3]:
    try:
        minimum = _vec3(box[0], f"{label}[{index}].minimum")
        maximum = _vec3(box[1], f"{label}[{index}].maximum")
    except (TypeError, IndexError) as exc:
        raise ValueError(f"{label}[{index}] must be (minimum, maximum)") from exc
    if minimum[0] > maximum[0] or minimum[1] > maximum[1] or minimum[2] > maximum[2]:
        raise ValueError(f"{label}[{index}] has inverted bounds")
    return minimum, maximum


def try_resolve_support(probe_point: Sequence[float], reference_support_height_m: float,
                        step_limit_m: float, ground_normal: Sequence[float],
                        ground_offset: float,
                        boxes: Sequence[Sequence[Sequence[float]]],
                        box_steppable: Sequence[bool],
                        box_count: Optional[int] = None,
                        ramps: Sequence[Sequence] = (),
                        ramp_count: Optional[int] = None,
                        props: Sequence[Sequence[Sequence[float]]] = (),
                        prop_count: Optional[int] = None) -> SupportSurface:
    """Highest candidate surface under the probe's XZ, with the ground as fallback.

    Selection is deterministic: the ground starts as the winner and a box, ramp or
    prop replaces it only when strictly higher, so ties keep the ground and, among
    equals, the earlier reader (boxes, then ramps, then props, each in registration
    order). ``boxes``/``props`` are sequences of ``(minimum, maximum)`` pairs; a
    ramp is ``(center, normal, right, half_width, half_run)``. Only the first
    ``box_count``/``ramp_count``/``prop_count`` entries are read (capacity is
    enforced at registration, not here).

    ``box_steppable`` is the motor's step mask, one entry per static box, and it
    IS the static-box candidacy answer: a true entry is in the sweep exclusion and
    is selectable, a false entry is a wall and is not. This function deliberately
    does NOT re-run :func:`is_step_within_limit` on a box top - doing that was the
    second half of the C09 ghost step, because the mask's reference (the pre-sweep
    ball bottom) and this query's reference (the post-sweep centre) could drift
    while the ball landed. Ramps and props have no mask and no sweep entry, so they
    keep the one-predicate band.

    A prop ``i`` reports ``FIRST_PROP_SURFACE_ID + i``; a ramp ``i`` reports
    ``FIRST_RAMP_SURFACE_ID + i`` and its surface normal is the plane normal.
    """
    probe = _vec3(probe_point, "probe_point")
    reference = _finite(reference_support_height_m, "reference_support_height_m")
    limit = _finite(step_limit_m, "step_limit_m")
    if limit < 0.0:
        raise ValueError("step_limit_m must be nonnegative")
    offset_raw = _finite(ground_offset, "ground_offset")
    normal, raw_length = _unit(_vec3(ground_normal, "ground_normal"), "ground_normal")
    if normal[1] <= _MIN_UPWARD_NORMAL_Y:
        raise ValueError("ground_normal must point upward (n.y > 1e-3)")
    offset = offset_raw / raw_length
    if not math.isfinite(offset):
        raise ValueError("normalized ground offset is non-finite")

    if box_count is None:
        box_count = len(boxes)
    if box_count < 0 or box_count > len(boxes):
        raise ValueError("box_count is outside the box list")
    if len(box_steppable) < box_count:
        raise ValueError("box_steppable does not cover box_count")
    for index in range(box_count):
        if not isinstance(box_steppable[index], bool):
            raise ValueError(f"box_steppable[{index}] must be bool")
    validated = tuple(_validate_box(boxes[i], i) for i in range(box_count))

    if ramp_count is None:
        ramp_count = len(ramps)
    if ramp_count < 0 or ramp_count > len(ramps):
        raise ValueError("ramp_count is outside the ramp list")
    validated_ramps = []
    for index in range(ramp_count):
        try:
            ramp_center_in, ramp_normal_in, ramp_right_in, half_width, half_run = ramps[index]
        except (TypeError, ValueError) as exc:
            raise ValueError(f"ramps[{index}] must be "
                             "(center, normal, right, half_width, half_run)") from exc
        code = validate_ramp(ramp_center_in, ramp_normal_in, ramp_right_in, half_width,
                             half_run, 89.0)
        if code != RAMP_REFUSAL_NONE:
            raise ValueError(f"ramps[{index}] is not a usable plane: {ramp_refusal_reason(code)}")
        validated_ramps.append((_vec3(ramp_center_in, "ramp center"),
                                _unit(_vec3(ramp_normal_in, "ramp normal"),
                                      "ramp normal")[0],
                                _vec3(ramp_right_in, "ramp right"),
                                _finite(half_width, "ramp half_width"),
                                _finite(half_run, "ramp half_run")))

    if prop_count is None:
        prop_count = len(props)
    if prop_count < 0 or prop_count > len(props):
        raise ValueError("prop_count is outside the prop list")
    validated_props = tuple(_validate_box(props[i], i, "props")
                            for i in range(prop_count))

    # y = (offset - nx*px - nz*pz) / ny on the ground plane.
    ground_height = (offset - normal[0] * probe[0] - normal[2] * probe[2]) / normal[1]
    if not math.isfinite(ground_height):
        raise ValueError("ground height is non-finite")

    to_plane = _dot(normal, probe) - offset
    ground_point = (probe[0] - normal[0] * to_plane,
                    probe[1] - normal[1] * to_plane,
                    probe[2] - normal[2] * to_plane)

    selected_id = GROUND_SURFACE_ID
    selected_height = ground_height
    selected_point = ground_point
    selected_normal = normal
    # The step decision is the SAME call the motor's sweep mask makes per box;
    # writing the band here instead was the second half of the C09 ghost step.
    active_count = 1 if is_step_within_limit(ground_height, reference, limit) else 0

    for index, (minimum, maximum) in enumerate(validated):
        if not box_steppable[index]:
            continue
        top = maximum[1]
        active_count += 1
        if probe[0] < minimum[0] or probe[0] > maximum[0]:
            continue
        if probe[2] < minimum[2] or probe[2] > maximum[2]:
            continue
        if top <= selected_height:
            continue
        selected_id = 1 + index
        selected_height = top
        selected_point = (probe[0], top, probe[2])
        selected_normal = (0.0, 1.0, 0.0)

    for index, (ramp_center, ramp_normal, ramp_right, half_width, half_run) in \
            enumerate(validated_ramps):
        ramp_height = ramp_height_at(ramp_center, ramp_normal, probe[0], probe[2])
        if not is_step_within_limit(ramp_height, reference, limit):
            continue
        active_count += 1
        if not ramp_contains(ramp_center, ramp_normal, ramp_right, half_width, half_run,
                             probe):
            continue
        if ramp_height <= selected_height:
            continue
        selected_id = FIRST_RAMP_SURFACE_ID + index
        selected_height = ramp_height
        selected_point = (probe[0], ramp_height, probe[2])
        selected_normal = ramp_normal

    for index, (minimum, maximum) in enumerate(validated_props):
        top = maximum[1]
        if not is_step_within_limit(top, reference, limit):
            continue
        active_count += 1
        if probe[0] < minimum[0] or probe[0] > maximum[0]:
            continue
        if probe[2] < minimum[2] or probe[2] > maximum[2]:
            continue
        if top <= selected_height:
            continue
        selected_id = FIRST_PROP_SURFACE_ID + index
        selected_height = top
        selected_point = (probe[0], top, probe[2])
        selected_normal = (0.0, 1.0, 0.0)

    return SupportSurface(selected_id, selected_point, selected_normal,
                          selected_height, active_count)


def apply_ramp_tangential_gravity(planar_velocity: Sequence[float],
                                 support_normal: Sequence[float], gravity_mps2: float,
                                 tangential_gravity_scale: float,
                                 fixed_step_seconds: float,
                                 max_speed_mps: float) -> Vec3:
    """One optional downhill acceleration step on a ramp (U3, OFF by default).

    Mirrors SupportSurfaceMath.TryApplyRampTangentialGravity: adds
    ``gravity * sin(slope) * scale * dt`` along the horizontal downhill direction,
    clamps the planar speed, and leaves a flat support unchanged. At scale 0 the
    velocity is returned unchanged, i.e. the P1 behaviour.
    """
    velocity = _vec3(planar_velocity, "planar_velocity")
    unit_n, _ = _unit(_vec3(support_normal, "support_normal"), "support_normal")
    if unit_n[1] <= 0.0:
        raise ValueError("support_normal must point upward")
    gravity = _finite(gravity_mps2, "gravity_mps2")
    if gravity < 0.0:
        raise ValueError("gravity_mps2 must be nonnegative")
    scale = _finite(tangential_gravity_scale, "tangential_gravity_scale")
    if scale < 0.0 or scale > 1.0:
        raise ValueError("tangential_gravity_scale must be in [0, 1]")
    step = _finite(fixed_step_seconds, "fixed_step_seconds")
    if step <= 0.0:
        raise ValueError("fixed_step_seconds must be positive")
    maximum = _finite(max_speed_mps, "max_speed_mps")
    if maximum <= 0.0:
        raise ValueError("max_speed_mps must be positive")

    horizontal = math.hypot(unit_n[0], unit_n[2])
    if horizontal <= MIN_RAMP_HORIZONTAL_NORMAL:
        return velocity
    downhill_x = unit_n[0] / horizontal
    downhill_z = unit_n[2] / horizontal
    delta_speed = gravity * horizontal * scale * step
    velocity_x = velocity[0] + downhill_x * delta_speed
    velocity_z = velocity[2] + downhill_z * delta_speed
    speed = math.hypot(velocity_x, velocity_z)
    if speed > maximum:
        factor = maximum / speed
        velocity_x *= factor
        velocity_z *= factor
    return (velocity_x, 0.0, velocity_z)


def support_dropped_away(support_height_m: float, previous_support_height_m: float,
                         drop_trigger_m: float, same_surface: bool) -> bool:
    """True when a DIFFERENT surface under the ball fell away by more than the trigger.

    ``same_surface`` suppresses the trigger while the same selected surface stays
    selected. A plane's height under the moving probe falls just because the ball
    rolled along it: on a 30 degree ramp at 4 m/s that is 46 mm per 20 ms step, and
    without this flag every downhill step would enter the gravity-fall state. It is
    not a second rule - "the surface dropped away" means a different surface; a real
    ledge changes the selected surface id (ramp -> ground, box -> ground, ...).
    """
    if not isinstance(same_surface, bool):
        raise ValueError("same_surface must be bool")
    height = _finite(support_height_m, "support_height_m")
    previous = _finite(previous_support_height_m, "previous_support_height_m")
    trigger = _finite(drop_trigger_m, "drop_trigger_m")
    if trigger < 0.0:
        raise ValueError("drop_trigger_m must be nonnegative")
    if same_surface:
        return False
    return height < previous - trigger


def resolve_fall_state(was_falling: bool, support_dropped: bool, ground_accepted: bool,
                       drop_clamped: bool) -> bool:
    """The falling state machine for one step; no jump, no upward velocity."""
    for value, name in ((was_falling, "was_falling"), (support_dropped, "support_dropped"),
                        (ground_accepted, "ground_accepted"), (drop_clamped, "drop_clamped")):
        if not isinstance(value, bool):
            raise ValueError(f"{name} must be bool")
    if not was_falling and not support_dropped:
        return False
    if ground_accepted and not drop_clamped:
        return False
    return True


def try_integrate_fall(fall_speed_mps: float, gravity_mps2: float, fixed_step_seconds: float,
                       max_fall_speed_mps: float) -> Tuple[float, float]:
    """Semi-implicit Euler: returns ``(next_fall_speed, fall_budget)`` for one step."""
    speed_in = _finite(fall_speed_mps, "fall_speed_mps")
    if speed_in < 0.0:
        raise ValueError("fall_speed_mps must be nonnegative")
    gravity = _finite(gravity_mps2, "gravity_mps2")
    if gravity < 0.0:
        raise ValueError("gravity_mps2 must be nonnegative")
    step = _finite(fixed_step_seconds, "fixed_step_seconds")
    if step <= 0.0:
        raise ValueError("fixed_step_seconds must be positive")
    maximum = _finite(max_fall_speed_mps, "max_fall_speed_mps")
    if maximum <= 0.0:
        raise ValueError("max_fall_speed_mps must be positive")
    speed = speed_in + gravity * step
    if not math.isfinite(speed):
        raise ValueError("fall speed is non-finite")
    speed = min(speed, maximum)
    budget = speed * step
    if not math.isfinite(budget):
        raise ValueError("fall budget is non-finite")
    return speed, budget
