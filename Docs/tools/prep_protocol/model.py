"""Small standard-library host model for transaction counterexamples.

This is an offline experiment, not a transport, anti-cheat, or Udon implementation.
All identity/arrival metadata is supplied by a trusted test harness fixture.
"""
from __future__ import annotations

from dataclasses import dataclass, field
import math
from typing import Any

MAX_INTEGER = 2_147_483_647


class ProtocolError(ValueError):
    """Malformed or stale host-fixture request/snapshot."""


@dataclass
class Player:
    session_generation: int
    banked_score: int = 0
    unbanked: int = 0
    life: str = "alive"
    held_powerup: str | None = None


@dataclass
class Item:
    generation: int
    gain: int
    available: bool = True


@dataclass(frozen=True)
class Request:
    round_id: str
    epoch: int
    actor_id: str
    session_generation: int
    sequence: int
    operation: str
    payload: dict[str, Any]
    received_tick: int
    # The harness-only sender identity; this is NOT a client-asserted secure identity.
    observed_sender_id: str

    @property
    def key(self) -> tuple[str, str, int, int]:
        return (self.round_id, self.actor_id, self.session_generation, self.sequence)


@dataclass
class SnapshotPart:
    snapshot_id: str
    revision: int
    epoch: int
    index: int
    total: int
    payload: dict[str, Any]


@dataclass
class Snapshot:
    snapshot_id: str
    revision: int
    epoch: int
    complete: bool
    parts: list[SnapshotPart]


