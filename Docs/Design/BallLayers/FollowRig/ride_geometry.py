"""Ride geometry: eye point and rider size, all proportional to the ball radius.

This module owns the *derivation*, not a second runtime; ``RideViewMath``, ``RideScaleMath`` and
``RideWaistMath`` in Runtime/Riding are the C# twins and the EditMode tests pin them against the
hand values the tests here derive. For a ball of radius ``r`` (semantic diameter ``D = 2 r``) it
answers:

* where the **eye point** is: ``up`` above and ``back`` behind the ball centre (what the follow
  frame writes to the rig root; the rider's head must land there),
* how big the **rider** must be (the eye height ``E`` to ask VRChat for) so that the rider's
  **hips are level with the top of the ball** (owner 2026-11-17), given ``kappa``: the avatar's
  own vertical distance from the viewpoint down to the hips, per metre of eye height. ``kappa`` is
  measured per avatar when it sits down; nothing here knows a body proportion.

Every length is a constant times ``r`` (no additive base, no floor), so the picture is identical
at any ball size: the motor's scale-invariant motion and this view are the same similarity
transform. Units are metres and degrees; frame: Unity world (left-handed, +Y up).

The chain (see Docs/Design/BallLayers/FollowRig/RIDE_GEOMETRY.md)::

    up   = (sin alpha / sin beta) * r         back = up / tan alpha        # the view rule
    hips_world = eye_point - kappa * E
    hips_world = ball_top = ball_centre + r                                # the owner's rule
      =>  E = (up - r) / kappa
"""
from dataclasses import dataclass
import math


# --------------------------------------------------------------------------------------
# The view rule. Two stated design intents plus the measured field of view.
# --------------------------------------------------------------------------------------

# The owner's report (2026-11-15): with the old constants the ball centre sat 40.1 deg below the
# crosshair and projected 99 px below the bottom of a 951x431 view. A pinhole puts a point at
# 40.1 deg exactly 99 px below a 431 px frame only when the vertical half-FOV is 30 deg, i.e. the
# default 60 deg vertical FOV: this is the FOV the report was taken at, not a preference.
HALF_FOV_DEG = 30.0

# "The ball occupies the lower third of the view": the ball's DIAMETER is a third of the frame
# height, so 2*beta = (1/3) * 2 * half_fov.
BALL_DIAMETER_FRAME_FRACTION = 1.0 / 3.0

# The silhouette must not be tangent to the bottom edge (filter overshoot, a user FOV a few
# degrees narrower): 2 deg is far larger than both.
BOTTOM_MARGIN_DEG = 2.0

BETA_DEG = HALF_FOV_DEG * BALL_DIAMETER_FRAME_FRACTION                 # ball angular radius
ALPHA_DEG = HALF_FOV_DEG - BETA_DEG - BOTTOM_MARGIN_DEG                # centre below horizon
UP_PER_RADIUS = math.sin(math.radians(ALPHA_DEG)) / math.sin(math.radians(BETA_DEG))
BACK_PER_RADIUS = UP_PER_RADIUS / math.tan(math.radians(ALPHA_DEG))

# The eye is this far above the ball's top, per metre of ball radius: (up - r) / r.
EYE_ABOVE_TOP_PER_RADIUS = UP_PER_RADIUS - 1.0

# Udon's target eye height is clamped to this range; VRChat supports nothing outside it
# (https://creators.vrchat.com/avatars/avatar-scaling/, https://docs.vrchat.com/docs/osc-avatar-scaling).
MIN_EYE_HEIGHT_M = 0.1
MAX_EYE_HEIGHT_M = 100.0

START_BALL_DIAMETER_M = 0.222


def eye_offset(radius_m: float) -> tuple:
    """(up, back): the eye point above and behind the ball centre."""
    if not math.isfinite(radius_m) or radius_m < 0.0:
        raise ValueError("radius must be finite and non-negative")
    return UP_PER_RADIUS * radius_m, BACK_PER_RADIUS * radius_m


def eye_above_waist(radius_m: float) -> float:
    """How far above the ball's top the eye point is (m): where a waist on the top sits below it."""
    up, _ = eye_offset(radius_m)
    return up - radius_m


def eye_height_for_diameter(diameter_m: float, kappa: float) -> tuple:
    """(E, clamped): the avatar eye height to request, and whether VRChat's range clamped it."""
    if not math.isfinite(diameter_m) or diameter_m <= 0.0:
        raise ValueError("diameter must be finite and positive")
    if not math.isfinite(kappa) or kappa <= 0.0:
        raise ValueError("kappa must be finite and positive")
    wanted = eye_above_waist(diameter_m / 2.0) / kappa
    clamped = min(max(wanted, MIN_EYE_HEIGHT_M), MAX_EYE_HEIGHT_M)
    return clamped, clamped != wanted


