"""Headless end-to-end smoke test for the lobby server. Runs the server in-process on ephemeral
ports, so it works anywhere Python 3.11 + msgpack exists — no .NET needed.

    python smoke_test.py

Exits nonzero on any failure. The .NET test runner (MouseKombat.Sim.Tests/LobbyServerTests.cs)
exercises the same wire with the REAL C# codec; this file keeps the Python side honest in
isolation and is the thing to run on the server machine after a deploy.
"""

from __future__ import annotations

import asyncio
import os
import socket
import sys
import tempfile

from protocol import (
    MSG_ADD_AI, MSG_BYE, MSG_CHAR_PICK, MSG_HELLO, MSG_HOST_SEND_TO, MSG_KICK_CONFIRM,
    MSG_KICK_LOGIN, MSG_KICKED, MSG_LOGIN_OK, MSG_LOBBY_CREATE, MSG_LOBBY_JOIN, MSG_LOBBY_LIST,
    MSG_LOBBY_PLAYER_JOINED, MSG_LOBBY_ROOMS, MSG_MATCH_ENDED, MSG_MATCH_INPUT_REPORT,
    MSG_MATCH_INPUTS, MSG_MATCH_RESULT, MSG_MATCH_START, MSG_MATCHMAKE_JOIN,
    MSG_MATCHMAKE_STATUS, MSG_PING_STATS, MSG_PONG, MSG_REJECTED, MSG_REMOVE_AI, MSG_ROOM_STATE,
    MSG_SEAT_CLAIM, MSG_SEAT_RELEASE, MSG_START_MATCH, MSG_WELCOME, PROTOCOL, FrameReader,
    decode_body, encode_frame,
)
from lobby_server import ENDPOINT_REPIN_AFTER, LobbyServer
from matchmaking import elo_update

GAME_VERSION = "0.0.7"
_fail = 0


def check(cond, label):
    global _fail
    print(("PASS " if cond else "FAIL ") + label, flush=True)
    if not cond:
        _fail += 1


class FakeClient:
    """A minimal lobby client: one TCP connection + optional UDP socket for match traffic."""

    def __init__(self, host, port, name, udp_port=40000):
        self.host, self.port = host, port
        self.name = name
        self.udp_port = udp_port
        self.player_id = 0
        self.room_id = ""
        self._frames = []
        self._rdr = FrameReader()
        self._writer = None
        self._reader = None

    async def connect(self):
        self._reader, self._writer = await asyncio.open_connection(self.host, self.port)
        return self

    async def send(self, msg_type, body):
        self._writer.write(encode_frame(msg_type, body))
        await self._writer.drain()

    async def recv(self, timeout=2.0):
        """Next frame as (type, decoded body), or None on timeout. Preserves order."""
        deadline = asyncio.get_running_loop().time() + timeout
        while True:
            if self._frames:
                return self._frames.pop(0)
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                return None
            try:
                data = await asyncio.wait_for(self._reader.read(8192), remaining)
            except asyncio.TimeoutError:
                return None
            if not data:
                raise ConnectionError(f"{self.name}: peer closed the connection")
            self._rdr.feed(data)
            while True:
                f = self._rdr.try_read()
                if f is None:
                    break
                self._frames.append((f[0], decode_body(f[1])))

    async def recv_until(self, msg_type, timeout=2.0):
        """Skip unrelated frames (e.g. RoomState broadcasts) until the wanted type arrives."""
        deadline = asyncio.get_running_loop().time() + timeout
        while True:
            f = await self.recv(max(0.05, deadline - asyncio.get_running_loop().time()))
            if f is None:
                return None
            if f[0] == msg_type:
                return f
            if asyncio.get_running_loop().time() >= deadline:
                return None

    async def wait_room_state(self, predicate, timeout=2.0):
        """Pop RoomState broadcasts until one satisfies `predicate(snapshot)` (stale frames from
        earlier changes may linger in the queue; the assertion must wait for the state it wants)."""
        deadline = asyncio.get_running_loop().time() + timeout
        while True:
            f = await self.recv_until(MSG_ROOM_STATE,
                                      max(0.05, deadline - asyncio.get_running_loop().time()))
            if f is None:
                return None
            if predicate(f[1]):
                return f
            if asyncio.get_running_loop().time() >= deadline:
                return None

    async def hello(self, game_version=GAME_VERSION, protocol=PROTOCOL, udp_port=None):
        await self.send(MSG_HELLO, [protocol, game_version, self.name, "",
                                    udp_port if udp_port is not None else self.udp_port])

    async def create(self, max_players=4, password="", searchable=True):
        await self.send(MSG_LOBBY_CREATE, [max_players, password, searchable])
        f = await self.recv_until(MSG_WELCOME)
        assert f is not None, f"{self.name}: no Welcome on create"
        self.player_id = f[1][0]
        self.room_id = f[1][2][2]
        await self.recv_until(MSG_ROOM_STATE)   # the create broadcast
        return f[1]

    async def join(self, room_id, password=""):
        await self.send(MSG_LOBBY_JOIN, [room_id, password])
        f = await self.recv_until(MSG_WELCOME)
        if f is None:
            return None
        self.player_id = f[1][0]
        self.room_id = f[1][2][2]
        await self.recv_until(MSG_ROOM_STATE)   # the join broadcast
        return f[1]

    async def claim(self, seat):
        await self.send(MSG_SEAT_CLAIM, [seat])

    async def pick(self, char):
        await self.send(MSG_CHAR_PICK, [char])

    def close(self):
        try:
            self._writer.close()
        except Exception:
            pass


