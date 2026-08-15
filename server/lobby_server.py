"""MouseKombat lobby server — the public room host for lobby games.

THE SPEC IS ../../MouseKombat.Net/PROTOCOL.md. A LAN game's host runs a mini room server
in-process (TcpRoomHost); a lobby game's rooms live here. Both speak the same messages, so the
client code is identical and only the endpoint differs. This file implements the room-authority
half (RoomState lives in room.py) plus the lobby-only extras:

* room directory: list (paged, searchable only, newest first), create (2..4 humans, optional
  4-digit password, searchable flag), join by 6-digit room id + password, hard cap 4 humans;
* version check at connect (Hello) against the configured game version — a mismatch is refused
  before anything else;
* the match UDP relay: fighters wrap every rollback datagram in
      u32 roomId (LE) + u8 srcSlot + u8 dstSlot + opaque payload
  and the server forwards the payload to the dstSlot holder's match endpoint;
* catch-up routing (mid-match spectating): MatchInputReport frames are forwarded to the host
  player, HostSendTo carries the host player's catch-up frames to a chosen member, and
  LobbyPlayerJoined tells the host player when someone joins so it can serve a catch-up.

Everything is in-memory (spec: no persistence), single-threaded asyncio. 2C2G comfortably
handles the target of under 100 concurrent players.

Run:  python lobby_server.py [--host H] [--port P] [--udp-port P] [--game-version V]
Config can also come from env: MK_HOST / MK_PORT / MK_UDP_PORT / MK_GAME_VERSION /
MK_PROTOCOL / MK_IDLE_TIMEOUT / MK_MAX_ROOMS.
"""

from __future__ import annotations

import argparse
import asyncio
import itertools
import logging
import os
import random
import signal
import sys
import time

from protocol import (
    MAX_FRAME_BYTES, MSG_ADD_AI, MSG_BYE, MSG_CHAR_PICK, MSG_HELLO, MSG_HOST_SEND_TO,
    MSG_LOBBY_CREATE, MSG_LOBBY_JOIN, MSG_LOBBY_LIST, MSG_LOBBY_PLAYER_JOINED,
    MSG_LOBBY_ROOMS, MSG_MATCH_ENDED, MSG_MATCH_INPUT_REPORT, MSG_MATCH_RESULT,
    MSG_MATCH_START, MSG_REJECTED, MSG_REMOVE_AI, MSG_ROOM_STATE, MSG_SEAT_CLAIM,
    MSG_SEAT_RELEASE, MSG_START_MATCH, MSG_WELCOME, PAGE_SIZE, PROTOCOL,
    ROOM_MEMBER_TYPES, HOST_SEND_TO_ALLOWED_TYPES, ProtocolError, FrameReader,
    decode_body, encode_frame, is_valid_password, is_valid_room_id, normalize_ip,
    sanitize_name,
)
from room import RoomState, SEAT_COUNT

log = logging.getLogger("lobby")


# --------------------------------------------------------------------------- models

class Member:
    """One TCP connection. In lobby phase it is a browser; after create/join it is a room member."""

    __slots__ = ("player_id", "name", "is_host", "reader", "writer", "tcp_ip",
                 "announced_udp_port", "udp_endpoint", "phase", "last_activity", "room")

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
        self.phase = "hello"        # "hello" -> "op" -> "room" -> "closed"
        self.last_activity = time.monotonic()
        self.room = None

    def touch(self):
        self.last_activity = time.monotonic()


class Room:
    """A lobby room: RoomState (authority) + the members connected to it + directory metadata."""

    __slots__ = ("id_int", "seq", "state", "password", "searchable", "created_at", "members")

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

    @property
    def host_member(self):
        return self.members.get(self.state.host_player_id)


_next_seq = itertools.count().__next__


# --------------------------------------------------------------------------- server

