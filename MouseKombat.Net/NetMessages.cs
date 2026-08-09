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
    // 20.. reserved for the lobby-only room list / create / join messages (期3-5)
}

public static class NetVersion
{
    // Bumped whenever the wire format changes in a way an older peer would misread. Separate from the
    // game version so a pure protocol fix does not require a game release note, and vice versa.
    public const int Protocol = 1;
}

[MessagePackObject]
public sealed class Hello
{
    [Key(0)] public int Protocol { get; set; }
    [Key(1)] public string GameVersion { get; set; } = "";
    [Key(2)] public string Name { get; set; } = "";
    // Set when joining a lobby room that has a password. Empty for LAN.
    [Key(3)] public string RoomPassword { get; set; } = "";
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
    [Key(8)] public string Seat0Endpoint { get; set; } = "";
    [Key(9)] public string Seat1Endpoint { get; set; } = "";
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
