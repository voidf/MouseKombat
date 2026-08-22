using MessagePack;
using MouseKombat.Sim;

namespace MouseKombat.Net;

// Wire messages. THE SPEC IS PROTOCOL.md — read it before touching anything here, and change both
// this file and server/lobby_server.py in the same commit.
//
// Bodies are MessagePack ARRAYS: [Key(n)] indices are positional and field names are never sent.
// Append only — never reorder, never repurpose an index, never delete one.

public enum MsgType : byte
{
    Hello = 1,
    Welcome = 2,
    Rejected = 3,
    RoomState = 4,
    SeatClaim = 5,
    SeatRelease = 6,
    CharPick = 7,
    AddAi = 8,
    StartMatch = 9,
    Bye = 10,
    MatchEnded = 11,
    RemoveAi = 12,   // host only: free an AI seat the host placed (Backspace in the seat screen)
    MatchResult = 13,// fighter -> host: this machine reached the knockout, here is the winner
    MatchCatchUp = 14,// host -> a player who JOINED mid-match: the match config and every confirmed
                      // input so far, which the joiner replays to reach the current state (see
                      // SpectateScreen; PROTOCOL.md § Mid-match spectating)
    MatchInputs = 15,// host -> a mid-match joiner: the confirmed inputs since the last batch, to keep
                      // their catch-up sim advancing once it has caught up
    MatchInputReport = 16,// fighter -> host (relay configuration only): the confirmed inputs since the
                      // last report, plus the match geometry. The relay host has no simulation of its
                      // own and nothing else to learn the inputs from — the fighters are the only
                      // machines that know them. This is what lets the relay host (and mid-match
                      // joiners in a relay room) watch (see PROTOCOL.md § Mid-match spectating)
    // Lobby-only messages (期3-5). Sent on the SAME connection as everything else; the lobby
    // server (server/lobby_server.py) implements the whole table by hand.
    LobbyList = 20,   // client -> lobby: request a page of searchable rooms (newest first)
    LobbyRooms = 21,  // lobby -> client: one page of the room list
    LobbyCreate = 22, // client -> lobby: create a room; this connection becomes the host player
    LobbyJoin = 23,   // client -> lobby: join an existing room by its 6-digit id
    HostSendTo = 24,  // host player -> lobby: forward a frame (type + raw msgpack body) to one
                      // member of the room — carries the catch-up stream (see § Mid-match spectating)
    MatchStart = 25,  // host player -> lobby: request a match start with the stage geometry
    LobbyPlayerJoined = 26,// lobby -> host player: a player just joined (mid-match catch-up hook)
}

public static class NetVersion
{
    // Bumped whenever the wire format changes in a way an older peer would misread. Separate from the
    // game version so a pure protocol fix does not require a game release note, and vice versa.
    //
    // 2: the match channel. Hello announces a bound match UDP port, StartMatch carries the host's port
    //    and whether spectating is possible, and MatchResult was added. All of it is append-only at the
    //    format level, so a v1 peer would not MISREAD anything — but it would announce no match port,
    //    and the host would then refuse to start a match against it with a message about a missing
    //    port rather than about versions. Refusing at the handshake says the true thing instead.
    public const int Protocol = 2;
}

[MessagePackObject]
public sealed class Hello
{
    [Key(0)] public int Protocol { get; set; }
    [Key(1)] public string GameVersion { get; set; } = "";
    [Key(2)] public string Name { get; set; } = "";
    // Set when joining a lobby room that has a password. Empty for LAN.
    [Key(3)] public string RoomPassword { get; set; } = "";
    // UDP port this client has ALREADY BOUND for match traffic. The host pairs it with the source
    // address of this TCP connection to get a full endpoint, which is how it can put the client into
    // a rollback session (or a relay) without any discovery step. Announced rather than negotiated, and
    // bound before it is announced, so the number cannot be stolen in between.
    [Key(4)] public int MatchUdpPort { get; set; }
    // md5 of this machine's Heroes/ + FireballTSCN/ + ParticleTSCN/ content (HeroLibrary). The
    // host/room compares it and refuses a mismatch — two machines with different frame data
    // would desync on frame 1. "" = built without a HeroLibrary (tests).
    [Key(5)] public string AssetHash { get; set; } = "";
}

