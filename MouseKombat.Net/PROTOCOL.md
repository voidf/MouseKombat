# MouseKombat wire protocol

**This file is the single source of truth.** The C# side (`MouseKombat.Net`) and the Python lobby
server (`server/lobby_server.py`) each implement it by hand, so any change has to land in three
places in the same commit: this document, the C# messages, the Python handlers.

## Design in one paragraph

One protocol, two hosts. A LAN game's host runs a mini room server in-process; a lobby game's rooms
live on the public Python server (`server/lobby_server.py`), and the client's transport class is
`LobbyRoomClient` instead of `TcpRoomClient` — everything else (messages, framing, room rules) is
the same. Room membership, seat claims and character picks go over a **reliable ordered** channel
(TCP). Once a match starts, per-frame inputs go over an **unreliable** channel (UDP) driven by the
rollback library, which has its own framing and is not described here.

## Transports

| Channel | Protocol | LAN port | Lobby port | Carries |
|---|---|---|---|---|
| Room | TCP | 5835 | 4954 | everything in this document |
| Match | UDP | 5835 | 4954 | rollback netcode payloads (opaque here) |

Same port *number* for both channels: TCP and UDP port spaces are independent, so no extra listener
is needed. The lobby relays match UDP by wrapping it in an envelope (§ Relay).

For a lobby game the **match UDP port clients dial is the LOBBY SERVER's** (announced in
`StartMatch.MatchUdpPort`), never the host player's — the server sits in the middle. There is no
second listener for spectator traffic: lobby spectators never dial UDP at all (§ Lobby).

## Lobby

The lobby server (`server/lobby_server.py`) is the public host. One protocol, two hosts: a LAN
game's host runs a mini room server in-process (`TcpRoomHost`); a lobby game's rooms live on the
server, which implements the same rules by hand (its `room.py` is the RoomState port). The client
side differs only in the transport: a LAN member is a `TcpRoomClient`, a lobby member a
`LobbyRoomClient` — the room messages are the same and only the endpoint differs.

### Connection flow

1. The client connects and sends `Hello` — **the server checks the version itself** against its
   configured game version: any mismatch → `Rejected`, connection closed. Nothing is relayed before
   that check.
2. The client then sends exactly one of `LobbyList` / `LobbyCreate` / `LobbyJoin` on the SAME
   connection. A browser connection may page through `LobbyList` and then create or join; it is
   reaped after `idleTimeout` of inactivity.
3. `LobbyCreate` / `LobbyJoin` are answered with the standard `Welcome`; the connection now IS the
   room membership, and the server plays the host's role: it authors `RoomState` after every accepted
   change, `StartMatch`, `MatchEnded`, `Bye`, and enforces every rule in § Room state — including
   host-only `AddAi`/`RemoveAi`, whose requester is the **host player** (the room creator).

Validation failures in the lobby phase (`LobbyJoin` with a wrong password, a full room, a malformed
create form) get `Rejected` **without** closing the connection — the browser must stay connected to
retry or pick another room. Handshake/protocol violations still close.

Directory rules:

* Rooms are in-memory only (spec: no persistence). Creation: `LobbyCreate{maxPlayers 2..4,
  password ""|4 digits, searchable}`. The room's capacity counts HUMANS only (AI seats consume no
  slot, matching § Room state); the hard cap is 4 humans regardless of the configured limit.
