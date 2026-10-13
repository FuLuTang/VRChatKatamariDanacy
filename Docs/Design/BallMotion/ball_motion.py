"""Offline deterministic ball-motion reference model; provisional, not a runtime contract."""
from dataclasses import dataclass
import math


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def wrap_degrees(v):
    return (v + 180.0) % 360.0 - 180.0


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
    max_speed_mps: float = 4.0
    acceleration_mps2: float = 5.0
    braking_mps2: float = 8.0
    friction_per_s: float = 1.2
    turn_rate_degps: float = 110.0
    max_lateral_mps: float = 1.2
    growth_speed_factor: float = 0.25
    growth_turn_factor: float = 0.35
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
        self._last_position = (0.0, 0.0, 0.0)

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
        size = (r-c.base_radius_m) / max(c.max_radius_m-c.base_radius_m, 1e-12)
        speed_cap = c.max_speed_mps * (1.0-c.growth_speed_factor*size)
        turn_cap = c.turn_rate_degps * (1.0-c.growth_turn_factor*size)
        s.heading_deg = (s.heading_deg + clamp(turn, -1, 1)*turn_cap*dt) % 360.0
        rad = math.radians(s.heading_deg)
        fx, fz = math.sin(rad), math.cos(rad)
        lx, lz = math.cos(rad), -math.sin(rad)
        fwd = clamp(forward, -1, 1)
        desired_ax, desired_az = fx*fwd*c.acceleration_mps2, fz*fwd*c.acceleration_mps2
        s.vx += desired_ax*dt
        s.vz += desired_az*dt
        # Exponential speed-independent friction, then bounded lateral velocity.
        damp = math.exp(-c.friction_per_s*dt)
        s.vx *= damp
        s.vz *= damp
        side = s.vx*lx + s.vz*lz
        side = clamp(side + clamp(lateral, -1, 1)*c.max_lateral_mps*dt, -c.max_lateral_mps, c.max_lateral_mps)
        forward_speed = s.vx*fx + s.vz*fz
        planar_speed = math.hypot(forward_speed, side)
        if planar_speed > speed_cap:
            scale = speed_cap / planar_speed
            forward_speed *= scale
            side *= scale
        s.vx, s.vz = forward_speed*fx + side*lx, forward_speed*fz + side*lz
        if abs(fwd) < 1e-9:
            # braking bound applies to coasting speed without reversing it
            v = math.hypot(s.vx, s.vz)
            new_v = max(0.0, v-c.braking_mps2*dt)
            if v:
                s.vx *= new_v/v
                s.vz *= new_v/v
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


@dataclass
class FollowRig:
    eye_height_m: float = 1.6
    height_filter_hz: float = 5.0
    yaw_filter_hz: float = 8.0
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0
    yaw_deg: float = 0.0
    roll_deg: float = 0.0
    pitch_deg: float = 0.0

    def update(self, position, orientation_euler_deg, dt_s):
        """Smooth position and yaw only; input tuple is (roll,pitch,yaw), degrees."""
        if dt_s < 0:
            raise ValueError("dt_s must be non-negative")
        tx, ty, tz = position
        alpha_h = 1.0-math.exp(-2*math.pi*self.height_filter_hz*dt_s)
        alpha_y = 1.0-math.exp(-2*math.pi*self.yaw_filter_hz*dt_s)
        self.x += (tx-self.x)*alpha_h
        self.y += (ty+self.eye_height_m-self.y)*alpha_h
        self.z += (tz-self.z)*alpha_h
        target_yaw = orientation_euler_deg[2] % 360.0
        self.yaw_deg = (self.yaw_deg + wrap_degrees(target_yaw-self.yaw_deg)*alpha_y) % 360.0
        self.roll_deg = 0.0
        self.pitch_deg = 0.0
        return self
