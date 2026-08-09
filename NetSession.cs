using Godot;
using System;
using System.Collections.Generic;
using System.Net;
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
    // Host only: someone joined the room. The host's match director listens and hands a mid-match
    // joiner the catch-up package (see MatchCatchUp / SpectateScreen).
    public event Action<int> PlayerJoined;
    // Client only: the host answered a mid-match join. The seat screen switches to the spectate
    // screen on CatchUpReceived; InputsReceived keeps that screen's sim advancing. Everything is also
    // buffered in PendingCatchUp / PendingStreamInputs, so messages that arrive while the scene is
    // changing are not lost — the spectate screen drains the buffers in _Ready.
    public event Action<MatchCatchUp> CatchUpReceived;
    public event Action<MatchInputs> InputsReceived;

    public TcpRoomHost Host { get; private set; }
    public TcpRoomClient Client { get; private set; }

    // ---- match channel ----
    //
    // UDP for the rollback session, on the SAME port number as the room's TCP port (PROTOCOL.md
    // § Transports: the two port spaces are independent).
    //
    // Only a CLIENT holds a socket here. Its port is ephemeral and has to be announced in Hello, so it
    // is bound once for the whole room — a room plays many matches, and disposing a Backdash session
    // closes whatever socket it was handed, so re-binding the same ephemeral port between matches would
    // be a race with every other process on the machine (see MatchSocket).
    //
    // The HOST needs none of that: its match port is the room's TCP port, which the client already knows
    // and which nothing else on the machine wants, so it is bound per match — by Backdash when the host
    // fights, by UdpMatchRelay when it only forwards. Those two cannot both own it, which is the real
    // reason it is not held open in between.
    public MatchSocket MatchSocket { get; private set; }
    public int MatchUdpPort => IsHost ? Port : (MatchSocket?.Port ?? 0);

    // Set while a match is running with two client fighters and no seat for us: we forward their UDP
    // and run no session at all.
    public UdpMatchRelay Relay { get; private set; }

    // The plan for the match currently starting / running, or null.
    public MatchPlan Plan { get; private set; }

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

    // ---- mid-match spectating (see MatchCatchUp) ----
    // Set on the client when the host answers a mid-match join. PendingStreamInputs holds every
    // MatchInputs batch received since, including ones that arrive while the spectate screen is being
    // loaded; the screen drains it in _Ready and then follows InputsReceived live.
    public MatchCatchUp PendingCatchUp { get; private set; }
    public readonly List<MatchInputs> PendingStreamInputs = new();

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
        // Fully qualified: the property below is also called MatchSocket, and reading
        // `MatchSocket.BindFree()` as a static call on the TYPE takes a second look otherwise.
        try { MatchSocket = MouseKombat.Net.MatchSocket.BindFree(); }
        catch (Exception e)
        {
            // No match port means the host would have nothing to send inputs to, so this is fatal to
            // joining — say so now rather than at "开始对战".
            GD.PushWarning($"[net] match socket bind failed: {e.Message}");
            Client = null;
            Disconnected?.Invoke($"无法绑定对局用 UDP 端口：{e.Message}");
            return;
        }
        Client.MatchUdpPort = MatchSocket.Port;
        Client.Connect(host, port, PlayerName, GameVersion, password);
    }

    // reason != null tells the other side why (the host broadcasts Bye, a client sends one).
    public void Leave(string reason)
    {
        EndMatchLocal();
        Host?.Stop(reason ?? "主机已关闭房间");
        Host?.Dispose();
        Host = null;
        Client?.Disconnect(reason);
        Client?.Dispose();
        Client = null;
        // CloseNow, not Dispose: Dispose is a no-op on MatchSocket (Backdash calls it when a match
        // session ends, and the socket must survive for the next match of the room). The room is
        // ending here, which is the one moment the port is really released.
        MatchSocket?.CloseNow();
        MatchSocket = null;
        Room = null;
        PendingCatchUp = null;
        PendingStreamInputs.Clear();
        _lockedDevices.Clear();
    }

    // Drops whatever the CURRENT match set up, keeping the room. Called when a match ends and when
    // leaving; the match socket survives both (see MatchSocket).
    public void EndMatchLocal()
    {
        Relay?.Dispose();
        Relay = null;
        Plan = null;
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

    // Host-only, the mirror of RequestAddAi: frees an AI seat (Backspace in the seat screen).
    public void RequestRemoveAi(int seat)
    {
        if (!IsHost) return;
        if (Host.Room.RemoveAi(Host.HostPlayerId, seat)) HostChanged();
    }

    public void RequestStartMatch(StartMatch setup)
    {
        if (!IsHost) return;
        if (!Host.Room.BeginMatch()) return;
        setup.Room = Host.Room.Snapshot();
        setup.MatchUdpPort = MatchUdpPort;
        // Told rather than re-derived: a client should not have to reimplement "the host only runs a
        // session when it drives a seat" to know whether it can watch.
        setup.SpectatingAvailable =
            MatchPlan.HostDrivesASeat(setup.Room, MatchPlan.HostIdOf(setup.Room));
        Host.Broadcast(MsgType.StartMatch, setup);
        HostChanged();
        BeginMatch(setup);
    }

    // Both sides land here — the host from RequestStartMatch, a client from the StartMatch message —
    // so the role decision is made by ONE piece of code (MatchPlan) from the same snapshot.
    private void BeginMatch(StartMatch setup)
    {
        EndMatchLocal();
        Plan = MatchPlan.Build(setup.Room, LocalPlayerId, IsHost,
                               HostMatchEndPoint(setup), ClientMatchEndPoint);

        if (Plan.Role == MatchRole.Relay)
        {
            try { Relay = new UdpMatchRelay(Port, Plan.RelayA, Plan.RelayB); }
            catch (Exception e)
            {
                GD.PushWarning($"[net] relay bind failed on {Port}: {e.Message}");
                Plan.Role = MatchRole.Idle;
                Plan.Problem = $"无法在 UDP {Port} 上中转对局：{e.Message}";
            }
        }
        MatchStarting?.Invoke(setup);
    }

    // Where the host's match traffic goes, from THIS machine's point of view. Null on the host itself
    // (it does not dial itself). The address is the one the TCP connection actually resolved to, so a
    // hostname with several A records cannot send room traffic one way and match traffic another.
    private IPEndPoint HostMatchEndPoint(StartMatch setup)
    {
        if (IsHost) return null;
        var addr = Client?.ConnectedAddress;
        if (addr == null || setup.MatchUdpPort <= 0) return null;
        return new IPEndPoint(addr, setup.MatchUdpPort);
    }

    // Host only: the endpoint a given client announced at handshake.
    private IPEndPoint ClientMatchEndPoint(int playerId) => Host?.MatchEndPointOf(playerId);

    // What the host's own role WOULD be if the match started right now. Used to refuse "开始对战" with
    // the actual reason instead of starting a match that cannot synchronize — e.g. a client that never
    // announced a match port, which no amount of waiting fixes.
    public MatchPlan PreviewPlan()
    {
        if (!IsHost || Room == null) return null;
        return MatchPlan.Build(Room, LocalPlayerId, true, null, ClientMatchEndPoint);
    }

    // A fighter that is NOT the host telling the host the match is over. The host cannot always see the
    // knockout itself (relay configuration), and the room state may only be changed by the host.
    public void ReportMatchResult(int winnerSeat)
    {
        if (IsHost) return;
        Client?.ReportMatchResult(winnerSeat);
    }

    // After a knockout: kick whoever dropped mid-match, clear the seats, tell everyone.
    public void RequestEndMatch(int winnerSeat)
    {
        if (!IsHost) return;
        if (!Host.Room.MatchRunning) return;   // already ended (a second fighter's report)
        int[] dropped = Host.Room.EndMatch();
        foreach (int id in dropped) Host.Kick(id, "本局结束，已断线");
        var msg = new MatchEnded { WinnerSeat = winnerSeat, DroppedPlayerIds = dropped };
        Host.Broadcast(MsgType.MatchEnded, msg);
        EndMatchLocal();
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
        // Forwarding runs on the game tick like every other transport here. The host is not simulating
        // in this configuration, so this is all it does for the match.
        Relay?.Poll();
    }

    private void PollHost()
    {
        Host.Poll();
        bool changed = false;
        int reportedWinner = int.MinValue;
        while (Host.TryDequeueEvent(out var e))
        {
            if (e.Kind is TcpRoomHost.EventKind.RoomChanged
                       or TcpRoomHost.EventKind.PlayerLeft) changed = true;
            else if (e.Kind == TcpRoomHost.EventKind.PlayerJoined)
            {
                changed = true;
                PlayerJoined?.Invoke(e.PlayerId);
            }
            else if (e.Kind == TcpRoomHost.EventKind.MatchResult) reportedWinner = e.Value;
        }

        // A fighter reached the knockout. This is how the match ends when the host is only relaying and
        // has no simulation of its own; when the host IS fighting it has usually ended the match already,
        // and RequestEndMatch is a no-op once MatchRunning is false.
        if (reportedWinner != int.MinValue && Host.Room.MatchRunning)
        {
            RequestEndMatch(reportedWinner);
            return;
        }
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
                    BeginMatch(e.Frame.As<StartMatch>());
                    break;
                case TcpRoomClient.EventKind.MatchEnded:
                    EndMatchLocal();
                    PendingCatchUp = null;
                    PendingStreamInputs.Clear();
                    MatchEnded?.Invoke(e.Frame.As<MatchEnded>());
                    break;
                case TcpRoomClient.EventKind.MatchCatchUp:
                    PendingCatchUp = e.Frame.As<MatchCatchUp>();
                    CatchUpReceived?.Invoke(PendingCatchUp);
                    break;
                case TcpRoomClient.EventKind.MatchInputs:
                {
                    var m = e.Frame.As<MatchInputs>();
                    PendingStreamInputs.Add(m);
                    InputsReceived?.Invoke(m);
                    break;
                }
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

    // ---- per-window device locking ----
    //
    // A gamepad is OS-global: every window on the machine reads every pad. Two game instances side by
    // side would both react to the same confirm press, so the seat screen lets a pad claim OUR seat
    // only while its window is focused, and once claimed the pad stays LOCKED to this instance —
    // it drives this instance's panels and (from 期3-4 4/4 on) this instance's in-match inputs until
    // the player releases the seat. Keyboards need none of this: the OS already delivers each key to
    // exactly one window.
    private readonly Dictionary<int, IInputSource> _lockedDevices = new();

    // Which device this window locked to a seat, or null. Keyed by seat, not by device: the seat is
    // what the in-match code will look up.
    public IInputSource LockedDevice(int seat) =>
        _lockedDevices.TryGetValue(seat, out var s) ? s : null;

    public void LockDevice(int seat, IInputSource src) => _lockedDevices[seat] = src;
    public void UnlockDevice(int seat) => _lockedDevices.Remove(seat);
}