* `LobbyList{page}` returns `LobbyRooms{page, totalPages, entries}` with entries
  `[roomId, hostName, hasPassword, players, maxPlayers]` — **searchable rooms only**, sorted newest
  first, 10 per page. `players` is the count the capacity check uses (room players, which includes a
  mid-match fighter's reserved slot), never the count of live connections: an entry that advertises a
  free slot must be joinable.
* Room id: 6 random digits, shown in the seat screen (`RoomSnapshot.RoomId`) and on the wire in the
  UDP envelope as the numeric u32.
* A room with a password can only be joined with the exact 4-digit password.
* A member joining mid-match is admitted (if the room is not full) exactly like any other join:
  `Welcome` + `RoomState`; the host player is told with `LobbyPlayerJoined{playerId}` and serves the
  newcomer a catch-up over `HostSendTo`.

### Who runs what

The room state and the directory live on the server. The **match session still runs on the host
player's machine**: it drives the seats it holds plus any AI seats, exactly as a LAN host would.
The server is a hub, never a simulation. Consequences:

* `MatchStart` (host player → server) carries the stage geometry — the server has no scene to read
  it from. The server refuses it (silently, like the LAN `BeginMatch`) unless both seats are ready,
  refuses it with `Rejected` when either fighting seat's holder announced no match port, then
  broadcasts the standard `StartMatch` with `MatchUdpPort` = the server's UDP port and
  `SpectatingAvailable = false` (`Seat0Endpoint`/`Seat1Endpoint` stay empty — the server is always
  the hub).
* A fighter reaching the knockout sends `MatchResult` to the server, which ends the match, kicks
  the dropped, and broadcasts `MatchEnded` + a fresh `RoomState` (§ Ending).
* **All lobby spectating is data, not session.** A seatless member — before or during the match —
  watches through the catch-up stream (`MatchCatchUp` + `MatchInputs`), never a Backdash spectator
  session. That is why `StartMatch.SpectatingAvailable` is always false in a lobby and why no
  spectator UDP port exists.

### Catch-up routing through the hub

The host player's machine is the catch-up authority (it runs the session, or — in the relay
configuration — merges the fighters' `MatchInputReport` into its `CatchUpHistory`). The server only
routes:

* fighter → server: `MatchInputReport` is forwarded **verbatim to the host player**;
* host player → server: `HostSendTo{targetPlayerId, type, body}` is forwarded **verbatim** to that
  member, with `type` restricted to `MatchCatchUp` (14) and `MatchInputs` (15). `body` is the raw
  msgpack body of the target frame — never re-packed, or it would turn an array into a bin;
* server → host player: `LobbyPlayerJoined{playerId}` whenever a member joins, so the match director
  can serve a catch-up to the newcomer (the LAN `PlayerJoined` event, on the wire).

A host player that disconnects (or quits) destroys the room: the OTHER members are told with `Bye`
(spec: 主持玩家 ESC/强退时其它玩家弹窗提示) — but **`Bye` ends the ROOM, never the connection**. Every
member of a destroyed room, and every leaver whether host player or not, **returns to the browse
phase** with the room's identity dropped (player id, host rights, learned match endpoint), so the same
socket pages the room list and creates/joins again. That is what lets a client show the browser after
ESC or after the host closed the room, without reconnecting and without retyping the lobby form (spec:
ESC 退出房间后回到选房界面; 建房后 ESC 保持大厅连接; 主持玩家退房后其它玩家保持连接回到选房界面).
The one `Bye` that does close a connection is the server shutting down.

A member leaving mid-match keeps its slot reserved **only while it holds a fighting seat**: the
opponent is still simulating against that seat, so the seat stays claimed, the player stays in the
snapshot as `Connected=false`, and the kick happens at match end (§ Ending, same as LAN). A
**seatless watcher** changes nothing about the match, so it is removed outright and its human slot is
free at once — reserving it left rooms advertising `2/4 人` that refused every joiner with 房间已满.

## Relay (lobby only)

The server sits in the middle of the match channel too. Fighters wrap every rollback datagram:

```
u32  roomId      little-endian, the numeric room id (6 digits, as RoomSnapshot.RoomId)
u8   srcSlot     the sender's seat, 0 or 1
u8   dstSlot     the target seat, 0 or 1 (never equal to srcSlot)
...  opaque rollback payload
```

The server forwards the payload to the dstSlot holder's match endpoint, and **never parses the
payload** — it is a dumb forwarder for match traffic and only understands room bookkeeping.

Endpoints: at handshake the server pairs the TCP source address with `Hello.MatchUdpPort`, but that
port is a LOCAL port; behind a NAT the public UDP port differs. So the first datagram from a member
is trusted by IP + seat claim, its observed source endpoint becomes the member's public match
endpoint, and **the member is then pinned to that endpoint for the rest of the match** — a datagram
claiming the same seat from another port is dropped. That pin is what prevents a member behind the
same NAT from injecting frames for another member's seat (IPs are shared there, so IP alone cannot
disambiguate).