class LobbyServer:
    def __init__(self, host: str, port: int, udp_port: int, game_version: str,
                 protocol: int = PROTOCOL, idle_timeout: float = 300.0, max_rooms: int = 500):
        self.host = host
        self.port = port
        self.udp_port = udp_port
        self.game_version = game_version or ""
        self.protocol = protocol
        self.idle_timeout = idle_timeout
        self.max_rooms = max_rooms
        self.rooms = {}             # int room id -> Room
        self._udp_transport = None
        self._udp_rebinding = False
        self._tcp_server = None

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
        asyncio.get_running_loop().create_task(self._reaper_loop())
        log.info("lobby up: tcp %s:%d udp %s:%d protocol %d game version %r",
                 self.host, self.port, self.host, self.udp_port, self.protocol,
                 self.game_version or "(unset — every Hello refused)")

    async def shutdown(self):
        for room in list(self.rooms.values()):
            self._destroy_room(room, "服务器已关闭")
        if self._tcp_server is not None:
            self._tcp_server.close()
            await self._tcp_server.wait_closed()

    # ---- TCP ----

    async def _handle_conn(self, reader, writer):
        m = Member(writer)
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
        if m.phase == "op":
            if msg_type == MSG_LOBBY_LIST:
                self._on_list(m, decode_body(body))
            elif msg_type == MSG_LOBBY_CREATE:
                self._on_create(m, decode_body(body))
            elif msg_type == MSG_LOBBY_JOIN:
                self._on_join(m, decode_body(body))
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
            self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version]))
            log.info("refused %s: %s (client protocol %r version %r, server %r/%r)",
                     m.tcp_ip, reason, protocol, game_version, self.protocol, self.game_version)
            m.phase = "closed"
            return
        m.name = sanitize_name(body[2])
        port = body[4] if isinstance(body[4], int) else 0
        m.announced_udp_port = port if 0 < port <= 65535 else 0
        m.phase = "op"
        log.info("connected %s as %r (match udp %d)", m.tcp_ip, m.name, m.announced_udp_port)

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
            entries.append([r.state.room_id, host.name if host else "?",
                            bool(r.password), len(r.members), r.state.max_players])
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
        id_int = self._fresh_room_id()
        room = Room(id_int, max_players, password, searchable)
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
                 len(room.members), room.state.max_players)

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
        state = room.state
        # Both fighting seats must be reachable over UDP, or starting is pointless (mirrors
        # MatchPlan.PreviewPlan refusing with the real reason).
        for seat in range(SEAT_COUNT):
            s = state.seat(seat)
            if s.occupant_player_id != 0:
                holder = room.members.get(s.occupant_player_id)
                if holder is None or holder.announced_udp_port <= 0:
                    self._send_raw(m, encode_frame(
                        MSG_REJECTED, ["另一位玩家没有上报对局端口，无法开始", self.protocol,
                                       self.game_version]))
                    return
        if not state.begin_match():
            return   # both seats not ready; silent like the LAN host
        geo = body if isinstance(body, list) and len(body) >= 7 else [40.0, 760.0, 800.0,
                                                                      120.0, 560.0, 650.0, 560.0]
        start = [state.snapshot(),
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
        dropped = state.end_match()
        for pid in dropped:
            self._kick(room, pid, "本局结束，已断线")
        self._broadcast(room, MSG_MATCH_ENDED, [winner_seat, dropped])
        self._broadcast_room(room)
        log.info("room %s match ended (winner seat %d, kicked %r)",
                 state.room_id, winner_seat, dropped)

    # ---- membership changes ----

    def _apply(self, room: Room, accepted: bool):
        """Accepted request => everyone needs the new snapshot. Refused => nothing changed, so
        nothing is sent; the requester's UI keeps showing the authoritative state it has."""
        if accepted:
            self._broadcast_room(room)

    def _send_welcome(self, m: Member, player_id: int, is_host: bool):
        self._send_raw(m, encode_frame(MSG_WELCOME, [player_id, is_host, m.room.state.snapshot()]))

    def _broadcast_room(self, room: Room):
        self._broadcast(room, MSG_ROOM_STATE, room.state.snapshot())

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
        """A member leaving (Bye). The host player leaving kills the room; a regular member's
        connection SURVIVES and returns to the browse phase — the same lobby connection keeps
        working for LobbyList/Create/Join, which is what lets the client show the browser again
        without reconnecting (spec: ESC 退出房间后回到选房界面，不断开大厅连接)."""
        room = m.room
        if room is None:
            m.phase = "closed"
            return
        if m.is_host:
            log.info("room %s destroyed: host %r left (%s)", room.state.room_id, m.name, reason)
            self._destroy_room(room, reason)
            m.phase = "closed"
            return
        room.members.pop(m.player_id, None)
        state = room.state
        if state.match_running:
            state.mark_disconnected(m.player_id)   # kicked at match end, seat kept mid-round
        else:
            state.remove_player(m.player_id)
        m.phase = "op"                             # back to browse: the connection stays open
        self._broadcast_room(room)
        log.info("%r left room %s (%d/%d), connection kept for browsing", m.name,
                 state.room_id, len(room.members), state.max_players)

    def _destroy_room(self, room: Room, reason: str):
        frame = encode_frame(MSG_BYE, [reason])
        for m in room.members.values():
            self._send_raw(m, frame)
            self._close_member(m)
        self.rooms.pop(room.id_int, None)

    def _on_conn_lost(self, m: Member):
        """EOF, protocol error or explicit close. A room member is removed (or the room is
        destroyed, for the host player); a lobby-phase browser is just dropped."""
        if m.phase == "room":
            self._leave_room(m, "连接断开")
        elif m.phase == "op":
            log.info("lobby browser %s disconnected", m.tcp_ip)
        m.phase = "closed"

    def _reject(self, m: Member, reason: str):
        """Fatal: send Rejected and close. For handshake/protocol violations only."""
        self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version]))
        log.info("refused %s: %s", m.tcp_ip, reason)
        m.phase = "closed"

    def _refuse(self, m: Member, reason: str):
        """Non-fatal: send Rejected but keep the connection. A wrong password or a full room
        must leave the browser connected so the player can retry or pick another room."""
        self._send_raw(m, encode_frame(MSG_REJECTED, [reason, self.protocol, self.game_version]))
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
            for m in list(self._conns()):
                if m.phase in ("hello", "op") and now - m.last_activity > self.idle_timeout:
                    log.info("idle lobby connection %s timed out", m.tcp_ip)
                    m.phase = "closed"
                    self._close_member(m)

    def _conns(self):
        for room in list(self.rooms.values()):
            yield from room.members.values()

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
            log.warning("udp drop: room %d not running (from %s)", room_id, addr)
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
        # endpoint is pinned, so a member can never claim another seat from a different source
        # port — that is the cross-member injection hole (two members behind one NAT share an IP).
        learned = (ip, addr[1])
        if msrc.udp_endpoint is not None and msrc.udp_endpoint != learned:
            log.warning("udp drop: pinned endpoint mismatch room %d seat %d (learned %s, got %s)",
                        room_id, src, msrc.udp_endpoint, learned)
            return
        msrc.udp_endpoint = learned
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
    args = p.parse_args(argv)
    if args.udp_port == 0:
        args.udp_port = args.port
    return args


def main(argv=None):
    args = _parse_args(argv)
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    srv = LobbyServer(args.host, args.port, args.udp_port, args.game_version,
                      args.protocol, args.idle_timeout, args.max_rooms)

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