@dataclass
class ProtocolModel:
    round_id: str
    epoch: int
    cutoff_tick: int
    players: dict[str, Player]
    items: dict[str, Item]
    ordering: str = "arrival"
    commit_sequence: int = 0
    phase: str = "playing"
    queue: list[Request] = field(default_factory=list)
    queued_keys: dict[tuple[str, str, int, int], Request] = field(default_factory=dict)
    committed_keys: dict[tuple[str, str, int, int], dict[str, Any]] = field(default_factory=dict)
    business_keys: dict[tuple[str, str, int, str, str], dict[str, Any]] = field(default_factory=dict)
    next_sequence: int = 0
    events: list[dict[str, Any]] = field(default_factory=list)

    ORDERINGS = {"arrival", "bank_before_ko", "ko_before_bank"}
    OPERATIONS = {"pickup", "magnet", "bank", "ko"}

    def __post_init__(self) -> None:
        _id(self.round_id, "round_id")
        _positive_int(self.epoch, "epoch")
        _nonnegative_int(self.cutoff_tick, "cutoff_tick")
        _nonnegative_int(self.commit_sequence, "commit_sequence")
        if not isinstance(self.ordering, str) or self.ordering not in self.ORDERINGS:
            raise ProtocolError("unknown ordering strategy")
        for player_id, player in self.players.items():
            _id(player_id, "player_id")
            _positive_int(player.session_generation, "session_generation")
            _nonnegative_int(player.banked_score, "banked_score")
            _nonnegative_int(player.unbanked, "unbanked")
            if not isinstance(player.life, str) or player.life not in {"alive", "ko"}:
                raise ProtocolError("invalid life state")
        for spawn_id, item in self.items.items():
            _id(spawn_id, "spawn_id")
            _positive_int(item.generation, "spawn_generation")
            _nonnegative_int(item.gain, "gain")

    def enqueue(self, request: Request) -> dict[str, Any]:
        """Queue only. A pending response is deliberately not a success celebration."""
        self._validate_request(request)
        if request.epoch != self.epoch:
            return self._result("rejected", "stale_epoch", request)
        old = self.committed_keys.get(request.key)
        if old is not None:
            return dict(old)
        if self.phase != "playing":
            return self._result("rejected", "not_playing", request)
        queued_request = self.queued_keys.get(request.key)
        if queued_request is not None:
            if queued_request != request:
                return self._result("rejected", "idempotency_key_conflict", request)
            return self._result("pending", "duplicate_queued", request)
        self.queued_keys[request.key] = request
        self.queue.append(request)
        return self._result("pending", "awaiting_host_commit", request)

    def commit_queued(self) -> list[dict[str, Any]]:
        """Apply one deterministic fixture schedule; policy is explicitly selectable."""
        queued = list(self.queue)
        self.queue.clear()
        self.phase = "settling"
        ordered = sorted(queued, key=self._sort_key)
        outcomes = []
        for request in ordered:
            if request.key in self.committed_keys:
                outcomes.append(self.committed_keys[request.key])
                continue
            if request.received_tick >= self.cutoff_tick:
                outcome = self._result("rejected", "after_cutoff", request)
            elif request.epoch != self.epoch:
                outcome = self._result("rejected", "stale_epoch", request)
            else:
                business_key = self._business_key(request)
                if business_key is not None and business_key in self.business_keys:
                    outcome = dict(self.business_keys[business_key])
                    outcome["reason"] = "duplicate_business_id"
                    outcome["duplicate_of"] = list(self.business_keys[business_key]["request_key"])
                else:
                    outcome = self._apply(request)
                    if business_key is not None and outcome["status"] == "committed":
                        self.business_keys[business_key] = dict(outcome)
            self.committed_keys[request.key] = outcome
            outcomes.append(outcome)
        self.phase = "results"
        return outcomes

    def _business_key(self, request: Request) -> tuple[str, str, int, str, str] | None:
        id_field = {"bank": "bank_id", "ko": "ko_id"}.get(request.operation)
        if id_field is None or not isinstance(request.payload, dict):
            return None
        value = request.payload.get(id_field)
        if not isinstance(value, str) or not value:
            return None
        return (request.round_id, request.actor_id, request.session_generation,
                request.operation, value)

    def _sort_key(self, request: Request) -> tuple[Any, ...]:
        rank = 0
        if self.ordering == "bank_before_ko":
            rank = {"bank": 0, "pickup": 1, "magnet": 1, "ko": 2}.get(request.operation, 9)
        elif self.ordering == "ko_before_bank":
            rank = {"ko": 0, "pickup": 1, "magnet": 1, "bank": 2}.get(request.operation, 9)
        # Stable request identity only makes this fixture reproducible; it is not fairness policy.
        return (request.received_tick, rank, request.actor_id, request.sequence)

    def _validate_request(self, request: Request) -> None:
        if not isinstance(request, Request):
            raise ProtocolError("request must be Request")
        _id(request.round_id, "round_id")
        _id(request.actor_id, "actor_id")
        _id(request.observed_sender_id, "observed_sender_id")
        if request.round_id != self.round_id:
            raise ProtocolError("wrong_round")
        _positive_int(request.epoch, "epoch")
        _positive_int(request.session_generation, "session_generation")
        _positive_int(request.sequence, "sequence")
        _nonnegative_int(request.received_tick, "received_tick")
        if not isinstance(request.operation, str) or request.operation not in self.OPERATIONS:
            raise ProtocolError("unknown_operation")
        if request.actor_id != request.observed_sender_id:
            raise ProtocolError("sender_fixture_mismatch")
        player = self.players.get(request.actor_id)
        if player is None:
            raise ProtocolError("unknown_player")
        if player.session_generation != request.session_generation:
            raise ProtocolError("stale_session_generation")
        if not isinstance(request.payload, dict):
            raise ProtocolError("payload must be object")
        # Reject non-JSON-like values and non-finite numeric data.
        _json_value(request.payload)

    def _apply(self, request: Request) -> dict[str, Any]:
        player = self.players[request.actor_id]
        if player.life != "alive":
            return self._result("rejected", "actor_not_alive", request)
        if request.operation == "pickup":
            return self._pickup(request, player)
        if request.operation == "magnet":
            return self._magnet(request, player)
        if request.operation == "bank":
            if set(request.payload) != {"bank_id"}:
                return self._result("rejected", "invalid_bank_payload", request)
            _id(request.payload["bank_id"], "bank_id")
            if player.unbanked <= 0:
                return self._result("rejected", "nothing_to_bank", request)
            amount = player.unbanked
            if player.banked_score + amount > MAX_INTEGER:
                return self._result("rejected", "score_limit", request)
            self._advance_commit()
            player.unbanked = 0
            player.banked_score += amount
            self._assert_player(player)
            return self._result("committed", "banked", request, amount=amount, bank_id=request.payload["bank_id"])
        if request.operation == "ko":
            if set(request.payload) != {"ko_id", "victim_id", "attacker_gain"}:
                return self._result("rejected", "invalid_ko_payload", request)
            _id(request.payload["ko_id"], "ko_id")
            _id(request.payload["victim_id"], "victim_id")
            _nonnegative_int(request.payload["attacker_gain"], "attacker_gain")
            victim = self.players.get(request.payload["victim_id"])
            if victim is None or request.payload["victim_id"] == request.actor_id:
                return self._result("rejected", "invalid_victim", request)
            if victim.life != "alive":
                return self._result("rejected", "victim_not_alive", request)
            if request.payload["attacker_gain"] > victim.unbanked:
                return self._result("rejected", "reward_exceeds_unbanked_loss", request)
            if player.unbanked + request.payload["attacker_gain"] > MAX_INTEGER:
                return self._result("rejected", "growth_limit", request)
            lost = victim.unbanked
            self._advance_commit()
            victim.unbanked = 0
            victim.life = "ko"
            victim.held_powerup = None
            player.unbanked += request.payload["attacker_gain"]
            self._assert_player(player)
            self._assert_player(victim)
            return self._result("committed", "ko_committed", request, victim=request.payload["victim_id"],
                                lost_unbanked=lost, attacker_gain=request.payload["attacker_gain"],
                                victim_banked_unchanged=victim.banked_score)
        raise ProtocolError("unreachable operation")

    def _pickup(self, request: Request, player: Player) -> dict[str, Any]:
        if set(request.payload) != {"spawn_id", "generation"}:
            return self._result("rejected", "invalid_pickup_payload", request)
        spawn_id = _id(request.payload["spawn_id"], "spawn_id")
        generation = _positive_int(request.payload["generation"], "generation")
        item = self.items.get(spawn_id)
        if item is None:
            return self._result("rejected", "unknown_spawn", request)
        _positive_int(item.generation, "spawn_generation")
        _nonnegative_int(item.gain, "gain")
        if generation != item.generation:
            return self._result("rejected", "stale_spawn_generation", request)
        if not item.available:
            return self._result("rejected", "spawn_unavailable", request)
        if player.unbanked + item.gain > MAX_INTEGER:
            return self._result("rejected", "growth_limit", request)
        self._advance_commit()
        item.available = False
        player.unbanked += item.gain
        self._assert_player(player)
        return self._result("committed", "pickup_committed", request, spawn_id=spawn_id,
                            generation=generation, gain=item.gain)

    def _magnet(self, request: Request, player: Player) -> dict[str, Any]:
        if set(request.payload) != {"candidates"} or not isinstance(request.payload["candidates"], list):
            return self._result("rejected", "invalid_magnet_payload", request)
        candidates = request.payload["candidates"]
        if len(candidates) > 64:
            return self._result("rejected", "candidate_limit", request)
        results = []
        seen: set[tuple[str, int]] = set()
        for candidate in candidates:
            if not isinstance(candidate, dict) or set(candidate) != {"spawn_id", "generation"}:
                results.append({"status": "rejected", "reason": "invalid_candidate"})
                continue
            try:
                spawn_id = _id(candidate["spawn_id"], "spawn_id")
                generation = _positive_int(candidate["generation"], "generation")
            except ProtocolError as exc:
                results.append({"status": "rejected", "reason": str(exc)})
                continue
            key = (spawn_id, generation)
            if key in seen:
                results.append({"status": "rejected", "reason": "duplicate_candidate", "spawn_id": spawn_id})
                continue
            seen.add(key)
            item = self.items.get(spawn_id)
            if item is None:
                results.append({"status": "rejected", "reason": "unknown_spawn", "spawn_id": spawn_id})
                continue
            _positive_int(item.generation, "spawn_generation")
            _nonnegative_int(item.gain, "gain")
            if generation != item.generation:
                results.append({"status": "rejected", "reason": "stale_spawn_generation", "spawn_id": spawn_id})
            elif not item.available:
                results.append({"status": "rejected", "reason": "spawn_unavailable", "spawn_id": spawn_id})
            elif player.unbanked + item.gain > MAX_INTEGER:
                results.append({"status": "rejected", "reason": "growth_limit", "spawn_id": spawn_id})
            else:
                self._advance_commit()
                item.available = False
                player.unbanked += item.gain
                self._assert_player(player)
                results.append({"status": "committed", "spawn_id": spawn_id, "generation": generation,
                                "gain": item.gain})
        return self._result("committed", "magnet_partial_result", request,
                            accepted=[r["spawn_id"] for r in results if r["status"] == "committed"],
                            candidates=results)

    def _advance_commit(self) -> None:
        if self.commit_sequence >= MAX_INTEGER:
            raise ProtocolError("commit_sequence_limit")
        self.commit_sequence += 1

    def _assert_player(self, player: Player) -> None:
        for name in ("banked_score", "unbanked", "session_generation"):
            _nonnegative_int(getattr(player, name), name)

    def _result(self, status: str, reason: str, request: Request, **extra: Any) -> dict[str, Any]:
        result = {"status": status, "reason": reason, "request_key": list(request.key),
                  "commit_sequence": self.commit_sequence}
        result.update(extra)
        self.events.append(dict(result))
        return result

    def recover(self, snapshot: Snapshot) -> dict[str, Any]:
        """Accept only a complete, internally consistent snapshot; otherwise cancel."""
        try:
            _validate_snapshot(snapshot, expected_round=self.round_id, current_epoch=self.epoch,
                               current_commit_sequence=self.commit_sequence)
        except ProtocolError as exc:
            self.phase = "cancelled"
            self.queue.clear()
            return {"status": "cancelled", "reason": str(exc), "round_id": self.round_id}
        header = _snapshot_header(snapshot)
        restored_players = {}
        for row in header["players"]:
            restored_players[row["player_id"]] = Player(
                row["session_generation"], row["banked_score"], row["unbanked"],
                row["life_state"], row["held_powerup"])
        restored_items = {}
        for row in header["items"]:
            restored_items[row["spawn_id"]] = Item(row["generation"], row["gain"], row["available"])
        self.players = restored_players
        self.items = restored_items
        self.commit_sequence = _snapshot_commit_sequence(snapshot)
        restored_business_keys = {}
        for row in header["business_ids"]:
            key = (self.round_id, row["actor_id"], row["session_generation"],
                   row["operation"], row["business_id"])
            restored_business_keys[key] = dict(row["result"])
        self.business_keys = restored_business_keys
        self.epoch = snapshot.epoch + 1
        self.phase = "playing"
        self.queue.clear()
        self.queued_keys.clear()
        # Response-key cache is scoped to the old authority epoch; persistent
        # business IDs and item state above carry cross-epoch exactly-once guards.
        self.committed_keys.clear()
        return {"status": "recovered_fixture", "epoch": self.epoch, "revision": snapshot.revision,
                "commit_sequence": _snapshot_commit_sequence(snapshot)}


