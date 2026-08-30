"""MouseKombat lobby server — the public room host for lobby games.

THE SPEC IS ../../MouseKombat.Net/PROTOCOL.md. A LAN game's host runs a mini room server
in-process (TcpRoomHost); a lobby game's rooms live here. Both speak the same messages, so the
client code is identical and only the endpoint differs. This file implements the room-authority
half (RoomState lives in room.py) plus the lobby-only extras:

* room directory: list (paged, searchable only, newest first), create (2..4 humans, optional
  4-digit password, searchable flag), join by 6-digit room id + password, hard cap 4 humans;
* version check at connect (Hello) against the configured game version — a mismatch is refused
  before anything else;
* account login (SQLite, one file — see player_store.py): the Hello name IS the account. A name
  seen for the first time is registered; a name already ONLINE on another connection starts the
  顶号 (kick-out) handshake — KickConfirm -> client confirms with KickLogin -> the old connection
  is torn down (out of the matchmaking pool, out of its room, a mid-match seat counts as a
  surrender) and only then is the account rebound to the new connection;
* matchmaking pool (matchmaking.py): MatchmakeJoin puts a logged-in browser into the pool; a
  heartbeat pairs players by dynamic score buckets, creates a 2-human room with both seats
  auto-claimed and auto-picked characters, and (config) auto-starts the match — the clients get
  the standard Welcome/StartMatch flow with no room screen in between;
* per-connection ping: the server heartbeats Ping and measures RTT from each Pong, then reports
  (self, opponent) RTTs to seated members (PingStats) for the in-match HUD;
* the match UDP relay: fighters wrap every rollback datagram in
      u32 roomId (LE) + u8 srcSlot + u8 dstSlot + opaque payload
  and the server forwards the payload to the dstSlot holder's match endpoint;
* catch-up routing (mid-match spectating): MatchInputReport frames are forwarded to the host
  player, HostSendTo carries the host player's catch-up frames to a chosen member, and
  LobbyPlayerJoined tells the host player when someone joins so it can serve a catch-up.

Room state is in-memory (spec: no persistence THERE); only player accounts persist, in one SQLite
file (WAL mode). Single-threaded asyncio. 2C2G comfortably handles the target of under 100
concurrent players.

Run:  python lobby_server.py [--host H] [--port P] [--udp-port P] [--game-version V]
                            [--config PATH] [--db PATH]
Config can also come from env: MK_HOST / MK_PORT / MK_UDP_PORT / MK_GAME_VERSION /
MK_PROTOCOL / MK_IDLE_TIMEOUT / MK_MAX_ROOMS. Matchmaking / db / ping settings come from
config.json (next to this file by default; --config overrides).
"""

from __future__ import annotations

import argparse
import asyncio
import itertools
import json
import logging
import os
import random
import signal
import sqlite3
import sys
import time

from protocol import (
    MAX_FRAME_BYTES, MSG_ADD_AI, MSG_BYE, MSG_CHAR_PICK, MSG_HELLO, MSG_HOST_SEND_TO,
    MSG_KICK_CONFIRM, MSG_KICK_LOGIN, MSG_KICKED, MSG_LOGIN_OK, MSG_LOBBY_CREATE,
    MSG_LOBBY_JOIN, MSG_LOBBY_LIST, MSG_LOBBY_PLAYER_JOINED, MSG_LOBBY_ROOMS,
    MSG_MATCH_ENDED, MSG_MATCH_INPUT_REPORT, MSG_MATCH_RESULT, MSG_MATCH_START,
    MSG_MATCHMAKE_CANCEL, MSG_MATCHMAKE_JOIN, MSG_MATCHMAKE_STATUS, MSG_PING, MSG_PING_STATS,
    MSG_PONG, MSG_REJECTED, MSG_REMOVE_AI, MSG_ROOM_STATE, MSG_SEAT_CLAIM, MSG_SEAT_RELEASE,
    MSG_START_MATCH, MSG_WELCOME, PAGE_SIZE, PROTOCOL, ROOM_MEMBER_TYPES,
    HOST_SEND_TO_ALLOWED_TYPES, ProtocolError, FrameReader, decode_body, encode_frame,
    is_valid_password, is_valid_room_id, normalize_ip, sanitize_name,
)
from player_store import Account, PlayerStore
from matchmaking import Matchmaker, elo_update
from room import RoomState, SEAT_COUNT

log = logging.getLogger("lobby")

# How long a pinned match endpoint may stay SILENT before another source from the same IP is allowed
# to take the pin over. The pin exists to stop a member behind the same NAT from injecting frames for
# another member's seat, and inside a running match a fighter sends ~60 datagrams a second — so a pin
# that has heard nothing for this long is a dead NAT mapping, not a live fighter. Without this a
# mid-match remap would kill the rest of the match. See _handle_udp_inner.
ENDPOINT_REPIN_AFTER = 2.0

# Defaults for config.json (the shipped config.json mirrors these). The file overrides these,
# --config points at a different file; anything missing in the file falls back to the values here.
DEFAULT_CONFIG = {
    "db_path": "lobby.db",
    "matchmaking": {
        "initial_score": 1000,          # the score a fresh account is registered with
        "k_factor": 32,                 # standard Elo K
        "score_floor": 0,               # a loser never goes below this
        "bucket_base": 100,             # acceptable score gap on the first second of waiting
        "bucket_growth_per_second": 15, # the gap widens by this much per second waited
        "bucket_max": 400,              # ... and never wider than this
        "tick_interval_seconds": 1.0,   # matchmaking heartbeat
        "auto_start": True,             # a matchmade room starts the match by itself
        "auto_characters": [0, 1, 2],   # random per-seat pick pool for the auto-claimed seats
    },
    "ping": {
        "interval_seconds": 2.0,        # server heartbeat; RTT is measured from each Pong
    },
}


def load_config(path: str | None) -> dict:
    """config.json merged over DEFAULT_CONFIG (shallow per section, so a partial section keeps the
    remaining defaults). A missing file is fine — the defaults are the config."""
    cfg = json.loads(json.dumps(DEFAULT_CONFIG))   # deep-enough copy of the defaults
    if path and os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            for key, value in data.items():
                if isinstance(value, dict) and isinstance(cfg.get(key), dict):
                    cfg[key].update(value)
                else:
                    cfg[key] = value
        except (OSError, ValueError) as e:
            raise SystemExit(f"config {path} is unreadable: {e}")
    return cfg


# --------------------------------------------------------------------------- models

