"""Matchmaking pool + Elo settlement for the lobby server.

Two independent pieces, both parameterised from config.json (see lobby_server.DEFAULT_CONFIG):

* :class:`Matchmaker` — the dynamic-bucket queue. A player's acceptable score gap starts at
  `bucket_base` and widens by `bucket_growth_per_second` for every second spent waiting, capped at
  `bucket_max`. A pair only forms when the gap fits BOTH players' current windows (bidirectional
  check), and among the candidates the closest gap wins. This is the "等待越久，接受分差越宽" rule
  that keeps a small population findable without flattening the ladder for the impatient.

* :func:`elo_update` — zero-sum Elo with a floor. The winner gains exactly what the loser loses
  (unless the loser is clamped at the floor, which accepts a little inflation), deltas are pinned to
  at least ±1 so a lopsided match still moves both ratings.
"""

from __future__ import annotations

import time
from typing import List, Optional, Tuple


def elo_update(winner_score: int, loser_score: int, k_factor: int = 32,
               score_floor: int = 0) -> Tuple[int, int]:
    """New (winner_score, loser_score) after one knockout."""
    expected_winner = 1.0 / (1.0 + 10 ** ((loser_score - winner_score) / 400.0))
    expected_loser = 1.0 - expected_winner
    winner_delta = round(k_factor * (1.0 - expected_winner))
    loser_delta = round(k_factor * (0.0 - expected_loser))   # negative
    # A foregone conclusion still pays out: never a 0-gain win or a 0-loss defeat.
    winner_delta = max(1, winner_delta)
    loser_delta = min(-1, loser_delta)
    new_winner = winner_score + winner_delta
    new_loser = max(score_floor, loser_score + loser_delta)  # 挫败感保底
    return new_winner, new_loser


class QueueEntry:
    """One player waiting in the pool. `score` and `asset_hash` are snapshotted at add() time —
    the pairing decisions must not chase a score that changed mid-wait, and two clients with
    DIFFERENT Heroes/ content must never be paired (they would desync on frame 1, the same gate
    the room join path enforces)."""

    __slots__ = ("member", "player_id", "score", "asset_hash", "started")

    def __init__(self, member, player_id: int, score: int, asset_hash: str, started: float):
        self.member = member
        self.player_id = player_id
        self.score = score
        self.asset_hash = asset_hash or ""
        self.started = started

    def bucket(self, now: float, base: int, growth: float, cap: int) -> int:
        return min(cap, base + int((now - self.started) * growth))


class Matchmaker:
    def __init__(self, bucket_base: int = 100, bucket_growth: float = 15.0, bucket_max: int = 400):
        self.bucket_base = bucket_base
        self.bucket_growth = bucket_growth
        self.bucket_max = bucket_max
        self.queue: List[QueueEntry] = []   # arbitrary order; the scan is O(n²) over a tiny n

    def __len__(self) -> int:
        return len(self.queue)

    def add(self, member, player_id: int, score: int, asset_hash: str) -> None:
        if self.contains(player_id):
            return
        self.queue.append(QueueEntry(member, player_id, score, asset_hash, time.monotonic()))

    def remove(self, member) -> bool:
        """Drop every entry belonging to this connection (顶号 / disconnect / joined a room)."""
        before = len(self.queue)
        self.queue = [e for e in self.queue if e.member is not member]
        return len(self.queue) != before

    def contains(self, player_id: int) -> bool:
        return any(e.player_id == player_id for e in self.queue)

    def update_and_match(self, now: Optional[float] = None) -> List[Tuple[QueueEntry, QueueEntry]]:
        """One heartbeat of pairing. Matched entries are REMOVED from the pool; the caller that
        fails to host the pair (e.g. the room table is full) must put them back with readd()."""
        now = time.monotonic() if now is None else now
        matched: List[Tuple[QueueEntry, QueueEntry]] = []
        i = 0
        while i < len(self.queue):
            a = self.queue[i]
            bucket_a = a.bucket(now, self.bucket_base, self.bucket_growth, self.bucket_max)
            best: Optional[QueueEntry] = None
            best_gap = None
            best_index = -1
            for j in range(i + 1, len(self.queue)):
                b = self.queue[j]
                if a.asset_hash and b.asset_hash and a.asset_hash != b.asset_hash:
                    continue   # different content = guaranteed desync; never pair
                gap = abs(a.score - b.score)
                bucket_b = b.bucket(now, self.bucket_base, self.bucket_growth, self.bucket_max)
                if gap <= bucket_a and gap <= bucket_b and (best_gap is None or gap < best_gap):
                    best, best_gap, best_index = b, gap, j
            if best is not None:
                matched.append((a, best))
                self.queue.pop(best_index)
                self.queue.pop(i)
            else:
                i += 1
        return matched

    def readd(self, entry: QueueEntry) -> None:
        """Put an unmatched pair entry back, keeping its original wait start (readd must not
        reset the bucket expansion — the player has been waiting all along)."""
        if not self.contains(entry.player_id):
            self.queue.append(entry)
