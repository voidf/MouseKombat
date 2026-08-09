"""Wire format for the MouseKombat lobby protocol.

THE SPEC IS ../../MouseKombat.Net/PROTOCOL.md — every number here is defined there, and a change
has to land in PROTOCOL.md, MouseKombat.Net/NetMessages.cs and this server in the same commit.

One lobby connection carries two phases on the SAME TCP socket:

* lobby phase: after Hello the client sends exactly one of LobbyList / LobbyCreate / LobbyJoin.
  A browser connection may send LobbyList repeatedly (paging) and then create or join.
* room phase: after LobbyCreate / LobbyJoin the connection IS the room membership; the server
  then plays the role TcpRoomHost plays for a LAN game (seats, characters, AI, match lifecycle)
  with the lobby-only routing messages (HostSendTo / LobbyPlayerJoined / MatchInputReport
  forwarding) on top.

Framing (identical to NetCodec.cs):

    u32 length   little-endian, counts the bytes AFTER this field (type byte + body)
    u8  type     MsgType below
    ... body     MessagePack, array form

Bodies are MessagePack ARRAYS (positional fields, like [Key(n)] in C#). Field ORDER is the
contract. Append only.
"""

from __future__ import annotations

import ipaddress
import msgpack
import unicodedata

# ---- frame limits (mirrors NetCodec.cs) ----
MAX_FRAME_BYTES = 1 << 20  # 1 MiB, far above any room snapshot

# ---- MsgType (mirrors MsgType enum in NetMessages.cs) ----
MSG_HELLO = 1
MSG_WELCOME = 2
MSG_REJECTED = 3
MSG_ROOM_STATE = 4
MSG_SEAT_CLAIM = 5
MSG_SEAT_RELEASE = 6
MSG_CHAR_PICK = 7
MSG_ADD_AI = 8
MSG_START_MATCH = 9
MSG_BYE = 10
MSG_MATCH_ENDED = 11
MSG_REMOVE_AI = 12
MSG_MATCH_RESULT = 13
MSG_MATCH_CATCH_UP = 14
MSG_MATCH_INPUTS = 15
MSG_MATCH_INPUT_REPORT = 16

# lobby-only (20.., reserved in the C# enum)
MSG_LOBBY_LIST = 20
MSG_LOBBY_ROOMS = 21
MSG_LOBBY_CREATE = 22
MSG_LOBBY_JOIN = 23
MSG_HOST_SEND_TO = 24
MSG_MATCH_START = 25
MSG_LOBBY_PLAYER_JOINED = 26

# Types a member may send in room phase. Anything else is ignored, exactly like the C# host
# ignores unknown client types (a stale or hostile peer must not crash the room).
ROOM_MEMBER_TYPES = frozenset({
    MSG_SEAT_CLAIM, MSG_SEAT_RELEASE, MSG_CHAR_PICK, MSG_ADD_AI, MSG_REMOVE_AI,
    MSG_MATCH_RESULT, MSG_MATCH_INPUT_REPORT, MSG_MATCH_START, MSG_HOST_SEND_TO, MSG_BYE,
})

# The host player's machine serves the catch-up data stream (MatchCatchUp / MatchInputs) to
# mid-match joiners; the server only routes it. Whitelisted so a compromised host player cannot
# inject arbitrary frames into another member's connection.
HOST_SEND_TO_ALLOWED_TYPES = frozenset({MSG_MATCH_CATCH_UP, MSG_MATCH_INPUTS})

PAGE_SIZE = 10  # room list page size (spec: 10 per page)
HARD_MAX_PLAYERS = 4  # hard per-room human cap (spec)

PROTOCOL = 2  # wire protocol version, must match NetVersion.Protocol


# ---- framing ----

class ProtocolError(Exception):
    """The stream is unusable: bad length, bad msgpack, wrong body shape. Close the conn."""


def encode_frame(msg_type: int, body: object) -> bytes:
    """Frame a message. `body` is a plain Python value; packed with msgpack (array form)."""
    packed = msgpack.packb(body, use_bin_type=True)
    n = len(packed) + 1
    if n > MAX_FRAME_BYTES:
        raise ProtocolError(f"frame too large ({n} > {MAX_FRAME_BYTES})")
    return n.to_bytes(4, "little") + bytes([msg_type]) + packed


def decode_body(body: bytes) -> object:
    """MessagePack body -> Python value. A malformed body is a protocol error."""
    try:
        return msgpack.unpackb(body, raw=False)
    except Exception as e:  # msgpack.UnpackException + friends
        raise ProtocolError(f"bad msgpack body: {e}") from e


class FrameReader:
    """Accumulates bytes off a socket and yields whole frames (mirrors NetCodec.FrameReader)."""

    def __init__(self):
        self._buf = bytearray()

    def feed(self, data: bytes) -> None:
        self._buf += data

    def try_read(self):
        """Next (type, body) frame, or None if a partial frame remains. Raises ProtocolError
        on a length field that cannot be trusted (same rule as the C# reader: drop, never
        believe a hostile or corrupt length)."""
        if len(self._buf) < 5:
            return None
        n = int.from_bytes(self._buf[0:4], "little")
        if n <= 0 or n > MAX_FRAME_BYTES:
            raise ProtocolError(f"frame length {n} out of range (max {MAX_FRAME_BYTES})")
        if len(self._buf) < 4 + n:
            return None
        frame = (self._buf[4], bytes(self._buf[5:4 + n]))
        del self._buf[:4 + n]
        return frame


# ---- shared value helpers ----

def sanitize_name(name: object, max_bytes: int = 18) -> str:
    """Display text only, never an identity. Same rule as RoomState.SanitizeName: strip
    control characters, trim, and bound to 18 UTF-8 bytes without splitting a code point.
    (The lobby UI itself caps the field at 16 bytes; the wire allows 18 like LAN.)"""
    if not isinstance(name, str):
        return "玩家"
    s = "".join(ch for ch in name if unicodedata.category(ch) != "Cc").strip()
    while s and len(s.encode("utf-8")) > max_bytes:
        s = s[:-1]
    return s or "玩家"


def is_valid_room_id(value: object) -> bool:
    return isinstance(value, str) and len(value) == 6 and value.isdigit()


def is_valid_password(value: object) -> bool:
    # "" = no password; otherwise exactly 4 digits (spec: 密码限制必须是4位整数).
    return value == "" or (isinstance(value, str) and len(value) == 4 and value.isdigit())


def normalize_ip(addr: str) -> str:
    """Normalize a peer address string so TCP peernames and UDP sources compare equal.
    A dual-mode listener reports IPv4 peers as ::ffff:a.b.c.d; map those back to plain IPv4."""
    try:
        a = ipaddress.ip_address(addr)
        if a.version == 6 and a.ipv4_mapped is not None:
            a = a.ipv4_mapped
        return str(a)
    except ValueError:
        return addr
