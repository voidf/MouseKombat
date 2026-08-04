using System;
using System.Collections.Generic;
using System.Linq;

namespace MouseKombat.Net;

// The authoritative room, as pure logic: no sockets, no Godot, no async. The host owns one of these
// and broadcasts a snapshot after every accepted change; clients only ever apply snapshots.
//
// Keeping the rules here rather than inside the socket handler is what makes them testable — every
// rule in PROTOCOL.md § Room state has an assertion, including the ones that are about REFUSING
// things (claiming a taken seat, releasing someone else's, a non-host adding an AI).
public sealed class RoomState
{
    public const int SeatCount = 2;

    private readonly Dictionary<int, PlayerInfo> _players = new();
    private readonly SeatInfo[] _seats = { new SeatInfo(), new SeatInfo() };
    private int _nextPlayerId = 1;   // 0 means "nobody", so ids start at 1

    public string RoomId { get; set; } = "";
    public int MaxPlayers { get; set; }          // 0 = unlimited (LAN)
    public bool MatchRunning { get; private set; }
    public int HostPlayerId { get; private set; }

    public IReadOnlyCollection<PlayerInfo> Players => _players.Values;
    public SeatInfo Seat(int i) => _seats[i];

    // Both seats occupied AND each has a character. Until then StartMatch is refused.
    public bool CanStart => !MatchRunning && _seats.All(s => s.Ready);

    // ---- membership ----

    // Returns the new player, or null when the room is full. MaxPlayers counts HUMANS: AI seats are
    // driven by the host and consume no slot (matching the lobby's "AI does not count" rule).
    public PlayerInfo AddPlayer(string name, bool isHost)
    {
        if (MaxPlayers > 0 && _players.Count >= MaxPlayers) return null;

        var p = new PlayerInfo
        {
            PlayerId = _nextPlayerId++,
            Name = SanitizeName(name),
            IsHost = isHost,
            Seat = -1,
            Connected = true,
        };
        _players[p.PlayerId] = p;
        if (isHost) HostPlayerId = p.PlayerId;
        return p;
    }

    // A player leaving frees whatever seat it held. Distinct from MarkDisconnected, which keeps the
    // player around until the match ends.
    public void RemovePlayer(int playerId)
    {
        if (!_players.TryGetValue(playerId, out var p)) return;
        ReleaseSeatOf(playerId);
        _players.Remove(playerId);
        if (HostPlayerId == playerId) HostPlayerId = 0;
    }

    // Mid-match drop. The seat is NOT freed and the player is NOT removed: their inputs are treated
    // as neutral for the rest of the round, and the kick happens when the match ends. Freeing the
    // seat mid-round would change what the other side is simulating against.
    public void MarkDisconnected(int playerId)
    {
        if (_players.TryGetValue(playerId, out var p)) p.Connected = false;
    }

    public IEnumerable<int> DisconnectedPlayerIds =>
        _players.Values.Where(p => !p.Connected).Select(p => p.PlayerId).ToArray();

    public PlayerInfo Find(int playerId) => _players.TryGetValue(playerId, out var p) ? p : null;

    // ---- seats ----

    // First claim wins. Returns false when refused, so the caller knows not to broadcast.
    public bool ClaimSeat(int playerId, int seat)
    {
        if (MatchRunning) return false;
        if (seat < 0 || seat >= SeatCount) return false;
        if (!_players.TryGetValue(playerId, out var p)) return false;
        if (_seats[seat].Occupied) return false;       // taken, including by an AI
        if (p.Seat == seat) return false;              // already there; nothing changed

        // one seat per player: taking a second gives up the first
        if (p.Seat >= 0) ClearSeat(p.Seat);

        _seats[seat] = new SeatInfo { OccupantPlayerId = playerId, Character = -1 };
        p.Seat = seat;
        return true;
    }