class Member:
    """One TCP connection. In lobby phase it is a browser (or a queued matchmaker); after
    create/join it is a room member. From Hello on it is also bound to an ACCOUNT (playerid +
    score from SQLite), which is the player's identity for scoring and 顶号."""

    __slots__ = ("player_id", "name", "is_host", "reader", "writer", "tcp_ip",
                 "announced_udp_port", "udp_endpoint", "udp_last_rx", "phase", "last_activity",
                 "room", "asset_hash",
                 "account_id", "account_score", "kick_pending",
                 "ping_seq", "ping_sent_at", "ping_rtt")

    def __init__(self, writer):
        self.player_id = 0
        self.name = "玩家"
        self.is_host = False
        self.reader = FrameReader()
        self.writer = writer
        self.tcp_ip = normalize_ip((writer.get_extra_info("peername") or ("?", 0))[0])
        # UDP port the client bound and announced in Hello. Only a GUESS at the public endpoint
        # when a NAT sits in between; the observed source of the client's first datagrams
        # replaces it (udp_endpoint). See handle_udp.
        self.announced_udp_port = 0
        self.udp_endpoint = None
        self.udp_last_rx = 0.0      # monotonic time of the last datagram accepted from udp_endpoint
        self.phase = "hello"        # "hello" -> ("op" | "kick_wait") -> "room" -> "closed"
        self.last_activity = time.monotonic()
        self.room = None
        self.asset_hash = ""        # Heroes/ content hash from Hello; gates room entry
        # ---- account (persistent identity; 0 = not bound yet) ----
        self.account_id = 0
        self.account_score = 0
        self.kick_pending = None    # Account awaiting the 顶号 confirm (phase "kick_wait")
        # ---- ping (server-measured RTT, seconds; None until the first Pong) ----
        self.ping_seq = 0
        self.ping_sent_at = None
        self.ping_rtt = None

    def touch(self):
        self.last_activity = time.monotonic()


class Room:
    """A lobby room: RoomState (authority) + the members connected to it + directory metadata."""

    __slots__ = ("id_int", "seq", "state", "password", "searchable", "created_at", "members",
                 "asset_hash")

    def __init__(self, id_int: int, max_players: int, password: str, searchable: bool):
        self.id_int = id_int
        self.seq = _next_seq()      # strict creation order; the room list sorts by it (a
        self.state = RoomState()    # monotonic clock can tick twice in one creation burst)
        self.state.room_id = f"{id_int:06d}"
        self.state.max_players = RoomState.clamp_max_players(max_players)
        self.password = password
        self.searchable = searchable
        self.created_at = time.monotonic()
        self.members = {}           # player_id -> Member
        self.asset_hash = ""        # stamped at creation from the host member's Hello

    @property
    def host_member(self):
        return self.members.get(self.state.host_player_id)


_next_seq = itertools.count().__next__


# --------------------------------------------------------------------------- server