[MessagePackObject]
public sealed class Welcome
{
    [Key(0)] public int PlayerId { get; set; }
    [Key(1)] public bool IsHost { get; set; }
    [Key(2)] public RoomSnapshot Room { get; set; }
}

[MessagePackObject]
public sealed class Rejected
{
    [Key(0)] public string Reason { get; set; } = "";
    // Filled in on a version mismatch so the client can show BOTH numbers instead of a vague message.
    [Key(1)] public int HostProtocol { get; set; }
    [Key(2)] public string HostGameVersion { get; set; } = "";
    // Filled in on an ASSET-hash mismatch (different Heroes/ content): the room's hash and the
    // joiner's own, so the popup can show both sides.
    [Key(3)] public string HostAssetHash { get; set; } = "";
    [Key(4)] public string YourAssetHash { get; set; } = "";
}

[MessagePackObject]
public sealed class PlayerInfo
{
    [Key(0)] public int PlayerId { get; set; }
    [Key(1)] public string Name { get; set; } = "";
    [Key(2)] public bool IsHost { get; set; }
    [Key(3)] public int Seat { get; set; } = -1;   // -1 = spectator
    [Key(4)] public bool Connected { get; set; } = true;
}

[MessagePackObject]
public sealed class SeatInfo
{
    [Key(0)] public int OccupantPlayerId { get; set; } = 0;   // 0 = empty
    [Key(1)] public int Character { get; set; } = -1;         // -1 = not chosen yet
    [Key(2)] public bool IsAi { get; set; }
    [Key(3)] public string AiModel { get; set; } = "";        // "" = built-in state machine

    [IgnoreMember] public bool Occupied => OccupantPlayerId != 0 || IsAi;
    [IgnoreMember] public bool Ready => Occupied && Character >= 0;
    [IgnoreMember] public CharacterId CharacterId => (CharacterId)(Character < 0 ? 0 : Character);
}

// The whole authoritative room, resent on every change. Seat select is small and infrequent, so a
// full snapshot cannot desync — unlike the match, which needs rollback precisely because per-frame
// state is too large to resend.
[MessagePackObject]
public sealed class RoomSnapshot
{
    [Key(0)] public PlayerInfo[] Players { get; set; } = System.Array.Empty<PlayerInfo>();
    [Key(1)] public SeatInfo[] Seats { get; set; } = { new SeatInfo(), new SeatInfo() };
    [Key(2)] public string RoomId { get; set; } = "";      // lobby games only
    [Key(3)] public int MaxPlayers { get; set; }           // 0 = unlimited (LAN)
    [Key(4)] public bool MatchRunning { get; set; }
}

[MessagePackObject]
public sealed class SeatClaim
{
    [Key(0)] public int Seat { get; set; }
}

[MessagePackObject]
public sealed class SeatRelease { }

[MessagePackObject]
public sealed class CharPick
{
    [Key(0)] public int Character { get; set; }
}

[MessagePackObject]
public sealed class AddAi
{
    [Key(0)] public int Seat { get; set; }
    [Key(1)] public string AiModel { get; set; } = "";   // "" = built-in state machine
    // The character to place, sent explicitly rather than read back from the seat: the AI flow
    // picks a character in the same breath as the model (the seat was never PickCharacter'd —
    // it does not belong to anyone yet), so the server cannot know it. -1 = fall back to the
    // seat's current character (the pre-0.0.8 behaviour; a LAN host filling an empty seat via
    // this message sends it).
    [Key(2)] public int Character { get; set; } = -1;
}

[MessagePackObject]
public sealed class RemoveAi
{
    [Key(0)] public int Seat { get; set; }
}