async def main():
    # The DB lands in a per-run temp dir: accounts persist WITHIN a run (that is what the
    # persistence scenario proves) but never leak between runs or into the repo.
    tmp = tempfile.mkdtemp(prefix="mk_lobby_smoke_")
    srv = LobbyServer("127.0.0.1", 0, 0, GAME_VERSION, idle_timeout=300, max_rooms=500,
                      db_path=os.path.join(tmp, "smoke_accounts.db"),
                      config_overrides={
                          "matchmaking": {"tick_interval_seconds": 0.2},
                          "ping": {"interval_seconds": 0.2},
                      })
    await srv.start()
    host, tcp_port, udp_port = "127.0.0.1", srv.port, srv.udp_port
    try:
        await run_scenarios(host, tcp_port, udp_port)
        await run_account_scenarios(host, tcp_port)
    finally:
        await srv.shutdown()


async def run_scenarios(host, port, udp_port):
    # ---- version gate ----
    bad = FakeClient(host, port, "旧版")
    await bad.connect()
    await bad.hello(game_version="0.0.6")
    f = await bad.recv()
    check(f is not None and f[0] == MSG_REJECTED and f[1][0] == "游戏版本不一致",
          "reject: wrong game version -> Rejected with reason")
    check(f[1][2] == GAME_VERSION, "reject: Rejected carries the server's version for display")
    bad.close()

    badp = FakeClient(host, port, "旧协议")
    await badp.connect()
    await badp.hello(protocol=1)
    f = await badp.recv()
    check(f is not None and f[0] == MSG_REJECTED and f[1][0] == "协议版本不一致",
          "reject: wrong protocol -> Rejected with reason")
    badp.close()

    # ---- create + welcome ----
    hostc = FakeClient(host, port, "房主", udp_port=40000)
    await hostc.connect()
    await hostc.hello()
    w = await hostc.create(4)
    check(w[1] is True and w[0] >= 1, "create: host player gets Welcome isHost=true")
    check(len(hostc.room_id) == 6 and hostc.room_id.isdigit(),
          f"create: room id is 6 digits ({hostc.room_id})")
    check(w[2][3] == 4, "create: snapshot carries maxPlayers=4")

    # ---- invalid create params ----
    # Unique name per attempt: with accounts, a name that reconnects before the server noticed
    # the previous EOF would meet the 顶号 popup instead of the create form.
    for i, (body, expect) in enumerate((([9, "", True], "2到4"), ([4, "12", True], "密码"),
                                        ([4, "12345", True], "密码"), (["4", "", True], "2到4"))):
        c = FakeClient(host, port, f"乱建{i}", udp_port=40090 + i)
        await c.connect()
        await c.hello()
        await c.send(MSG_LOBBY_CREATE, body)
        f = await c.recv_until(MSG_REJECTED)
        check(f is not None and expect in f[1][0],
              f"create: refused for {body} ({expect})")
        c.close()

    # ---- join + LobbyPlayerJoined + broadcast ----
    mem = FakeClient(host, port, "玩家乙", udp_port=40001)
    await mem.connect()
    await mem.hello()
    w = await mem.join(hostc.room_id)
    check(w is not None and w[1] is False, "join: member gets Welcome isHost=false")
    f = await hostc.wait_room_state(lambda s: len(s[0]) == 2)
    check(f is not None, "join: RoomState broadcast shows 2 players")
    f = await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    check(f is not None and f[1][0] == mem.player_id,
          "join: host player is told LobbyPlayerJoined with the new player id")

    # ---- seats + chars + AI rules ----
    await hostc.claim(0)
    await mem.claim(1)
    f = await hostc.wait_room_state(
        lambda s: s[1][0][0] == hostc.player_id and s[1][1][0] == mem.player_id)
    check(f is not None, "seats: both claimed")
    await hostc.pick(0)
    await mem.pick(1)
    f = await mem.wait_room_state(lambda s: s[1][0][1] == 0 and s[1][1][1] == 1)
    check(f is not None, "seats: chars picked, both human")
    # double claim of a taken seat is refused silently; a later accepted change proves it
    await mem.claim(0)
    await mem.send(MSG_SEAT_RELEASE, [])   # accepted -> broadcast
    await mem.claim(1)                     # restore the seat
    f = await mem.wait_room_state(
        lambda s: s[1][0][0] == hostc.player_id and s[1][1][0] == mem.player_id)
    check(f is not None, "seats: claim on a taken seat is refused")
    await mem.pick(1)                      # the release cleared the character; re-pick
    await mem.wait_room_state(lambda s: s[1][0][1] == 0 and s[1][1][1] == 1)
    # non-host AddAi refused
    await mem.send(MSG_ADD_AI, [0, ""])
    await mem.send(MSG_SEAT_RELEASE, [])
    await mem.claim(1)
    await mem.pick(1)
    f = await mem.wait_room_state(lambda s: s[1][1][2] is False)
    check(f is not None, "ai: non-host AddAi is refused")
    await hostc.wait_room_state(lambda s: all(x[1] >= 0 for x in s[1]))   # both seats ready

    # ---- MatchStart flow ----
    await hostc.send(MSG_MATCH_START, [40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0])
    f = await mem.recv_until(MSG_START_MATCH)
    check(f is not None, "start: StartMatch broadcast to members")
    check(f[1][10] == udp_port, "start: MatchUdpPort is the server's UDP port")
    check(f[1][11] is False, "start: SpectatingAvailable=false (lobby spectates via data stream)")
    check(f[1][8] == "" and f[1][9] == "", "start: seat endpoints stay empty (server is the hub)")
    f = await mem.wait_room_state(lambda s: s[4] is True)
    check(f is not None, "start: snapshot says MatchRunning")
    await hostc.recv_until(MSG_START_MATCH)     # drain the host player's own copies
    await hostc.recv_until(MSG_ROOM_STATE)

    # ---- UDP relay: A -> B -> A ----
    sinks = await udp_relay_sinks(host, udp_port, hostc, mem)
    check(sinks[0], "udp: relay forwards both directions")
    ta, tb, pa, pb = sinks[1:]
    # a datagram claiming the OTHER seat from a foreign source port is dropped (cross-member
    # injection attempt; the server pins a member to its first observed endpoint). If it had
    # been forwarded, it would land on hostc's sink.
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.sendto(envelope(int(hostc.room_id), 1, 0, b"spoof"), (host, udp_port))
    sock.close()
    try:
        await asyncio.wait_for(pa.q.get(), 0.5)
        check(False, "udp: a datagram claiming another seat from a foreign port is dropped")
    except asyncio.TimeoutError:
        check(True, "udp: a datagram claiming another seat from a foreign port is dropped")
    ta.close()
    tb.close()
    # ---- HostSendTo routing (catch-up stream) ----
    payload = [100, [1, 2, 3], [4, 5]]
    body_bytes = encode_frame(MSG_MATCH_INPUTS, payload)[5:]   # the msgpack body, sent as bin
    await hostc.send(MSG_HOST_SEND_TO, [mem.player_id, MSG_MATCH_INPUTS, body_bytes])
    f = await mem.recv_until(MSG_MATCH_INPUTS)
    check(f is not None and f[1] == payload,
          "catchup: HostSendTo delivers a MatchInputs frame verbatim")
    # non-host HostSendTo is dropped
    await mem.send(MSG_HOST_SEND_TO, [hostc.player_id, MSG_MATCH_INPUTS, b"\x00"])
    f = await hostc.recv_until(MSG_ROOM_STATE, timeout=0.3)
    check(f is None, "catchup: a non-host HostSendTo is dropped")
    # HostSendTo carrying a disallowed type is dropped
    await hostc.send(MSG_HOST_SEND_TO, [mem.player_id, MSG_MATCH_RESULT, b"\x00"])
    f = await mem.recv_until(MSG_ROOM_STATE, timeout=0.3)
    check(f is None, "catchup: HostSendTo with a disallowed type is dropped")

    # ---- MatchInputReport forwarding to the host player ----
    report = [50, [7, 8], [9, 10], 40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0]
    await mem.send(MSG_MATCH_INPUT_REPORT, report)
    f = await hostc.recv_until(MSG_MATCH_INPUT_REPORT)
    check(f is not None and f[1] == report,
          "catchup: fighter's MatchInputReport reaches the host player")

    # ---- mid-match WATCHER leaves: its human slot is freed at once ----
    # The reserve-the-slot rule is for FIGHTERS (the opponent simulates against their seat). A seatless
    # watcher that walks out mid-match must free its slot immediately, or the room advertises "2/4 人"
    # and answers every joiner with 房间已满 — and each leave/re-join cycle leaks one more slot.
    watcher = FakeClient(host, port, "观战者", udp_port=40002)
    await watcher.connect()
    await watcher.hello()
    w = await watcher.join(hostc.room_id)
    watcher_id = w[0]
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await watcher.send(MSG_BYE, ["玩家离开了房间"])
    f = await hostc.wait_room_state(lambda s: watcher_id not in [p[0] for p in s[0]])
    check(f is not None, "spectate: a mid-match watcher leaving is removed from the room")
    watcher.close()
    # Same for an abrupt drop, and the freed slot really is re-usable: fill the room to its cap with
    # watchers that come and go, then prove a fresh client still gets in.
    ghosts_ok = True
    for i in range(3):
        ghost = FakeClient(host, port, f"幽灵{i}", udp_port=40200 + i)
        await ghost.connect()
        await ghost.hello()
        ghosts_ok = ghosts_ok and await ghost.join(hostc.room_id) is not None
        await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
        ghost.close()          # abrupt mid-match disconnect, no seat held
        await hostc.wait_room_state(lambda s: len(s[0]) == 2)
    check(ghosts_ok, "spectate: each watcher can join while the match runs")
    late = FakeClient(host, port, "迟到观战", udp_port=40203)
    await late.connect()
    await late.hello()
    check(await late.join(hostc.room_id) is not None,
          "spectate: watchers coming and going mid-match leak no slots (room still joinable)")
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    # The browser's count is the one the capacity check uses, so a listed free slot is a real one.
    lc = FakeClient(host, port, "数人头", udp_port=40204)
    await lc.connect()
    await lc.hello()
    await lc.send(MSG_LOBBY_LIST, [0])
    f = await lc.recv_until(MSG_LOBBY_ROOMS)
    entry = next((e for e in f[1][2] if e[0] == hostc.room_id), None) if f else None
    check(entry is not None and entry[3] == 3,
          "list: the advertised player count matches the room's real occupancy")
    lc.close()
    await late.send(MSG_BYE, ["玩家离开了房间"])
    await hostc.wait_room_state(lambda s: late.player_id not in [p[0] for p in s[0]])
    late.close()

    # ---- mid-match FIGHTER drop: seat reserved, kicked at match end ----
    # A separate room so the running match above is not disturbed.
    fr_host = FakeClient(host, port, "掉线房主", udp_port=40210)
    await fr_host.connect()
    await fr_host.hello()
    await fr_host.create(4)
    fr_mem = FakeClient(host, port, "掉线拳手", udp_port=40211)
    await fr_mem.connect()
    await fr_mem.hello()
    await fr_mem.join(fr_host.room_id)
    await fr_host.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await fr_host.claim(0)
    await fr_mem.claim(1)
    await fr_host.wait_room_state(
        lambda s: s[1][0][0] == fr_host.player_id and s[1][1][0] == fr_mem.player_id)
    await fr_host.pick(0)
    await fr_mem.pick(1)
    await fr_host.wait_room_state(lambda s: s[1][0][1] == 0 and s[1][1][1] == 1)
    await fr_host.send(MSG_MATCH_START, [40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0])
    await fr_host.recv_until(MSG_START_MATCH)
    fr_mem_id = fr_mem.player_id
    fr_mem.close()   # abrupt disconnect mid-match, holding seat 1
    f = await fr_host.wait_room_state(
        lambda s: fr_mem_id in [p[0] for p in s[0]]
                  and {p[0]: p[4] for p in s[0]}[fr_mem_id] is False
                  and s[1][1][0] == fr_mem_id)
    check(f is not None,
          "drop: a mid-match fighter keeps its seat and is marked Connected=false")
    await fr_host.send(MSG_MATCH_RESULT, [0])
    f = await fr_host.recv_until(MSG_MATCH_ENDED)
    check(f is not None and f[1][1] == [fr_mem_id], "drop: match end kicks the dropped fighter")
    f = await fr_host.wait_room_state(
        lambda s: s[4] is False and all(x[0] == 0 and not x[2] for x in s[1]))
    check(f is not None, "drop: seats cleared after the match")
    await fr_host.send(MSG_BYE, ["主持玩家已离开房间"])
    fr_host.close()

    # ---- back to the first room: end its match so the seats are free again ----
    await hostc.send(MSG_MATCH_RESULT, [0])
    f = await mem.recv_until(MSG_MATCH_ENDED)
    check(f is not None, "match: MatchResult ends the first room's match")
    f = await mem.wait_room_state(
        lambda s: s[4] is False and all(x[0] == 0 and not x[2] for x in s[1]))
    check(f is not None, "match: seats cleared after the match")

    # ---- re-pick after the match, second match starts ----
    await hostc.claim(0)
    await mem.claim(1)
    await hostc.wait_room_state(
        lambda s: s[1][0][0] == hostc.player_id and s[1][1][0] == mem.player_id)
    await hostc.pick(2)
    await mem.pick(3)
    await hostc.wait_room_state(lambda s: s[1][0][1] == 2 and s[1][1][1] == 3)
    await hostc.send(MSG_MATCH_START, [40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0])
    f = await mem.recv_until(MSG_START_MATCH)
    check(f is not None, "rematch: a room can start a second match")
    await hostc.recv_until(MSG_START_MATCH)      # drain the host player's own copy

    # ---- the second match's endpoints are RE-LEARNED (the user's frozen room) ----
    # Both fighters kept the same local socket (the game binds one per room), but the NAT mapping the
    # server saw in the first match went quiet between matches and the router handed the socket a
    # different public port. If the server kept the first match's pin, every datagram of this match is
    # dropped ("pinned endpoint mismatch ... learned ('x', 3766), got ('x', 3768)") and both sides
    # freeze at 同步失败. New source ports here stand in for the remap.
    #
    # Deliberately IMPATIENT: the whole exchange has to finish well inside ENDPOINT_REPIN_AFTER, or a
    # pass would only prove the silent-pin takeover below, not the match-start reset.
    ok = await udp_remapped_relay_ok(host, udp_port, int(hostc.room_id), 40010, 40011,
                                     keep_bound=(hostc.udp_port, mem.udp_port),
                                     attempts=3, per_try=0.25)
    check(ok, "rematch: the relay re-learns both fighters' endpoints for the new match")

    # ---- a remap MID-match is recovered once the pinned mapping goes silent ----
    # The pin is what stops a member behind the same NAT from injecting frames for another seat, so it
    # only yields to a new source after the old endpoint has delivered nothing for a whole match's
    # worth of frames. Without this a mid-match remap would end the match for good.
    await asyncio.sleep(ENDPOINT_REPIN_AFTER + 0.2)
    ok = await udp_remapped_relay_ok(host, udp_port, int(hostc.room_id), 40012, 40013,
                                     keep_bound=(hostc.udp_port, mem.udp_port, 40010, 40011))
    check(ok, "rematch: a silent pinned endpoint is taken over by the member's new source port")

    # ---- AI placement carries its character (the seat was never PickCharacter'd) ----
    # End the second match first so both seats are free again.
    await hostc.send(MSG_MATCH_RESULT, [1])
    await mem.recv_until(MSG_MATCH_ENDED)
    await mem.wait_room_state(
        lambda s: s[4] is False and all(x[0] == 0 for x in s[1]))
    await hostc.send(MSG_ADD_AI, [1, "", 2])       # seat 1, character 2, built-in AI
    f = await mem.wait_room_state(lambda s: s[1][1][2] is True and s[1][1][1] == 2)
    check(f is not None, "ai: the host player places an AI with its character")
    await hostc.send(MSG_REMOVE_AI, [1])
    await mem.wait_room_state(lambda s: not s[1][1][2])
    # Re-pick for the final hand-off below.
    await hostc.claim(0)
    await mem.claim(1)
    await hostc.wait_room_state(
        lambda s: s[1][0][0] == hostc.player_id and s[1][1][0] == mem.player_id)
    await hostc.pick(0)
    await mem.pick(1)
    await hostc.wait_room_state(lambda s: s[1][0][1] == 0 and s[1][1][1] == 1)

    # ---- a member leaving KEEPS the connection: back to browse, same socket ----
    bye_mem = FakeClient(host, port, "回头客", udp_port=40008)
    await bye_mem.connect()
    await bye_mem.hello()
    w = await bye_mem.join(hostc.room_id)
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await bye_mem.send(MSG_BYE, ["玩家离开了房间"])
    f = await hostc.wait_room_state(lambda s: len(s[0]) == 2)   # the leaver dropped out
    check(f is not None, "leave: the member is removed from the room")
    await bye_mem.send(MSG_LOBBY_LIST, [0])
    f = await bye_mem.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None and f[1][0] == 0,
          "leave: the member's connection survives and can browse again")
    await bye_mem.join(hostc.room_id)   # and can re-join the same room on the same socket
    check(bye_mem.player_id != 0,
          "leave: the member can re-join a room on the same connection")
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)

    # ---- host player leaves BY Bye: room destroyed, members told, the HOST keeps browsing ----
    # (spec: 建房后 ESC 保持大厅连接、回到选房界面). The host's own socket must come back as a plain
    # browser with NO host rights left over, or the next room it joins as a member would hand it
    # AddAi/MatchStart.
    byehost = FakeClient(host, port, "闪退房主", udp_port=40009)
    await byehost.connect()
    await byehost.hello()
    await byehost.create(4, "", True)
    doomed_room = byehost.room_id
    guest = FakeClient(host, port, "被赶客", udp_port=40010)
    await guest.connect()
    await guest.hello()
    await guest.join(doomed_room)
    await byehost.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await byehost.send(MSG_BYE, ["主持玩家已离开房间"])
    f = await guest.recv_until(MSG_BYE)
    check(f is not None and f[1][0] == "主持玩家已离开房间",
          "host leave: the room is destroyed and the members are told why")
    # The GUEST's connection must survive the room it was in (spec: 主持玩家退房后其它玩家保持连接回到
    # 选房界面刷新房间列表). Closing it threw every member back to the main menu with the lobby form to
    # retype — the whole point of the Bye is to explain the ROOM ending, not the connection.
    await guest.send(MSG_LOBBY_LIST, [0])
    f = await guest.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None and doomed_room not in [e[0] for e in f[1][2]],
          "host leave: a member keeps its connection and browses again (the room is gone)")
    w = await guest.join(hostc.room_id)
    check(w is not None and w[1] is False,
          "host leave: that member re-joins another room on the same connection")
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await guest.send(MSG_BYE, ["玩家离开了房间"])
    await hostc.wait_room_state(lambda s: guest.player_id not in [p[0] for p in s[0]])
    await byehost.send(MSG_LOBBY_LIST, [0])
    f = await byehost.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None and doomed_room not in [e[0] for e in f[1][2]],
          "host leave: the host keeps browsing on the same connection, its room is gone")
    w = await byehost.join(hostc.room_id)
    check(w is not None and w[1] is False,
          "host leave: the former host re-joins as a plain member (no host rights kept)")
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    await byehost.send(MSG_BYE, ["玩家离开了房间"])
    await hostc.wait_room_state(lambda s: byehost.player_id not in [p[0] for p in s[0]])
    byehost.close()
    guest.close()

    # ---- host player DISCONNECTS -> room destroyed, members told, their connections KEPT ----
    hostc.close()
    f = await mem.recv_until(MSG_BYE)
    check(f is not None and f[1][0] == "主持玩家已断开连接",
          "destroy: a host player dropping tells the members why (from THEIR point of view)")
    await mem.send(MSG_LOBBY_LIST, [0])
    f = await mem.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None, "destroy: the member's connection survives the destroyed room")
    await mem.create(4, "", False)   # and it is a plain browser again: it can host its own room
    check(mem.player_id != 0, "destroy: the former member can create a room on the same connection")
    await mem.send(MSG_BYE, ["主持玩家已离开房间"])
    mem.close()

    # ---- list paging + searchable + password + full room ----
    # Keep every creator alive: GC of the client object closes its connection, and a host
    # player leaving destroys its room.
    room_ids = []
    room_hosts = []
    for i in range(12):
        rc = FakeClient(host, port, f"房主{i}", udp_port=40100 + i)
        await rc.connect()
        await rc.hello()
        await rc.create(4, "1234" if i == 0 else "", True)
        room_ids.append(rc.room_id)
        room_hosts.append(rc)
    hidden = FakeClient(host, port, "隐藏房", udp_port=40120)
    await hidden.connect()
    await hidden.hello()
    await hidden.create(4, "", False)
    hidden_room = hidden.room_id

    bc = FakeClient(host, port, "浏览者", udp_port=40121)
    await bc.connect()
    await bc.hello()
    await bc.send(MSG_LOBBY_LIST, [0])
    f = await bc.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None and f[1][0] == 0 and len(f[1][2]) == 10 and f[1][1] == 2,
          "list: page 0 has 10 entries, 2 pages total")
    ids_page0 = {e[0] for e in f[1][2]}
    check(len(ids_page0) == 10, "list: page 0 entry ids are unique")
    await bc.send(MSG_LOBBY_LIST, [1])
    f = await bc.recv_until(MSG_LOBBY_ROOMS)
    check(f is not None and len(f[1][2]) == 2, "list: page 1 has the remaining 2 entries")
    ids_page1 = {e[0] for e in f[1][2]}
    check(ids_page1 == {room_ids[0], room_ids[1]},
          "list: oldest two rooms are on page 1 (newest first)")
    check(hidden_room not in ids_page0 and hidden_room not in ids_page1,
          "list: non-searchable rooms are hidden")
    entry = next(e for e in f[1][2] if e[0] == room_ids[0])
    check(entry[2] is True and entry[3] == 1 and entry[4] == 4,
          "list: entry shows hasPassword/players/maxPlayers")

    # password join
    pw = FakeClient(host, port, "密码侠", udp_port=40122)
    await pw.connect()
    await pw.hello()
    await pw.send(MSG_LOBBY_JOIN, [room_ids[0], "0000"])
    f = await pw.recv_until(MSG_REJECTED)
    check(f is not None and f[1][0] == "密码错误", "join: wrong password refused")
    w = await pw.join(room_ids[0], "1234")
    check(w is not None, "join: right password accepted")

    # full room: room_ids[0] (max 4) has the creator + pw; two more fit, the fifth is refused
    f1 = FakeClient(host, port, "满员1", udp_port=40123)
    await f1.connect(); await f1.hello()
    check(await f1.join(room_ids[0], "1234") is not None, "join: third member accepted")
    f2 = FakeClient(host, port, "满员2", udp_port=40124)
    await f2.connect(); await f2.hello()
    check(await f2.join(room_ids[0], "1234") is not None, "join: fourth member accepted")
    f3 = FakeClient(host, port, "满员3", udp_port=40125)
    await f3.connect(); await f3.hello()
    await f3.send(MSG_LOBBY_JOIN, [room_ids[0], "1234"])
    f = await f3.recv_until(MSG_REJECTED)
    check(f is not None and f[1][0] == "房间已满", "join: the 5th member is refused (hard cap 4)")

    # hidden room joinable by id
    hj = FakeClient(host, port, "ID侠", udp_port=40126)
    await hj.connect()
    await hj.hello()
    check(await hj.join(hidden_room, "") is not None, "join: hidden room joinable by id")

    # StartMatch refused when a fighter announced no match port
    noport_host = FakeClient(host, port, "无端口房", udp_port=0)
    await noport_host.connect()
    await noport_host.hello(udp_port=0)
    await noport_host.create(2)
    nid = noport_host.room_id
    noport_guest = FakeClient(host, port, "无端口客", udp_port=0)
    await noport_guest.connect()
    await noport_guest.hello(udp_port=0)
    await noport_guest.join(nid)
    await noport_host.claim(0)
    await noport_guest.claim(1)
    await noport_host.wait_room_state(
        lambda s: s[1][0][0] == noport_host.player_id and s[1][1][0] == noport_guest.player_id)
    await noport_host.pick(0)
    await noport_guest.pick(1)
    await noport_host.wait_room_state(lambda s: s[1][0][1] == 0 and s[1][1][1] == 1)
    await noport_host.send(MSG_MATCH_START, [40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0])
    f = await noport_host.recv_until(MSG_REJECTED)
    check(f is not None and "对局端口" in f[1][0],
          "start: refused when a fighter announced no match port")


