"""Ride geometry: the push stance, all proportional to the ball radius.

This module owns the *derivation*, not a second runtime; ``RideViewMath``, ``RideScaleMath`` and
``RideStanceMath`` in Runtime/Riding are the C# twins and the EditMode tests pin them against the
same hand values the tests here derive. For a ball of radius ``r`` (semantic diameter ``D = 2 r``)
it answers:

* where the **eye point** is: ``up`` above and ``back`` behind the ball centre (what the follow
  frame writes to the rig root; the rider's head must land there),
* how big the **rider** is (the eye height ``E`` to ask VRChat for): standing behind the ball, feet
  on the ground, and a hand reaching from the shoulder ``PUSH_ANGLE_DEG`` below the horizontal
  meets the ball's back surface with the arm along the ball's radius.

The body numbers are those of the PLAYER's real body, because in VR the hands are the player's own
controllers and VRChat maps the real body onto the avatar's eye height. The avatar only enters
through ``eta`` (its measured head height above the standing station's anchor per metre of eye
height, about 1 for a standing pose), which the runtime measures and divides the eye height by.

Every length is a constant times ``r`` (no additive base), so contact point, ball top, eye, feet
and hands form one figure that only scales: the motor's scale-invariant motion and this stance are
the same similarity transform. Units: metres, degrees. Side view: x forward, y up, ball contact
point with the ground at the origin.

The chain (Docs/Design/BallLayers/FollowRig/RIDE_GEOMETRY.md section 1)::

    shoulder = eye + (-SHOULDER_BEHIND * E, -SHOULDER_BELOW * E)
    centre   = shoulder + (reach * E + r) * (cos psi, -sin psi)      # arm along the radius
    eye.y    = E                                                     # feet on the ground
      =>  e = E / r = (1 + sin psi) / (1 - SHOULDER_BELOW - reach * sin psi)
          up = (e - 1) r,  back = (-SHOULDER_BEHIND * e + (1 + reach * e) cos psi) r
"""
from dataclasses import dataclass
import math


# --------------------------------------------------------------------------------------
# The real player's body, per metre of eye height. Segment proportions after Drillis & Contini
# (1966) as reproduced in Winter, "Biomechanics and Motor Control of Human Movement": eye 0.936 H,
# shoulder 0.818 H, upper arm 0.186 H, forearm 0.146 H, hand 0.108 H (palm centre taken at half).
# SHOULDER_BEHIND is an estimate (eyes about 9 cm ahead of the shoulder joints at 1.62 m eye height).
# --------------------------------------------------------------------------------------

SHOULDER_BELOW = 0.125            # (0.936 - 0.818) / 0.936 = 0.126
SHOULDER_BEHIND = 0.055           # estimate
ARM_REACH = 0.41                  # (0.186 + 0.146 + 0.054) / 0.936 = 0.412, shoulder to palm centre
PUSH_REACH_FRACTION = 0.8         # elbows slightly bent
PUSH_ANGLE_DEG = 45.0             # the one tuning knob

# Udon's target eye height is clamped to this range; VRChat supports nothing outside it
# (https://creators.vrchat.com/avatars/avatar-scaling/).
MIN_EYE_HEIGHT_M = 0.1
MAX_EYE_HEIGHT_M = 100.0

# The panel layout is authored for this eye height; the panel root scales with E / this.
PANEL_REFERENCE_EYE_M = 1.64

# Panels as (name, azimuth right deg, elevation up deg, width units, height units, metres per unit),
# centre pivot, distance 1.6 m at the reference eye (RideUxAssembler carries the same numbers). The
# HUD sits low on the left, beside the ball, so a rider looking down at the hands sees it too.
PANELS = (("StoryHud", -38.0, -12.0, 1000.0, 260.0, 0.0014),
          ("RideResultCard", 0.0, 6.0, 1152.0, 576.0, 0.001),
          ("RideExitPanel", 24.0, -8.0, 360.0, 150.0, 0.0014))
PANEL_DISTANCE_M = 1.6


def push_reach() -> float:
    return PUSH_REACH_FRACTION * ARM_REACH


