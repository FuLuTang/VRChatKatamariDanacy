"""DRAFT host-only size mapping; capped presentation never mutates a ledger."""
from dataclasses import dataclass
import math
import sys


@dataclass(frozen=True)
class CoreRadiusWorldM:
    value: float
    def __post_init__(self): _positive_finite(self.value, "coreRadiusWorldM")

@dataclass(frozen=True)
class CollisionRadiusWorldM:
    value: float
    def __post_init__(self): _positive_finite(self.value, "collisionRadiusWorldM")

@dataclass(frozen=True)
class SemanticDiameter:
    value: float
    def __post_init__(self): _positive_finite(self.value, "semanticDiameter")

@dataclass(frozen=True)
class SizeParameters:
    baseline_volume_units: float
    cubic_meters_per_volume_unit: float
    max_core_radius_world_m: float
    collision_to_core_radius_ratio: float
    semantic_diameter_per_core_diameter: float
    def __post_init__(self):
        for name in ("baseline_volume_units", "cubic_meters_per_volume_unit",
                     "max_core_radius_world_m", "collision_to_core_radius_ratio",
                     "semantic_diameter_per_core_diameter"):
            _positive_finite(getattr(self, name), name)

@dataclass(frozen=True)
class SizeState:
    core_radius_world_m: CoreRadiusWorldM
    collision_radius_world_m: CollisionRadiusWorldM
    semantic_diameter: SemanticDiameter
    effective_volume_units: float


def _as_finite(v, name):
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise ValueError(f"{name} must be numeric and finite")
    try:
        converted = float(v)
    except (OverflowError, ValueError):
        raise ValueError(f"{name} is outside finite float range") from None
    if not math.isfinite(converted):
        raise ValueError(f"{name} must be finite")
    return converted

def _positive_finite(v, name):
    if _as_finite(v, name) <= 0:
        raise ValueError(f"{name} must be positive and finite")

def _nonnegative_finite(v, name):
    if _as_finite(v, name) < 0:
        raise ValueError(f"{name} must be non-negative and finite")

def _finite_result(value, name):
    if not math.isfinite(value):
        raise OverflowError(f"{name} overflow")
    return value

def _log_radius(volume_units: float, scale: float) -> float:
    """log sphere-equivalent radius, avoiding product/cube intermediate overflow."""
    if volume_units <= 0:
        raise ValueError("volume must be positive")
    return (math.log(volume_units) + math.log(scale) + math.log(3.0)
            - math.log(4.0 * math.pi)) / 3.0

def _radius_from_log(log_radius: float) -> float:
    if log_radius > math.log(sys.float_info.max):
        raise OverflowError("core radius overflow")
    value = math.exp(log_radius)
    return _finite_result(value, "core radius")

def core_radius_from_volume(volume_units: float, cubic_meters_per_volume_unit: float) -> CoreRadiusWorldM:
    _positive_finite(volume_units, "volume_units")
    _positive_finite(cubic_meters_per_volume_unit, "cubic_meters_per_volume_unit")
    radius = _radius_from_log(_log_radius(float(volume_units), float(cubic_meters_per_volume_unit)))
    return CoreRadiusWorldM(radius)

def derive_size(confirmed_volume_units: float, parameters: SizeParameters) -> SizeState:
    """Derive presented dimensions; cap applies only to this mapping."""
    _nonnegative_finite(confirmed_volume_units, "confirmed_volume_units")
    base = float(parameters.baseline_volume_units)
    scale = float(parameters.cubic_meters_per_volume_unit)
    max_radius = float(parameters.max_core_radius_world_m)
    base_log_r = _log_radius(base, scale)
    cap_log_r = math.log(max_radius)
    if cap_log_r < base_log_r:
        raise ValueError("max radius is below baseline radius")

    confirmed = float(confirmed_volume_units)
    # log-sum-exp preserves the exact mathematical sum domain without overflowing.
    log_base = math.log(base)
    if confirmed == 0.0:
        log_total = log_base
    else:
        log_confirmed = math.log(confirmed)
        hi, lo = max(log_base, log_confirmed), min(log_base, log_confirmed)
        log_total = hi + math.log1p(math.exp(lo - hi))
    cap_log_volume = math.log(4.0 * math.pi / 3.0) + 3.0 * cap_log_r - math.log(scale)
    log_effective = min(log_total, cap_log_volume)
    # Expose a finite effective-volume diagnostic; radius uses log_effective directly.
    effective = (sys.float_info.max if log_effective >= math.log(sys.float_info.max)
                 else math.exp(log_effective))
    if effective <= 0 or not math.isfinite(effective):
        raise OverflowError("effective presentation volume invalid")
    raw_log_r = (log_effective + math.log(scale) + math.log(3.0)
                 - math.log(4.0 * math.pi)) / 3.0
    core_value = max_radius if raw_log_r >= cap_log_r else _radius_from_log(raw_log_r)
    collision_value = _finite_result(core_value * parameters.collision_to_core_radius_ratio,
                                     "collision radius")
    semantic_value = _finite_result((2.0 * core_value) * parameters.semantic_diameter_per_core_diameter,
                                    "semantic diameter")
    return SizeState(CoreRadiusWorldM(core_value), CollisionRadiusWorldM(collision_value),
                     SemanticDiameter(semantic_value), effective)

def rebuild_after_reset(new_confirmed_volume_units: float, parameters: SizeParameters) -> SizeState:
    return derive_size(new_confirmed_volume_units, parameters)

def core_diameter_world_m(radius: CoreRadiusWorldM) -> float:
    if not isinstance(radius, CoreRadiusWorldM):
        raise TypeError("expected CoreRadiusWorldM, not a diameter or collision radius")
    return _finite_result(2.0 * radius.value, "core diameter")
