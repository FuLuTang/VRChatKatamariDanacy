"""Ride geometry: ball framing, eye point and seat pose, derived from three stated numbers.

This module owns the *derivation*, not a second runtime. It answers, for a ball radius ``r``:

* where the player's **eye** must be (``up_m`` above / ``back_m`` behind the ball centre),
* what that does to the ball's **screen position** at pitch 0,
* where the **seat** (the ``VRCStation`` transform, i.e. the avatar's root) must be so the
  player's head lands on that eye point.

The three inputs are ``alpha`` (the ball's centre, in degrees below the horizon at pitch 0),
``beta`` (the ball's angular radius) and ``eye_floor_m`` (the eye's minimum height above the
ground the ball rests on). They are **not** independent knobs: each is derived below from a
measured number or a stated design intent, so a reader can check the arithmetic instead of
trusting a coefficient. The runtime keeps three serialized floats on ``KatamariBallMotor``
and the EditMode tests pin the C# against ``eye_for_radius`` here.

Why the eye point needs a *floor* at all: VRChat's camera is the avatar's head, so the eye
point is reachable only through the seat, and the seat is ``eye - seatedHeadOffset``. A
proportional-only eye point (``h = k * r``) drops the seat below the ground for a small ball
(``r = 0.15`` gives ``h = 0.267``, so the seat lands 0.79 m underground). The floor is what a
human body forces, not a taste: it is the tallest seated head this project measured plus a
clearance so a seat at the floor is on the ground, not in it.

Two regimes, one formula::

    h(r) = max( (sin alpha / sin beta) * r , eye_floor_m - r )
    d(r) = h(r) / tan(alpha)

``alpha`` is invariant in both branches, so the ball's centre is always at the same place in
the frame; only its apparent size changes, saturating at ``beta`` once ``r`` passes the
crossover. Clamping ``h`` (the rails) rescales ``d`` with it, so a rail can change how big the
ball looks but never where it sits.

Units are metres and degrees; the coordinate frame is Unity world (left-handed, +Y up).
"""
from dataclasses import dataclass
import math

Vector3 = tuple


# --------------------------------------------------------------------------------------
# Measured inputs. Each is a fact about a measurement, not a knob.
# --------------------------------------------------------------------------------------

# The owner's report (2026-11-15): with the shipped constants the ball's centre sat 40.1 deg
# below the crosshair and the ball was entirely outside a 951x431 view, its centre projecting
# 99 px below the bottom edge. A pinhole projection puts a point at 40.1 deg exactly 99 px
# below the bottom of a 431 px frame when the vertical half-FOV is 30 deg - i.e. the default
# 60 deg vertical FOV. So this is the FOV the report was taken at, not a preference.
MEASURED_HALF_FOV_DEG = 30.0

# Tallest seated head this repo has measured: ClientSim, 2026-11-15. Real hardware measured
# ~0.85 m (Docs/Design/BallMotor/README.md, "跟随架"). The rail is the max because a clamp
# below a real measurement leaves the camera visibly high.
MEASURED_SEATED_HEAD_ABOVE_SEAT_M = 1.204159


# --------------------------------------------------------------------------------------
# Stated design intents. Two numbers; everything else below is arithmetic.
# --------------------------------------------------------------------------------------

# "The ball occupies the lower third of the view": the ball's DIAMETER spans one third of the
# frame height. Frame height = 2 * half_fov, so 2*beta = (1/3) * 2 * half_fov.
BALL_DIAMETER_FRAME_FRACTION = 1.0 / 3.0

# The silhouette must not be tangent to the bottom edge: a tangent ball loses its bottom pixel
# row to rounding and to the height filter's overshoot (5 Hz low-pass, a few cm), and a user
# FOV a few degrees narrower would clip it. 2 deg is far larger than both effects.
BOTTOM_MARGIN_DEG = 2.0

# A seat at the floor must be on the ground, not in it.
SEAT_GROUND_CLEARANCE_M = 0.05


@dataclass(frozen=True)
class Frame:
    """The framing model's inputs; every default is the one place its number lives."""

    half_fov_deg: float = MEASURED_HALF_FOV_DEG
    ball_frame_fraction: float = BALL_DIAMETER_FRAME_FRACTION
    bottom_margin_deg: float = BOTTOM_MARGIN_DEG
    seated_head_m: float = MEASURED_SEATED_HEAD_ABOVE_SEAT_M
    seat_clearance_m: float = SEAT_GROUND_CLEARANCE_M

    # ---- derived (checked by the tests, never typed twice) ----
    @property
    def beta_deg(self) -> float:
        """The ball's angular radius: half its diameter, `fraction` of the frame height."""
        return self.half_fov_deg * self.ball_frame_fraction

    @property
    def alpha_deg(self) -> float:
        """The ball centre's angle below the horizon: as low as the bottom margin allows."""
        return self.half_fov_deg - self.beta_deg - self.bottom_margin_deg

    @property
    def eye_floor_m(self) -> float:
        """Minimum eye height above the ground the ball rests on (a seat on that ground)."""
        return self.seated_head_m + self.seat_clearance_m

    @property
    def up_per_radius(self) -> float:
        """`h = up_per_radius * r` in the framing branch: sin(alpha)/sin(beta)."""
        return math.sin(math.radians(self.alpha_deg)) / math.sin(math.radians(self.beta_deg))

    @property
    def back_per_radius(self) -> float:
        """`d = back_per_radius * r` in the framing branch: cos(alpha)/sin(beta)."""
        return math.cos(math.radians(self.alpha_deg)) / math.sin(math.radians(self.beta_deg))

    @property
    def crossover_radius_m(self) -> float:
        """Radius at which the framing branch overtakes the eye floor."""
        return self.eye_floor_m / (1.0 + self.up_per_radius)