async def udp_relay_sinks(host, udp_port, a_client, b_client):
    """A and B are in a running match (seats 0 and 1). Returns (roundtrip_ok, ta, tb, pa, pb)
    with pa/pb the receive queues of the A/B UDP endpoints (kept OPEN so the caller can also
    assert that a spoofed datagram is NOT forwarded). Uses asyncio UDP endpoints: a blocking
    socket in the middle of the event loop would freeze the server's own datagram handling
    (everything here runs on one loop)."""
    room_id = int(a_client.room_id)

    class Sink(asyncio.DatagramProtocol):
        def __init__(self):
            self.q = asyncio.Queue()

        def datagram_received(self, data, addr):
            self.q.put_nowait(bytes(data))

    loop = asyncio.get_running_loop()
    ta, pa = await loop.create_datagram_endpoint(
        lambda: Sink(), local_addr=("127.0.0.1", a_client.udp_port))
    tb, pb = await loop.create_datagram_endpoint(
        lambda: Sink(), local_addr=("127.0.0.1", b_client.udp_port))
    ok = True
    try:
        payload_a = b"rollback-A-001"
        ta.sendto(envelope(room_id, 0, 1, payload_a), (host, udp_port))
        ok = ok and await asyncio.wait_for(pb.q.get(), 2.0) == payload_a
        payload_b = b"rollback-B-002"
        tb.sendto(envelope(room_id, 1, 0, payload_b), (host, udp_port))
        ok = ok and await asyncio.wait_for(pa.q.get(), 2.0) == payload_b
    except asyncio.TimeoutError:
        ok = False
    return ok, ta, tb, pa, pb


