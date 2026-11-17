"""Offline deterministic ball-motion reference model; provisional, not a runtime contract.

Follow-camera geometry is deliberately not part of this module: the only follow-rig
model is `Docs/Design/BallLayers/FollowRig/follow_rig.py` (see
`Docs/OPEN_DECISIONS.md` (k)). This module contains no eye height.
"""
from dataclasses import dataclass
import math


# Reverse speed cap as a fraction of the (size-scaled) forward cap (PROVISIONAL).
REVERSE_SPEED_FRACTION = 0.5


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


@dataclass
class BallState:
    x: float = 0.0
    y: float = 0.5
    z: float = 0.0
    vx: float = 0.0
    vy: float = 0.0
    vz: float = 0.0
    heading_deg: float = 0.0
    spin_x_deg: float = 0.0
    spin_z_deg: float = 0.0
    radius_m: float = 0.5
    stuck_seconds: float = 0.0
    out_of_bounds: bool = False


@dataclass(frozen=True)
class MotionConfig:
    fixed_dt_s: float = 1.0 / 120.0
    base_radius_m: float = 0.5
    max_radius_m: float = 2.0
    # Length-type values below are at the START diameter D0 and scale with
    # s = D / D0 (= radius / base_radius_m); time constants do not scale.
    # Owner 2026-11-17, Docs/Design/BallMotor/BALL_INERTIA.md; all PROVISIONAL.
    max_speed_mps: float = 4.0
    acceleration_mps2: float = 7.5
    braking_mps2: float = 6.0  # thrust opposing the forward velocity only
    friction_per_s: float = 0.9
    rolling_resistance_mps2: float = 1.3  # linear coast-down, only with no forward input
    lateral_grip_per_s: float = 2.0  # extra exponential decay of sideways velocity
    turn_rate_degps: float = 110.0
    max_lateral_mps: float = 1.2
    stuck_timeout_s: float = 2.0
    bounds_half_extent_m: float = 1000.0


def radius_from_growth(base_radius_m, growth_fraction, max_radius_m):
    """Linear illustrative growth mapping; fraction is clamped to [0,1]."""
    return base_radius_m + (max_radius_m - base_radius_m) * clamp(growth_fraction, 0.0, 1.0)


@dataclass(frozen=True)
class Plane:
    """One-sided solid plane: signed distance >= radius is free space."""
    nx: float
    ny: float
    nz: float
    offset_m: float = 0.0  # dot(normal, point) = offset

    def unit_normal(self):
        length = math.sqrt(self.nx*self.nx + self.ny*self.ny + self.nz*self.nz)
        if length == 0:
            raise ValueError("plane normal must be non-zero")
        return self.nx/length, self.ny/length, self.nz/length


def sweep_sphere_plane(start, end, radius_m, plane):
    """Return center at first contact (or end), using continuous segment-plane sweep."""
    n = plane.unit_normal()
    d0 = start[0]*n[0] + start[1]*n[1] + start[2]*n[2] - plane.offset_m - radius_m
    d1 = end[0]*n[0] + end[1]*n[1] + end[2]*n[2] - plane.offset_m - radius_m
    if d0 < 0:
        return tuple(start), n
    if d1 >= 0 or d1 >= d0:
        return tuple(end), n
    t = clamp(d0 / (d0 - d1), 0.0, 1.0)
    return tuple(start[i] + (end[i]-start[i])*t for i in range(3)), n


def remove_inward_velocity(state, normal):
    dot = state.vx*normal[0] + state.vy*normal[1] + state.vz*normal[2]
    if dot < 0:
        state.vx -= dot*normal[0]
        state.vy -= dot*normal[1]
        state.vz -= dot*normal[2]


