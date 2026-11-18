"""Independent FollowRig host reference; no HMD or ball-spin input.

与 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/FollowRig/FollowRigMath.cs`
同构：`try_update` 的参数与 C# `TryUpdate` 同名同序，状态由调用方持有（这里由
`FollowRig` 这个宿主代替 C# 测试里的 `RigHarness`）。改一处必须同时改另一处。
C# 返回 `false` 的路径在这里对应 `ValueError`（输入非法）或 `OverflowError`
（滤波指数/alpha 溢出、候选帧非有限）。

精度域不同（唯一有意的差异）：C# 的参数是 float32，本模型是 Python double。所以
`2*pi*hz*dt` 超过 float32 上限（约 3.4e38）时 C# 就返回 false，而 double 要超过
约 1.8e308 才溢出——这是类型精度差异，不是语义差异；两边都走「拒绝且不提交」这同
一条路径，Python 测试用真正 double 溢出的输入覆盖它。
"""
from dataclasses import dataclass
import math
from typing import Optional, Tuple

Vector3 = Tuple[float, float, float]


def _vector3(value, name: str) -> Vector3:
    if not isinstance(value, (tuple, list)) or len(value) != 3:
        raise ValueError(f"{name} must be a 3-vector")
    result = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            raise ValueError(f"{name} must contain real numbers")
        converted = float(component)
        if not math.isfinite(converted):
            raise ValueError(f"{name} must be finite")
        result.append(converted)
    return (result[0], result[1], result[2])