def envelope(room_id, src, dst, payload):
    return room_id.to_bytes(4, "little") + bytes([src, dst]) + payload


async def udp_remapped_relay_ok(host, udp_port, room_id, port_a, port_b,
                                keep_bound=(), attempts=5, per_try=1.0):
    """Both fighters of a RUNNING match send from source ports the server has never seen (a NAT
    remap). Returns True when the relay forwards in both directions.

    Order matters: seat 1 goes first, so seat 0's endpoint is learned before anything has to be
    delivered to it. Seat 1's first datagram goes to whatever seat 0 is currently pinned to (or its
    announced port, which is only ever the initial guess) — so every port the server might still aim
    at must be passed in keep_bound and stay OPEN. That is also what the real client does (it owns its
    match socket for the whole room); an unbound one answers ICMP port-unreachable, and on Windows
    that kills the server's UDP transport until it rebinds (see LobbyServer._udp_died), losing exactly
    the datagrams under test."""

    class Sink(asyncio.DatagramProtocol):
        def __init__(self):
            self.q = asyncio.Queue()

        def datagram_received(self, data, addr):
            self.q.put_nowait(bytes(data))

    loop = asyncio.get_running_loop()
    transports = []
    try:
        for p in keep_bound:
            t, _ = await loop.create_datagram_endpoint(lambda: Sink(), local_addr=("127.0.0.1", p))
            transports.append(t)
        ta, pa = await loop.create_datagram_endpoint(lambda: Sink(), local_addr=("127.0.0.1", port_a))
        transports.append(ta)
        tb, pb = await loop.create_datagram_endpoint(lambda: Sink(), local_addr=("127.0.0.1", port_b))
        transports.append(tb)

        payload_a = b"remapped-A-001"
        payload_b = b"remapped-B-002"
        # UDP: retry rather than fail on a single lost datagram.
        for _ in range(attempts):
            tb.sendto(envelope(room_id, 1, 0, b"remapped-B-000"), (host, udp_port))
            ta.sendto(envelope(room_id, 0, 1, payload_a), (host, udp_port))
            try:
                if await asyncio.wait_for(pb.q.get(), per_try) != payload_a:
                    return False
            except asyncio.TimeoutError:
                continue
            tb.sendto(envelope(room_id, 1, 0, payload_b), (host, udp_port))
            try:
                return await asyncio.wait_for(pa.q.get(), per_try) == payload_b
            except asyncio.TimeoutError:
                continue
        return False
    finally:
        for t in transports:
            t.close()