def _validate_snapshot(snapshot: Snapshot, expected_round: str, current_epoch: int,
                       current_commit_sequence: int) -> None:
    if not isinstance(snapshot, Snapshot) or snapshot.complete is not True:
        raise ProtocolError("snapshot_incomplete")
    _id(snapshot.snapshot_id, "snapshot_id")
    _positive_int(snapshot.revision, "snapshot_revision")
    _positive_int(snapshot.epoch, "snapshot_epoch")
    if snapshot.epoch != current_epoch:
        raise ProtocolError("snapshot_epoch_mismatch")
    if snapshot.epoch >= MAX_INTEGER:
        raise ProtocolError("epoch_limit")
    if not isinstance(snapshot.parts, list) or not snapshot.parts:
        raise ProtocolError("snapshot_missing_parts")
    for part in snapshot.parts:
        if not isinstance(part, SnapshotPart):
            raise ProtocolError("invalid_snapshot_part")
        _id(part.snapshot_id, "part_snapshot_id")
        _positive_int(part.revision, "part_revision")
        _positive_int(part.epoch, "part_epoch")
        _nonnegative_int(part.index, "part_index")
        _positive_int(part.total, "part_total")
    totals = {p.total for p in snapshot.parts}
    if len(totals) != 1:
        raise ProtocolError("mixed_part_total")
    total = next(iter(totals))
    _positive_int(total, "part_total")
    if len(snapshot.parts) != total:
        raise ProtocolError("snapshot_missing_parts")
    indices = set()
    for part in snapshot.parts:
        if (part.snapshot_id, part.revision, part.epoch) != (snapshot.snapshot_id, snapshot.revision, snapshot.epoch):
            raise ProtocolError("mixed_snapshot_version")
        _nonnegative_int(part.index, "part_index")
        if part.index >= total or part.index in indices:
            raise ProtocolError("duplicate_or_invalid_part_index")
        indices.add(part.index)
        if not isinstance(part.payload, dict):
            raise ProtocolError("invalid_part_payload")
        _json_value(part.payload)
    if indices != set(range(total)):
        raise ProtocolError("snapshot_missing_parts")
    if total != 1:
        raise ProtocolError("unsupported_snapshot_parts")
    header = _snapshot_header(snapshot)
    if header.get("round_id") != expected_round:
        raise ProtocolError("snapshot_wrong_round")
    snapshot_sequence = _nonnegative_int(header.get("commit_sequence"), "commit_sequence")
    if snapshot_sequence < current_commit_sequence:
        raise ProtocolError("snapshot_commit_sequence_regressed")
    if (not isinstance(header.get("players"), list) or not isinstance(header.get("items"), list)
            or not isinstance(header.get("business_ids"), list)):
        raise ProtocolError("snapshot_missing_ledgers")
    seen_players: set[str] = set()
    player_sessions: dict[str, int] = {}
    for row in header["players"]:
        if not isinstance(row, dict) or set(row) != {
                "player_id", "session_generation", "banked_score", "unbanked", "life_state", "held_powerup"}:
            raise ProtocolError("invalid_snapshot_player")
        player_id = _id(row["player_id"], "snapshot_player_id")
        if player_id in seen_players:
            raise ProtocolError("duplicate_snapshot_player")
        seen_players.add(player_id)
        player_sessions[player_id] = _positive_int(row["session_generation"], "session_generation")
        _nonnegative_int(row["banked_score"], "banked_score")
        _nonnegative_int(row["unbanked"], "unbanked")
        if not isinstance(row["life_state"], str) or row["life_state"] not in {"alive", "ko"}:
            raise ProtocolError("invalid_snapshot_life_state")
        if row["held_powerup"] is not None:
            _id(row["held_powerup"], "held_powerup")
    seen_items: set[str] = set()
    for row in header["items"]:
        if not isinstance(row, dict) or set(row) != {"spawn_id", "generation", "gain", "available"}:
            raise ProtocolError("invalid_snapshot_item")
        spawn_id = _id(row["spawn_id"], "snapshot_spawn_id")
        if spawn_id in seen_items:
            raise ProtocolError("duplicate_snapshot_item")
        seen_items.add(spawn_id)
        _positive_int(row["generation"], "spawn_generation")
        _nonnegative_int(row["gain"], "gain")
        if not isinstance(row["available"], bool):
            raise ProtocolError("invalid_snapshot_availability")
    seen_business: set[tuple[str, int, str, str]] = set()
    for row in header["business_ids"]:
        if not isinstance(row, dict) or set(row) != {
                "actor_id", "session_generation", "operation", "business_id", "result"}:
            raise ProtocolError("invalid_snapshot_business_id")
        actor_id = _id(row["actor_id"], "business_actor_id")
        session_generation = _positive_int(row["session_generation"], "session_generation")
        if player_sessions.get(actor_id) != session_generation:
            raise ProtocolError("business_id_player_session_mismatch")
        operation = row["operation"]
        if not isinstance(operation, str) or operation not in {"bank", "ko"}:
            raise ProtocolError("invalid_business_operation")
        business_id = _id(row["business_id"], "business_id")
        key = (actor_id, session_generation, operation, business_id)
        if key in seen_business:
            raise ProtocolError("duplicate_snapshot_business_id")
        seen_business.add(key)
        if not isinstance(row["result"], dict) or row["result"].get("status") != "committed":
            raise ProtocolError("invalid_snapshot_business_result")
        _json_value(row["result"])