def _scalar(value, name: str, *, non_negative: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a real number")
    converted = float(value)
    if not math.isfinite(converted) or (non_negative and converted < 0.0):
        raise ValueError(f"{name} must be finite" + (" and non-negative" if non_negative else ""))
    return converted


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _wrap360(degrees: float) -> float:
    # C#: `degrees % 360f`, then `+= 360f` if negative. Python's `%` already
    # returns a non-negative result for a positive divisor.
    wrapped = degrees % 360.0
    if wrapped < 0.0:
        wrapped += 360.0
    return wrapped


def _shortest_delta(target: float, current: float) -> float:
    # Same branch form as C# (not the +180/-180 round trip, which in float
    # would round a difference below ~4e-6 deg to zero).
    difference = (target - current) % 360.0
    if difference >= 180.0:
        difference -= 360.0
    if difference < -180.0:
        difference += 360.0
    return difference


def _filter_alpha(hertz: float, dt_s: float, name: str) -> float:
    # C# TryFilterAlpha: 0 Hz or 0 dt holds the previous value. The overflow
    # boundary is float32's in C# and double's here; see the module docstring.
    if hertz == 0.0 or dt_s == 0.0:
        return 0.0
    exponent = (2.0 * math.pi * hertz) * dt_s
    if not math.isfinite(exponent):
        raise OverflowError(f"{name} filter exponent overflow")
    candidate = 1.0 - math.exp(-exponent)
    if not math.isfinite(candidate):
        raise OverflowError(f"{name} filter alpha overflow")
    if candidate < 0.0 or candidate > 1.0:
        raise OverflowError(f"{name} filter alpha out of range")
    return candidate


@dataclass(frozen=True)
class FollowFrame:
    """Filtered presentation transform; deliberately has position and yaw only."""
    position: Vector3
    yaw_deg: float


def try_update(height_filter_hz: float,
               yaw_filter_hz: float,
               ball_position: Vector3,
               ball_radius_m: float,
               horizontal_yaw_deg: float,
               dt_s: float,
               eye_base_up_m: float,
               eye_up_per_radius: float,
               eye_up_min_m: float,
               eye_up_max_m: float,
               eye_base_back_m: float,
               eye_back_per_radius: float,
               eye_max_back_m: float,
               reset_to_teleport: bool,
               has_previous_frame: bool,
               previous_rig_yaw_deg: float,
               previous_height_m: float) -> Tuple[FollowFrame, float]:
    """Advance the filtered frame by one sample; returns (frame, height above centre).

    `ball_position` is the ball's CENTRE and `ball_radius_m` its drawn radius.
    `height_m` in the result is the committed height above that centre; pass it
    back with the returned yaw on the next call. Yaw is in
    [0, 360). The frame is ``ballCentre - forward * back + up * height`` with
    ``back = eye_base_back_m + radius * eye_back_per_radius`` and
    ``height = eye_base_up_m + radius * eye_up_per_radius``, so the camera sits
    behind and above the ball and both terms grow with the radius.

    The max rails bound the constant terms only (``eye_base_*_m``); the
    radius-proportional terms are the stance and are left unclamped, because a
    fixed metre ceiling would pin the eye inside a big ball. ``eye_up_min_m`` is
    a floor on the total height above the centre.
    """
    # Validation order mirrors FollowRigMath.TryUpdate exactly.
    filter_hz = _scalar(height_filter_hz, "height_filter_hz", non_negative=True)
    yaw_hz = _scalar(yaw_filter_hz, "yaw_filter_hz", non_negative=True)
    position = _vector3(ball_position, "ball_position")
    radius = _scalar(ball_radius_m, "ball_radius_m", non_negative=True)
    yaw_input = _scalar(horizontal_yaw_deg, "horizontal_yaw_deg")
    dt = _scalar(dt_s, "dt_s", non_negative=True)
    base_up = _scalar(eye_base_up_m, "eye_base_up_m", non_negative=True)
    up_per_radius = _scalar(eye_up_per_radius, "eye_up_per_radius", non_negative=True)
    up_min = _scalar(eye_up_min_m, "eye_up_min_m", non_negative=True)
    up_max = _scalar(eye_up_max_m, "eye_up_max_m", non_negative=True)
    if up_min > up_max:
        raise ValueError("eye_up_min_m must not exceed eye_up_max_m")
    base_back = _scalar(eye_base_back_m, "eye_base_back_m", non_negative=True)
    back_per_radius = _scalar(eye_back_per_radius, "eye_back_per_radius", non_negative=True)
    max_back = _scalar(eye_max_back_m, "eye_max_back_m", non_negative=True)
    if not isinstance(reset_to_teleport, bool):
        raise ValueError("reset_to_teleport must be a bool")
    if not isinstance(has_previous_frame, bool):
        raise ValueError("has_previous_frame must be a bool")

    # The previous frame is read only by the filtered branch, so a reset does
    # not force the caller to repair a stale frame first.
    use_previous = has_previous_frame and not reset_to_teleport
    if use_previous:
        previous_yaw = _scalar(previous_rig_yaw_deg, "previous_rig_yaw_deg")
        previous_height = _scalar(previous_height_m, "previous_height_m")

    alpha_height = _filter_alpha(filter_hz, dt, "height")
    alpha_yaw = _filter_alpha(yaw_hz, dt, "yaw")

    # Behind and above the ball, both measured from its CENTRE and both growing
    # with its radius.
    # The max rails cap only the constant terms; the radius terms are the stance and
    # must not be pinned (a fixed metre ceiling puts the eye inside a 400 m ball).
    base_height = min(base_up, up_max)
    target_height = base_height + radius * up_per_radius
    if not math.isfinite(target_height):
        raise OverflowError("eye target height overflow")
    if target_height < up_min:
        target_height = up_min

    constant_back = min(base_back, max_back)
    back = constant_back + radius * back_per_radius
    if not math.isfinite(back):
        raise OverflowError("eye back offset overflow")
    if back < 0.0:
        back = 0.0

    target_yaw = _wrap360(yaw_input)
    if not use_previous:
        yaw = target_yaw
    else:
        yaw = _wrap360(previous_yaw
                       + _shortest_delta(target_yaw, previous_yaw) * alpha_yaw)

    # Behind the ball along the frame's own (filtered) yaw, so the offset
    # direction and the facing written to the transform can never disagree.
    yaw_radians = math.radians(yaw)
    x = position[0] - math.sin(yaw_radians) * back
    z = position[2] - math.cos(yaw_radians) * back

    if not use_previous:
        height = target_height
    else:
        # A lowered rail must not make the stored height illegal; a shrunken ball is
        # allowed to glide down, so the floor is the only clamp the filter needs.
        prior_height = max(previous_height, up_min)
        height = max(prior_height + (target_height - prior_height) * alpha_height, up_min)

    y = position[1] + height
    if not math.isfinite(x) or not math.isfinite(y):
        raise OverflowError("FollowRig candidate overflow")
    if not math.isfinite(z):
        raise OverflowError("FollowRig candidate overflow")
    if not math.isfinite(height) or not math.isfinite(yaw):
        raise OverflowError("FollowRig candidate overflow")

    return FollowFrame((x, y, z), yaw), height


class FollowRig:
    """Host that owns the caller-side filtered state, like the C# RigHarness.

    Input is a world position, a draw radius and a scalar horizontal heading in
    degrees. The caller must not pass a ball quaternion, roll/pitch, HMD pose,
    controller view axis or Camera. The eye geometry coefficients and their
    rails are per-call parameters, not defaults: the motor's serialized fields
    are the only editable source, so this module copies no product value.
    """

    def __init__(self, height_filter_hz: float = 5.0, yaw_filter_hz: float = 8.0):
        self.height_filter_hz = _scalar(height_filter_hz, "height_filter_hz", non_negative=True)
        self.yaw_filter_hz = _scalar(yaw_filter_hz, "yaw_filter_hz", non_negative=True)
        self._frame: Optional[FollowFrame] = None
        self._height_m: Optional[float] = None

    @property
    def frame(self) -> Optional[FollowFrame]:
        return self._frame

    @property
    def height_m(self) -> Optional[float]:
        """Last committed height above the ball centre (the next call's baseline)."""
        return self._height_m

    def update(self, ball_position: Vector3, ball_radius_m: float,
               horizontal_yaw_deg: float, dt_s: float,
               eye_base_up_m: float, eye_up_per_radius: float,
               eye_up_min_m: float, eye_up_max_m: float,
               eye_base_back_m: float, eye_back_per_radius: float,
               eye_max_back_m: float, *, reset: bool = False) -> FollowFrame:
        """Update by explicit horizontal heading; ``reset`` means respawn/teleport.

        Inputs are validated before state changes. A reset/first sample snaps to
        the new reference transform; normal updates use independent exponential
        filters and shortest-arc yaw interpolation.
        """
        has_previous = self._frame is not None
        previous_yaw = self._frame.yaw_deg if self._frame else 0.0
        previous_height = self._height_m if self._height_m is not None else 0.0
        frame, height = try_update(
            self.height_filter_hz, self.yaw_filter_hz,
            ball_position, ball_radius_m, horizontal_yaw_deg, dt_s,
            eye_base_up_m, eye_up_per_radius, eye_up_min_m, eye_up_max_m,
            eye_base_back_m, eye_back_per_radius, eye_max_back_m,
            reset, has_previous, previous_yaw, previous_height)
        # Atomic commit after try_update returned a fully checked candidate.
        self._frame = frame
        self._height_m = height
        return frame