@dataclass(frozen=True)
class EyeGeometry:
    """One eye point, plus the two angles a viewer sees at pitch 0.

    ``alpha_deg`` is positive BELOW the horizon (the camera looks horizontally, so this is
    also the ball centre's angle below the crosshair). ``beta_deg`` is the silhouette's angular
    radius. Both come from the geometry, never from the coefficients, so a legacy linear pair
    reports the angle it actually produces.
    """

    radius_m: float
    up_m: float
    back_m: float
    alpha_deg: float
    beta_deg: float

    @property
    def distance_m(self) -> float:
        """Eye-to-ball-centre distance."""
        return math.hypot(self.up_m, self.back_m)

    @property
    def top_deg(self) -> float:
        """Silhouette top, below the horizon (negative would mean it crosses the horizon)."""
        return self.alpha_deg - self.beta_deg

    @property
    def bottom_deg(self) -> float:
        """Silhouette bottom, below the horizon."""
        return self.alpha_deg + self.beta_deg

    @property
    def eye_above_ground_m(self) -> float:
        """Eye height above the plane the ball rests on (ball centre + radius)."""
        return self.radius_m + self.up_m

    @property
    def clearance_above_surface_m(self) -> float:
        """Eye distance above the ball's own top surface along +Y."""
        return self.eye_above_ground_m - 2.0 * self.radius_m


def _geometry(radius_m: float, up_m: float, back_m: float) -> EyeGeometry:
    if not math.isfinite(radius_m) or not math.isfinite(up_m) or not math.isfinite(back_m):
        raise ValueError("ride geometry inputs must be finite")
    if radius_m < 0.0 or up_m < 0.0 or back_m < 0.0:
        raise ValueError("radius, up and back must be non-negative")
    if back_m <= 0.0:
        raise ValueError("back must be positive or the angle below the horizon is undefined")
    distance = math.hypot(up_m, back_m)
    alpha = math.degrees(math.atan2(up_m, back_m))
    # asin is exact for a sphere's silhouette half-angle; the ball always fits inside the
    # distance because up and back are measured FROM the centre.
    beta = math.degrees(math.asin(min(1.0, radius_m / distance)))
    return EyeGeometry(radius_m, up_m, back_m, alpha, beta)


def eye_for_radius(radius_m: float, frame: Frame = Frame()) -> EyeGeometry:
    """The proposed eye point: framing branch, floored by the human body branch."""
    if not math.isfinite(radius_m) or radius_m < 0.0:
        raise ValueError("radius must be finite and non-negative")
    framing_up = frame.up_per_radius * radius_m
    floor_up = frame.eye_floor_m - radius_m
    up = framing_up if framing_up >= floor_up else floor_up
    if up < 0.0:
        # A ball taller than the eye floor: the eye sits at the centre's height, i.e. level
        # with it. Reached only if the floor is ever set below a product radius.
        up = 0.0
    back = up / math.tan(math.radians(frame.alpha_deg))
    return _geometry(radius_m, up, back)


def legacy_eye(radius_m: float,
               up_base_m: float = 0.55, up_per_radius: float = 2.4,
               back_base_m: float = 0.75, back_per_radius: float = 2.2) -> EyeGeometry:
    """The shipped 2026-11-15 linear model, for the regression and the algebra in the doc."""
    return _geometry(radius_m,
                     up_base_m + up_per_radius * radius_m,
                     back_base_m + back_per_radius * radius_m)


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


@dataclass(frozen=True)
class BallOnScreen:
    """The ball's silhouette in frame pixels at pitch 0 (0 px = top edge)."""

    view_height_px: float
    half_fov_deg: float
    centre_px_from_centre: float
    top_px_from_centre: float
    bottom_px_from_centre: float

    @property
    def centre_px_from_top(self) -> float:
        return self.view_height_px * 0.5 + self.centre_px_from_centre

    @property
    def top_px_from_top(self) -> float:
        return self.view_height_px * 0.5 + self.top_px_from_centre

    @property
    def bottom_px_from_top(self) -> float:
        return self.view_height_px * 0.5 + self.bottom_px_from_centre

    @property
    def centre_px_below_bottom(self) -> float:
        """Positive means the centre is outside the frame, below the bottom edge."""
        return self.centre_px_from_centre - self.view_height_px * 0.5

    @property
    def top_px_below_bottom(self) -> float:
        return self.top_px_from_centre - self.view_height_px * 0.5

    @property
    def centre_visible(self) -> bool:
        return self.centre_px_below_bottom < 0.0

    @property
    def silhouette_fully_visible(self) -> bool:
        return self.bottom_px_below_bottom < 0.0

    @property
    def bottom_px_below_bottom(self) -> float:
        return self.bottom_px_from_centre - self.view_height_px * 0.5


