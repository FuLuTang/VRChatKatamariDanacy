"""Offline radial-shell proxy reference model (standard library only).

DRAFT: host-side experiment, not Unity/Udon/runtime code or a frozen interface.
The quaternion transform uses explicit host Hamilton algebra; Unity parity requires
an independent direction-arrow probe before integration.
"""
from dataclasses import dataclass
import math
from typing import Dict, Iterable, Sequence, Tuple

Vec3 = Tuple[float, float, float]
Quaternion = Tuple[float, float, float, float]  # x, y, z, w
_DOT_EPSILON = 1e-12


@dataclass(frozen=True)
class RadialConfig:
    # Both are provisional prototypes pending performance validation.
    direction_count: int = 32
    max_extreme_candidates: int = 4


@dataclass(frozen=True)
class AttachmentProxy:
    """Confirmed contribution input; item_id must be a stable instance key."""
    item_id: str
    endpoints_world: Tuple[Vec3, ...]
    is_long_extreme_candidate: bool = False


@dataclass(frozen=True)
class RadialSnapshot:
    directions: Tuple[Vec3, ...]
    support_lengths: Tuple[float, ...]
    extreme_candidate_ids: Tuple[str, ...]


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


def _dot(a: Vec3, b: Vec3) -> float:
    value = a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    if not math.isfinite(value):
        raise ValueError("non-finite intermediate dot product")
    return value


def direction_length_to_point(direction: Sequence[float], length: float) -> Vec3:
    """Return endpoint p=d*l; require a unit local-frame direction explicitly."""
    unit = _vec3(direction, "direction")
    radial_length = _finite(length, "length")
    if radial_length < 0.0:
        raise ValueError("length must be nonnegative")
    magnitude = math.hypot(*unit)
    if not math.isfinite(magnitude) or abs(magnitude - 1.0) > _DOT_EPSILON:
        raise ValueError("direction must be unit length")
    point = tuple(unit[i] * radial_length for i in range(3))
    if not all(math.isfinite(component) for component in point):
        raise ValueError("direction-length endpoint is non-finite")
    return point  # type: ignore[return-value]


def _rotate(quaternion: Quaternion, vector: Vec3) -> Vec3:
    """Apply normalized host Hamilton active rotation to a vector."""
    try:
        if len(quaternion) != 4:
            raise ValueError
        q = tuple(_finite(quaternion[i], f"quaternion[{i}]") for i in range(4))
    except (TypeError, IndexError, ValueError) as exc:
        raise ValueError("rotation quaternion must have four finite components") from exc
    norm = math.hypot(*q)
    if not math.isfinite(norm) or norm <= 1e-15:
        raise ValueError("rotation quaternion must be non-zero")
    x, y, z, w = (component / norm for component in q)
    # q * (v, 0) * conjugate(q), expanded without importing vector libraries.
    tx = 2.0 * (y * vector[2] - z * vector[1])
    ty = 2.0 * (z * vector[0] - x * vector[2])
    tz = 2.0 * (x * vector[1] - y * vector[0])
    result = (vector[0] + w * tx + y * tz - z * ty,
              vector[1] + w * ty + z * tx - x * tz,
              vector[2] + w * tz + x * ty - y * tx)
    if not all(math.isfinite(value) for value in result):
        raise ValueError("quaternion transform produced non-finite coordinates")
    return result


def world_to_ball_local(point_world: Sequence[float], ball_position_world: Sequence[float],
                        ball_rotation_world: Sequence[float]) -> Vec3:
    """Transform a world endpoint into local frame under host quaternion algebra."""
    point = _vec3(point_world, "point_world")
    position = _vec3(ball_position_world, "ball_position_world")
    delta = tuple(point[i] - position[i] for i in range(3))
    if not all(math.isfinite(value) for value in delta):
        raise ValueError("world-to-local subtraction overflowed")
    try:
        if len(ball_rotation_world) != 4:
            raise ValueError
        q = tuple(_finite(ball_rotation_world[i], f"ball_rotation_world[{i}]")
                  for i in range(4))
    except (TypeError, IndexError, ValueError) as exc:
        raise ValueError("ball_rotation_world must be a finite quaternion") from exc
    inverse = (-q[0], -q[1], -q[2], q[3])
    return _rotate(inverse, delta)  # type: ignore[arg-type]


def _fixed_directions(count: int) -> Tuple[Vec3, ...]:
    # Deterministic Fibonacci sphere; count is a provisional prototype setting.
    golden_angle = math.pi * (3.0 - math.sqrt(5.0))
    result = []
    for i in range(count):
        y = 1.0 - 2.0 * (i + 0.5) / count
        radius = math.sqrt(max(0.0, 1.0 - y * y))
        theta = golden_angle * i
        result.append((math.cos(theta) * radius, y, math.sin(theta) * radius))
    return tuple(result)