class BallSimulator:
    def __init__(self, config=MotionConfig()):
        if config.fixed_dt_s <= 0 or config.base_radius_m <= 0 or config.max_radius_m < config.base_radius_m:
            raise ValueError("invalid fixed step or radius bounds")
        self.config = config
        self.state = BallState(radius_m=config.base_radius_m)
        self._accumulator = 0.0

    def set_growth(self, growth_fraction):
        c = self.config
        self.state.radius_m = radius_from_growth(c.base_radius_m, growth_fraction, c.max_radius_m)

    def advance(self, elapsed_s, forward=0.0, turn=0.0, lateral=0.0, walls=(), slope_deg=0.0):
        """Accumulate wall-clock time and execute only fixed-size simulation steps."""
        if elapsed_s < 0:
            raise ValueError("elapsed_s must be non-negative")
        self._accumulator += elapsed_s
        count = 0
        while self._accumulator + 1e-12 >= self.config.fixed_dt_s:
            self._step(forward, turn, lateral, walls, slope_deg)
            self._accumulator -= self.config.fixed_dt_s
            count += 1
        return count

    def _step(self, forward, turn, lateral, walls, slope_deg):
        s, c = self.state, self.config
        dt = c.fixed_dt_s
        r = clamp(s.radius_m, c.base_radius_m, c.max_radius_m)
        s.radius_m = r
        k = r / c.base_radius_m  # s = D / D0
        speed_cap = c.max_speed_mps * k
        s.heading_deg = (s.heading_deg + clamp(turn, -1, 1)*c.turn_rate_degps*dt) % 360.0
        rad = math.radians(s.heading_deg)
        fx, fz = math.sin(rad), math.cos(rad)
        lx, lz = math.cos(rad), -math.sin(rad)
        fwd = clamp(forward, -1, 1)
        # Split the world velocity along the heading the step ENDED with, so a turn
        # leaves the old direction behind as lateral velocity (drift).
        vf = s.vx*fx + s.vz*fz
        vl = s.vx*lx + s.vz*lz
        has_input = abs(fwd) >= 1e-9
        if has_input:
            # Input against the current forward velocity uses the brake rate.
            rate = (c.braking_mps2 if vf*fwd < 0 else c.acceleration_mps2) * k
            vf += fwd*rate*dt
        vf *= math.exp(-c.friction_per_s*dt)
        vl *= math.exp(-(c.friction_per_s + c.lateral_grip_per_s)*dt)
        # Strafe input adds sideways speed but never pushes it past max_lateral.
        side_in = clamp(lateral, -1, 1)
        if side_in > 0:
            vl = max(vl, min(vl + side_in*c.max_lateral_mps*k*dt, c.max_lateral_mps*k))
        elif side_in < 0:
            vl = min(vl, max(vl + side_in*c.max_lateral_mps*k*dt, -c.max_lateral_mps*k))
        if not has_input:
            # Rolling resistance: linear, floors at zero, never reverses.
            vf = math.copysign(max(0.0, abs(vf)-c.rolling_resistance_mps2*k*dt), vf)
        vf = clamp(vf, -REVERSE_SPEED_FRACTION*speed_cap, speed_cap)
        planar_speed = math.hypot(vf, vl)
        if planar_speed > speed_cap:
            scale = speed_cap / planar_speed
            vf *= scale
            vl *= scale
        s.vx, s.vz = vf*fx + vl*lx, vf*fz + vl*lz
        start = (s.x, s.y, s.z)
        end = (s.x+s.vx*dt, s.y+s.vy*dt, s.z+s.vz*dt)
        for wall in walls:
            end, n = sweep_sphere_plane(start, end, r, wall)
            remove_inward_velocity(s, n)
            start = end
        s.x, s.y, s.z = end
        # Analytic infinite ramp y = tan(angle)*x; center is constrained to support height.
        slope = math.radians(clamp(slope_deg, -89.0, 89.0))
        floor_y = math.tan(slope)*s.x
        s.y = max(s.y, floor_y+r)
        # Visual spin is derived separately from travel; it never feeds collision/gameplay.
        distance = math.hypot(s.vx, s.vz)*dt
        s.spin_x_deg = (s.spin_x_deg + math.degrees(distance/r)) % 360.0
        s.stuck_seconds = s.stuck_seconds+dt if distance < 1e-4 and abs(fwd) > .1 else 0.0
        c_ext = c.bounds_half_extent_m
        if abs(s.x) > c_ext or abs(s.z) > c_ext:
            s.out_of_bounds = True
            s.x, s.y, s.z = 0.0, r, 0.0
            s.vx = s.vy = s.vz = 0.0
            s.stuck_seconds = 0.0
        elif s.stuck_seconds >= c.stuck_timeout_s:
            # Conservative recovery: stop and lift to analytic support; no arbitrary teleport.
            s.vx = s.vy = s.vz = 0.0
            s.y = max(s.y, floor_y+r)
            s.stuck_seconds = 0.0