def ball_on_screen(geometry: EyeGeometry, view_height_px: float,
                   half_fov_deg: float) -> BallOnScreen:
    return BallOnScreen(
        view_height_px=view_height_px,
        half_fov_deg=half_fov_deg,
        centre_px_from_centre=px_below_axis(geometry.alpha_deg, view_height_px, half_fov_deg),
        top_px_from_centre=px_below_axis(geometry.top_deg, view_height_px, half_fov_deg),
        bottom_px_from_centre=px_below_axis(geometry.bottom_deg, view_height_px, half_fov_deg),
    )


# --------------------------------------------------------------------------------------
# Seat pose. The seat carries the avatar's ROOT, so it is the eye point minus the measured
# head offset. This is the only quantity the player's own eye height enters.
# --------------------------------------------------------------------------------------

@dataclass(frozen=True)
class SeatPose:
    """Where the station transform must be, in the rig frame, for a measured seated head."""

    up_m: float                 # above the ball centre
    back_m: float               # behind the ball centre, same axis as EyeGeometry.back_m
    above_ground_m: float       # above the plane the ball rests on
    head_m: float               # the seated head offset that produced it

    @property
    def below_ground_m(self) -> float:
        """Positive means the seat is buried: the avatar's legs are under the terrain."""
        return -self.above_ground_m


def seat_for(radius_m: float, geometry: EyeGeometry, seated_head_m: float,
             vertical_limit_m: float = 1.3) -> SeatPose:
    """Seat = eye - clamp(head, 0, verticalLimit).

    The clamp is the legacy rail's behaviour (a measurement beyond it is partly applied);
    `vertical_limit_m=float('inf')` models a world that trusts the measurement in full.
    """
    if not math.isfinite(seated_head_m) or seated_head_m < 0.0:
        raise ValueError("seated head must be finite and non-negative")
    head = seated_head_m
    if head > vertical_limit_m:
        head = vertical_limit_m
    up = geometry.up_m - head
    return SeatPose(up_m=up, back_m=geometry.back_m,
                    above_ground_m=radius_m + up, head_m=seated_head_m)


# --------------------------------------------------------------------------------------
# The table the design doc prints, so the doc cannot drift from the model.
# --------------------------------------------------------------------------------------

PRODUCT_RADII_M = (0.15, 0.30, 0.55, 1.10, 5.0, 15.0)


def table(frame: Frame = Frame(), radii=PRODUCT_RADII_M, view_height_px: float = 431.0):
    """Rows of (r, up, back, distance, ball diameter deg, ball centre px, top px from top)."""
    rows = []
    for radius in radii:
        geometry = eye_for_radius(radius, frame)
        on_screen = ball_on_screen(geometry, view_height_px, frame.half_fov_deg)
        rows.append({
            "radius_m": radius,
            "diameter_m": 2.0 * radius,
            "up_m": geometry.up_m,
            "back_m": geometry.back_m,
            "distance_m": geometry.distance_m,
            "eye_above_ground_m": geometry.eye_above_ground_m,
            "alpha_deg": geometry.alpha_deg,
            "beta_deg": geometry.beta_deg,
            "screen_diameter_deg": 2.0 * geometry.beta_deg,
            "centre_px_from_top": on_screen.centre_px_from_top,
            "seat_above_ground_m": seat_for(radius, geometry, frame.seated_head_m).above_ground_m,
        })
    return rows


if __name__ == "__main__":
    frame = Frame()
    print(f"half_fov={frame.half_fov_deg:g} beta={frame.beta_deg:g} alpha={frame.alpha_deg:g} "
          f"floor={frame.eye_floor_m:.6f} k_up={frame.up_per_radius:.6f} "
          f"k_back={frame.back_per_radius:.6f} crossover={frame.crossover_radius_m:.6f}")
    print("r      D      up      back    dist    eye↑gnd  alpha  beta   diam°  centre_px  seat↑gnd")
    for row in table(frame):
        print(f"{row['radius_m']:<6g} {row['diameter_m']:<6g} "
              f"{row['up_m']:<7.4f} {row['back_m']:<7.4f} {row['distance_m']:<7.4f} "
              f"{row['eye_above_ground_m']:<8.4f} {row['alpha_deg']:<6.3f} {row['beta_deg']:<6.3f} "
              f"{row['screen_diameter_deg']:<6.3f} {row['centre_px_from_top']:<10.2f} "
              f"{row['seat_above_ground_m']:.4f}")
