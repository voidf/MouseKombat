"""Authoritative room state — the Python port of MouseKombat.Net/RoomState.cs.

This is the room logic the C# host runs in-process for a LAN game; the lobby server runs the
same rules per room. Every rule here mirrors the C# side exactly, because clients behave
identically in both modes (one protocol, two hosts — see PROTOCOL.md).

Seat/player/member rules (PROTOCOL.md § Room state):

* Two fighting seats. First claim wins; a claim on a taken seat is refused.
* One seat per player. Claiming a second releases the first.
* Anyone may release their own seat. Nobody can release someone else's.
* AI seats are host-only, and the host may put an AI in either seat regardless of what it
  holds itself. The AI runs on the host player's machine and consumes no player slot.
* A match may start only when both seats are occupied and each has a character.
* MaxPlayers counts HUMANS (AI seats are driven by the host player, so they consume no slot).
"""

from __future__ import annotations

from protocol import HARD_MAX_PLAYERS, sanitize_name

SEAT_COUNT = 2


class PlayerInfo:
    __slots__ = ("player_id", "name", "is_host", "seat", "connected")

    def __init__(self, player_id: int, name: str, is_host: bool):
        self.player_id = player_id
        self.name = name
        self.is_host = is_host
        self.seat = -1
        self.connected = True

    def snapshot(self):
        return [self.player_id, self.name, self.is_host, self.seat, self.connected]


class SeatInfo:
    __slots__ = ("occupant_player_id", "character", "is_ai", "ai_model")

    def __init__(self, occupant_player_id: int = 0, character: int = -1,
                 is_ai: bool = False, ai_model: str = ""):
        self.occupant_player_id = occupant_player_id  # 0 = empty
        self.character = character                    # -1 = not chosen yet
        self.is_ai = is_ai
        self.ai_model = ai_model                       # "" = built-in state machine

    @property
    def occupied(self) -> bool:
        return self.occupant_player_id != 0 or self.is_ai

    @property
    def ready(self) -> bool:
        return self.occupied and self.character >= 0

    def snapshot(self):
        return [self.occupant_player_id, self.character, self.is_ai, self.ai_model]


class RoomState:
    def __init__(self):
        self.room_id = ""
        self.max_players = 0            # 0 = unlimited (LAN); lobby rooms are 2..HARD_MAX_PLAYERS
        self.match_running = False
        self.host_player_id = 0
        self._players = {}              # player_id -> PlayerInfo
        self._seats = [SeatInfo(), SeatInfo()]
        self._next_player_id = 1        # 0 means "nobody", so ids start at 1

    # ---- membership ----

    def add_player(self, name: str, is_host: bool):
        """Returns the new PlayerInfo, or None when the room is full."""
        if self.max_players > 0 and len(self._players) >= self.max_players:
            return None
        p = PlayerInfo(self._next_player_id, sanitize_name(name), is_host)
        self._next_player_id += 1
        self._players[p.player_id] = p
        if is_host:
            self.host_player_id = p.player_id
        return p

    def remove_player(self, player_id: int) -> None:
        p = self._players.pop(player_id, None)
        if p is None:
            return
        self._release_seat_of(player_id)
        if self.host_player_id == player_id:
            self.host_player_id = 0

    def mark_disconnected(self, player_id: int) -> None:
        """Mid-match drop. The seat is NOT freed and the player is NOT removed: their inputs
        are treated as neutral for the rest of the round, and the kick happens at match end."""
        p = self._players.get(player_id)
        if p is not None:
            p.connected = False

    @property
    def disconnected_ids(self):
        return [p.player_id for p in self._players.values() if not p.connected]

    def find(self, player_id: int):
        return self._players.get(player_id)

    @property
    def players(self):
        return sorted(self._players.values(), key=lambda p: p.player_id)

    def seat(self, i: int) -> SeatInfo:
        return self._seats[i]

    # ---- seats ----

    def claim_seat(self, player_id: int, seat: int) -> bool:
        if self.match_running:
            return False
        if seat < 0 or seat >= SEAT_COUNT:
            return False
        p = self._players.get(player_id)
        if p is None:
            return False
        if self._seats[seat].occupied:
            return False          # taken, including by an AI
        if p.seat == seat:
            return False          # already there; nothing changed
        # one seat per player: taking a second gives up the first
        if p.seat >= 0:
            self._clear_seat(p.seat)
        self._seats[seat] = SeatInfo(occupant_player_id=player_id)
        p.seat = seat
        return True

    def release_seat(self, player_id: int) -> bool:
        if self.match_running:
            return False
        p = self._players.get(player_id)
        if p is None or p.seat < 0:
            return False
        self._clear_seat(p.seat)
        p.seat = -1
        return True

    def pick_character(self, player_id: int, character: int) -> bool:
        if self.match_running:
            return False
        if character < 0:
            return False
        p = self._players.get(player_id)
        if p is None or p.seat < 0:
            return False
        if self._seats[p.seat].occupant_player_id != player_id:
            return False
        self._seats[p.seat].character = character
        return True

    # HOST ONLY, and the host may fill either seat regardless of what it holds itself.
    def add_ai(self, requester_id: int, seat: int, character: int, ai_model: str) -> bool:
        if self.match_running:
            return False
        if seat < 0 or seat >= SEAT_COUNT:
            return False
        if requester_id != self.host_player_id or self.host_player_id == 0:
            return False
        if self._seats[seat].occupied:
            return False
        if character < 0:
            return False
        self._seats[seat] = SeatInfo(character=character, is_ai=True, ai_model=ai_model or "")
        return True

    # HOST ONLY, the mirror of add_ai.
    def remove_ai(self, requester_id: int, seat: int) -> bool:
        if self.match_running:
            return False
        if seat < 0 or seat >= SEAT_COUNT:
            return False
        if requester_id != self.host_player_id or self.host_player_id == 0:
            return False
        if not self._seats[seat].is_ai:
            return False
        self._clear_seat(seat)
        return True

    def _clear_seat(self, seat: int) -> None:
        occupant = self._seats[seat].occupant_player_id
        self._seats[seat] = SeatInfo()
        if occupant != 0:
            p = self._players.get(occupant)
            if p is not None and p.seat == seat:
                p.seat = -1

    def _release_seat_of(self, player_id: int) -> None:
        for i in range(SEAT_COUNT):
            if self._seats[i].occupant_player_id == player_id:
                self._clear_seat(i)

    # ---- match lifecycle ----

    @property
    def can_start(self) -> bool:
        return not self.match_running and all(s.ready for s in self._seats)

    def begin_match(self) -> bool:
        if not self.can_start:
            return False
        self.match_running = True
        return True

    def end_match(self):
        """After a knockout: drop anyone who disconnected mid-match, clear every seat so the
        room picks again from scratch. Returns the dropped player ids."""
        self.match_running = False
        dropped = self.disconnected_ids
        for pid in dropped:
            self.remove_player(pid)
        for i in range(SEAT_COUNT):
            self._clear_seat(i)
        for p in self._players.values():
            p.seat = -1
        return dropped

    # ---- snapshot ----

    def snapshot(self):
        return [
            [p.snapshot() for p in self.players],  # stable order: by player id
            [s.snapshot() for s in self._seats],
            self.room_id,
            self.max_players,
            self.match_running,
        ]

    # Sanity guard for the hard cap: the lobby server never builds a room above 4 humans,
    # but the check lives here so no future caller can bypass it.
    @staticmethod
    def clamp_max_players(value: int) -> int:
        return max(2, min(int(value), HARD_MAX_PLAYERS))
