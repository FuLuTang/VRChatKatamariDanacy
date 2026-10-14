"""T55 independent FollowRig host reference; no HMD or ball-spin input."""
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


def _angle360(degrees: float) -> float:
    result = degrees % 360.0
    return 0.0 if result == 360.0 else result


def _shortest_delta(target: float, current: float) -> float:
    return (target - current + 180.0) % 360.0 - 180.0


def _alpha(hz: float, dt_s: float, name: str) -> float:
    if hz == 0.0 or dt_s == 0.0:
        return 0.0
    exponent = (2.0 * math.pi * hz) * dt_s
    if not math.isfinite(exponent):
        raise OverflowError(f"{name} filter exponent overflow")
    return -math.expm1(-exponent)


@dataclass(frozen=True)
class FollowFrame:
    """Filtered presentation transform; deliberately has position and yaw only."""
    position: Vector3
    yaw_deg: float


class FollowRig:
    """A filtered horizontal reference frame extracted from the T46 baseline.

    Input is a world position and a scalar horizontal heading in degrees. The
    caller must not pass a ball quaternion, roll/pitch, HMD pose, or controller
    view axis. Vertical output is ball Y plus a filtered eye-height offset; the
    offset is clamped to the explicit per-call bounds so lag cannot escape them.
    """

    def __init__(self, height_filter_hz: float = 5.0, yaw_filter_hz: float = 8.0):
        self.height_filter_hz = _scalar(height_filter_hz, "height_filter_hz", non_negative=True)
        self.yaw_filter_hz = _scalar(yaw_filter_hz, "yaw_filter_hz", non_negative=True)
        self._frame: Optional[FollowFrame] = None
        self._eye_offset_m = 0.0

    @property
    def frame(self) -> Optional[FollowFrame]:
        return self._frame

    @staticmethod
    def _bounded(value: float, low: float, high: float) -> float:
        return max(low, min(high, value))

    def update(self, ball_position: Vector3, horizontal_yaw_deg: float, dt_s: float,
               eye_height_m: float, eye_height_min_m: float,
               eye_height_max_m: float, *, reset: bool = False) -> FollowFrame:
        """Update by explicit horizontal heading; ``reset`` means respawn/teleport.

        Inputs are validated before state changes. A reset/first sample snaps to
        the new reference transform; normal updates use independent exponential
        filters and shortest-arc yaw interpolation. Eye-height bounds are caller
        parameters, not defaults or claimed VR comfort values.
        """
        position = _vector3(ball_position, "ball_position")
        yaw_input = _scalar(horizontal_yaw_deg, "horizontal_yaw_deg")
        dt = _scalar(dt_s, "dt_s", non_negative=True)
        eye_height = _scalar(eye_height_m, "eye_height_m")
        low = _scalar(eye_height_min_m, "eye_height_min_m")
        high = _scalar(eye_height_max_m, "eye_height_max_m")
        if low > high:
            raise ValueError("eye_height_min_m must not exceed eye_height_max_m")
        if not isinstance(reset, bool):
            raise ValueError("reset must be a bool")
        target_offset = self._bounded(eye_height, low, high)
        target_yaw = _angle360(yaw_input)
        alpha_h = _alpha(self.height_filter_hz, dt, "height")
        alpha_y = _alpha(self.yaw_filter_hz, dt, "yaw")

        if self._frame is None or reset:
            candidate_offset = target_offset
            candidate_yaw = target_yaw
            candidate_x = position[0]
            candidate_z = position[2]
        else:
            # Keep the previous offset legal if bounds change between updates.
            prior_offset = self._bounded(self._eye_offset_m, low, high)
            candidate_offset = self._bounded(
                prior_offset + (target_offset - prior_offset) * alpha_h, low, high)
            candidate_yaw = _angle360(
                self._frame.yaw_deg + _shortest_delta(target_yaw, self._frame.yaw_deg) * alpha_y)
            candidate_x = self._frame.position[0] + (position[0] - self._frame.position[0]) * alpha_h
            candidate_z = self._frame.position[2] + (position[2] - self._frame.position[2]) * alpha_h

        candidate_y = position[1] + candidate_offset
        candidate_position = (candidate_x, candidate_y, candidate_z)
        if not all(math.isfinite(value) for value in candidate_position + (candidate_yaw, candidate_offset)):
            raise OverflowError("FollowRig candidate overflow")
        frame = FollowFrame(candidate_position, candidate_yaw)
        # Atomic commit after all candidate calculations and checks.
        self._frame = frame
        self._eye_offset_m = candidate_offset
        return frame