@dataclass(frozen=True)
class Ride:
    """Everything about one ball size and one avatar (kappa), in metres and degrees."""

    diameter_m: float
    kappa: float
    up_m: float                   # eye above the ball centre
    back_m: float                 # eye behind the ball centre
    eye_height_m: float           # E: what SetAvatarEyeHeightByMeters is asked for
    hips_below_eye_m: float       # kappa * E (the clamp can make it differ from the rule)
    waist_above_ball_bottom_m: float
    clamped: bool

    @property
    def alpha_deg(self) -> float:
        return math.degrees(math.atan2(self.up_m, self.back_m))

    @property
    def beta_deg(self) -> float:
        return math.degrees(math.asin(min(1.0, self.diameter_m / 2.0
                                          / math.hypot(self.up_m, self.back_m))))

    @property
    def bottom_deg(self) -> float:
        return self.alpha_deg + self.beta_deg

    @property
    def top_deg(self) -> float:
        return self.alpha_deg - self.beta_deg


def ride_for(diameter_m: float, kappa: float) -> Ride:
    """The rider/ball arrangement for a ball of this semantic diameter and an avatar's kappa."""
    radius = diameter_m / 2.0
    up, back = eye_offset(radius)
    eye_height, clamped = eye_height_for_diameter(diameter_m, kappa)
    hips_below = kappa * eye_height
    return Ride(diameter_m=diameter_m, kappa=kappa, up_m=up, back_m=back,
                eye_height_m=eye_height, hips_below_eye_m=hips_below,
                waist_above_ball_bottom_m=radius + up - hips_below, clamped=clamped)


# --------------------------------------------------------------------------------------
# Smoothing and the rate limit of the eye height request (BallRideStation).
# --------------------------------------------------------------------------------------

def smooth(current: float, target: float, dt_s: float, hz: float) -> float:
    """The project's usual 1 - exp(-2 pi hz dt) low pass; zero hz or dt holds the value."""
    if hz <= 0.0 or dt_s <= 0.0:
        return current
    alpha = 1.0 - math.exp(-2.0 * math.pi * hz * dt_s)
    return current + (target - current) * alpha


def should_apply(applied: float, candidate: float, step_fraction: float) -> bool:
    """Debounce: only a change of at least ``step_fraction`` of the applied value is sent."""
    if applied <= 0.0:
        return True
    return abs(candidate - applied) >= applied * step_fraction


def should_send(applied: float, candidate: float, step_fraction: float,
                seconds_since_send: float, min_interval_s: float) -> bool:
    """Rate limit: the debounce step and at least ``min_interval_s`` since the last send."""
    return seconds_since_send >= min_interval_s and should_apply(applied, candidate, step_fraction)


# --------------------------------------------------------------------------------------
# Screen projection. A pinhole camera: y = (H/2) * tan(theta) / tan(half_fov).
# --------------------------------------------------------------------------------------

def px_below_axis(angle_below_axis_deg: float, view_height_px: float,
                  half_fov_deg: float) -> float:
    """Signed pixels below the frame's centre line; negative is above it."""
    if half_fov_deg <= 0.0 or half_fov_deg >= 90.0:
        raise ValueError("half_fov must be in (0, 90) degrees")
    return (view_height_px * 0.5) * math.tan(math.radians(angle_below_axis_deg)) \
        / math.tan(math.radians(half_fov_deg))


# --------------------------------------------------------------------------------------
# The table the design doc prints, so the doc cannot drift from the model. The kappa values are
# illustrations of the spread between avatars, not measurements.
# --------------------------------------------------------------------------------------

PRODUCT_DIAMETERS_M = (0.222, 0.5, 0.8, 2.0, 20.0)
ILLUSTRATIVE_KAPPAS = (0.2, 0.35, 0.5)


def table(diameters=PRODUCT_DIAMETERS_M, kappas=ILLUSTRATIVE_KAPPAS):
    return [ride_for(d, k) for d in diameters for k in kappas]


if __name__ == "__main__":
    print(f"alpha={ALPHA_DEG:g} beta={BETA_DEG:g} k_up={UP_PER_RADIUS:.6f} "
          f"k_back={BACK_PER_RADIUS:.6f} (up-r)/r={EYE_ABOVE_TOP_PER_RADIUS:.6f}")
    print("D       kappa  E        hips<eye  waist^bottom  bottom deg")
    for ride in table():
        print(f"{ride.diameter_m:<7g} {ride.kappa:<6g} {ride.eye_height_m:<8.4f} "
              f"{ride.hips_below_eye_m:<9.4f} {ride.waist_above_ball_bottom_m:<13.4f} "
              f"{ride.bottom_deg:.2f}")
