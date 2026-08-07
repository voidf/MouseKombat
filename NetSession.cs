using Godot;
using System;
using MouseKombat.Net;
using MouseKombat.Sim;

// Owns the network connection for as long as the player is in a networked game, and polls it.
//
// An autoload because the connection has to OUTLIVE scene changes: the LAN menu creates it, the seat
// screen uses it, the match uses it, and returning to the seat screen after a knockout must not
// reconnect. Screens subscribe to the events below in _Ready and unsubscribe in _ExitTree.
//
// Both transports are polled from _PhysicsProcess, which is also the tick the match simulation runs
// on — one clock for everything (see the note on Player.TickAnimation).
//
// The host/client asymmetry is hidden behind the Request* methods on purpose. The host is
// authoritative and has no socket to itself, so its own seat clicks mutate RoomState directly, while a
// client's go over the wire and come back as a snapshot. Screens should never care which they are.
public partial class NetSession : Node
{
    public static NetSession Instance { get; private set; }

    // Raised on the main thread from _PhysicsProcess; safe to touch scene nodes from.
    public event Action RoomChanged;
    public event Action<string> Disconnected;     // reason, for the popup
    public event Action<StartMatch> MatchStarting;
    public event Action<MatchEnded> MatchEnded;

    public TcpRoomHost Host { get; private set; }
    public TcpRoomClient Client { get; private set; }

    public bool IsHost => Host != null;
    public bool Active => Host != null || Client != null;
    public string PlayerName { get; private set; } = "玩家";
    public string Mode { get; private set; } = ReplayData.ModeLan;
    public string HostAddress { get; private set; } = "";
    public int Port { get; private set; }

    // Which player WE are. For a host this is its own RoomState entry; for a client it comes from
    // Welcome.
    public int LocalPlayerId => IsHost ? Host.HostPlayerId : (Client?.PlayerId ?? 0);

    public RoomSnapshot Room { get; private set; }

