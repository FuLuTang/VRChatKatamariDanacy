"""Host-only attachment-record draft. No authority or renderer policy lives here."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import struct
from typing import Iterable

Vector3 = tuple[float, float, float]
Quaternion = tuple[float, float, float, float]  # xyzw
U64_MAX = (1 << 64) - 1
U32_MAX = (1 << 32) - 1


def _finite_tuple(value, n: int, name: str, *, positive: bool = False) -> tuple[float, ...]:
    if not isinstance(value, tuple) or len(value) != n:
        raise ValueError(f"{name} must be a tuple of {n} numbers")
    out = []
    for v in value:
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            raise ValueError(f"{name} must contain numbers")
        f = float(v)
        if not math.isfinite(f) or (positive and f <= 0):
            raise ValueError(f"invalid {name}")
        out.append(f)
    return tuple(out)


def _q_normalize(q: Quaternion) -> Quaternion:
    q = _finite_tuple(q, 4, "quaternion")
    # scale first to avoid overflow in a naive sum-of-squares norm
    scale = max(abs(v) for v in q)
    if scale == 0:
        raise ValueError("zero quaternion")
    scaled = tuple(v / scale for v in q)
    norm = math.sqrt(sum(v * v for v in scaled))
    if not math.isfinite(norm) or norm == 0:
        raise ValueError("invalid quaternion norm")
    return tuple(v / norm for v in scaled)  # type: ignore[return-value]


def _q_conjugate(q: Quaternion) -> Quaternion:
    return (-q[0], -q[1], -q[2], q[3])


def _q_mul(a: Quaternion, b: Quaternion) -> Quaternion:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    q = (aw*bx + ax*bw + ay*bz - az*by,
         aw*by - ax*bz + ay*bw + az*bx,
         aw*bz + ax*by - ay*bx + az*bw,
         aw*bw - ax*bx - ay*by - az*bz)
    if not all(math.isfinite(v) for v in q):
        raise OverflowError("quaternion multiplication overflow")
    return q


def _rotate(q: Quaternion, v: Vector3) -> Vector3:
    q = _q_normalize(q)
    v = _finite_tuple(v, 3, "vector")
    p = (v[0], v[1], v[2], 0.0)
    r = _q_mul(_q_mul(q, p), _q_conjugate(q))
    result = (r[0], r[1], r[2])
    if not all(math.isfinite(x) for x in result):
        raise OverflowError("rotation overflow")
    return result


def _add(a: Vector3, b: Vector3) -> Vector3:
    r = tuple(x + y for x, y in zip(a, b))
    if not all(math.isfinite(x) for x in r):
        raise OverflowError("vector addition overflow")
    return r  # type: ignore[return-value]


def _sub(a: Vector3, b: Vector3) -> Vector3:
    r = tuple(x - y for x, y in zip(a, b))
    if not all(math.isfinite(x) for x in r):
        raise OverflowError("vector subtraction overflow")
    return r  # type: ignore[return-value]


@dataclass(frozen=True)
class Pose:
    position: Vector3
    rotation: Quaternion


@dataclass(frozen=True)
class LocalAttachmentPose:
    position: Vector3
    rotation: Quaternion
    object_scale: Vector3


def world_to_local(ball_world: Pose, object_world: Pose,
                   object_scale: Vector3 = (1.0, 1.0, 1.0)) -> LocalAttachmentPose:
    bp = _finite_tuple(ball_world.position, 3, "ball position")
    op = _finite_tuple(object_world.position, 3, "object position")
    scale = _finite_tuple(object_scale, 3, "object scale", positive=True)
    bq = _q_normalize(ball_world.rotation)
    oq = _q_normalize(object_world.rotation)
    inv = _q_conjugate(bq)
    local_position = _rotate(inv, _sub(op, bp))
    local_rotation = _q_normalize(_q_mul(inv, oq))
    return LocalAttachmentPose(local_position, local_rotation, scale)  # type: ignore[arg-type]


def local_to_world(ball_world: Pose, local: LocalAttachmentPose) -> Pose:
    bp = _finite_tuple(ball_world.position, 3, "ball position")
    local_position = _finite_tuple(local.position, 3, "local position")
    local_scale = _finite_tuple(local.object_scale, 3, "object scale", positive=True)
    bq = _q_normalize(ball_world.rotation)
    lq = _q_normalize(local.rotation)
    position = _add(bp, _rotate(bq, local_position))
    rotation = _q_normalize(_q_mul(bq, lq))
    _ = local_scale  # scale is retained independently; not applied to the pose.
    return Pose(position, rotation)


def _uint(value: int, bits: int, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value >= (1 << bits):
        raise ValueError(f"{name} must be unsigned {bits}-bit integer")
    return value


def _length_prefixed_text(fields: tuple[tuple[str, str], ...]) -> bytes:
    encoded = []
    for name, field in fields:
        if not isinstance(field, str) or not field:
            raise ValueError(f"{name} must be non-empty text")
        raw = field.encode("utf-8")
        if len(raw) > U32_MAX:
            raise ValueError(f"{name} too long")
        encoded.append(struct.pack(">I", len(raw)) + raw)
    return b"".join(encoded)


def _event_identity_bytes(context_id: str, ball_id: str, instance_id: str, generation: int,
                          attach_sequence: int, event_id: str) -> bytes:
    """Encode transaction identity; this is separate from the remote proxy key."""
    text = _length_prefixed_text((
        ("context_id", context_id), ("ball_id", ball_id),
        ("instance_id", instance_id), ("event_id", event_id),
    ))
    generation = _uint(generation, 32, "generation")
    sequence = _uint(attach_sequence, 64, "attach_sequence")
    return text + struct.pack(">IQ", generation, sequence)


def event_identity_hash(context_id: str, ball_id: str, instance_id: str, generation: int,
                        attach_sequence: int, event_id: str) -> str:
    """Deterministic transaction identity digest, not authentication."""
    return hashlib.sha256(_event_identity_bytes(context_id, ball_id, instance_id,
                                                generation, attach_sequence, event_id)).hexdigest()


def remote_proxy_key_hash(ball_seed: str, instance_id: str, generation: int,
                          attach_sequence: int) -> str:
    """Hash the contract's four remote-key fields; caller owns ball_seed semantics.

    This draft requires caller-supplied non-empty canonical text for ball_seed and
    instance_id. Text fields use UTF-8 with unsigned 32-bit byte lengths; generation
    and sequence use fixed-width unsigned 32-bit and 64-bit big-endian integers. No
    context, ball ID, or transaction event ID is inferred or included. If upstream
    defines a non-text seed, its canonical encoding must be decided before adoption.
    This digest is neither authentication nor a collision-free identity proof.
    """
    text = _length_prefixed_text((("ball_seed", ball_seed), ("instance_id", instance_id)))
    generation = _uint(generation, 32, "generation")
    sequence = _uint(attach_sequence, 64, "attach_sequence")
    payload = text + struct.pack(">IQ", generation, sequence)
    return hashlib.sha256(payload).hexdigest()


@dataclass(frozen=True)
class ConfirmedAttachmentEvent:
    """Read-only upstream fact; sequence and event ID are never locally generated."""
    context_id: str
    ball_id: str
    instance_id: str
    generation: int
    attach_sequence: int
    event_id: str
    ball_pose: Pose
    object_pose: Pose
    object_scale: Vector3 = (1.0, 1.0, 1.0)


@dataclass(frozen=True)
class AttachmentRecord:
    context_id: str
    ball_id: str
    instance_id: str
    generation: int
    attach_sequence: int
    event_id: str
    local_pose: LocalAttachmentPose
    event_identity_hash: str

    @property
    def identity(self) -> tuple[str, str, str, int, int, str]:
        return (self.context_id, self.ball_id, self.instance_id,
                self.generation, self.attach_sequence, self.event_id)


class IdentityConflictError(ValueError):
    pass


class StaleOrRevokedError(ValueError):
    pass


class AttachmentLedger:
    """Bounded-host reference store of complete confirmed identity and local pose only."""
    def __init__(self):
        self._records: dict[tuple, AttachmentRecord] = {}
        self._revoked: set[tuple] = set()

    @property
    def records(self) -> tuple[AttachmentRecord, ...]:
        return tuple(sorted(self._records.values(), key=lambda r: r.identity))

    def query(self, *, context_id: str | None = None, ball_id: str | None = None,
              instance_id: str | None = None, generation: int | None = None) -> tuple[AttachmentRecord, ...]:
        return tuple(r for r in self.records
                     if (context_id is None or r.context_id == context_id)
                     and (ball_id is None or r.ball_id == ball_id)
                     and (instance_id is None or r.instance_id == instance_id)
                     and (generation is None or r.generation == generation))

    def apply(self, event: ConfirmedAttachmentEvent) -> AttachmentRecord:
        key_bytes = _event_identity_bytes(event.context_id, event.ball_id, event.instance_id,
                                          event.generation, event.attach_sequence, event.event_id)
        key = (event.context_id, event.ball_id, event.instance_id, event.generation,
               event.attach_sequence, event.event_id)
        if key in self._revoked:
            raise StaleOrRevokedError("revoked attachment replay")
        # Construct and validate the full candidate before changing any state.
        local = world_to_local(event.ball_pose, event.object_pose, event.object_scale)
        candidate = AttachmentRecord(*key, local, hashlib.sha256(key_bytes).hexdigest())
        previous = self._records.get(key)
        if previous is not None:
            if previous != candidate:
                raise IdentityConflictError("same confirmation identity has conflicting payload")
            return previous
        self._records[key] = candidate
        return candidate

    def revoke(self, identity: tuple) -> bool:
        if not isinstance(identity, tuple) or len(identity) != 6:
            raise ValueError("identity must be complete six-field tuple")
        _event_identity_bytes(*identity)
        # Atomic one-operation state change; tombstone prevents replay resurrection.
        existed = identity in self._records
        self._records.pop(identity, None)
        self._revoked.add(identity)
        return existed

    def clear_context(self, context_id: str) -> None:
        if not isinstance(context_id, str) or not context_id:
            raise ValueError("context_id must be non-empty")
        self._records = {k: v for k, v in self._records.items() if k[0] != context_id}
        self._revoked = {k for k in self._revoked if k[0] != context_id}

    def rebuild_snapshot(self, context_id: str, events: Iterable[ConfirmedAttachmentEvent],
                         revoked_identities: Iterable[tuple] = ()) -> None:
        """Validate a complete context snapshot in temporary state, then swap atomically."""
        if not isinstance(context_id, str) or not context_id:
            raise ValueError("context_id must be non-empty")
        incoming = tuple(events)
        if any(e.context_id != context_id for e in incoming):
            raise ValueError("snapshot contains an event from another context")
        temporary = AttachmentLedger()
        for event in incoming:
            temporary.apply(event)
        tombstones = set()
        for identity in tuple(revoked_identities):
            if not isinstance(identity, tuple) or len(identity) != 6 or identity[0] != context_id:
                raise ValueError("snapshot tombstone has invalid identity or context")
            _event_identity_bytes(*identity)
            tombstones.add(identity)
        if tombstones.intersection(temporary._records):
            raise ValueError("snapshot contains an identity both active and revoked")
        kept = {k: v for k, v in self._records.items() if k[0] != context_id}
        kept.update(temporary._records)
        revoked = {k for k in self._revoked if k[0] != context_id}
        revoked.update(tombstones)
        self._records, self._revoked = kept, revoked
