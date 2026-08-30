"""Persistent player accounts for the lobby server — one SQLite file.

The `players` table (spec):

    playerid    INTEGER PRIMARY KEY AUTOINCREMENT  -- uint64 自增主键, from 1
    name        TEXT UNIQUE NOT NULL               -- the login text (the identity)
    score       INTEGER NOT NULL                   -- Elo rating, default 1000, drives matchmaking
    created_at  TIMESTAMP DEFAULT CURRENT_TIMESTAMP

The file is opened in WAL mode so a reader (backup, an operator poking with the sqlite3 CLI) never
blocks the writer. Every statement here is a single indexed row touch, so calling them inline from
the asyncio event loop is fine for the target scale (<100 concurrent players): a lookup is
microseconds, and the server is single-threaded anyway — no cross-thread locking is needed.

`name` is the login identity (UNIQUE, case-sensitive like every other part of the protocol —
RoomState names already compare byte-wise). `playerid` exists so future business logic can index by
the integer instead of the text; today nothing on the wire depends on it beyond LoginOk.
"""

from __future__ import annotations

import os
import sqlite3
from typing import Tuple


class Account:
    """One row of `players`, as the server passes it around."""

    __slots__ = ("player_id", "name", "score")

    def __init__(self, player_id: int, name: str, score: int):
        self.player_id = player_id
        self.name = name
        self.score = score


class PlayerStore:
    def __init__(self, path: str, initial_score: int = 1000):
        parent = os.path.dirname(os.path.abspath(path))
        if parent:
            os.makedirs(parent, exist_ok=True)
        self._initial_score = initial_score
        self._conn = sqlite3.connect(path)
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA synchronous=NORMAL")
        self._conn.execute(
            """
            CREATE TABLE IF NOT EXISTS players (
                playerid    INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT UNIQUE NOT NULL,
                score       INTEGER NOT NULL DEFAULT 1000,
                created_at  TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )
            """
        )
        # UNIQUE(name) already gives lookups an index; this is the spec's explicit one, kept so the
        # schema reads exactly like the design doc.
        self._conn.execute("CREATE INDEX IF NOT EXISTS idx_players_name ON players(name)")
        self._conn.commit()

    # ---- reads / writes (all called on the event loop thread) ----

    def find_or_create(self, name: str) -> Account:
        """The account for this login name, created with the initial score on first sight."""
        row = self._conn.execute(
            "SELECT playerid, score FROM players WHERE name = ?", (name,)
        ).fetchone()
        if row is not None:
            return Account(int(row[0]), name, int(row[1]))
        try:
            cur = self._conn.execute(
                "INSERT INTO players (name, score) VALUES (?, ?)", (name, self._initial_score)
            )
            self._conn.commit()
            return Account(int(cur.lastrowid), name, self._initial_score)
        except sqlite3.IntegrityError:
            # Two hellos with the same name in one event-loop turn cannot happen (single thread),
            # but a UNIQUE race is cheap to honour correctly anyway.
            row = self._conn.execute(
                "SELECT playerid, score FROM players WHERE name = ?", (name,)
            ).fetchone()
            if row is None:
                raise
            return Account(int(row[0]), name, int(row[1]))

    def update_score(self, player_id: int, score: int) -> None:
        self._conn.execute(
            "UPDATE players SET score = ? WHERE playerid = ?", (int(score), int(player_id))
        )
        self._conn.commit()

    def close(self) -> None:
        try:
            self._conn.commit()
            self._conn.close()
        except sqlite3.Error:
            pass
