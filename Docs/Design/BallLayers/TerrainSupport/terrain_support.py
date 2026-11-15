"""Terrain-support reference model; standard-library host reference only.

DRAFT: no Unity/Udon or world-scale claim.  This mirrors, name for name and in
the same order, the C# in
``VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs``
(P1 + P2 of Docs/Design/BallMotor/TERRAIN_SUPPORT.md): which local surface is
under the ball centre, whether a static box or an absorb-proof prop is a step or
a wall, which absorb-proof prop tops enter the support table, and the gravity
fall state once that surface drops away.  Runs with ``python3 -m unittest
discover -s Docs/tests``.

One implementation per language is the repository rule for ball layers; if the
C# changes, this file and the tests change in the same commit.
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
                        box_count: Optional[int] = None,
                        props: Sequence[Sequence[Sequence[float]]] = (),
                        prop_count: Optional[int] = None) -> SupportSurface:
    """Highest candidate surface under the probe's XZ, with the ground as fallback.

    Selection is deterministic: the ground starts as the winner and a box or prop
    replaces it only when strictly higher, so ties keep the ground and, among
    equals, the earlier reader (boxes before props, then registration order).
    ``boxes``/``props`` are sequences of ``(minimum, maximum)`` pairs; only the
    first ``box_count``/``prop_count`` entries are read (capacity is enforced at
    registration, not here).  A prop ``i`` reports ``FIRST_PROP_SURFACE_ID + i``;
    the host only sends props its classifier called ``PROP_ROLE_SUPPORT``.
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
    validated = tuple(_validate_box(boxes[i], i) for i in range(box_count))

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
        selected_id = 1 + index
        selected_height = top
        selected_point = (probe[0], top, probe[2])
        selected_normal = (0.0, 1.0, 0.0)

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


def support_dropped_away(support_height_m: float, previous_support_height_m: float,
                         drop_trigger_m: float) -> bool:
    """True when the surface under the ball fell away by more than the trigger."""
    height = _finite(support_height_m, "support_height_m")
    previous = _finite(previous_support_height_m, "previous_support_height_m")
    trigger = _finite(drop_trigger_m, "drop_trigger_m")
    if trigger < 0.0:
        raise ValueError("drop_trigger_m must be nonnegative")
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