[MessagePackObject]
public sealed class StartMatch
{
    [Key(0)] public RoomSnapshot Room { get; set; }
    [Key(1)] public float StageMinX { get; set; } = 40f;
    [Key(2)] public float StageMaxX { get; set; } = 760f;
    [Key(3)] public float WorldWidth { get; set; } = 800f;
    [Key(4)] public float P1StartX { get; set; } = 120f;
    [Key(5)] public float P1StartY { get; set; } = 560f;
    [Key(6)] public float P2StartX { get; set; } = 650f;
    [Key(7)] public float P2StartY { get; set; } = 560f;
    // How each fighting seat is reached over UDP. Empty for a seat driven locally (the host's own
    // seat, or an AI seat, whose inputs enter the match as the host's).
    //
    // UNUSED BY LAN, and deliberately so: the host is always the hub (spec: 走房主中转，不做 P2P), so
    // every client dials MatchUdpPort below and never needs another client's address — which also means
    // one player's IP is never handed to another. These two stay for the lobby relay in 期3-5.
    [Key(8)] public string Seat0Endpoint { get; set; } = "";
    [Key(9)] public string Seat1Endpoint { get; set; } = "";
    // The host's UDP port for this match. Same number as the room's TCP port for a LAN game, but sent
    // explicitly rather than assumed: a lobby game relays through a different port entirely.
    [Key(10)] public int MatchUdpPort { get; set; }
    // Spectating requires the host to be RUNNING the session, which it only does when it drives at
    // least one seat (its own or an AI's). When two clients fight and the host holds nothing, the host
    // is a dumb UDP relay with no session to attach a spectator to, so nobody can watch. Sent rather
    // than re-derived so a client does not have to reimplement the rule.
    [Key(11)] public bool SpectatingAvailable { get; set; }
}

// A fighter telling the host the match is over.
//
// Needed because of the relay configuration: when both fighters are clients and the host holds no seat,
// the host runs no simulation and has no other way to learn that a knockout happened — so the room would
// stay MatchRunning forever and nobody could pick again. Fighters always send it; the host ignores a
// second report because the first one already cleared MatchRunning.
[MessagePackObject]
public sealed class MatchResult
{
    [Key(0)] public int WinnerSeat { get; set; } = -1;
}

[MessagePackObject]
public sealed class Bye
{
    [Key(0)] public string Reason { get; set; } = "";
}

[MessagePackObject]
public sealed class MatchEnded
{
    [Key(0)] public int WinnerSeat { get; set; } = -1;
    // Players who dropped during the match: kicked now rather than mid-round, where their inputs were
    // simply treated as neutral.
    [Key(1)] public int[] DroppedPlayerIds { get; set; } = System.Array.Empty<int>();
}

// The host answering a mid-match joiner ("what is happening right now, and what has happened so far").
// Sent once, right after Welcome, when the joiner arrives while MatchRunning and the host runs the
// match session itself (so it knows the inputs — see PROTOCOL.md § Mid-match spectating for why the
// relay configuration cannot).
//
// The body is the same input history a replay stores: every CONFIRMED frame of both seats, packed
// with ReplayData.Pack. The joiner builds a GameSim from the config, steps through the history to
// reach the current state, then keeps stepping as MatchInputs batches arrive.
[MessagePackObject]
public sealed class MatchCatchUp
{
    // The authoritative snapshot the match started from: seats carry the characters, players the
    // names. Resent rather than trusted from the joiner's own (possibly stale) snapshot, exactly like
    // StartMatch does.
    [Key(0)] public RoomSnapshot Room { get; set; }
    [Key(1)] public float StageMinX { get; set; } = 40f;
    [Key(2)] public float StageMaxX { get; set; } = 760f;
    [Key(3)] public float WorldWidth { get; set; } = 800f;
    [Key(4)] public float P1StartX { get; set; } = 120f;
    [Key(5)] public float P1StartY { get; set; } = 560f;
    [Key(6)] public float P2StartX { get; set; } = 650f;
    [Key(7)] public float P2StartY { get; set; } = 560f;
    // Confirmed frames in the history. 0 is legal (joined before the first frame); the stream then
    // feeds everything from frame 0.
    [Key(8)] public int FrameCount { get; set; }
    [Key(9)] public ushort[] P1Inputs { get; set; } = System.Array.Empty<ushort>();
    [Key(10)] public ushort[] P2Inputs { get; set; } = System.Array.Empty<ushort>();
}

// One batch of new confirmed frames for a mid-match joiner. The host sends it per physics tick; on a
// healthy link that is one frame per message (two ushorts), and a burst simply carries more.
[MessagePackObject]
public sealed class MatchInputs
{
    // Frame number of P1[0] / P2[0]. The joiner verifies this is exactly its next frame and drops
    // anything else: a gap would silently shift the whole rest of the match.
    [Key(0)] public int StartFrame { get; set; }
    [Key(1)] public ushort[] P1 { get; set; } = System.Array.Empty<ushort>();
    [Key(2)] public ushort[] P2 { get; set; } = System.Array.Empty<ushort>();
}