async def run_account_scenarios(host, port):
    # ---- login: a fresh name is registered with the configured initial score ----
    a = FakeClient(host, port, "积分甲", udp_port=40100)
    await a.connect()
    await a.hello()
    f = await a.recv_until(MSG_LOGIN_OK)
    check(f is not None, "login: a fresh name gets LoginOk")
    check(isinstance(f[1][0], int) and f[1][0] > 0, f"login: playerid is a positive int ({f[1][0]})")
    check(f[1][1] == 1000, f"login: a fresh account starts at 1000 ({f[1][1]})")
    acc_a = f[1][0]

    # ---- duplicate login: the 顶号 popup path ----
    b = FakeClient(host, port, "积分甲", udp_port=40101)
    await b.connect()
    await b.hello()
    f = await b.recv_until(MSG_KICK_CONFIRM)
    check(f is not None and f[1][0] == "积分甲" and f[1][1] == 1000,
          "kick: a same-name login gets KickConfirm{name, score}")
    # A browse op aimed at a kick-wait connection is refused without closing (the stale parked
    # LobbyList an old client sends right after Hello).
    await b.send(MSG_LOBBY_LIST, [0])
    f = await b.recv_until(MSG_REJECTED)
    check(f is not None and "登录" in f[1][0], "kick: browse ops are refused until the login completes")
    # Confirm -> the old session is told, closed, and the SAME account binds to the new one.
    await b.send(MSG_KICK_LOGIN, ["积分甲"])
    f = await b.recv_until(MSG_LOGIN_OK)
    check(f is not None and f[1][0] == acc_a, "kick: the confirm binds the SAME account id")
    f = await a.recv_until(MSG_KICKED)
    check(f is not None and "其他设备" in f[1][0], "kick: the old session is told why")
    closed = False
    try:
        f = await a.recv(timeout=2.0)
        closed = f is None   # no frame left — just the EOF
    except ConnectionError:
        closed = True
    check(closed, "kick: the old connection is closed right after Kicked")

    # A third login sees the popup too; closing WITHOUT confirming leaves the account with b.
    c = FakeClient(host, port, "积分甲", udp_port=40102)
    await c.connect()
    await c.hello()
    check(await c.recv_until(MSG_KICK_CONFIRM) is not None, "kick: a third login also sees the popup")
    c.close()
    await asyncio.sleep(0.1)   # let the server process the abandoned popup's EOF

    # ---- matchmaking: two queued players pair into an auto room, the match auto-starts ----
    d = FakeClient(host, port, "积分乙", udp_port=40103)
    await d.connect()
    await d.hello()
    check(await d.recv_until(MSG_LOGIN_OK) is not None, "login: second account ready")
    await b.send(MSG_MATCHMAKE_JOIN, ["积分甲"])
    f = await b.recv_until(MSG_MATCHMAKE_STATUS)
    check(f is not None and f[1][0] is True, "mm: joining the pool answers status searching=true")
    await d.send(MSG_MATCHMAKE_JOIN, ["积分乙"])
    w_b = await b.recv_until(MSG_WELCOME, timeout=5.0)
    w_d = await d.recv_until(MSG_WELCOME, timeout=5.0)
    check(w_b is not None and w_d is not None, "mm: two queued players pair into a room")
    check(w_b[1][2][2] == w_d[1][2][2], "mm: both land in the SAME room")
    check(w_b[1][1] is True and w_d[1][1] is False, "mm: the first queuer is the host player")
    snap = w_b[1][2]
    check(snap[1][0][0] != 0 and snap[1][1][0] != 0, "mm: both seats are auto-claimed")
    check(snap[1][0][1] >= 0 and snap[1][1][1] >= 0, "mm: characters are auto-picked")
    check(all(len(p) == 7 and p[5] > 0 for p in snap[0]),
          "mm: snapshot players carry accountid + score (append-only fields)")
    sm = await b.recv_until(MSG_START_MATCH, timeout=5.0)
    check(sm is not None, "mm: the match auto-starts (no room screen round-trip)")
    # The server heartbeat reaches a seated fighter with (self, opponent) RTTs for the HUD.
    ps = await b.recv_until(MSG_PING_STATS, timeout=3.0)
    check(ps is not None and ps[1][0] >= 0 and ps[1][1] >= 0,
          f"ping: a seated fighter receives RTT stats ({ps[1] if ps else None})")
    await b.send(MSG_PONG, [99999])   # a stale echo must be ignored without breaking anything

    # ---- Elo settle: b (seat 0) wins, both scores move, the DB keeps them ----
    await b.send(MSG_MATCH_RESULT, [0])
    me = await b.recv_until(MSG_MATCH_ENDED)
    check(me is not None, "mm: MatchEnded follows the result")
    rs = await b.recv_until(MSG_ROOM_STATE, timeout=2.0)
    entries = {p[1]: p for p in rs[1][0]} if rs is not None else {}
    got_a = entries.get("积分甲", [0] * 7)[6]
    got_b = entries.get("积分乙", [0] * 7)[6]
    check(got_a == 1016 and got_b == 984,
          f"mm: Elo settled 1016/984 for equal scores (got {got_a}/{got_b})")
    b.close()
    await asyncio.sleep(0.1)
    b2 = FakeClient(host, port, "积分甲", udp_port=40104)
    await b2.connect()
    await b2.hello()
    f = await b2.recv_until(MSG_LOGIN_OK)
    check(f is not None and f[1][1] == 1016, "persist: the settled score survives a reconnect")

    # ---- 顶号 while IN GAME: the kicked fighter surrenders, the opponent wins, scores settle ----
    e = FakeClient(host, port, "积分丙", udp_port=40105)
    await e.connect()
    await e.hello()
    await e.recv_until(MSG_LOGIN_OK)
    w_e = await e.create(2)
    g = FakeClient(host, port, "积分丁", udp_port=40106)
    await g.connect()
    await g.hello()
    w_g = await g.join(w_e[2][2])   # create() returns [player_id, is_host, snapshot]; [2][2] = room id
    check(w_e is not None and w_g is not None, "surrender: room ready")
    await e.claim(0)
    await g.claim(1)
    await e.pick(0)
    await g.pick(1)
    await g.wait_room_state(lambda s: s[4] is False and s[1][0][2] >= 0 and s[1][1][2] >= 0)
    await e.send(MSG_MATCH_START, [40.0, 760.0, 800.0, 120.0, 560.0, 650.0, 560.0])
    check(await g.recv_until(MSG_START_MATCH) is not None, "surrender: the match is running")
    # h logs in as 积分丙 and confirms the 顶号
    h = FakeClient(host, port, "积分丙", udp_port=40107)
    await h.connect()
    await h.hello()
    check(await h.recv_until(MSG_KICK_CONFIRM) is not None, "surrender: the popup appears")
    await h.send(MSG_KICK_LOGIN, ["积分丙"])
    check(await h.recv_until(MSG_LOGIN_OK) is not None, "surrender: the new session is bound")
    check(await e.recv_until(MSG_KICKED) is not None, "surrender: the old session is told")
    me_g = await g.recv_until(MSG_MATCH_ENDED, timeout=3.0)
    check(me_g is not None and me_g[1][0] == 1,
          f"surrender: the opponent is awarded the match (winner seat {me_g[1][0] if me_g else '?'})")
    closed = False
    try:
        closed = await e.recv(timeout=2.0) is None
    except ConnectionError:
        closed = True
    check(closed, "surrender: the kicked connection is closed")
    e.close()
    h.close()          # h holds the account; it must go offline before the reconnect below
    await asyncio.sleep(0.1)
    e2 = FakeClient(host, port, "积分丙", udp_port=40108)
    await e2.connect()
    await e2.hello()
    f = await e2.recv_until(MSG_LOGIN_OK)
    score = f[1][1] if f is not None else None
    check(f is not None and score == 984,
          f"surrender: the kicked player lost Elo (score {score})")
    e2.close()
    await asyncio.sleep(0.1)
    h2 = FakeClient(host, port, "积分丙", udp_port=40109)
    await h2.connect()
    await h2.hello()
    f = await h2.recv_until(MSG_LOGIN_OK)
    score = f[1][1] if f is not None else None
    check(f is not None and score == 984, "surrender: the account keeps its settled score")

    # ---- 顶号 while IN QUEUE: the pool entry goes with the kicked session ----
    x = FakeClient(host, port, "积分戊", udp_port=40110)
    await x.connect()
    await x.hello()
    await x.recv_until(MSG_LOGIN_OK)
    await x.send(MSG_MATCHMAKE_JOIN, ["积分戊"])
    check(await x.recv_until(MSG_MATCHMAKE_STATUS) is not None, "queue-kick: x is queued")
    y = FakeClient(host, port, "积分戊", udp_port=40111)
    await y.connect()
    await y.hello()
    check(await y.recv_until(MSG_KICK_CONFIRM) is not None, "queue-kick: the popup appears")
    await y.send(MSG_KICK_LOGIN, ["积分戊"])
    check(await y.recv_until(MSG_LOGIN_OK) is not None, "queue-kick: the new session binds")
    check(await x.recv_until(MSG_KICKED) is not None, "queue-kick: the queued session is told")
    # If x's ghost stayed in the pool, z would pair with it on the very next tick (gap 0).
    z = FakeClient(host, port, "积分己", udp_port=40112)
    await z.connect()
    await z.hello()
    await z.recv_until(MSG_LOGIN_OK)
    await z.send(MSG_MATCHMAKE_JOIN, ["积分己"])
    f = await z.recv_until(MSG_WELCOME, timeout=2.5)
    check(f is None, "queue-kick: no ghost pairing — the kicked entry left the pool")

    # ---- Elo math (unit-level) ----
    check(elo_update(1000, 1000) == (1016, 984), "elo: equal scores -> +16/-16 at K=32")
    check(elo_update(1300, 1000) == (1305, 995), "elo: the favourite gains little, loses little")
    check(elo_update(900, 1100) == (924, 1076), "elo: the underdog's upset pays well")
    check(elo_update(1000, 1) == (1001, 0), "elo: the loser never drops below the floor, the winner still gains 1")

    for cl in (b2, d, g, h, x, y, z):
        cl.close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
    print("ALL PASS" if _fail == 0 else f"{_fail} FAILURE(S)")
    sys.exit(0 if _fail == 0 else 1)
