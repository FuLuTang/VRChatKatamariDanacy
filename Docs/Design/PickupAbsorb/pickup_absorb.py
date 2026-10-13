"""DRAFT offline reference model for pickup/absorb; not a runtime contract."""
from __future__ import annotations

from dataclasses import dataclass
from math import floor, sqrt
from typing import Dict, Iterable, Optional, Set, Tuple


@dataclass(frozen=True)
class Item:
    spawn_id: str
    generation: int
    position: Tuple[float, float, float]
    bounds: Tuple[float, float, float]
    kind: str
    value: float
    absorbable: bool = True

    @property
    def key(self) -> Tuple[str, int]:
        return self.spawn_id, self.generation


class SpatialRegistry:
    """Uniform-grid broadphase; exact proximity is checked by the caller."""
    def __init__(self, cell_size: float):
        if cell_size <= 0:
            raise ValueError("cell_size must be positive")
        self.cell_size = cell_size
        self.items: Dict[Tuple[str, int], Item] = {}
        self.cells: Dict[Tuple[int, int, int], Set[Tuple[str, int]]] = {}
        self._item_cells: Dict[Tuple[str, int], Set[Tuple[int, int, int]]] = {}

    def _cells_for(self, item: Item) -> Set[Tuple[int, int, int]]:
        # Index the item's axis-aligned bounding box (position ± half bounds).
        lo = [floor((p - s / 2) / self.cell_size) for p, s in zip(item.position, item.bounds)]
        hi = [floor((p + s / 2) / self.cell_size) for p, s in zip(item.position, item.bounds)]
        return {(x, y, z) for x in range(lo[0], hi[0] + 1)
                for y in range(lo[1], hi[1] + 1) for z in range(lo[2], hi[2] + 1)}

    def register(self, item: Item) -> None:
        if item.value < 0 or any(size < 0 for size in item.bounds):
            raise ValueError("value and bounds must be non-negative")
        self.remove(item.key)
        self.items[item.key] = item
        cells = self._cells_for(item)
        self._item_cells[item.key] = cells
        for cell in cells:
            self.cells.setdefault(cell, set()).add(item.key)

    def remove(self, key: Tuple[str, int]) -> None:
        self.items.pop(key, None)
        for cell in self._item_cells.pop(key, set()):
            members = self.cells[cell]
            members.discard(key)
            if not members:
                del self.cells[cell]

    def nearby(self, position: Tuple[float, float, float], radius: float) -> Tuple[Item, ...]:
        if radius < 0:
            raise ValueError("radius must be non-negative")
        lo = [floor((p - radius) / self.cell_size) for p in position]
        hi = [floor((p + radius) / self.cell_size) for p in position]
        keys = set()
        for x in range(lo[0], hi[0] + 1):
            for y in range(lo[1], hi[1] + 1):
                for z in range(lo[2], hi[2] + 1):
                    keys.update(self.cells.get((x, y, z), ()))
        return tuple(self.items[k] for k in sorted(keys) if k in self.items)


@dataclass(frozen=True)
class PickupRules:
    # Explicit placeholders: deliberately no accepted/default gameplay tuning.
    contact_radius_world_m: float
    max_item_size_world_m: float
    allowed_types: frozenset[str]
    max_growth_radius_world_m: float


def eligible(item: Item, ball_position: Tuple[float, float, float], radius: float,
             rules: PickupRules) -> bool:
    if not item.absorbable or item.kind not in rules.allowed_types:
        return False
    if max(item.bounds, default=0.0) > rules.max_item_size_world_m:
        return False
    if radius >= rules.max_growth_radius_world_m:
        return False
    distance = sqrt(sum((a - b) ** 2 for a, b in zip(item.position, ball_position)))
    # Bounds proxy permits contact when bounding radius overlaps the ball reach.
    item_radius = sqrt(sum((s / 2) ** 2 for s in item.bounds))
    return distance <= radius + rules.contact_radius_world_m + item_radius


@dataclass(frozen=True)
class Claim:
    request_id: str
    player_id: str
    item_key: Tuple[str, int]
    commit_sequence: int


class AuthorityLedger:
    """Single-authority illustrative arbiter; first valid committed claim wins."""
    def __init__(self):
        self.claims: Dict[Tuple[str, int], Claim] = {}
        self.requests: Dict[str, Claim] = {}
        self.sequence = 0

    def claim(self, request_id: str, player_id: str, item: Item, valid: bool = True) -> Optional[Claim]:
        if request_id in self.requests:
            return self.requests[request_id]
        if not valid or item.key in self.claims:
            return None
        self.sequence += 1
        result = Claim(request_id, player_id, item.key, self.sequence)
        self.claims[item.key] = result
        self.requests[request_id] = result
        return result

    def snapshot(self) -> Tuple[Claim, ...]:
        return tuple(sorted(self.claims.values(), key=lambda c: c.commit_sequence))


class Ball:
    """Confirmed ledger state plus rollback-capable local-only predictions."""
    def __init__(self, base_radius: float, growth_per_value: float, max_radius: float):
        if min(base_radius, growth_per_value, max_radius) < 0 or base_radius > max_radius:
            raise ValueError("invalid growth parameters")
        self.base_radius = base_radius
        self.growth_per_value = growth_per_value
        self.max_radius = max_radius
        self.confirmed: Dict[Tuple[str, int], float] = {}
        self.predicted: Dict[Tuple[str, int], float] = {}

    @property
    def confirmed_value(self) -> float:
        return sum(self.confirmed.values())

    @property
    def radius(self) -> float:
        return min(self.max_radius, self.base_radius + self.growth_per_value * self.confirmed_value)

    def predict(self, item: Item) -> None:
        if item.key not in self.confirmed:
            self.predicted[item.key] = item.value

    def reconcile(self, authority: AuthorityLedger, registry: SpatialRegistry) -> None:
        self.predicted.clear()
        self.confirmed = {claim.item_key: registry.items[claim.item_key].value
                          for claim in authority.snapshot() if claim.item_key in registry.items}

    def rebuild_from_snapshot(self, claims: Iterable[Claim], registry: SpatialRegistry) -> None:
        self.predicted.clear()
        self.confirmed = {c.item_key: registry.items[c.item_key].value for c in claims
                          if c.item_key in registry.items}