The pin is **per match, not per connection**, and it yields to a new source once the pinned endpoint
has gone silent (`ENDPOINT_REPIN_AFTER`, 2 s — a fighter sends ~60 datagrams a second, so a pin that
quiet is a dead NAT mapping). Both are needed because a client keeps ONE local match socket for the
whole room (its port was announced in `Hello`) while the server only ever sees a NAT *mapping* of it:
a mapping that goes quiet between matches is re-allocated to a different public port, and pinning
across matches dropped every datagram of the next match (`udp drop: pinned endpoint mismatch ...
learned ('x', 3766), got ('x', 3768)`), which froze the room at 等待对方同步 / 同步失败.

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
| 8 | `AddAi` | client → host | host only: put an AI in a seat (carries the character too — the AI flow never PickCharacter'd the seat) |
| 9 | `StartMatch` | host → all | both seats ready; carries the match setup |
| 10 | `Bye` | either | leaving / shutting down / kicked, with a reason |
| 11 | `MatchEnded` | host → all | a knockout happened; everyone returns to seat select |
| 12 | `RemoveAi` | client → host | host only: free an AI seat (Backspace in the seat screen) |
| 13 | `MatchResult` | fighter → host | reached the knockout; the host ends the match in room state |
| 14 | `MatchCatchUp` | host → joiner | a player who joined mid-match: config + confirmed input history (§ Mid-match spectating) |
| 15 | `MatchInputs` | host → joiner | new confirmed frames since the last batch, to keep the catch-up sim advancing |
| 16 | `MatchInputReport` | fighter → host | the fighter's confirmed frames since the last report + the match geometry (§ Mid-match spectating, relay configuration) |
| 20 | `LobbyList` | client → lobby | request a page of searchable rooms, newest first (§ Lobby) |
| 21 | `LobbyRooms` | lobby → client | one page of the room list |
| 22 | `LobbyCreate` | client → lobby | create a room; this connection becomes the host player |
| 23 | `LobbyJoin` | client → lobby | join an existing room by its 6-digit id |
| 24 | `HostSendTo` | host player → lobby | forward a frame (type + raw msgpack body) to one room member — the catch-up stream |
| 25 | `MatchStart` | host player → lobby | request a match start with the stage geometry |
| 26 | `LobbyPlayerJoined` | lobby → host player | a player just joined (mid-match catch-up hook) |

Per-frame match input is **not** in this table: it goes over UDP inside the rollback library's own
framing and is opaque to everything here (see `MouseKombat.Net/RollbackMatch.cs`). The one thing this
side defines about it is the 10-bit input packing, which is deliberately the same one a replay file
uses (`ReplayData.Pack`), so a networked match and a replay of it are fed byte-identical streams.

## Handshake

1. Client binds its match UDP port, then connects and sends
   `Hello{protocol, gameVersion, name, matchUdpPort}`.
2. The host compares BOTH numbers with its own. Any mismatch → `Rejected{reason}` then close.
   Version compatibility is explicitly not attempted: a desynced simulation is worse than a refusal.
3. Otherwise → `Welcome{playerId, isHost, snapshot}`, then `RoomState` to everyone.

`name` is display text only, capped at 18 UTF-8 bytes, control characters stripped. It is **never**
an identity: `playerId` is.

`matchUdpPort` is **bound before it is announced**, so the number cannot be taken by something else in
between. The host pairs it with the **source address of the TCP connection** — never with an address
from the message — and stores the result as that client's match endpoint. A client that announces
nothing gets a null endpoint, which makes the host refuse to start rather than guess.

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
  regardless of what it holds itself. `AddAi` carries the character (`[2]`, fallback to the seat's
  current character when absent): the AI flow picks a character in the same breath as the model, and
  the seat was never `CharPick`ed — it belongs to nobody yet, so the server cannot read the pick
  back from the seat. The AI runs on the host's machine and its inputs enter the match as if the
  host had sent them. Only the host may `RemoveAi` to free such a seat (the Backspace key in the
  seat screen); a human seat can only be released by its own holder.
* A match may start only when both seats are occupied and each has a character.

## Match lifecycle

`StartMatch` carries what the match needs that is not already in the snapshot: the host's match UDP
port, whether spectating is possible, and (for the lobby) the stage geometry. Recording a replay needs
the names and characters, which the snapshot already has.

### The host is always the hub

There is no P2P. Every client aims its rollback traffic at `StartMatch.matchUdpPort` on the host, and
therefore never learns another client's address. Which gives five configurations, all decided by
`MatchPlan.Build` from one snapshot and asserted in the test runner:

| Seats | Host | Other clients |
|---|---|---|
| host + client | fighter, dials that client directly | spectators |
| host + AI | fighter, **drives both seats** (the AI's input enters as the host's) | spectators |
| AI + AI | fighter, drives both seats | spectators |
| client + client | **relay only**, runs no session (`UdpMatchRelay` forwards verbatim) | cannot watch |
| anything, port missing | refuses to start, with the reason | — |

An **AI seat always runs on the host**, whichever seat it is, so it needs no player id and consumes no
player slot. That is also why the host can be a "fighter" while holding no seat itself.

**Spectating requires the host to be running a session**, which it only does when it drives at least
one seat. With two client fighters the host is a bare forwarder and there is nothing for a spectator
session to attach to; `StartMatch.spectatingAvailable` says so rather than making each client
re-derive the rule. Spectators who were in the room when the match started join the session directly;
a machine that joins **mid-match** cannot be added to a running session (Backdash refuses
`AddSpectator` once synchronization completes), which is what the section below exists for.

### Mid-match spectating

A player joining while `MatchRunning` gets the match as data instead of a session:

1. The host answers the join with `MatchCatchUp`: the authoritative snapshot the match started from
   (characters, names), the stage geometry and start positions, and the **confirmed** frames of both
   seats so far (packed with `ReplayData.Pack`, the same 10-bit stream a replay file stores). Only
   the confirmed prefix is sent: everything after it is speculative and a rollback may still rewrite
   it.
2. The joiner builds a `GameSim` from the config and **replays the history** to reach the current
   state — fast-forward, no prediction. Determinism is what makes this exact: the handshake already
   refused a version mismatch, and the sim is fixed-point, so the same inputs from the same start
   land on the same state on every machine.
3. The host then sends `MatchInputs` once per physics tick: the confirmed frames since the joiner's
   last batch, tracked per joiner so the stream is gap-free. The joiner's sim steps monotonically
   and can never be rewound, which is exactly why the stream carries only frames the host has
   confirmed (`RollbackMatch.ConfirmedFrame`) — a speculative frame corrected by a later rollback
   would diverge the joiner's view forever. The joiner follows the fight at the confirmation lag
   plus TCP latency, which is fine for watching.

Seats are frozen while the match runs, so the snapshot carried in `MatchCatchUp` stays accurate for
its whole lifetime. The stream ends when the match ends; `MatchEnded` brings the joiner back to seat
select like everyone else.

This only works when the host knows the inputs, which is exactly the case above: the host runs the
match session itself (host + client, host + AI, AI + AI). The relay configuration (both fighters are
clients, the host only forwards) works too, with one extra hop: every non-host fighter reports the
frames it CONFIRMED since its last report over TCP (`MatchInputReport`, one per physics tick, plus
the match geometry — the relay host has no scene of its own to read it from). The host merges the
reports into the same catch-up buffer and can then serve mid-match joiners AND watch the match
itself, exactly as if it had run the session — the relay-host seat screen switches to the spectate
view once the first report lands. A relay host that has not received a report yet shows
"正在获取对局数据…" rather than pretending to know the geometry.

Joining mid-match is inherently a **catch-up**: the joiner has not seen the fight up to the point it
joined, and nothing pretends otherwise.

### Ports

The host's match port is the room's TCP port, so it needs no announcing — but it is bound **per match**,
because when the host fights the rollback library owns it and when the host relays `UdpMatchRelay` does,
and those two cannot both hold it. A client's port is ephemeral, so it is bound **for the whole room**
and outlives each session (see `MatchSocket`): re-binding the same ephemeral port between matches would
be a race with every other process on the machine.

### Ending

After a knockout the host sends `MatchEnded`; everyone returns to seat select and the seat/character
state is cleared, so the room re-picks. A **fighter** who dropped during the match is kicked at that
point, not mid-round: mid-round their inputs are simply treated as neutral (the rollback session
derives that from its own `Disconnected` flag, so both machines substitute neutral on the same frames).
A dropped member that held **no seat** is not kept at all — it leaves the room the moment it goes, so
its human slot is free immediately (`RoomState.HoldsSeat` / `room.py holds_seat` decide which is which).

In-match `Esc` is disabled: one player walking out mid-round would leave the other simulating against
a seat nobody drives, and the rules above already decide when a match ends.

A knockout seen on a **predicted** frame is not acted on immediately. Starting the victory sequence
stops the simulation, so a rollback retracting the KO would never arrive; the host and client would then
disagree about whether the match was over. The knockout therefore has to stand for more frames than the
prediction window before the splash starts (`GameManager.NetKoConfirmFrames`).

## Relay (lobby only)

See § Lobby and § Relay above: the server is a dumb forwarder for match UDP (envelope
`u32 roomId + u8 srcSlot + u8 dstSlot + payload`, endpoint pinning after the first datagram) and a
routing hub for the room channel (lobby messages 20..26; everything else is authored by the server
itself, never relayed). TCP is not byte-relayed — the server implements the room authority.

## Note on the serializer

MessagePack's default resolver generates code at runtime, which is fine under JIT — every desktop
Godot export uses it. An iOS export is AOT and would need the source-generated resolver instead.
That is a resolver registration change; **the wire format is unaffected.**