// A fighter telling the host what it CONFIRMED since the last report. The body is the same shape as
// MatchInputs (a slice of the 10-bit input stream), plus the match geometry: in the relay
// configuration the host has no scene of its own to read the geometry from, and the fighters are the
// only machines that know it. Sent every physics tick for as long as the fighter runs a session; a
// few floats per tick is nothing on a LAN, and it keeps the message shape trivial.
[MessagePackObject]
public sealed class MatchInputReport
{
    // Frame number of P1[0] / P2[0].
    [Key(0)] public int StartFrame { get; set; }
    [Key(1)] public ushort[] P1 { get; set; } = System.Array.Empty<ushort>();
    [Key(2)] public ushort[] P2 { get; set; } = System.Array.Empty<ushort>();
    // The match geometry, repeated on every report. Sent rather than assumed because these are the
    // fighters' AUTHORED values (their scene's slots), which is the only source of truth.
    [Key(3)] public float StageMinX { get; set; }
    [Key(4)] public float StageMaxX { get; set; }
    [Key(5)] public float WorldWidth { get; set; }
    [Key(6)] public float P1StartX { get; set; }
    [Key(7)] public float P1StartY { get; set; }
    [Key(8)] public float P2StartX { get; set; }
    [Key(9)] public float P2StartY { get; set; }
}

// ---- lobby-only messages (期3-5). See PROTOCOL.md § Lobby. ----

[MessagePackObject]
public sealed class LobbyList
{
    [Key(0)] public int Page { get; set; }      // 0-based; server pages at 10 entries per page
}

[MessagePackObject]
public sealed class LobbyRoomEntry
{
    [Key(0)] public string RoomId { get; set; } = "";
    [Key(1)] public string HostName { get; set; } = "";
    [Key(2)] public bool HasPassword { get; set; }
    [Key(3)] public int Players { get; set; }   // humans; AI seats never count
    [Key(4)] public int MaxPlayers { get; set; }
    [Key(5)] public string AssetHash { get; set; } = "";   // shown as the first 6 hex digits
}

[MessagePackObject]
public sealed class LobbyRooms
{
    [Key(0)] public int Page { get; set; }
    [Key(1)] public int TotalPages { get; set; }
    [Key(2)] public LobbyRoomEntry[] Entries { get; set; } = System.Array.Empty<LobbyRoomEntry>();
}

[MessagePackObject]
public sealed class LobbyCreate
{
    [Key(0)] public int MaxPlayers { get; set; } = 4;   // 2..4 (spec: 房间人数限制 2~4)
    [Key(1)] public string Password { get; set; } = ""; // "" or exactly 4 digits
    [Key(2)] public bool Searchable { get; set; } = true;
    [Key(3)] public string AssetHash { get; set; } = ""; // stamps the room; joins with a
                                                          // different hash are refused
}

[MessagePackObject]
public sealed class LobbyJoin
{
    [Key(0)] public string RoomId { get; set; } = "";   // 6 digits, as shown in the room list
    [Key(1)] public string Password { get; set; } = "";
}

// The host player routing its match director's catch-up stream through the lobby server.
// Body is the RAW msgpack body (a MatchCatchUp or MatchInputs array), forwarded verbatim.
[MessagePackObject]
public sealed class HostSendTo
{
    [Key(0)] public int TargetPlayerId { get; set; }
    [Key(1)] public byte Type { get; set; }             // 14 MatchCatchUp / 15 MatchInputs only
    [Key(2)] public byte[] Body { get; set; } = System.Array.Empty<byte>();
}

// The host player asking the lobby server to start the match, carrying the stage geometry the
// server has no scene to read itself. The server answers with the standard StartMatch broadcast.
[MessagePackObject]
public sealed class MatchStart
{
    [Key(0)] public float StageMinX { get; set; } = 40f;
    [Key(1)] public float StageMaxX { get; set; } = 760f;
    [Key(2)] public float WorldWidth { get; set; } = 800f;
    [Key(3)] public float P1StartX { get; set; } = 120f;
    [Key(4)] public float P1StartY { get; set; } = 560f;
    [Key(5)] public float P2StartX { get; set; } = 650f;
    [Key(6)] public float P2StartY { get; set; } = 560f;
}

// The lobby server telling the host player that a member joined, so the host player's match
// director can serve that joiner a catch-up (the LAN PlayerJoined event, on the wire).
[MessagePackObject]
public sealed class LobbyPlayerJoined
{
    [Key(0)] public int PlayerId { get; set; }
}
