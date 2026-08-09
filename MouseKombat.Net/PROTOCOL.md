# MouseKombat wire protocol

**This file is the single source of truth.** The C# side (`MouseKombat.Net`) and the Python lobby
server (`server/lobby_server.py`) each implement it by hand, so any change has to land in three
places in the same commit: this document, the C# messages, the Python handlers.

## Design in one paragraph

One protocol, two hosts. A LAN game's host runs a mini room server in-process; a lobby game's rooms
live on the public Python server. Both speak exactly the same messages, so the client code is
identical and only the endpoint differs. Room membership, seat claims and character picks go over a
**reliable ordered** channel (TCP). Once a match starts, per-frame inputs go over an **unreliable**
channel (UDP) driven by the rollback library, which has its own framing and is not described here.

## Transports

| Channel | Protocol | LAN port | Lobby port | Carries |
|---|---|---|---|---|
| Room | TCP | 5835 | 4954 | everything in this document |
| Match | UDP | 5835 | 4954 | rollback netcode payloads (opaque here) |

Same port *number* for both channels: TCP and UDP port spaces are independent, so no extra listener
is needed. The lobby relays match UDP by wrapping it in an envelope (§ Relay).

## Framing

Every TCP message is:

```
u32  length   little-endian, counts the bytes AFTER this field
u8   type     MsgType below
...  body     MessagePack, array form
```

Length-prefixing is mandatory because TCP is a stream: a read can return half a message or three of
them. `NetCodec` buffers until a whole frame is present.

`length` is capped (`NetCodec.MaxFrameBytes`). A peer claiming a huge frame is dropped rather than
trusted — otherwise a hostile or corrupt length is an instant out-of-memory.

## Bodies are MessagePack ARRAYS, not maps

Fields are positional (`[Key(0)]`, `[Key(1)]`, …), so field *names* are never transmitted. Both sides
must therefore agree on field ORDER. Rules:

* **Append only.** Never reorder, never repurpose an index, never delete — add at the end.
* A reader that sees more fields than it knows ignores the tail; a reader that sees fewer treats the
  missing ones as default. That is what makes a field addition backward compatible.
* Even so, a version mismatch is refused up front (§ Handshake), so this tolerance is a safety net
  rather than a compatibility strategy.

## MsgType

| # | Name | Direction | Purpose |
|---|---|---|---|
| 1 | `Hello` | client → host | first frame; protocol + game version, display name |
| 2 | `Welcome` | host → client | accepted; assigns a player id, carries the room snapshot |
| 3 | `Rejected` | host → client | refused, with a reason; the host closes after sending |
| 4 | `RoomState` | host → all | full authoritative snapshot (see § Room state) |
| 5 | `SeatClaim` | client → host | request seat 0 or 1 |
| 6 | `SeatRelease` | client → host | give up whatever seat this player holds |
| 7 | `CharPick` | client → host | choose a character for the seat this player holds |
| 8 | `AddAi` | client → host | host only: put an AI in a seat |
| 9 | `StartMatch` | host → all | both seats ready; carries the match setup |
| 10 | `Bye` | either | leaving / shutting down / kicked, with a reason |
| 11 | `MatchEnded` | host → all | a knockout happened; everyone returns to seat select |
| 12 | `RemoveAi` | client → host | host only: free an AI seat (Backspace in the seat screen) |
| 20.. | reserved | | lobby-only messages (room list / create / join) land here in 期3-5 |

## Handshake

1. Client connects and sends `Hello{protocol, gameVersion, name}`.
2. The host compares BOTH numbers with its own. Any mismatch → `Rejected{reason}` then close.
   Version compatibility is explicitly not attempted: a desynced simulation is worse than a refusal.
3. Otherwise → `Welcome{playerId, isHost, snapshot}`, then `RoomState` to everyone.

`name` is display text only, capped at 18 UTF-8 bytes, control characters stripped. It is **never**
an identity: `playerId` is.

## Room state

The host is authoritative. Clients send requests and apply whatever snapshot comes back; they never
mutate their own copy optimistically. Seat select is small and infrequent, so a full snapshot per
change is simpler and impossible to desync — unlike the match, which needs rollback precisely
because per-frame state is too big to resend.

Snapshot contents: the room's players (id, name, seat or none, whether host) and the two seats
(occupant player id or none, chosen character, whether it is an AI and which model).

Rules the host enforces:

* Two fighting seats. First claim wins; a claim on a taken seat is ignored.
* One seat per player. Claiming a second releases the first.
* Anyone may release their own seat at any time. Nobody can release someone else's.
* Unlimited spectators — a player with no seat is a spectator, not an error.
* **AI seats are host-only.** Only the host may `AddAi`, and the host may put an AI in either seat
  regardless of what it holds itself. The AI runs on the host's machine and its inputs enter the
  match as if the host had sent them. Only the host may `RemoveAi` to free such a seat (the
  Backspace key in the seat screen); a human seat can only be released by its own holder.
* A match may start only when both seats are occupied and each has a character.

## Match lifecycle

`StartMatch` carries what the match needs that is not already in the snapshot: the stage bounds, the
start positions, and how to reach the other fighter over UDP. Recording a replay needs the names and
characters, which the snapshot already has.

After a knockout the host sends `MatchEnded`; everyone returns to seat select and the seat/character
state is cleared, so the room re-picks. A player who dropped during the match is kicked at that
point, not mid-round: mid-round their inputs are simply treated as neutral.

## Relay (lobby only)

For lobby games the server sits in the middle. TCP messages are relayed to the room's members
unchanged. Match UDP is wrapped:

```
u32  roomId
u8   srcSlot
u8   dstSlot
...  opaque rollback payload
```

The server never parses the payload — it is a dumb forwarder for match traffic and only understands
room bookkeeping.

## Note on the serializer

MessagePack's default resolver generates code at runtime, which is fine under JIT — every desktop
Godot export uses it. An iOS export is AOT and would need the source-generated resolver instead.
That is a resolver registration change; **the wire format is unaffected.**