    // A player may only release its OWN seat.
    public bool ReleaseSeat(int playerId)
    {
        if (MatchRunning) return false;
        if (!_players.TryGetValue(playerId, out var p) || p.Seat < 0) return false;
        ClearSeat(p.Seat);
        p.Seat = -1;
        return true;
    }

    public bool PickCharacter(int playerId, int character)
    {
        if (MatchRunning) return false;
        if (character < 0) return false;
        if (!_players.TryGetValue(playerId, out var p) || p.Seat < 0) return false;
        if (_seats[p.Seat].OccupantPlayerId != playerId) return false;
        _seats[p.Seat].Character = character;
        return true;
    }

    // HOST ONLY, and the host may fill either seat regardless of what it holds itself. The AI runs on
    // the host's machine and its inputs enter the match as if the host had sent them, which is why it
    // needs no player id and consumes no player slot.
    public bool AddAi(int requesterId, int seat, int character, string aiModel)
    {
        if (MatchRunning) return false;
        if (seat < 0 || seat >= SeatCount) return false;
        if (requesterId != HostPlayerId || HostPlayerId == 0) return false;
        if (_seats[seat].Occupied) return false;
        if (character < 0) return false;

        _seats[seat] = new SeatInfo
        {
            OccupantPlayerId = 0,
            Character = character,
            IsAi = true,
            AiModel = aiModel ?? "",
        };
        return true;
    }

    private void ClearSeat(int seat)
    {
        int occupant = _seats[seat].OccupantPlayerId;
        _seats[seat] = new SeatInfo();
        if (occupant != 0 && _players.TryGetValue(occupant, out var p) && p.Seat == seat) p.Seat = -1;
    }

    private void ReleaseSeatOf(int playerId)
    {
        for (int i = 0; i < SeatCount; i++)
            if (_seats[i].OccupantPlayerId == playerId) ClearSeat(i);
    }

    // ---- match lifecycle ----

    public bool BeginMatch()
    {
        if (!CanStart) return false;
        MatchRunning = true;
        return true;
    }

    // After a knockout: drop anyone who disconnected mid-match, then clear every seat so the room
    // picks again from scratch (spec: "每局游戏结束后都返回到占座选人界面，清空选人界面状态").
    public int[] EndMatch()
    {
        MatchRunning = false;
        var dropped = DisconnectedPlayerIds.ToArray();
        foreach (int id in dropped) RemovePlayer(id);
        for (int i = 0; i < SeatCount; i++) ClearSeat(i);
        foreach (var p in _players.Values) p.Seat = -1;
        return dropped;
    }

    // ---- snapshot ----

    public RoomSnapshot Snapshot() => new RoomSnapshot
    {
        Players = _players.Values
            .OrderBy(p => p.PlayerId)          // stable order so clients render a stable list
            .Select(p => new PlayerInfo
            {
                PlayerId = p.PlayerId, Name = p.Name, IsHost = p.IsHost,
                Seat = p.Seat, Connected = p.Connected,
            }).ToArray(),
        Seats = _seats.Select(s => new SeatInfo
        {
            OccupantPlayerId = s.OccupantPlayerId, Character = s.Character,
            IsAi = s.IsAi, AiModel = s.AiModel,
        }).ToArray(),
        RoomId = RoomId,
        MaxPlayers = MaxPlayers,
        MatchRunning = MatchRunning,
    };

    // Display text only, never an identity. Same rule as the replay header: strip control characters
    // (they would corrupt a line-based log or header) and bound to 18 UTF-8 bytes without splitting a
    // multi-byte character.
    public static string SanitizeName(string name, int maxBytes = 18)
    {
        if (string.IsNullOrEmpty(name)) return "玩家";
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
            if (!char.IsControl(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        while (System.Text.Encoding.UTF8.GetByteCount(s) > maxBytes && s.Length > 0)
            s = s.Substring(0, s.Length - 1);
        return s.Length == 0 ? "玩家" : s;
    }
}