class RadialProxy:
    """Sparse radial supports with per-item contributions and atomic rebuilding.

    Inserting a confirmed item only recalculates directions in its endpoint
    hemispheres.  A removal reconstructs affected supports from every surviving
    item, so a smaller co-directional contribution survives the largest one's
    detachment.  Candidate overflow rejects; it never silently evicts an item.
    """
    def __init__(self, core_radius: float, config: RadialConfig = RadialConfig()):
        radius = _finite(core_radius, "core_radius")
        if radius < 0.0:
            raise ValueError("core_radius must be nonnegative")
        if isinstance(config.direction_count, bool) or config.direction_count != 32:
            raise ValueError("direction_count is fixed at provisional value 32")
        if (isinstance(config.max_extreme_candidates, bool)
                or not isinstance(config.max_extreme_candidates, int)
                or not 0 <= config.max_extreme_candidates <= 4):
            raise ValueError("max_extreme_candidates must be an integer in [0, 4]")
        self.config = config
        self.core_radius = radius
        self.directions = _fixed_directions(config.direction_count)
        self._supports = [self.core_radius] * config.direction_count
        self._items: Dict[str, Tuple[Vec3, ...]] = {}
        self._candidates: Dict[str, Tuple[Vec3, ...]] = {}
        self._item_directions: Dict[str, Tuple[int, ...]] = {}

    @property
    def candidate_ids(self) -> Tuple[str, ...]:
        return tuple(sorted(self._candidates))

    @property
    def support_lengths(self) -> Tuple[float, ...]:
        return tuple(self._supports)

    def _near_directions(self, points: Tuple[Vec3, ...]) -> Tuple[int, ...]:
        return tuple(i for i, direction in enumerate(self.directions)
                     if any(_dot(point, direction) > 0.0 for point in points))

    def _support_for(self, index: int, items: Dict[str, Tuple[Vec3, ...]]) -> float:
        direction = self.directions[index]
        value = self.core_radius
        for points in items.values():
            for point in points:
                value = max(value, _dot(point, direction))
        if not math.isfinite(value):
            raise ValueError("radial support is non-finite")
        return value

    def attach(self, proxy: AttachmentProxy, ball_position_world: Sequence[float],
               ball_rotation_world: Sequence[float]) -> Tuple[int, ...]:
        if not isinstance(proxy.item_id, str) or not proxy.item_id.strip():
            raise ValueError("item_id must be a non-empty stable instance key")
        if proxy.item_id in self._items:
            raise ValueError("item is already attached")
        if not isinstance(proxy.is_long_extreme_candidate, bool):
            raise ValueError("is_long_extreme_candidate must be bool")
        try:
            endpoints = tuple(_vec3(point, "endpoint_world")
                              for point in proxy.endpoints_world)
        except TypeError as exc:
            raise ValueError("endpoints_world must be an iterable of 3-vectors") from exc
        if not endpoints:
            raise ValueError("at least one pre-baked proxy endpoint is required")
        if (proxy.is_long_extreme_candidate
                and len(self._candidates) >= self.config.max_extreme_candidates):
            raise ValueError("long-object extreme candidate limit reached")

        local_points = tuple(world_to_ball_local(point, ball_position_world,
                                                 ball_rotation_world)
                             for point in endpoints)
        affected = self._near_directions(local_points)
        proposed_items = dict(self._items)
        proposed_items[proxy.item_id] = local_points
        updated = {index: self._support_for(index, proposed_items) for index in affected}

        # Commit only after every transform and affected support validated.
        self._items = proposed_items
        self._item_directions[proxy.item_id] = affected
        if proxy.is_long_extreme_candidate:
            self._candidates[proxy.item_id] = local_points
        for index, value in updated.items():
            self._supports[index] = value
        return affected

    def detach(self, item_id: str) -> Tuple[int, ...]:
        """Remove one stable item contribution; no world drop trajectory is owned here."""
        if item_id not in self._items:
            return ()
        affected = self._item_directions[item_id]
        proposed_items = dict(self._items)
        del proposed_items[item_id]
        updated = {index: self._support_for(index, proposed_items) for index in affected}

        self._items = proposed_items
        del self._item_directions[item_id]
        self._candidates.pop(item_id, None)
        for index, value in updated.items():
            self._supports[index] = value
        return affected

    def rebuild(self, core_radius: float,
                ledger: Iterable[Tuple[AttachmentProxy, Vec3, Quaternion]]) -> None:
        """Atomically replace state from caller-supplied confirmed ledger entries."""
        rebuilt = RadialProxy(core_radius, self.config)
        for proxy, position, rotation in ledger:
            rebuilt.attach(proxy, position, rotation)
        self.core_radius = rebuilt.core_radius
        self.directions = rebuilt.directions
        self._supports = rebuilt._supports
        self._items = rebuilt._items
        self._candidates = rebuilt._candidates
        self._item_directions = rebuilt._item_directions

    def support_length(self, direction_index: int) -> float:
        if (isinstance(direction_index, bool) or not isinstance(direction_index, int)
                or not 0 <= direction_index < len(self._supports)):
            raise IndexError("direction_index outside the fixed direction set")
        return self._supports[direction_index]

    def protrusion(self, direction_index: int) -> float:
        """max(0, l_i-r); core radius is supplied by caller, not derived here."""
        return max(0.0, self.support_length(direction_index) - self.core_radius)

    def snapshot(self) -> RadialSnapshot:
        return RadialSnapshot(self.directions, self.support_lengths, self.candidate_ids)