    public static string GameVersion =>
        (string)ProjectSettings.GetSetting("application/config/version", "");

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;   // keep polling even if the game is paused
    }

    // ---- lifecycle ----

    public bool StartHosting(string bindAddress, int port, string playerName, string mode)
    {
        Leave(null);
        PlayerName = RoomState.SanitizeName(playerName);
        Mode = mode;
        HostAddress = bindAddress;
        try
        {
            Host = new TcpRoomHost();
            Host.Start(bindAddress, port, PlayerName, GameVersion);
            Port = Host.Port;
            Room = Host.Room.Snapshot();
            RoomChanged?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] hosting failed: {e.Message}");
            Host = null;
            Disconnected?.Invoke($"无法在 {bindAddress}:{port} 建立房间：{e.Message}");
            return false;
        }
    }

    public void JoinRoom(string host, int port, string playerName, string mode, string password = "")
    {
        Leave(null);
        PlayerName = RoomState.SanitizeName(playerName);
        Mode = mode;
        HostAddress = host;
        Port = port;
        Client = new TcpRoomClient();
        Client.Connect(host, port, PlayerName, GameVersion, password);
    }

    // reason != null tells the other side why (the host broadcasts Bye, a client sends one).
    public void Leave(string reason)
    {
        Host?.Stop(reason ?? "主机已关闭房间");
        Host?.Dispose();
        Host = null;
        Client?.Disconnect(reason);
        Client?.Dispose();
        Client = null;
        Room = null;
    }

    // ---- requests: one call site for screens, whichever side we are ----

    public void RequestClaimSeat(int seat)
    {
        if (IsHost) { if (Host.Room.ClaimSeat(Host.HostPlayerId, seat)) HostChanged(); }
        else Client?.ClaimSeat(seat);
    }

    public void RequestReleaseSeat()
    {
        if (IsHost) { if (Host.Room.ReleaseSeat(Host.HostPlayerId)) HostChanged(); }
        else Client?.ReleaseSeat();
    }

    public void RequestPickCharacter(CharacterId c)
    {
        if (IsHost) { if (Host.Room.PickCharacter(Host.HostPlayerId, (int)c)) HostChanged(); }
        else Client?.PickCharacter((int)c);
    }

    // Host-only by protocol. A client calling this is refused by the host, so the screen simply does
    // not offer it — but the guard stays because "the UI hides it" is not a rule.
    public void RequestAddAi(int seat, CharacterId c, string model)
    {
        if (!IsHost) return;
        if (Host.Room.AddAi(Host.HostPlayerId, seat, (int)c, model)) HostChanged();
    }

    public void RequestStartMatch(StartMatch setup)
    {
        if (!IsHost) return;
        if (!Host.Room.BeginMatch()) return;
        setup.Room = Host.Room.Snapshot();
        Host.Broadcast(MsgType.StartMatch, setup);
        HostChanged();
        MatchStarting?.Invoke(setup);
    }

    // After a knockout: kick whoever dropped mid-match, clear the seats, tell everyone.
    public void RequestEndMatch(int winnerSeat)
    {
        if (!IsHost) return;
        int[] dropped = Host.Room.EndMatch();
        foreach (int id in dropped) Host.Kick(id, "本局结束，已断线");
        var msg = new MatchEnded { WinnerSeat = winnerSeat, DroppedPlayerIds = dropped };
        Host.Broadcast(MsgType.MatchEnded, msg);
        HostChanged();
        MatchEnded?.Invoke(msg);
    }

    private void HostChanged()
    {
        Host.BroadcastRoom();
        Room = Host.Room.Snapshot();
        RoomChanged?.Invoke();
    }

    // ---- polling ----

    public override void _PhysicsProcess(double delta)
    {
        if (Host != null) PollHost();
        if (Client != null) PollClient();
    }

    private void PollHost()
    {
        Host.Poll();
        bool changed = false;
        while (Host.TryDequeueEvent(out var e))
            if (e.Kind is TcpRoomHost.EventKind.RoomChanged
                       or TcpRoomHost.EventKind.PlayerJoined
                       or TcpRoomHost.EventKind.PlayerLeft) changed = true;
        if (!changed) return;
        Room = Host.Room.Snapshot();
        RoomChanged?.Invoke();
    }

    private void PollClient()
    {
        Client.Poll();
        while (Client.TryDequeueEvent(out var e))
        {
            switch (e.Kind)
            {
                case TcpRoomClient.EventKind.Connected:
                case TcpRoomClient.EventKind.RoomChanged:
                    Room = Client.Room;
                    RoomChanged?.Invoke();
                    break;
                case TcpRoomClient.EventKind.MatchStarting:
                    MatchStarting?.Invoke(e.Frame.As<StartMatch>());
                    break;
                case TcpRoomClient.EventKind.MatchEnded:
                    MatchEnded?.Invoke(e.Frame.As<MatchEnded>());
                    break;
                case TcpRoomClient.EventKind.Rejected:
                case TcpRoomClient.EventKind.Disconnected:
                {
                    string why = e.Detail ?? Client.LastError ?? "连接已断开";
                    Client.Dispose();
                    Client = null;
                    Room = null;
                    Disconnected?.Invoke(why);
                    return;
                }
            }
        }
    }

    public override void _ExitTree()
    {
        Leave(null);
        if (Instance == this) Instance = null;
    }

    // ---- helpers the screens share ----

    public SeatInfo Seat(int i) =>
        Room != null && i >= 0 && i < Room.Seats.Length ? Room.Seats[i] : new SeatInfo();

    // Which seat WE hold, or -1.
    public int LocalSeat()
    {
        if (Room == null) return -1;
        int me = LocalPlayerId;
        for (int i = 0; i < Room.Seats.Length; i++)
            if (Room.Seats[i].OccupantPlayerId == me) return i;
        return -1;
    }

    public bool BothSeatsReady()
    {
        if (Room == null) return false;
        foreach (var s in Room.Seats) if (!s.Ready) return false;
        return true;
    }
}
