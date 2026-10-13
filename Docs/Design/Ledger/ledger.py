"""DRAFT offline reference model for an authoritative round ledger.

This is executable Python standard-library reference code, not a Unity/Udon contract.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, Iterable, Optional, Tuple


class Phase(str, Enum):
    LOBBY = "Lobby"
    READY = "Ready"
    PREPARING = "Preparing"
    PLAYING = "Playing"
    SETTLING = "Settling"
    RESULTS = "Results"


class Lifecycle(str, Enum):
    ACTIVE = "Active"
    KO = "KO"
    RESPAWN_PENDING = "RespawnPending"
    DISCONNECTED = "Disconnected"


@dataclass(frozen=True, order=True)
class EventKey:
    item_instance_id: str
    round_id: str
    generation: int


@dataclass(frozen=True)
class Claim:
    key: EventKey
    player_id: str
    frame: int
    request_sequence: int


@dataclass(frozen=True)
class Commit:
    key: EventKey
    winner: str
    sequence: int
    gain: int


@dataclass
class PlayerLedger:
    confirmed_volume: int = 0
    banked_score: int = 0


@dataclass
class Player:
    ledger: PlayerLedger = field(default_factory=PlayerLedger)
    life: Lifecycle = Lifecycle.ACTIVE
    predicted: Dict[EventKey, int] = field(default_factory=dict)
    respawn_at: Optional[float] = None
    generation: int = 0


@dataclass(frozen=True)
class Snapshot:
    round_id: str
    phase: Phase
    phase_ends_at: Optional[float]
    revision: int
    commit_sequence: int
    roster: Tuple[Tuple[str, int, int, str], ...]
    commits: Tuple[Commit, ...]


class InvalidTransition(ValueError):
    pass


class Ledger:
    """Single-authority model. `same_frame_tie` is a DRAFT placeholder policy."""
    ALLOWED = {
        Phase.LOBBY: {Phase.READY}, Phase.READY: {Phase.PREPARING, Phase.LOBBY},
        Phase.PREPARING: {Phase.PLAYING, Phase.LOBBY},
        Phase.PLAYING: {Phase.SETTLING}, Phase.SETTLING: {Phase.RESULTS},
        Phase.RESULTS: {Phase.LOBBY},
    }

    def __init__(self, round_id: str, *, same_frame_tie: str = "stable_player_id",
                 round_duration_placeholder: Optional[float] = None):
        self.round_id = round_id
        self.phase = Phase.LOBBY
        self.phase_ends_at: Optional[float] = None
        self.players: Dict[str, Player] = {}
        self.item_winners: Dict[EventKey, str] = {}
        self.commits: Dict[EventKey, Commit] = {}
        self.commit_sequence = 0
        self.revision = 0
        self.same_frame_tie = same_frame_tie
        self.round_duration_placeholder = round_duration_placeholder
        self._claims: Dict[EventKey, list[Claim]] = {}

    def transition(self, target: Phase, now: float, timeout: Optional[float] = None) -> None:
        if target not in self.ALLOWED[self.phase]:
            raise InvalidTransition(f"{self.phase.value} -> {target.value} is not allowed")
        if timeout is not None and timeout <= 0:
            raise ValueError("timeout must be positive")
        self.phase = target
        self.phase_ends_at = None if timeout is None else now + timeout
        self.revision += 1

    def advance_timeout(self, now: float) -> bool:
        """Apply only phase timeouts with deterministic transitions; no client clock/state."""
        if self.phase_ends_at is None or now < self.phase_ends_at:
            return False
        targets = {Phase.READY: Phase.PREPARING, Phase.PREPARING: Phase.PLAYING,
                   Phase.PLAYING: Phase.SETTLING, Phase.SETTLING: Phase.RESULTS,
                   Phase.RESULTS: Phase.LOBBY}
        target = targets.get(self.phase)
        if target is None:
            self.phase_ends_at = None
            return False
        self.transition(target, now)
        return True

    def join(self, player_id: str) -> Snapshot:
        self.players.setdefault(player_id, Player())
        self.revision += 1
        return self.snapshot()

    def predict(self, player_id: str, key: EventKey, gain: int) -> None:
        self._validate_key(key)
        if gain < 0 or player_id not in self.players:
            raise ValueError("invalid prediction")
        self.players[player_id].predicted[key] = gain

    def submit_claim(self, claim: Claim) -> bool:
        """Accept a claim for batching; does not affect confirmed game values."""
        self._validate_key(claim.key)
        p = self.players.get(claim.player_id)
        if self.phase != Phase.PLAYING or p is None or p.life != Lifecycle.ACTIVE:
            return False
        if claim.key in self.item_winners:
            return False
        batch = self._claims.setdefault(claim.key, [])
        ident = (claim.player_id, claim.request_sequence)
        if any((c.player_id, c.request_sequence) == ident for c in batch):
            return True
        batch.append(claim)
        return True

    def resolve_claims(self, key: EventKey, gain: int) -> Optional[Commit]:
        """Resolve batch: earliest frame wins; same-frame tie stable ID (DRAFT)."""
        self._validate_key(key)
        if key in self.commits:
            return self.commits[key]
        claims = self._claims.pop(key, [])
        if not claims:
            return None
        winner = min(claims, key=lambda c: (c.frame, c.player_id, c.request_sequence))
        self.commit_sequence += 1
        commit = Commit(key, winner.player_id, self.commit_sequence, gain)
        self.commits[key] = commit
        self.item_winners[key] = winner.player_id
        player = self.players[winner.player_id]
        player.ledger.confirmed_volume += gain
        for claimant in claims:
            p = self.players[claimant.player_id]
            p.predicted.pop(key, None)  # loser rollback; winner prediction replaced by authoritative value
        self.revision += 1
        return commit

    def apply_commit(self, commit: Commit) -> bool:
        """Idempotently replay an authority commit at a replica."""
        self._validate_key(commit.key)
        old = self.commits.get(commit.key)
        if old is not None:
            if old != commit:
                raise ValueError("conflicting replay for committed event key")
            return False
        if commit.sequence != self.commit_sequence + 1:
            raise ValueError("commit sequence gap or reorder")
        if commit.winner not in self.players or commit.gain < 0:
            raise ValueError("invalid commit")
        self.commits[commit.key] = commit
        self.item_winners[commit.key] = commit.winner
        self.players[commit.winner].ledger.confirmed_volume += commit.gain
        for p in self.players.values():
            p.predicted.pop(commit.key, None)
        self.commit_sequence = commit.sequence
        self.revision += 1
        return True

    def bank(self, player_id: str, amount: int, request_id: str) -> bool:
        """Confirmed-only atomic conversion; request ID deduplicates bank operation."""
        if not request_id or self.phase != Phase.PLAYING:
            return False
        marker = (player_id, request_id)
        if marker in getattr(self, "_bank_requests", set()):
            return False
        p = self.players.get(player_id)
        if p is None or p.life != Lifecycle.ACTIVE or amount <= 0 or amount > p.ledger.confirmed_volume:
            return False
        if not hasattr(self, "_bank_requests"):
            self._bank_requests = set()
        self._bank_requests.add(marker)
        p.ledger.confirmed_volume -= amount
        p.ledger.banked_score += amount
        self.revision += 1
        return True

    def ko(self, victim: str, attacker: Optional[str], now: float,
           respawn_delay_placeholder: float, attacker_share_placeholder: int = 0) -> bool:
        """One lifecycle transaction: victim's unbanked confirmed value is removed."""
        p = self.players.get(victim)
        if p is None or p.life != Lifecycle.ACTIVE or self.phase != Phase.PLAYING:
            return False
        if respawn_delay_placeholder < 0 or attacker_share_placeholder < 0:
            raise ValueError("placeholder values must be non-negative")
        lost = p.ledger.confirmed_volume
        p.ledger.confirmed_volume = 0
        p.predicted.clear()
        p.life = Lifecycle.RESPAWN_PENDING
        p.respawn_at = now + respawn_delay_placeholder
        p.generation += 1
        if attacker and attacker != victim and attacker in self.players:
            # Share remains explicitly configured; zero default avoids silently inventing balance.
            share = min(lost, attacker_share_placeholder)
            self.players[attacker].ledger.confirmed_volume += share
        self.revision += 1
        return True

    def respawn(self, player_id: str, now: float, *, protection_duration_placeholder: float = 0) -> bool:
        p = self.players.get(player_id)
        if p is None or p.life != Lifecycle.RESPAWN_PENDING or p.respawn_at is None or now < p.respawn_at:
            return False
        p.life = Lifecycle.ACTIVE
        p.respawn_at = None
        p.predicted.clear()
        self.revision += 1
        return True

    def disconnect(self, player_id: str) -> bool:
        p = self.players.get(player_id)
        if p is None:
            return False
        p.life = Lifecycle.DISCONNECTED
        p.predicted.clear()
        p.respawn_at = None
        for key, claims in list(self._claims.items()):
            self._claims[key] = [c for c in claims if c.player_id != player_id]
        self.revision += 1
        return True

    def snapshot(self) -> Snapshot:
        roster = tuple(sorted((pid, p.ledger.confirmed_volume, p.ledger.banked_score, p.life.value)
                              for pid, p in self.players.items()))
        return Snapshot(self.round_id, self.phase, self.phase_ends_at, self.revision,
                        self.commit_sequence, roster, tuple(self.commits[k] for k in sorted(self.commits)))

    def install_snapshot(self, snapshot: Snapshot) -> None:
        """Replace replica's committed view atomically; late join sees one revision."""
        if snapshot.round_id != self.round_id:
            raise ValueError("snapshot belongs to another round")
        self.phase, self.phase_ends_at = snapshot.phase, snapshot.phase_ends_at
        self.revision, self.commit_sequence = snapshot.revision, snapshot.commit_sequence
        self.players = {}
        for pid, volume, banked, life in snapshot.roster:
            self.players[pid] = Player(PlayerLedger(volume, banked), Lifecycle(life))
        self.commits = {c.key: c for c in snapshot.commits}
        self.item_winners = {c.key: c.winner for c in snapshot.commits}
        self._claims.clear()

    def _validate_key(self, key: EventKey) -> None:
        if key.round_id != self.round_id or not key.item_instance_id or key.generation < 0:
            raise ValueError("event key round/item/generation invalid")