def _snapshot_header(snapshot: Snapshot) -> dict[str, Any]:
    for part in snapshot.parts:
        if part.index == 0:
            return part.payload
    raise ProtocolError("snapshot_missing_header")


def _snapshot_commit_sequence(snapshot: Snapshot) -> int:
    return int(_snapshot_header(snapshot)["commit_sequence"])


def _id(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value or len(value) > 128 or value.strip() != value:
        raise ProtocolError(f"invalid_{name}")
    return value


def _positive_int(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0 or value > MAX_INTEGER:
        raise ProtocolError(f"invalid_{name}")
    return value


def _nonnegative_int(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > MAX_INTEGER:
        raise ProtocolError(f"invalid_{name}")
    return value


def _json_value(value: Any) -> None:
    if value is None or isinstance(value, (str, bool)):
        return
    if isinstance(value, int):
        if abs(value) > MAX_INTEGER:
            raise ProtocolError("integer_out_of_range")
        return
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ProtocolError("non_finite_number")
        return
    if isinstance(value, list):
        for item in value:
            _json_value(item)
        return
    if isinstance(value, dict):
        for key, item in value.items():
            if not isinstance(key, str):
                raise ProtocolError("non_string_json_key")
            _json_value(item)
        return
    raise ProtocolError("non_json_value")