def eye_height_per_radius(psi_deg: float = PUSH_ANGLE_DEG) -> float:
    s = math.sin(math.radians(psi_deg))
    denominator = 1.0 - SHOULDER_BELOW - push_reach() * s
    if denominator <= 0.0:
        raise ValueError("the arm cannot reach down that far")
    return (1.0 + s) / denominator


def eye_up_per_radius(psi_deg: float = PUSH_ANGLE_DEG) -> float:
    return eye_height_per_radius(psi_deg) - 1.0


def eye_back_per_radius(psi_deg: float = PUSH_ANGLE_DEG) -> float:
    e = eye_height_per_radius(psi_deg)
    return -SHOULDER_BEHIND * e + (1.0 + push_reach() * e) * math.cos(math.radians(psi_deg))


def eye_height_for_diameter(diameter_m: float, eta: float = 1.0) -> tuple:
    """(E, clamped): the avatar eye height to request, and whether VRChat's range clamped it."""
    if not math.isfinite(diameter_m) or diameter_m <= 0.0:
        raise ValueError("diameter must be finite and positive")
    if not math.isfinite(eta) or eta <= 0.0:
        raise ValueError("eta must be finite and positive")
    wanted = eye_height_per_radius() * diameter_m / 2.0 / eta
    clamped = min(max(wanted, MIN_EYE_HEIGHT_M), MAX_EYE_HEIGHT_M)
    return clamped, clamped != wanted


@dataclass(frozen=True)
class Stance:
    """One push angle, per unit radius (multiply every length by r), in the side view."""

    psi_deg: float
    e: float                       # eye height / r
    up: float                      # eye above the ball centre / r
    back: float                    # eye behind the ball centre / r

    @property
    def eye(self):
        return (-self.back, self.up + 1.0)

    @property
    def shoulder(self):
        return (-self.back - SHOULDER_BEHIND * self.e, self.up + 1.0 - SHOULDER_BELOW * self.e)

    @property
    def hand(self):
        a = math.radians(self.psi_deg)
        return (-math.cos(a), 1.0 + math.sin(a))

    @property
    def feet(self):
        return (-self.back, 0.0)

    @property
    def centre_depression_deg(self) -> float:
        return math.degrees(math.atan2(self.up, self.back))

    @property
    def half_angle_deg(self) -> float:
        return math.degrees(math.asin(1.0 / math.hypot(self.up, self.back)))

    @property
    def top_edge_deg(self) -> float:
        """The ball's upper silhouette below the horizon, at pitch 0."""
        return self.centre_depression_deg - self.half_angle_deg

    @property
    def bottom_edge_deg(self) -> float:
        return self.centre_depression_deg + self.half_angle_deg

    @property
    def ground_visible_ahead_of_centre(self) -> float:
        """Where the ground reappears past the ball, ahead of the ball centre, per r."""
        return self.e / math.tan(math.radians(self.top_edge_deg)) - self.back

    @property
    def top_below_eye_per_e(self) -> float:
        return (self.up - 1.0) / self.e

    @property
    def hand_from_eye_per_e(self):
        """(forward, down) from the eye to the contact point, per metre of eye height."""
        hx, hy = self.hand
        ex, ey = self.eye
        return ((hx - ex) / self.e, (ey - hy) / self.e)

    @property
    def diameter_per_e(self) -> float:
        return 2.0 / self.e


def stance(psi_deg: float = PUSH_ANGLE_DEG) -> Stance:
    return Stance(psi_deg=psi_deg, e=eye_height_per_radius(psi_deg),
                  up=eye_up_per_radius(psi_deg), back=eye_back_per_radius(psi_deg))


def hand_reach_needed_per_e(stance_: Stance, arm_reach: float) -> float:
    """Fraction of a player's straight reach (shoulder to palm, per E) the push uses."""
    return push_reach() / arm_reach


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


def panel_scale(eye_height_m: float) -> float:
    if not math.isfinite(eye_height_m) or eye_height_m <= 0.0:
        return 1.0
    return eye_height_m / PANEL_REFERENCE_EYE_M