class LobbyServer:
    def __init__(self, host: str, port: int, udp_port: int, game_version: str,
                 protocol: int = PROTOCOL, idle_timeout: float = 300.0, max_rooms: int = 500,
                 config_path: str | None = None, db_path: str | None = None,
                 config_overrides: dict | None = None):
        self.host = host
        self.port = port
        self.udp_port = udp_port
        self.game_version = game_version or ""
        self.protocol = protocol
        self.idle_timeout = idle_timeout
        self.max_rooms = max_rooms
        self.rooms = {}             # int room id -> Room
        self.conns = set()          # every live connection, room member or browser (see _reaper_loop)
        self._udp_transport = None
        self._udp_rebinding = False
        self._tcp_server = None

        # ---- account persistence + online table + matchmaking ----
        self.cfg = load_config(config_path)
        if config_overrides:
            for key, value in config_overrides.items():
                if isinstance(value, dict) and isinstance(self.cfg.get(key), dict):
                    self.cfg[key].update(value)
                else:
                    self.cfg[key] = value
        self.mm_cfg = self.cfg["matchmaking"]
        self.ping_cfg = self.cfg["ping"]
        db = db_path or self.cfg["db_path"]
        if not os.path.isabs(db):
            # A relative db path means "next to the config file", so the server's data stays
            # where the operator put the config regardless of the working directory.
            base = os.path.dirname(os.path.abspath(config_path)) if config_path else \
                os.path.dirname(os.path.abspath(__file__))
            db = os.path.join(base, db)
        self.db_path = db
        self.players = PlayerStore(db, self.mm_cfg["initial_score"])
        self.online = {}            # account playerid -> Member (the session that owns it)
        self._matchmaker = Matchmaker(self.mm_cfg["bucket_base"],
                                      self.mm_cfg["bucket_growth_per_second"],
                                      self.mm_cfg["bucket_max"])
        self._mm_task = None
        self._ping_task = None

    # ---- lifecycle ----

    async def start(self):
        self._tcp_server = await asyncio.start_server(
            self._handle_conn, self.host, self.port, limit=MAX_FRAME_BYTES + 64)
        # Resolve port 0 to what the OS actually assigned, so tests can discover the endpoint
        # and StartMatch announces the REAL udp port.
        self.port = self._tcp_server.sockets[0].getsockname()[1]
        transport, _ = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: _UdpProtocol(self._handle_udp, self._udp_died), local_addr=(self.host, self.udp_port))
        self._udp_transport = transport
        self.udp_port = transport.get_extra_info("sockname")[1]
        loop = asyncio.get_running_loop()
        loop.create_task(self._reaper_loop())
        self._mm_task = loop.create_task(self._matchmaker_loop())
        self._ping_task = loop.create_task(self._ping_loop())
        log.info("lobby up: tcp %s:%d udp %s:%d protocol %d game version %r db %s",
                 self.host, self.port, self.host, self.udp_port, self.protocol,
                 self.game_version or "(unset — every Hello refused)", self.db_path)

    async def shutdown(self):
        for room in list(self.rooms.values()):
            self._destroy_room(room, "服务器已关闭", close=True)
        # Close every remaining connection BEFORE wait_closed: on Python 3.13+ wait_closed()
        # also waits for every client handler, and a stray browser (a crash mid-request, a test
        # that leaked a client) would otherwise hold the shutdown hostage forever.
        for m in list(self.conns):
            m.phase = "closed"
            self._close_member(m)
        if self._tcp_server is not None:
            self._tcp_server.close()
            try:
                await asyncio.wait_for(self._tcp_server.wait_closed(), timeout=2.0)
            except asyncio.TimeoutError:
                pass
        for task in (self._mm_task, self._ping_task):
            if task is not None:
                task.cancel()
        self.players.close()

    # ---- TCP ----

    async def _handle_conn(self, reader, writer):
        m = Member(writer)
        self.conns.add(m)
        try:
            while True:
                data = await reader.read(8192)
                if not data:
                    break
                m.touch()
                m.reader.feed(data)
                while True:
                    frame = m.reader.try_read()   # may raise ProtocolError
                    if frame is None:
                        break
                    self._route(m, frame[0], frame[1])
                    if m.phase == "closed":
                        return
        except (ProtocolError, ConnectionError, asyncio.IncompleteReadError,
                OSError, ValueError) as e:
            log.info("conn %s:%s dropped: %s", m.tcp_ip,
                     (m.writer.get_extra_info("peername") or (0,))[1], e)
        finally:
            self._on_conn_lost(m)
            self.conns.discard(m)
            try:
                writer.close()
            except Exception:
                pass

    def _route(self, m: Member, msg_type: int, body: bytes):
        if m.phase == "hello":
            if msg_type == MSG_HELLO:
                self._on_hello(m, decode_body(body))
            else:
                self._reject(m, "未完成握手")
            return
        if msg_type == MSG_PONG:
            # The client auto-pongs from every phase past the handshake; the RTT it produces feeds
            # the match HUD, and the periodic reply also keeps an idle browser off the reaper.
            self._on_pong(m, decode_body(body))
            return
        if m.phase == "kick_wait":
            # Mid-顶号: the account is confirmed online elsewhere. Only the confirm proceeds;
            # browse ops aimed at this connection (a stale client's parked LobbyList) are refused
            # without closing, exactly like any other lobby-phase validation failure.
            if msg_type == MSG_KICK_LOGIN:
                self._on_kick_login(m, decode_body(body))
            else:
                self._refuse(m, "请先完成登录")
            return
        if m.phase == "op":
            if msg_type == MSG_LOBBY_LIST:
                self._on_list(m, decode_body(body))
            elif msg_type == MSG_LOBBY_CREATE:
                self._on_create(m, decode_body(body))
            elif msg_type == MSG_LOBBY_JOIN:
                self._on_join(m, decode_body(body))
            elif msg_type == MSG_MATCHMAKE_JOIN:
                self._on_matchmake_join(m)
            elif msg_type == MSG_MATCHMAKE_CANCEL:
                self._on_matchmake_cancel(m)
            else:
                self._reject(m, "未加入房间")
            return
        if m.phase == "room":
            self._on_room_message(m, msg_type, decode_body(body))
            return
        # "closed": ignore anything already buffered.

    # ---- lobby phase ----

    def _on_hello(self, m: Member, body):
        if not isinstance(body, list) or len(body) < 5:
            self._reject(m, "握手数据不完整")
            return
        protocol, game_version = body[0], body[1]
        if protocol != self.protocol or game_version != self.game_version:
            reason = "协议版本不一致" if protocol != self.protocol else "游戏版本不一致"
            self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version, "", ""]))
            log.info("refused %s: %s (client protocol %r version %r, server %r/%r)",
                     m.tcp_ip, reason, protocol, game_version, self.protocol, self.game_version)
            m.phase = "closed"
            return
        m.name = sanitize_name(body[2])
        port = body[4] if isinstance(body[4], int) else 0
        m.announced_udp_port = port if 0 < port <= 65535 else 0
        m.asset_hash = body[5] if len(body) > 5 and isinstance(body[5], str) else ""
        self._login(m, m.name)

    # ---- account login + 顶号 (kick-out) ----
    #
    # The Hello NAME is the login. The account row (playerid, score) is created on first sight;
    # the ONLINE check is what may route the connection through the kick-out handshake instead of
    # straight into the browse phase:
    #
    #   login ok                        -> phase "op", LoginOk{playerid, score}
    #   account online elsewhere        -> phase "kick_wait", KickConfirm{name, score}
    #                                        client confirms with KickLogin:
    #                                          1. the OLD connection is torn down COMPLETELY —
    #                                             out of the matchmaking pool, out of its room (a
    #                                             mid-match seat counts as a surrender, scored),
    #                                             Kicked{reason} sent, socket closed, online
    #                                             entry removed — all before step 2 touches
    #                                             anything;
    #                                          2. the account rebinds to the NEW connection and
    #                                             it receives LoginOk (with the score AS SETTLED,
    #                                             in case the surrender just moved it).

    def _login(self, m: Member, name: str):
        try:
            account = self.players.find_or_create(name)
        except sqlite3.Error as e:
            log.error("db error on login %r: %s", name, e)
            self._reject(m, "服务器账号数据不可用")
            return
        online = self.online.get(account.player_id)
        if online is not None and online is not m and not online.writer.is_closing():
            m.kick_pending = account
            m.phase = "kick_wait"
            self._send_raw(m, encode_frame(MSG_KICK_CONFIRM, [account.name, account.score]))
            log.info("login %r: account #%d is online elsewhere — awaiting 顶号 confirm",
                     name, account.player_id)
            return
        self._bind_account(m, account)

    def _bind_account(self, m: Member, account: Account):
        """The connection becomes the account's ONE online session and enters the browse phase."""
        m.account_id = account.player_id
        m.account_score = account.score
        m.kick_pending = None
        self.online[account.player_id] = m
        m.phase = "op"
        self._send_raw(m, encode_frame(MSG_LOGIN_OK, [m.account_id, m.account_score]))
        log.info("login %r as account #%d (score %d, match udp %d)",
                 m.name, m.account_id, m.account_score, m.announced_udp_port)

    def _on_kick_login(self, m: Member, body):
        if m.phase != "kick_wait" or m.kick_pending is None:
            self._refuse(m, "没有待确认的登录")
            return
        account = m.kick_pending
        m.kick_pending = None
        old = self.online.get(account.player_id)
        if old is not None and old is not m:
            # Tear the old session down FIRST (spec: the old connection must be fully destroyed
            # before the new one binds the session). A chain of confirmations kicks whoever
            # CURRENTLY holds the account — the winner of a race between two confirmers.
            self._kick_session(old, "您的账号在其他设备上登录，本机连接已被断开")
        # Re-read: the surrender above may have just settled the account's score.
        account = self.players.find_or_create(account.name)
        self._bind_account(m, account)

    def _kick_session(self, old: Member, reason: str):
        """Force one online session offline because its ACCOUNT was taken over.

        Order matters and is the whole point: matchmaking queue, then room membership, then the
        online table — every in-memory handle cleared synchronously — and only then the Kicked
        frame and the physical close. By the time this returns, nothing references `old` except
        its own dying socket."""
        self._matchmaker.remove(old)
        if old.phase == "room" and old.room is not None:
            room = old.room
            state = room.state
            if state.match_running and state.holds_seat(old.player_id):
                # In a match: count it as a surrender for the kicked player NOW (spec) — the
                # opponent gets the win, the Elo settles, and nobody sims against a ghost seat.
                seat = 0 if state.seat(0).occupant_player_id == old.player_id else 1
                self._end_match(room, 1 - seat)
            self._leave_room(old, "账号在其他设备上登录")
        if self.online.get(old.account_id) is old:
            self.online.pop(old.account_id, None)
        old.account_id = 0
        old.kick_pending = None
        self._send_raw(old, encode_frame(MSG_KICKED, [reason]))
        # phase "closed" makes the socket's own _on_conn_lost a no-op: the room and the online
        # table were already handled here, and _leave_room must not run twice.
        old.phase = "closed"
        self._close_member(old)
        log.info("kicked session of %r (%s)", old.name, reason)

    # ---- matchmaking pool ----

    def _on_matchmake_join(self, m: Member):
        if m.account_id == 0:
            self._refuse(m, "请先完成登录")
            return
        if self._matchmaker.contains(m.account_id):
            return
        self._matchmaker.add(m, m.account_id, m.account_score, m.asset_hash)
        self._send_raw(m, encode_frame(MSG_MATCHMAKE_STATUS, [True, 0]))
        log.info("matchmaking: %r (score %d) joined the pool (%d waiting)",
                 m.name, m.account_score, len(self._matchmaker))

    def _on_matchmake_cancel(self, m: Member):
        if self._matchmaker.remove(m):
            self._send_raw(m, encode_frame(MSG_MATCHMAKE_STATUS, [False, 0]))
            log.info("matchmaking: %r left the pool", m.name)

    async def _matchmaker_loop(self):
        interval = max(0.05, float(self.mm_cfg["tick_interval_seconds"]))
        while True:
            await asyncio.sleep(interval)
            try:
                self._matchmake_tick()
            except Exception as e:  # noqa: BLE001 — a pool error must not kill the heartbeat
                log.error("matchmaker tick failed: %s", e)

    def _matchmake_tick(self):
        for a, b in self._matchmaker.update_and_match():
            if a.member.phase != "op" or b.member.phase != "op":
                # Stale pair (a connection died between the queue scan and this tick): the
                # conn-lost cleanup removes dead entries, so just wait for the next tick.
                continue
            if not self._create_matchmade_room(a, b):
                self._matchmaker.readd(a)
                self._matchmaker.readd(b)

    def _create_matchmade_room(self, a, b) -> bool:
        """Both queue entries become the two fighters of a fresh, hidden, 2-human room: seats
        claimed, characters auto-picked, standard Welcome/RoomState, and (config) the match
        auto-starts — the clients ride the normal StartMatch flow straight into the fight."""
        if len(self.rooms) >= self.max_rooms:
            log.warning("matchmaking: room table full, pair left queued")
            return False
        ma, mb = a.member, b.member
        id_int = self._fresh_room_id()
        room = Room(id_int, 2, "", False)
        room.asset_hash = ma.asset_hash
        pa = room.state.add_player(ma.name, is_host=True)
        pb = room.state.add_player(mb.name, is_host=False)
        if pa is None or pb is None:
            return False
        room.members[pa.player_id] = ma
        room.members[pb.player_id] = mb
        for member, player in ((ma, pa), (mb, pb)):
            member.player_id = player.player_id
            member.is_host = player.is_host
            member.phase = "room"
            member.room = room
        room.state.claim_seat(pa.player_id, 0)
        room.state.claim_seat(pb.player_id, 1)
        pool = self.mm_cfg["auto_characters"] or [0]
        room.state.pick_character(pa.player_id, random.choice(pool))
        room.state.pick_character(pb.player_id, random.choice(pool))
        self.rooms[id_int] = room
        self._send_welcome(ma, pa.player_id, True)
        self._send_welcome(mb, pb.player_id, False)
        self._broadcast_room(room)
        log.info("matchmaking: %r vs %r -> room %s (auto)", ma.name, mb.name, room.state.room_id)
        if self.mm_cfg["auto_start"]:
            self._start_match(room, ma, None)
        return True

    # ---- ping ----

    def _on_pong(self, m: Member, body):
        seq = body[0] if isinstance(body, list) and body and isinstance(body[0], int) else None
        if seq is None or m.ping_sent_at is None or seq != m.ping_seq:
            return   # a stale echo of a ping we already gave up matching
        sample = max(0.0, time.monotonic() - m.ping_sent_at)
        m.ping_sent_at = None
        # Exponential smoothing: one lost or bursty cycle must not swing the HUD.
        m.ping_rtt = sample if m.ping_rtt is None else (0.7 * m.ping_rtt + 0.3 * sample)

    def _send_ping_stats(self, m: Member):
        """(self, opponent) RTT for a member holding a fighting seat — what the match HUD shows."""
        if m.phase != "room" or m.room is None:
            return
        state = m.room.state
        seat = 0 if state.seat(0).occupant_player_id == m.player_id else \
            1 if state.seat(1).occupant_player_id == m.player_id else -1
        if seat < 0:
            return
        self_rtt = int(m.ping_rtt * 1000) if m.ping_rtt is not None else 0
        opp_rtt = 0
        other = state.seat(1 - seat)
        if not other.is_ai:
            om = m.room.members.get(other.occupant_player_id)
            if om is not None and om.ping_rtt is not None:
                opp_rtt = int(om.ping_rtt * 1000)
        self._send_raw(m, encode_frame(MSG_PING_STATS, [self_rtt, opp_rtt]))

    async def _ping_loop(self):
        interval = max(0.05, float(self.ping_cfg["interval_seconds"]))
        while True:
            await asyncio.sleep(interval)
            for m in list(self.conns):
                if m.phase not in ("op", "room", "kick_wait"):
                    continue
                m.ping_seq += 1
                m.ping_sent_at = time.monotonic()
                self._send_raw(m, encode_frame(MSG_PING, [m.ping_seq]))
                self._send_ping_stats(m)

    def _on_list(self, m: Member, body):
        page = body[0] if isinstance(body, list) and body and isinstance(body[0], int) else 0
        page = max(0, page)
        rooms = [r for r in self.rooms.values() if r.searchable]
        rooms.sort(key=lambda r: (r.seq, r.id_int), reverse=True)   # newest first
        total_pages = max(1, (len(rooms) + PAGE_SIZE - 1) // PAGE_SIZE)
        start = page * PAGE_SIZE
        entries = []
        for r in rooms[start:start + PAGE_SIZE]:
            host = r.state.find(r.state.host_player_id)
            # The displayed count must be the one the CAPACITY CHECK uses (RoomState.add_player counts
            # players, not live connections), or the browser advertises a free slot the join then
            # refuses with 房间已满 — which is exactly what a mid-match fighter's reserved slot looked
            # like ("2/4 人" on a room nobody could enter).
            entries.append([r.state.room_id, host.name if host else "?",
                            bool(r.password), len(r.state.players), r.state.max_players,
                            r.asset_hash])
        self._send_raw(m, encode_frame(MSG_LOBBY_ROOMS, [page, total_pages, entries]))

    def _on_create(self, m: Member, body):
        if not isinstance(body, list) or len(body) < 3:
            self._refuse(m, "建房数据不完整")
            return
        max_players = body[0]
        password = body[1] if isinstance(body[1], str) else ""
        searchable = bool(body[2])
        if not isinstance(max_players, int) or max_players < 2 or max_players > 4:
            self._refuse(m, "房间人数上限需在2到4之间")
            return
        if not is_valid_password(password):
            self._refuse(m, "密码必须是4位整数（或留空）")
            return
        if len(self.rooms) >= self.max_rooms:
            self._refuse(m, "服务器房间已满")
            return
        # Joining a room by hand ends the wait in the pool — one player cannot be both.
        self._matchmaker.remove(m)
        id_int = self._fresh_room_id()
        room = Room(id_int, max_players, password, searchable)
        room.asset_hash = m.asset_hash
        player = room.state.add_player(m.name, is_host=True)
        room.members[player.player_id] = m
        m.player_id = player.player_id
        m.is_host = True
        m.phase = "room"
        m.room = room
        self.rooms[id_int] = room
        self._send_welcome(m, player.player_id, True)
        self._broadcast_room(room)
        log.info("room %s created by %r (max %d, password %s, searchable %s)",
                 room.state.room_id, m.name, room.state.max_players,
                 "yes" if password else "no", searchable)

    def _on_join(self, m: Member, body):
        if not isinstance(body, list) or len(body) < 2:
            self._refuse(m, "加入数据不完整")
            return
        room_id, password = body[0], body[1]
        if not is_valid_room_id(room_id):
            self._refuse(m, "房间ID应为6位数字")
            return
        room = self.rooms.get(int(room_id))
        if room is None:
            self._refuse(m, "房间不存在")
            return
        if room.password != (password if isinstance(password, str) else ""):
            self._refuse(m, "密码错误")
            return
        if room.asset_hash and m.asset_hash != room.asset_hash:
            self._send_raw(m, encode_frame(MSG_REJECTED, [
                "资源版本不一致，无法进房", self.protocol, self.game_version,
                room.asset_hash, m.asset_hash]))
            log.info("refused %r join room %s: asset hash %r vs room %r",
                     m.name, room.state.room_id, m.asset_hash[:6], room.asset_hash[:6])
            m.phase = "closed"
            self._close_member(m)
            return
        # Joining a room by hand ends the wait in the pool — one player cannot be both.
        self._matchmaker.remove(m)
        player = room.state.add_player(m.name, is_host=False)
        if player is None:
            self._refuse(m, "房间已满")
            return
        room.members[player.player_id] = m
        m.player_id = player.player_id
        m.phase = "room"
        m.room = room
        self._send_welcome(m, player.player_id, False)
        self._broadcast_room(room)
        host = room.host_member
        if host is not None and host.player_id != player.player_id:
            self._send_raw(host, encode_frame(MSG_LOBBY_PLAYER_JOINED, [player.player_id]))
        log.info("%r joined room %s (%d/%d)", m.name, room.state.room_id,
                 len(room.state.players), room.state.max_players)

    # ---- room phase ----

    def _on_room_message(self, m: Member, msg_type: int, body):
        room = m.room
        state = room.state
        if msg_type not in ROOM_MEMBER_TYPES:
            return   # clients never send these; ignore like the C# host does

        if msg_type == MSG_SEAT_CLAIM:
            seat = body[0] if isinstance(body, list) and body else -1
            self._apply(room, state.claim_seat(m.player_id, seat))
        elif msg_type == MSG_SEAT_RELEASE:
            self._apply(room, state.release_seat(m.player_id))
        elif msg_type == MSG_CHAR_PICK:
            char = body[0] if isinstance(body, list) and body else -1
            self._apply(room, state.pick_character(m.player_id, char))
        elif msg_type == MSG_ADD_AI:
            # Host-only, enforced by RoomState. The character travels WITH the message (the AI flow
            # never PickCharacter'd the seat — it belongs to nobody yet); -1 or a missing field means
            # "whatever the seat already holds" (old-build compatibility).
            seat = body[0] if isinstance(body, list) and body else -1
            model = body[1] if isinstance(body, list) and len(body) > 1 else ""
            character = body[2] if isinstance(body, list) and len(body) > 2 else -1
            if character < 0:
                character = state.seat(seat).character
            self._apply(room, state.add_ai(m.player_id, seat, character, model))
        elif msg_type == MSG_REMOVE_AI:
            seat = body[0] if isinstance(body, list) and body else -1
            self._apply(room, state.remove_ai(m.player_id, seat))
        elif msg_type == MSG_MATCH_RESULT:
            if state.match_running:
                winner = body[0] if isinstance(body, list) and body else -1
                self._end_match(room, winner)
        elif msg_type == MSG_MATCH_INPUT_REPORT:
            # A fighter's confirmed-input report (relay configuration). Forwarded to the host
            # player, whose machine merges it into its catch-up history and serves joiners.
            if state.match_running:
                host = room.host_member
                if host is not None and host.player_id != m.player_id:
                    self._send_raw(host, encode_frame(msg_type, body))
        elif msg_type == MSG_MATCH_START:
            if m.is_host:
                self._on_match_start(room, m, body)
        elif msg_type == MSG_HOST_SEND_TO:
            if m.is_host and isinstance(body, list) and len(body) >= 3:
                target = room.members.get(body[0] if isinstance(body[0], int) else 0)
                frame_type, payload = body[1], body[2]
                if target is not None and isinstance(frame_type, int) \
                        and frame_type in HOST_SEND_TO_ALLOWED_TYPES \
                        and isinstance(payload, (bytes, bytearray)):
                    # Forward the frame VERBATIM: the payload is already the msgpack body the
                    # host player received from its match director; re-packing it would turn it
                    # into a bin instead of the original array.
                    payload = bytes(payload)
                    if len(payload) + 1 <= MAX_FRAME_BYTES:
                        frame = (len(payload) + 1).to_bytes(4, "little") \
                            + bytes([frame_type]) + payload
                        self._send_raw(target, frame)
        elif msg_type == MSG_BYE:
            reason = body[0] if isinstance(body, list) and body and isinstance(body[0], str) else ""
            self._leave_room(m, reason or "玩家离开了房间")

    def _on_match_start(self, room: Room, m: Member, body):
        self._start_match(room, m, body if isinstance(body, list) else None)

    def _start_match(self, room: Room, requester: Member, geo):
        """`geo` is the host player's stage geometry from MatchStart, or None for the matchmaking
        auto-start: the fighters read stage bounds from their OWN scene (the wire geometry exists
        for spectate catch-ups, which take it from the fighters' reports), so the defaults are as
        good an answer as any for a match nobody dials in by hand."""
        state = room.state
        # Both fighting seats must be reachable over UDP, or starting is pointless (mirrors
        # MatchPlan.PreviewPlan refusing with the real reason).
        for seat in range(SEAT_COUNT):
            s = state.seat(seat)
            if s.occupant_player_id != 0:
                holder = room.members.get(s.occupant_player_id)
                if holder is None or holder.announced_udp_port <= 0:
                    self._send_raw(requester, encode_frame(
                        MSG_REJECTED, ["另一位玩家没有上报对局端口，无法开始", self.protocol,
                                       self.game_version, "", ""]))
                    return
        if not state.begin_match():
            return   # both seats not ready; silent like the LAN host
        self._forget_match_endpoints(room)
        geo = geo if geo is not None and len(geo) >= 7 else [40.0, 760.0, 800.0,
                                                             120.0, 560.0, 650.0, 560.0]
        start = [self._room_snapshot(room),
                 float(geo[0]), float(geo[1]), float(geo[2]),
                 float(geo[3]), float(geo[4]), float(geo[5]), float(geo[6]),
                 "", "",                      # Seat0/1Endpoints: never used (server is the hub)
                 self.udp_port,
                 False]                       # SpectatingAvailable: lobby spectating is data-only
        self._broadcast(room, MSG_START_MATCH, start)
        self._broadcast_room(room)   # LAN's RequestStartMatch broadcasts RoomState too
        log.info("room %s match started (%d members)", state.room_id, len(room.members))

    def _end_match(self, room: Room, winner_seat: int):
        state = room.state
        seats = [state.seat(0), state.seat(1)]   # captured BEFORE end_match clears them: the
        dropped = state.end_match()              # Elo settle needs to know who fought
        self._settle_scores(room, seats, winner_seat)
        self._forget_match_endpoints(room)
        for pid in dropped:
            self._kick(room, pid, "本局结束，已断线")
        self._broadcast(room, MSG_MATCH_ENDED, [winner_seat, dropped])
        self._broadcast_room(room)
        log.info("room %s match ended (winner seat %d, kicked %r)",
                 state.room_id, winner_seat, dropped)

    def _settle_scores(self, room: Room, seats, winner_seat: int):
        """Elo settle for a match that just ended between TWO HUMAN fighters. A match with an AI
        seat teaches the ladder nothing, so it does not touch the scores. Zero-sum except at the
        score floor; the DB is written before the in-memory copies move, so a failed write can
        never leave the two views disagreeing."""
        if winner_seat not in (0, 1):
            return
        win_seat, lose_seat = seats[winner_seat], seats[1 - winner_seat]
        if win_seat.is_ai or lose_seat.is_ai:
            return
        wm = room.members.get(win_seat.occupant_player_id)
        lm = room.members.get(lose_seat.occupant_player_id)
        if wm is None or lm is None or wm.account_id == 0 or lm.account_id == 0:
            return
        if wm.account_id == lm.account_id:
            return
        new_w, new_l = elo_update(wm.account_score, lm.account_score,
                                  self.mm_cfg["k_factor"], self.mm_cfg["score_floor"])
        try:
            self.players.update_score(wm.account_id, new_w)
            self.players.update_score(lm.account_id, new_l)
        except sqlite3.Error as e:
            log.error("db error on score settle (room %s): %s", room.state.room_id, e)
            return
        w_old, l_old = wm.account_score, lm.account_score
        wm.account_score, lm.account_score = new_w, new_l
        log.info("score settle (room %s): %r %d -> %d, %r %d -> %d",
                 room.state.room_id, wm.name, w_old, new_w, lm.name, l_old, new_l)

    def _forget_match_endpoints(self, room: Room):
        """Match endpoints are learned PER MATCH, never once per connection.

        A client keeps ONE local UDP socket for the whole room (its port was announced in Hello and
        rebinding it between matches is a race — see MatchSocket), but what the server sees is a NAT
        MAPPING of that socket, and a mapping that goes quiet between matches is re-allocated by the
        router to a different public port. Pinning across matches therefore dropped every datagram of
        the next match ("udp drop: pinned endpoint mismatch ... learned ('x', 3766), got ('x', 3768)")
        and froze the room at 等待对方同步 / 同步失败. Forgetting the pins at both ends of a match makes
        each match learn the endpoints exactly like the first one did."""
        for m in room.members.values():
            m.udp_endpoint = None
            m.udp_last_rx = 0.0

    # ---- membership changes ----

    def _apply(self, room: Room, accepted: bool):
        """Accepted request => everyone needs the new snapshot. Refused => nothing changed, so
        nothing is sent; the requester's UI keeps showing the authoritative state it has."""
        if accepted:
            self._broadcast_room(room)

    def _send_welcome(self, m: Member, player_id: int, is_host: bool):
        self._send_raw(m, encode_frame(MSG_WELCOME, [player_id, is_host, self._room_snapshot(m.room)]))

    def _room_snapshot(self, room: Room):
        """The authoritative snapshot, with each HUMAN player's persistent identity (account
        playerid + score) appended to its PlayerInfo — the score is what the match HUD shows and
        the playerid is the stable key future business logic will index by. Append-only fields:
        a client that does not know them ignores the tail (PROTOCOL.md § Bodies)."""
        snap = room.state.snapshot()
        for p in snap[0]:
            member = room.members.get(p[0]) if isinstance(p[0], int) else None
            if member is not None and member.account_id:
                p.append(member.account_id)
                p.append(member.account_score)
            else:
                p.append(0)
                p.append(0)
        return snap

    def _broadcast_room(self, room: Room):
        self._broadcast(room, MSG_ROOM_STATE, self._room_snapshot(room))

    def _broadcast(self, room: Room, msg_type: int, body):
        frame = encode_frame(msg_type, body)
        for m in room.members.values():
            self._send_raw(m, frame)

    def _kick(self, room: Room, player_id: int, reason: str):
        """Host-initiated removal (the match ended and this player had dropped)."""
        m = room.members.get(player_id)
        if m is None:
            return
        self._send_raw(m, encode_frame(MSG_BYE, [reason]))
        self._close_member(m)

    def _leave_room(self, m: Member, reason: str):
        """A member leaving (Bye). The room dies with its host player, but NO connection does — not
        the leaver's, and not the other members' either: everyone returns to the browse phase, where
        the same lobby connection keeps working for LobbyList/Create/Join. That is what lets a client
        show the room browser again without reconnecting (spec: ESC 退出房间后回到选房界面，不断开大厅
        连接; 主持玩家退房后其它玩家保持连接回到选房界面). The other members of a destroyed room are
        still TOLD with Bye — they just are not disconnected by it."""
        room = m.room
        if room is None:
            m.phase = "closed"
            return
        if m.is_host:
            log.info("room %s destroyed: host %r left (%s)", room.state.room_id, m.name, reason)
            # Out of the member table FIRST, so _destroy_room does not hand our own connection the
            # Bye we are the cause of.
            room.members.pop(m.player_id, None)
            self._destroy_room(room, reason)
            self._to_browse(m)
            return
        room.members.pop(m.player_id, None)
        state = room.state
        # The mid-match reserve rule is about FIGHTERS: the opponent is still simulating against that
        # seat, so it stays claimed and the player is kicked at match end. A SEATLESS watcher changes
        # nothing about the match, so its human slot is freed the instant it leaves — reserving it too
        # is what left a room advertising "2/4 人" while refusing every joiner with 房间已满.
        if state.match_running and state.holds_seat(m.player_id):
            state.mark_disconnected(m.player_id)   # kicked at match end, seat kept mid-round
        else:
            state.remove_player(m.player_id)
        self._to_browse(m)                         # back to browse: the connection stays open
        self._broadcast_room(room)
        log.info("%r left room %s (%d/%d), connection kept for browsing", m.name,
                 state.room_id, len(state.players), state.max_players)

    def _to_browse(self, m: Member):
        """Back to the browse phase on the SAME connection. Every room-scoped field goes with the
        room: a stale is_host would hand host rights (AddAi/MatchStart/HostSendTo) in the NEXT room
        this connection joins, and a stale udp_endpoint would pin a match endpoint learned in
        another room."""
        m.phase = "op"
        m.room = None
        m.player_id = 0
        m.is_host = False
        m.udp_endpoint = None
        m.udp_last_rx = 0.0

    def _destroy_room(self, room: Room, reason: str, close: bool = False):
        """The room is gone (its host player left). Every remaining member is told with Bye and then
        returned to the BROWSE phase on the same connection — a destroyed room must not cost a player
        its lobby connection, or the client is thrown back to the main menu and has to retype the whole
        lobby form (spec: 主持玩家退房后其它玩家保持连接回到选房界面刷新房间列表).

        close=True is the server SHUTTING DOWN: there is no browse phase left to return to."""
        frame = encode_frame(MSG_BYE, [reason])
        for m in room.members.values():
            self._send_raw(m, frame)
            if close:
                self._close_member(m)
            else:
                self._to_browse(m)
        room.members.clear()
        self.rooms.pop(room.id_int, None)

    def _on_conn_lost(self, m: Member):
        """EOF, protocol error or explicit close. A room member is removed (or the room is
        destroyed, for the host player); a lobby-phase browser is just dropped. Either way the
        ACCOUNT goes offline with the connection — it stops waiting in the matchmaking pool and
        frees the online slot, so the next login with this name meets no 顶号 wall."""
        if m.phase == "room":
            # The reason is what the OTHER members are shown for a host player, so name the event
            # from their side: their own connection is fine, the host's is not.
            self._leave_room(m, "主持玩家已断开连接" if m.is_host else "连接断开")
        elif m.phase == "op":
            log.info("lobby browser %s disconnected", m.tcp_ip)
        self._matchmaker.remove(m)
        if m.account_id and self.online.get(m.account_id) is m:
            self.online.pop(m.account_id, None)
            log.info("account #%d (%r) is offline", m.account_id, m.name)
        m.account_id = 0
        m.kick_pending = None
        m.phase = "closed"

    def _reject(self, m: Member, reason: str):
        """Fatal: send Rejected and close. For handshake/protocol violations only."""
        self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version, "", ""]))
        log.info("refused %s: %s", m.tcp_ip, reason)
        m.phase = "closed"

    def _refuse(self, m: Member, reason: str):
        """Non-fatal: send Rejected but keep the connection. A wrong password or a full room
        must leave the browser connected so the player can retry or pick another room."""
        self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version, "", ""]))
        log.info("refused %s: %s", m.tcp_ip, reason)

    def _close_member(self, m: Member):
        try:
            m.writer.close()
        except Exception:
            pass

    def _send_raw(self, m: Member, frame: bytes):
        try:
            w = m.writer
            if w.is_closing():
                return
            w.write(frame)
            # A peer that stops reading must not balloon our memory: drop it once the kernel
            # buffer backs up.
            if w.transport.get_write_buffer_size() > MAX_FRAME_BYTES:
                w.close()
        except (ConnectionError, OSError, RuntimeError):
            pass

    def _fresh_room_id(self) -> int:
        while True:
            rid = random.randint(100000, 999999)   # 6-digit random room id
            if rid not in self.rooms:
                return rid

    # Windows Proactor tears down the UDP receive loop on a ConnectionReset — an ICMP "port
    # unreachable" from a datagram forwarded to a peer that closed its socket (the fighters'
    # sessions die at match end, and a last in-flight packet can still be relayed). Without this
    # the relay would silently stop forwarding every later match. Linux keeps the transport alive
    # on error_received, so the rebind is a no-op there in practice; on Windows it is what keeps
    # consecutive matches playable.
    def _udp_died(self):
        if self._udp_rebinding:
            return
        self._udp_rebinding = True
        asyncio.get_running_loop().create_task(self._rebind_udp())

    async def _rebind_udp(self):
        try:
            # abort(), not close(): close() waits for pending operations, and on Windows the
            # Proactor's UDP receive loop is already dead — the port stays held for seconds and
            # the rebind keeps hitting WinError 10048.
            self._udp_transport.abort()
        except Exception:
            pass
        try:
            for attempt in range(50):
                try:
                    transport, _ = await asyncio.get_running_loop().create_datagram_endpoint(
                        lambda: _UdpProtocol(self._handle_udp, self._udp_died),
                        local_addr=(self.host, self.udp_port))
                    self._udp_transport = transport
                    log.warning("udp transport rebound on %s:%d (attempt %d)",
                                self.host, self.udp_port, attempt + 1)
                    return
                except OSError:
                    await asyncio.sleep(0.05)
            log.warning("udp transport rebind failed after retries")
        finally:
            self._udp_rebinding = False

    async def _reaper_loop(self):
        while True:
            await asyncio.sleep(30)
            now = time.monotonic()
            for m in self._conns():
                # A connection that never finished the handshake is dead weight and goes on the
                # configured timeout. A BROWSER (past Hello, in no room) is a player looking at the
                # room list, so it gets a much longer grace — dropping an AFK player after five
                # minutes of reading the list would read as "the lobby disconnected me". A
                # KICK_WAIT connection is the 顶号 popup waiting for a human: same short grace as a
                # handshake, since the account is still held by the OTHER (live) session and this
                # half-open state must not outlive the popup indefinitely.
                limit = self.idle_timeout if m.phase in ("hello", "kick_wait") else self.idle_timeout * 4
                if m.phase in ("hello", "kick_wait", "op") and now - m.last_activity > limit:
                    log.info("idle lobby connection %s timed out (phase %s)", m.tcp_ip, m.phase)
                    m.phase = "closed"
                    self._close_member(m)

    def _conns(self):
        # Every connection, not just room members: a browser (phase "op" — never joined a room, or
        # left one) is exactly what the idle timeout is for, and it lives in no room's member table.
        return list(self.conns)

    # ---- UDP (match relay) ----

    def _handle_udp(self, data: bytes, addr):
        # Belt and braces: an exception here would kill the whole UDP protocol (asyncio does not
        # catch datagram_received errors), taking every match in every room down with it. UDP
        # traffic is best-effort by design; drop the datagram, keep the server alive.
        try:
            self._handle_udp_inner(data, addr)
        except Exception as e:  # noqa: BLE001
            log.warning("udp handler error: %s", e)

    def _handle_udp_inner(self, data: bytes, addr):
        if len(data) < 6:
            return
        room_id = int.from_bytes(data[0:4], "little")
        src, dst = data[4], data[5]
        room = self.rooms.get(room_id)
        if room is None:
            log.warning("udp drop: room %d not found (from %s)", room_id, addr)
            return
        state = room.state
        if not state.match_running:
            # Normal noise: the fighters' last in-flight datagrams arrive after the match ended.
            log.debug("udp drop: room %d not running (from %s)", room_id, addr)
            return   # only match traffic travels on UDP
        if src not in (0, 1) or dst not in (0, 1) or src == dst:
            return
        sa, da = state.seat(src), state.seat(dst)
        ip = normalize_ip(addr[0])
        if sa.is_ai:
            # An AI seat is driven by the HOST player's machine (its inputs enter as the host's),
            # so a datagram claiming an AI seat may only come from the host player — but it is a
            # REAL fighter's packet and must be forwarded, exactly like the host's own seat.
            msrc = room.host_member
            if msrc is None or ip != msrc.tcp_ip:
                return
        else:
            msrc = room.members.get(sa.occupant_player_id)
            if msrc is None or ip != msrc.tcp_ip:
                return   # a datagram claiming a seat must come from that member's connection
        if da.is_ai:
            # The AI seat has no socket of its own: its opponent's packets are driven by the host
            # player's session, so they are forwarded to the HOST's endpoint.
            mdst = room.host_member
        else:
            mdst = room.members.get(da.occupant_player_id)
        if mdst is None:
            return
        # Learn the member's PUBLIC match endpoint from what it actually sends: the announced
        # port is a LOCAL port, and behind a NAT the public UDP port differs. The FIRST datagram
        # (or any from the announced endpoint) is trusted by IP + seat claim; after that the
        # endpoint is pinned FOR THIS MATCH, so a member can never claim another seat from a
        # different source port — that is the cross-member injection hole (two members behind one
        # NAT share an IP). The pins are dropped at every match boundary (_forget_match_endpoints)
        # and a pin that stopped delivering can be taken over (below), so neither a NAT remap
        # between matches nor one mid-match kills the room any more.
        learned = (ip, addr[1])
        now = time.monotonic()
        if msrc.udp_endpoint is not None and msrc.udp_endpoint != learned:
            silent = now - msrc.udp_last_rx
            if silent < ENDPOINT_REPIN_AFTER:
                log.warning("udp drop: pinned endpoint mismatch room %d seat %d (learned %s, got %s)",
                            room_id, src, msrc.udp_endpoint, learned)
                return
            # The pinned mapping has heard nothing for a whole match's worth of frames: it is dead
            # (a NAT remap), and refusing the new source would end the match for good.
            log.info("udp re-pin: room %d seat %d %s -> %s (old endpoint silent %.1fs)",
                     room_id, src, msrc.udp_endpoint, learned, silent)
        msrc.udp_endpoint = learned
        msrc.udp_last_rx = now
        if mdst.udp_endpoint is not None:
            target = mdst.udp_endpoint
        else:
            target = (mdst.tcp_ip, mdst.announced_udp_port)   # initial guess
        if target[1] <= 0:
            log.warning("udp drop: dst %s has no port (room %d)", mdst.name, room_id)
            return
        try:
            self._udp_transport.sendto(data[6:], target)
        except OSError:
            pass


