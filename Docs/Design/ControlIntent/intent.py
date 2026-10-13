"""DRAFT, host-only reference model for normalized control intents.

This is not an Udon/Unity runtime implementation or a frozen public contract.
"""
from dataclasses import dataclass
from enum import Enum
from typing import Dict, Iterable, List, Mapping, Optional, Tuple


class IntentKind(str, Enum):
    MOVE2D = "Move2D"
    LOOK2D = "Look2D"
    USE_ITEM_OR_JUMP = "UseItemOrJump"
    INTERACT_OR_SEAT = "InteractOrSeat"
    EXIT_STATION = "ExitStation"
    RESPAWN = "Respawn"
    COMFORT_TURN = "ComfortTurn"


@dataclass(frozen=True)
class Intent:
    """One intent sample. x/y are normalized axes; value carries settings state."""
    kind: IntentKind
    timestamp: float
    x: float = 0.0
    y: float = 0.0
    value: Optional[bool] = None


@dataclass(frozen=True)
class InputFrame:
    """Canonical frame after a source adapter has translated physical controls."""
    timestamp: float
    move: Tuple[float, float] = (0.0, 0.0)
    look: Tuple[float, float] = (0.0, 0.0)
    jump: bool = False
    interact: bool = False
    exit_station: bool = False
    respawn: bool = False
    comfort_turn: Optional[bool] = None
    menu_open: bool = False


def _clamp_axis(value: float) -> float:
    value = float(value)
    if value < -1.0:
        return -1.0
    if value > 1.0:
        return 1.0
    return value


def _vector(pair: Iterable[float]) -> Tuple[float, float]:
    values = tuple(pair)
    if len(values) != 2:
        raise ValueError("2D axis must contain exactly two values")
    return (_clamp_axis(values[0]), _clamp_axis(values[1]))


# Physical bindings deliberately sit outside the movement layer. Adapters map device
# readings into the same canonical InputFrame; unresolved bindings remain explicit.
SOURCE_BINDINGS: Mapping[str, Mapping[str, str]] = {
    "A": {"move": "two virtual controller handles / hand-tracking candidate",
          "look": "horizontal reference/look intent (binding TBD)",
          "use": "user-configured Jump action", "interact": "VRChat Interact",
          "comfort": "native Comfort Turning (compatibility TBD)"},
    "B": {"move": "left stick", "look": "right stick / horizontal look axis",
          "use": "user-configured Jump action", "interact": "VRChat Interact",
          "comfort": "native Comfort Turning (on/off interaction TBD)"},
    "Desktop": {"move": "WASD / configured 2D movement", "look": "mouse",
                "use": "Space / Jump", "interact": "left mouse button (T04 verified)",
                "comfort": "not applicable"},
}


class IntentAdapter:
    """Turn canonical frames into a deterministic ordered intent stream.

    Axes are emitted while non-zero. Edge actions are emitted once per rising edge;
    debounce_seconds=0.2 is a T04-order-of-magnitude draft parameter, not a tuned value.
    """
    def __init__(self, debounce_seconds: float = 0.2):
        if debounce_seconds < 0:
            raise ValueError("debounce_seconds must be non-negative")
        self.debounce_seconds = float(debounce_seconds)
        self._previous: Dict[str, bool] = {}
        self._last_action: Dict[str, float] = {}
        self._comfort: Optional[bool] = None

    def reset(self) -> None:
        self._previous.clear()
        self._last_action.clear()
        self._comfort = None

    def process(self, frame: InputFrame, seated: bool = False) -> Tuple[Intent, ...]:
        if frame.timestamp < 0:
            raise ValueError("timestamp must be non-negative")
        # Menus suppress every game-facing intent and clear edge memory so a held
        # control during menu closure cannot synthesize a stale press.
        if frame.menu_open:
            # Track physical down state without emitting; keep held controls latched
            # across menu closure until release, preventing a synthetic rising edge.
            self._previous.update({
                "jump": bool(frame.jump),
                "interact": bool(frame.interact),
                "exit": bool(frame.exit_station),
                "respawn": bool(frame.respawn),
            })
            self._comfort = frame.comfort_turn
            return ()

        out: List[Intent] = []
        mx, my = _vector(frame.move)
        lx, ly = _vector(frame.look)
        if mx or my:
            out.append(Intent(IntentKind.MOVE2D, frame.timestamp, mx, my))
        if lx or ly:
            out.append(Intent(IntentKind.LOOK2D, frame.timestamp, lx, ly))

        if frame.comfort_turn is not None and frame.comfort_turn != self._comfort:
            self._comfort = frame.comfort_turn
            out.append(Intent(IntentKind.COMFORT_TURN, frame.timestamp, value=frame.comfort_turn))

        for name, down, kind in (
            ("jump", frame.jump, IntentKind.USE_ITEM_OR_JUMP),
            ("interact", frame.interact, IntentKind.INTERACT_OR_SEAT),
            ("exit", frame.exit_station, IntentKind.EXIT_STATION),
            ("respawn", frame.respawn, IntentKind.RESPAWN),
        ):
            was_down = self._previous.get(name, False)
            self._previous[name] = bool(down)
            if down and not was_down:
                last = self._last_action.get(name)
                if last is None or frame.timestamp - last >= self.debounce_seconds:
                    out.append(Intent(kind, frame.timestamp))
                    self._last_action[name] = frame.timestamp
        # seated is intentionally behavioral context only: Jump intent still maps to
        # item use, never to a physical jump. This model has no actual-jump output.
        del seated
        return tuple(out)


def prompt_visible(*, seated: bool, menu_open: bool = False) -> bool:
    """Host-side policy: hide the local interact prompt while seated or in menu."""
    return not seated and not menu_open


def adapt_source(source: str, frames: Iterable[InputFrame], *, seated: bool = False,
                 debounce_seconds: float = 0.2) -> Tuple[Intent, ...]:
    """Convenience adapter entry; source names validate routing provenance only."""
    if source not in SOURCE_BINDINGS:
        raise ValueError("unknown input source: %s" % source)
    adapter = IntentAdapter(debounce_seconds)
    result: List[Intent] = []
    for frame in frames:
        result.extend(adapter.process(frame, seated=seated))
    return tuple(result)