def _direction(azimuth_deg: float, elevation_deg: float):
    a, e = math.radians(azimuth_deg), math.radians(elevation_deg)
    return (math.cos(e) * math.sin(a), math.sin(e), math.cos(e) * math.cos(a))


def panel_clearance_deg(panel, stance_: Stance = None, samples: int = 40) -> float:
    """Smallest angle between any point of the panel and the ball's silhouette, degrees (negative
    means the panel overlaps the ball). Pitch 0, eye on the eye point. The panel root scales with E
    and every stance length is proportional to E, so the angle does not depend on the ball size."""
    st = stance_ or stance()
    _, az, el, w, h, scale = panel
    centre_dir = _direction(0.0, -st.centre_depression_deg)
    a, e = math.radians(az), math.radians(el)
    c = [PANEL_DISTANCE_M * x for x in _direction(az, el)]
    right = (math.cos(a), 0.0, -math.sin(a))
    up = (-math.sin(e) * math.sin(a), math.cos(e), -math.sin(e) * math.cos(a))
    best = math.inf
    for i in range(samples + 1):
        for j in range(samples + 1):
            x, y = (i / samples - 0.5) * w * scale, (j / samples - 0.5) * h * scale
            p = [c[k] + right[k] * x + up[k] * y for k in range(3)]
            n = math.sqrt(sum(q * q for q in p))
            dot = sum(p[k] / n * centre_dir[k] for k in range(3))
            best = min(best, math.degrees(math.acos(max(-1.0, min(1.0, dot)))) - st.half_angle_deg)
    return best


# --------------------------------------------------------------------------------------
# Tables and the side-view figure the design doc prints, so the doc cannot drift from the model.
# --------------------------------------------------------------------------------------

PRODUCT_DIAMETERS_M = (0.222, 0.3, 0.5, 0.8, 2.0, 20.0)
CANDIDATE_ANGLES_DEG = (35.0, 40.0, 45.0, 50.0, 55.0)


def size_table(diameters=PRODUCT_DIAMETERS_M):
    """(D, E, up, back, panel distance) in metres at eta = 1."""
    rows = []
    s = stance()
    for d in diameters:
        r = d / 2.0
        e, _ = eye_height_for_diameter(d)
        rows.append((d, e, s.up * r, s.back * r, PANEL_DISTANCE_M * panel_scale(e)))
    return rows


REACH_DIAMETERS_M = (0.3, 1.0, 2.0, 5.0)


def reach_table(diameters=REACH_DIAMETERS_M):
    """(D, E, eye above ground, eye behind the centre, contact forward of the eye, contact below the
    eye, eye-to-contact distance, shoulder-to-contact distance) in metres at eta = 1."""
    rows = []
    s = stance()
    a = math.radians(s.psi_deg)
    for d in diameters:
        r = d / 2.0
        e, _ = eye_height_for_diameter(d)
        fwd = (s.back - math.cos(a)) * r
        down = (s.up - math.sin(a)) * r
        sx, sy = s.shoulder
        hx, hy = s.hand
        rows.append((d, e, e, s.back * r, fwd, down, math.hypot(fwd, down),
                     math.hypot(hx - sx, hy - sy) * r))
    return rows