class _UdpProtocol(asyncio.DatagramProtocol):
    def __init__(self, on_datagram, on_fatal=None):
        self._on_datagram = on_datagram
        self._on_fatal = on_fatal

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        self._on_datagram(data, addr)

    def error_received(self, exc):
        log.warning("udp error_received: %r", exc)
        if self._on_fatal is not None:
            self._on_fatal()

    def connection_lost(self, exc):
        log.warning("udp connection_lost: %r", exc)


# --------------------------------------------------------------------------- main

def _parse_args(argv):
    p = argparse.ArgumentParser(description="MouseKombat lobby server")
    p.add_argument("--host", default=os.environ.get("MK_HOST", "0.0.0.0"))
    p.add_argument("--port", type=int, default=int(os.environ.get("MK_PORT", "4954")))
    p.add_argument("--udp-port", type=int, default=0)
    p.add_argument("--game-version", default=os.environ.get("MK_GAME_VERSION", ""))
    p.add_argument("--protocol", type=int, default=int(os.environ.get("MK_PROTOCOL", str(PROTOCOL))))
    p.add_argument("--idle-timeout", type=float,
                   default=float(os.environ.get("MK_IDLE_TIMEOUT", "300")))
    p.add_argument("--max-rooms", type=int, default=int(os.environ.get("MK_MAX_ROOMS", "500")))
    p.add_argument("--config", default=None,
                   help="path to config.json (default: config.json next to this file)")
    p.add_argument("--db", default=None,
                   help="path to the SQLite account file (overrides config db_path)")
    args = p.parse_args(argv)
    if args.udp_port == 0:
        args.udp_port = args.port
    return args


def main(argv=None):
    args = _parse_args(argv)
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    config_path = args.config
    if config_path is None:
        default_cfg = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")
        config_path = default_cfg if os.path.exists(default_cfg) else None
    srv = LobbyServer(args.host, args.port, args.udp_port, args.game_version,
                      args.protocol, args.idle_timeout, args.max_rooms,
                      config_path=config_path, db_path=args.db)

    async def run():
        await srv.start()
        stop = asyncio.Event()
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, stop.set)
            except NotImplementedError:
                pass  # Windows: no signal handlers; Ctrl+C raises KeyboardInterrupt instead
        await stop.wait()
        await srv.shutdown()

    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
