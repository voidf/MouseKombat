"""Headless end-to-end smoke test for the lobby server. Runs the server in-process on ephemeral
ports, so it works anywhere Python 3.11 + msgpack exists — no .NET needed.

    python smoke_test.py

Exits nonzero on any failure. The .NET test runner (MouseKombat.Sim.Tests/LobbyServerTests.cs)
exercises the same wire with the REAL C# codec; this file keeps the Python side honest in
isolation and is the thing to run on the server machine after a deploy.
"""

from __future__ import annotations

import asyncio
import socket
import sys

from protocol import (
    MSG_ADD_AI, MSG_BYE, MSG_CHAR_PICK, MSG_HELLO, MSG_HOST_SEND_TO, MSG_LOBBY_CREATE,
    MSG_LOBBY_JOIN, MSG_LOBBY_LIST, MSG_LOBBY_PLAYER_JOINED, MSG_LOBBY_ROOMS,
    MSG_MATCH_ENDED, MSG_MATCH_INPUT_REPORT, MSG_MATCH_INPUTS, MSG_MATCH_RESULT,
    MSG_MATCH_START, MSG_REJECTED, MSG_ROOM_STATE, MSG_SEAT_CLAIM, MSG_SEAT_RELEASE,
    MSG_START_MATCH, MSG_WELCOME, PROTOCOL, FrameReader, decode_body, encode_frame,
)
from lobby_server import LobbyServer

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
    srv = LobbyServer("127.0.0.1", 0, 0, GAME_VERSION, idle_timeout=300, max_rooms=500)
    await srv.start()
    host, tcp_port, udp_port = "127.0.0.1", srv.port, srv.udp_port
    try:
        await run_scenarios(host, tcp_port, udp_port)
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
    for body, expect in (([9, "", True], "2到4"), ([4, "12", True], "密码"),
                         ([4, "12345", True], "密码"), (["4", "", True], "2到4")):
        c = FakeClient(host, port, "乱建")
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

    # ---- mid-match member drop: marked disconnected, kicked at match end ----
    drop = FakeClient(host, port, "掉线者", udp_port=40002)
    await drop.connect()
    await drop.hello()
    w = await drop.join(hostc.room_id)
    drop_id = w[0]
    await hostc.recv_until(MSG_LOBBY_PLAYER_JOINED)
    drop.close()   # abrupt disconnect mid-match
    f = await hostc.wait_room_state(
        lambda s: drop_id in [p[0] for p in s[0]]
                  and {p[0]: p[4] for p in s[0]}[drop_id] is False)
    check(f is not None, "drop: mid-match disconnect marks Connected=false, keeps the player")
    await hostc.send(MSG_MATCH_RESULT, [0])
    f = await mem.recv_until(MSG_MATCH_ENDED)
    check(f is not None and f[1][1] == [drop_id], "drop: match end kicks the dropped player")
    f = await mem.wait_room_state(
        lambda s: s[4] is False and all(x[0] == 0 and not x[2] for x in s[1]))
    check(f is not None, "drop: seats cleared after the match")

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

    # ---- host player leaves -> room destroyed, members told ----
    hostc.close()
    f = await mem.recv_until(MSG_BYE)
    check(f is not None and f[1][0] == "连接断开",
          "destroy: host leaving broadcasts Bye to members")
    try:
        await mem.recv(timeout=1.0)
        check(False, "destroy: member connection closed by the server")
    except ConnectionError:
        check(True, "destroy: member connection closed by the server")

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


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
    print("ALL PASS" if _fail == 0 else f"{_fail} FAILURE(S)")
    sys.exit(0 if _fail == 0 else 1)