def side_view_svg(stance_: Stance = None, width_px: int = 640) -> str:
    """A side view of the stance in units of r: ground, ball, eye, shoulder, arm, feet, sight line."""
    st = stance_ or stance()
    xmin, xmax, ymin, ymax = -2.6, 1.6, -0.3, st.e + 0.4
    scale = width_px / (xmax - xmin)
    height_px = int((ymax - ymin) * scale) + 1

    def p(pt):
        return ((pt[0] - xmin) * scale, (ymax - pt[1]) * scale)

    def line(a, b, colour, w=2, dash=None):
        (x1, y1), (x2, y2) = p(a), p(b)
        d = f' stroke-dasharray="{dash}"' if dash else ""
        return (f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
                f'stroke="{colour}" stroke-width="{w}"{d}/>')

    def dot(pt, colour, label, dx=6, dy=-6):
        x, y = p(pt)
        return (f'<circle cx="{x:.1f}" cy="{y:.1f}" r="4" fill="{colour}"/>'
                f'<text x="{x + dx:.1f}" y="{y + dy:.1f}" font-size="13" fill="{colour}">{label}</text>')

    cx, cy = p((0.0, 1.0))
    eye, shoulder, hand, feet = st.eye, st.shoulder, st.hand, st.feet
    hips = (eye[0], st.e * (1.0 - 0.434))
    top_tangent_len = 6.0
    a = math.radians(st.top_edge_deg)
    sight_end = (eye[0] + top_tangent_len * math.cos(a), eye[1] - top_tangent_len * math.sin(a))
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width_px}" height="{height_px}" '
        f'viewBox="0 0 {width_px} {height_px}" font-family="sans-serif">',
        '<rect width="100%" height="100%" fill="white"/>',
        line((xmin, 0.0), (xmax, 0.0), "#555", 2),
        f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{scale:.1f}" fill="#f4d35e" stroke="#a07b00" '
        'stroke-width="2"/>',
        line(eye, sight_end, "#888", 1, "6,4"),
        line(eye, hips, "#3a6ea5", 6),
        line(hips, feet, "#3a6ea5", 6),
        line(shoulder, hand, "#d1495b", 4),
        line(hand, (0.0, 1.0), "#d1495b", 1, "4,3"),
        dot((0.0, 0.0), "#333", "G 触地点", 6, 16),
        dot((0.0, 2.0), "#333", "T 球顶", 6, -6),
        dot(eye, "#3a6ea5", "V 视点", -52, -6),
        dot(shoulder, "#3a6ea5", "S 肩", -36, 4),
        dot(hand, "#d1495b", "H 手", 6, 16),
        dot(feet, "#3a6ea5", "F 座位=脚", -78, 16),
        f'<text x="8" y="18" font-size="13" fill="#333">推球站姿（单位 r）：ψ={st.psi_deg:g}°，'
        f'E={st.e:.3f}r，眼点在球心上 {st.up:.3f}r、后 {st.back:.3f}r</text>',
        f'<text x="8" y="36" font-size="13" fill="#333">红=手臂（沿球半径推向球心）；'
        f'虚线=越过球顶的视线（俯 {st.top_edge_deg:.1f}°）</text>',
        '</svg>',
    ]
    return "\n".join(parts)


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1 and sys.argv[1] == "--svg":
        print(side_view_svg())
        sys.exit(0)
    s = stance()
    print(f"psi={s.psi_deg:g} e={s.e:.6f} up={s.up:.6f} back={s.back:.6f}")
    print("psi  e      D/E    up     back   centre  half   top    bottom  ground-ahead  top<eye  hand fwd/down (E)")
    for psi in CANDIDATE_ANGLES_DEG:
        t = stance(psi)
        f, d = t.hand_from_eye_per_e
        print(f"{psi:<4g} {t.e:.3f}  {t.diameter_per_e:.3f}  {t.up:.3f}  {t.back:.3f}  "
              f"{t.centre_depression_deg:5.1f}  {t.half_angle_deg:5.1f}  {t.top_edge_deg:5.1f}  "
              f"{t.bottom_edge_deg:5.1f}   {t.ground_visible_ahead_of_centre:5.2f}r       "
              f"{t.top_below_eye_per_e:.3f}    {f:.3f}/{d:.3f}")
    print("D      E       up      back    panel distance")
    rows = size_table()
    for d, e, up, back, pd in rows:
        print(f"{d:<6g} {e:<7.4f} {up:<7.4f} {back:<7.4f} {pd:.3f}")
    print("D    E      eye-y  back   hand fwd  hand down  eye-hand  shoulder-hand")
    for d, e, y, back, f, dn, eh, sh in reach_table():
        print(f"{d:<4g} {e:.3f}  {y:.3f}  {back:.3f}  {f:.3f}     {dn:.3f}      {eh:.3f}     {sh:.3f}")
    print("panel           az     el    clearance from the ball (deg)")
    for panel in PANELS:
        print(f"{panel[0]:<15} {panel[1]:<6g} {panel[2]:<5g} {panel_clearance_deg(panel):.1f}")
