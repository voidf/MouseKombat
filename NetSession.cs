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
    // CatchUpReceived: client only — the host answered a mid-match join; the seat screen switches to
    // the spectate screen. InputsReceived: BOTH sides — a spectate screen's sim advancing stream. On
    // a client it is the host's MatchInputs batches; on a relay host it is the fighters' reports
    // (merged in PollHost). Everything is also buffered in PendingCatchUp / PendingStreamInputs, so
    // messages that arrive while the scene is changing are not lost — the spectate screen drains the
    // buffers in _Ready.
    public event Action<MatchCatchUp> CatchUpReceived;
    public event Action<MatchInputs> InputsReceived;

    public TcpRoomHost Host { get; private set; }
    public TcpRoomClient Client { get; private set; }

    // ---- lobby mode ----
    // One connection to the lobby server (LobbyRoomClient), which covers BOTH the browse phase
    // (room list / create / join, driven by the lobby menu) and the room phase (this machine is a
    // member; the server is the room authority). The room creator is the "host player": it holds
    // the host-only rights (AI seats, match start, catch-up serving) but is still a client of the
    // server. See PROTOCOL.md § Lobby.
    public LobbyRoomClient Lobby { get; private set; }
    public bool IsLobby => Lobby != null;
    public bool IsLobbyHostPlayer => Lobby != null && Lobby.IsHostPlayer;

    // The lobby socket wrapper for the CURRENT match, or null (a lobby match with every seat
    // driven locally has no UDP traffic at all). Created per match in BeginMatch, wraps MatchSocket
    // with the {roomId, mySeat, otherSeat} envelope (see LobbyMatchSocket).
    public LobbyMatchSocket LobbySocket { get; private set; }

    // Raised when the lobby server answers a LobbyList page (browse phase; the lobby menu renders
    // it). Rejected: a lobby-phase refusal that leaves the connection usable (wrong password, full
    // room, create-form errors) — the menu shows the reason and stays put.
    public event Action<LobbyRooms> LobbyRoomsReceived;
    public event Action<string> LobbyRejected;

    // The lobby ROOM ended under us (its host player left, or we were kicked) while the lobby
    // CONNECTION stayed alive. Every screen that shows a room listens: it tells the player why and
    // returns to the room browser, which still has a live connection to page (PROTOCOL.md § Lobby).
    // Never raised for a real drop — that is Disconnected.
    public event Action<string> LobbyRoomClosed;

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

    public bool IsHost => Host != null || IsLobbyHostPlayer;
    public bool Active => Host != null || Client != null || Lobby != null;
    public string PlayerName { get; private set; } = "玩家";
    public string Mode { get; private set; } = ReplayData.ModeLan;
    public string HostAddress { get; private set; } = "";
    public int Port { get; private set; }

    // Which player WE are. For a LAN host this is its own RoomState entry; for a client it comes
    // from Welcome; for a lobby member it is the server-assigned id (the host PLAYER is a member too).
    public int LocalPlayerId =>
        Host != null ? Host.HostPlayerId
        : (Lobby != null ? Lobby.PlayerId
        : (Client?.PlayerId ?? 0));

    public RoomSnapshot Room { get; private set; }

    // ---- mid-match spectating (see MatchCatchUp) ----
    // Set on the client when the host answers a mid-match join. PendingStreamInputs holds every
    // MatchInputs batch received since, including ones that arrive while the spectate screen is being
    // loaded; the screen drains it in _Ready and then follows InputsReceived live.
    public MatchCatchUp PendingCatchUp { get; private set; }
    public readonly List<MatchInputs> PendingStreamInputs = new();

    // The buffer is a HAND-OFF, not a record of the match: once a screen has turned the package into
    // a spectate view it must go, or the next screen that checks it (the seat screen, when the
    // spectator comes back mid-match) replays the SAME package from frame 0 and then rejects every
    // live batch as a stream gap — the "ESC 回到本场战斗的第一帧然后卡住" freeze.
    public void ClearPendingCatchUp() => PendingCatchUp = null;

    // ---- relay-config spectating: the host's catch-up authority ----
    //
    // When both fighters are clients the host runs NO session and has no simulation of its own, so it
    // can neither watch nor serve mid-match joiners — the fighters are the only machines that know the
    // inputs. They report every CONFIRMED frame over TCP (MatchInputReport), which this buffer merges.
    // The relay host's own seat screen then enters the spectate screen (it replays the same way a
    // joiner does), and joiners who arrive mid-match are served from here too. Both consumers follow
    // the InputsReceived event.
    //
    // Only meaningful while Relay != null. In host-session configurations the host's GameManager is
    // the catch-up authority instead, and this buffer stays empty.
    public readonly ReplayData CatchUpHistory = new();
    public float CatchUpStageMinX = 40f, CatchUpStageMaxX = 760f, CatchUpWorldWidth = 800f;
    public float CatchUpP1StartX = 120f, CatchUpP1StartY = 560f;
    public float CatchUpP2StartX = 650f, CatchUpP2StartY = 560f;
    // True once the first report landed (it carries the geometry). Until then the relay host cannot
    // build a sim, so its seat screen waits.
    public bool CatchUpReady { get; private set; }
    private readonly Dictionary<int, int> _relaySpectatorNextFrame = new();  // joiner id -> next frame

    public static string GameVersion =>
        (string)ProjectSettings.GetSetting("application/config/version", "");

    // md5 over Heroes/ + FireballTSCN/ + ParticleTSCN/ (HeroLibrary). Empty when the library
    // has not scanned — tests and headless runs — which turns every gate into a no-op.
    public static string AssetHash => HeroLibrary.Instance?.AssetHash ?? "";

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
            Host.Start(bindAddress, port, PlayerName, GameVersion, AssetHash);
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
        Client.Connect(host, port, PlayerName, GameVersion, password, AssetHash);
    }

    // Connects to the lobby server for BROWSING. The lobby menu then pages through the room list
    // and creates or joins a room on this same connection. Like JoinRoom, the match socket is bound
    // up front: every lobby machine is a client of the server, so every one announces a port.
    public void ConnectLobby(string host, int port, string playerName)
    {
        Leave(null);
        PlayerName = RoomState.SanitizeName(playerName);
        Mode = ReplayData.ModeLobby;
        HostAddress = host;
        Port = port;
        try { MatchSocket = MouseKombat.Net.MatchSocket.BindFree(); }
        catch (Exception e)
        {
            GD.PushWarning($"[net] match socket bind failed: {e.Message}");
            Disconnected?.Invoke($"无法绑定对局用 UDP 端口：{e.Message}");
            return;
        }
        Lobby = new LobbyRoomClient { MatchUdpPort = MatchSocket.Port };
        Lobby.Connect(host, port, PlayerName, GameVersion, AssetHash);
    }

    // ---- lobby browse phase (the lobby menu) ----
    public void RequestLobbyList(int page) => Lobby?.ListRooms(page);
    public void RequestLobbyCreate(int maxPlayers, string password, bool searchable) =>
        Lobby?.CreateRoom(maxPlayers, password, searchable);
    public void RequestLobbyJoin(string roomId, string password) =>
        Lobby?.JoinRoom(roomId, password);

    // Leave a lobby room but KEEP the lobby connection: the server returns it to the browse phase,
    // so the player lands back in the room browser without reconnecting (spec: ESC 退出房间后回到
    // 选房界面). The host PLAYER still destroys the room and drops everyone — use Leave() for that.
    public void LeaveLobbyRoom(string reason)
    {
        EndMatchLocal();
        Lobby?.LeaveRoom(reason ?? "玩家离开了房间");
        Room = null;
        PendingCatchUp = null;
        PendingStreamInputs.Clear();
        _lockedDevices.Clear();
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
        Lobby?.Disconnect(reason);
        Lobby?.Dispose();
        Lobby = null;
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
        LobbySocket = null;
        Plan = null;
        // Everything match-scoped goes with it. The relay catch-up buffer is refilled by the next
        // match's fighters; PendingStreamInputs is a SHARED buffer (a client's stream batches, a
        // relay host's merged reports) and a stale batch from a finished match would replay the
        // wrong fight — the next spectate screen drains it believing it belongs to its match.
        CatchUpHistory.P1Inputs.Clear();
        CatchUpHistory.P2Inputs.Clear();
        CatchUpReady = false;
        _relaySpectatorNextFrame.Clear();
        PendingCatchUp = null;
        if (PendingStreamInputs.Count > 0)
        {
            // A shared buffer (a client's stream batches, a relay host's merged reports) that only
            // the match's spectate screen drains. Leftovers here would replay the wrong fight.
            GD.Print($"[net] dropped {PendingStreamInputs.Count} stale catch-up stream batch(es) at match end");
            PendingStreamInputs.Clear();
        }
    }

    // ---- requests: one call site for screens, whichever side we are ----

    public void RequestClaimSeat(int seat)
    {
        if (Lobby != null) { Lobby.ClaimSeat(seat); return; }
        if (IsHost) { if (Host.Room.ClaimSeat(Host.HostPlayerId, seat)) HostChanged(); }
        else Client?.ClaimSeat(seat);
    }

    public void RequestReleaseSeat()
    {
        if (Lobby != null) { Lobby.ReleaseSeat(); return; }
        if (IsHost) { if (Host.Room.ReleaseSeat(Host.HostPlayerId)) HostChanged(); }
        else Client?.ReleaseSeat();
    }

    public void RequestPickCharacter(CharacterId c)
    {
        if (Lobby != null) { Lobby.PickCharacter((int)c); return; }
        if (IsHost) { if (Host.Room.PickCharacter(Host.HostPlayerId, (int)c)) HostChanged(); }
        else Client?.PickCharacter((int)c);
    }

    // Host-only by protocol. A client calling this is refused by the host, so the screen simply does
    // not offer it — but the guard stays because "the UI hides it" is not a rule.
    public void RequestAddAi(int seat, CharacterId c, string model)
    {
        if (!IsHost) return;
        if (Lobby != null) { Lobby.AddAi(seat, (int)c, model); return; }
        if (Host.Room.AddAi(Host.HostPlayerId, seat, (int)c, model)) HostChanged();
    }

    // Host-only, the mirror of RequestAddAi: frees an AI seat (Backspace in the seat screen).
    public void RequestRemoveAi(int seat)
    {
        if (!IsHost) return;
        if (Lobby != null) { Lobby.RemoveAi(seat); return; }
        if (Host.Room.RemoveAi(Host.HostPlayerId, seat)) HostChanged();
    }

    public void RequestStartMatch(StartMatch setup)
    {
        if (!IsHost) return;
        if (Lobby != null)
        {
            // The server decides CanStart (and refuses when a fighter never announced a match
            // port) and answers with the standard StartMatch broadcast.
            Lobby.RequestMatchStart(new MatchStart
            {
                StageMinX = setup.StageMinX,
                StageMaxX = setup.StageMaxX,
                WorldWidth = setup.WorldWidth,
                P1StartX = setup.P1StartX,
                P1StartY = setup.P1StartY,
                P2StartX = setup.P2StartX,
                P2StartY = setup.P2StartY,
            });
            return;
        }
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
                               HostMatchEndPoint(setup), ClientMatchEndPoint, lobby: IsLobby);

        if (Plan.Role == MatchRole.Relay && !IsLobby)
        {
            try { Relay = new UdpMatchRelay(Port, Plan.RelayA, Plan.RelayB); }
            catch (Exception e)
            {
                GD.PushWarning($"[net] relay bind failed on {Port}: {e.Message}");
                Plan.Role = MatchRole.Idle;
                Plan.Problem = $"无法在 UDP {Port} 上中转对局：{e.Message}";
            }
        }

        // In a lobby the fighters' UDP goes through the SERVER, so the session's socket has to wrap
        // every datagram in the {roomId, mySeat, otherSeat} envelope. Exactly one local seat means
        // the other seat is a remote human fighter (an AI seat would be driven locally too, and then
        // there is no UDP at all). The envelope never changes over the match.
        if (IsLobby && Plan.Role == MatchRole.Fighter && Plan.RemoteEndPoint != null)
        {
            int mySeat = -1;
            for (int i = 0; i < RoomState.SeatCount; i++)
                if (Plan.LocalSeat[i]) { mySeat = i; break; }
            if (mySeat >= 0 && MatchSocket != null && Room != null
                && int.TryParse(Room.RoomId, out int roomIdNum))
            {
                LobbySocket = new LobbyMatchSocket(MatchSocket, roomIdNum, mySeat, 1 - mySeat);
            }
        }
        // Belt and braces for the one path a lobby fighter has no hub endpoint: a StartMatch that
        // failed to carry the server's match port. Nothing to dial and no envelope — refusing here
        // beats letting Backdash bind an arbitrary port that nobody is listening on.
        if (IsLobby && Plan.Role == MatchRole.Fighter && Plan.RemoteEndPoint == null && !Plan.DrivesAnySeat)
        {
            Plan.Role = MatchRole.Idle;
            Plan.Problem = "服务器未提供对局端口";
        }
        MatchStarting?.Invoke(setup);
    }

    // Where the hub's match traffic goes, from THIS machine's point of view. Null on a LAN host (it
    // does not dial itself). The address is the one the TCP connection actually resolved to, so a
    // hostname with several A records cannot send room traffic one way and match traffic another.
    private IPEndPoint HostMatchEndPoint(StartMatch setup)
    {
        if (IsHost && !IsLobby) return null;
        var addr = Client?.ConnectedAddress ?? Lobby?.ConnectedAddress;
        if (addr == null || setup.MatchUdpPort <= 0) return null;
        return new IPEndPoint(addr, setup.MatchUdpPort);
    }

    // Host only: the endpoint a given client announced at handshake.
    private IPEndPoint ClientMatchEndPoint(int playerId) => Host?.MatchEndPointOf(playerId);

    // What the host's own role WOULD be if the match started right now. Used to refuse "开始对战" with
    // the actual reason instead of starting a match that cannot synchronize — e.g. a client that never
    // announced a match port, which no amount of waiting fixes. In a lobby the SERVER owns that
    // refusal (it knows every announced port), so the preview just reports the role from the server's
    // snapshot without any endpoint checks.
    public MatchPlan PreviewPlan()
    {
        if (!IsHost || Room == null) return null;
        return MatchPlan.Build(Room, LocalPlayerId, true, null, ClientMatchEndPoint, lobby: IsLobby);
    }

    // A fighter that is NOT the host telling the host the match is over. The host cannot always see the
    // knockout itself (relay configuration), and the room state may only be changed by the host.
    public void ReportMatchResult(int winnerSeat)
    {
        if (Lobby != null) { Lobby.ReportMatchResult(winnerSeat); return; }
        if (IsHost) return;
        Client?.ReportMatchResult(winnerSeat);
    }

    // After a knockout: kick whoever dropped mid-match, clear the seats, tell everyone.
    // In a lobby the SERVER owns the room state: everyone (including the host player) reports the
    // knockout with MatchResult, and the server broadcasts MatchEnded + a cleared snapshot back.
    public void RequestEndMatch(int winnerSeat)
    {
        if (!IsHost) return;
        if (Lobby != null) { Lobby.ReportMatchResult(winnerSeat); return; }
        if (!Host.Room.MatchRunning) return;   // already ended (a second fighter's report)
        int[] dropped = Host.Room.EndMatch();
        foreach (int id in dropped) Host.Kick(id, "本局结束，已断线");
        var msg = new MatchEnded { WinnerSeat = winnerSeat, DroppedPlayerIds = dropped };
        Host.Broadcast(MsgType.MatchEnded, msg);
        EndMatchLocal();
        HostChanged();
        MatchEnded?.Invoke(msg);
    }

    // Route one catch-up frame to a room member: LAN sends it through the in-process host's socket,
    // a lobby through the server (HostSendTo). The lobby body must stay the RAW msgpack body, so the
    // frame is encoded here and only the body travels in HostSendTo.
    public void SendTo(int playerId, MsgType type, object payload)
    {
        if (Lobby != null)
        {
            byte[] frame = NetCodec.Encode(type, payload);
            var body = new byte[frame.Length - NetCodec.HeaderBytes];
            Buffer.BlockCopy(frame, NetCodec.HeaderBytes, body, 0, body.Length);
            Lobby.HostSendTo(playerId, type, body);
            return;
        }
        Host?.SendTo(playerId, type, payload);
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
        if (Lobby != null) PollLobby();
        // Forwarding runs on the game tick like every other transport here. The host is not simulating
        // in this configuration, so this is all it does for the match.
        Relay?.Poll();
        // Relay-config spectating: a LAN relay host pushes UDP itself AND streams to spectators; a
        // lobby host player only streams (the server pushes UDP), so the stream runs for either.
        if ((Host != null && Relay != null) || IsLobby) StreamRelaySpectators();
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
                // The relay host serves mid-match joiners itself (no GameManager exists here): the
                // history is confirmed by construction (fighters only report confirmed frames), so a
                // join any time after the first report can be answered immediately.
                if (Relay != null && Room != null && Room.MatchRunning && CatchUpReady)
                    ServeRelayCatchUp(e.PlayerId);
            }
            else if (e.Kind == TcpRoomHost.EventKind.MatchResult) reportedWinner = e.Value;
            else if (e.Kind == TcpRoomHost.EventKind.InputReport) MergeInputReport(e.PlayerId, e.Report);
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

    // A fighter's confirmed-input report (relay configuration only). Merged into CatchUpHistory,
    // which feeds both the relay host's own spectate screen and the catch-ups it serves to mid-match
    // joiners. Report frames are CONFIRMED by construction — the fighter only reports what its own
    // session confirmed — so nothing here needs the trim the host-session path applies.
    //
    // playerId is the reporting fighter's id on a LAN (from the host's socket event); a lobby's
    // forwarded report carries no sender, so it is 0 there — diagnostics only.
    private void MergeInputReport(int playerId, MatchInputReport r)
    {
        if (r == null) return;
        if (!CatchUpReady)
        {
            CatchUpStageMinX = r.StageMinX;
            CatchUpStageMaxX = r.StageMaxX;
            CatchUpWorldWidth = r.WorldWidth;
            CatchUpP1StartX = r.P1StartX; CatchUpP1StartY = r.P1StartY;
            CatchUpP2StartX = r.P2StartX; CatchUpP2StartY = r.P2StartY;
            CatchUpReady = true;
            GD.Print($"[catchup] relay host: first input report from player {playerId}, "
                     + $"{r.P1.Length} frame(s)");
        }
        int n = System.Math.Min(r.P1.Length, r.P2.Length);
        if (n == 0) return;
        for (int i = 0; i < n; i++)
            CatchUpHistory.RecordAt(r.StartFrame + i, ReplayData.Unpack(r.P1[i]), ReplayData.Unpack(r.P2[i]));
        // The relay host's own spectate screen (and nothing else) follows this: delivered as an
        // event now, and buffered for the window between the seat screen's catch-up build and the
        // spectate screen's subscription — same buffer a joiner's TCP batches land in.
        var batch = new MatchInputs { StartFrame = r.StartFrame, P1 = r.P1, P2 = r.P2 };
        PendingStreamInputs.Add(batch);
        InputsReceived?.Invoke(batch);
        ServeRelayCatchUpToLurkers();
    }

    // Anyone in the room without a seat and without a catch-up yet gets served, on every merge: the
    // serve-on-join path misses a player who arrived in the window before the first report (there
    // was no history to send then), and a spectator who was in the room from the START of a relay
    // match never had a PlayerJoined event at all.
    private void ServeRelayCatchUpToLurkers()
    {
        if (!CatchUpReady || Room == null || !Room.MatchRunning) return;
        int hostId = IsLobby ? LocalPlayerId : (Host?.HostPlayerId ?? 0);
        foreach (var p in Room.Players)
        {
            if (p.PlayerId == hostId) continue;
            if (p.Seat >= 0) continue;                                // a fighter, not a watcher
            if (_relaySpectatorNextFrame.ContainsKey(p.PlayerId)) continue;   // already served
            ServeRelayCatchUp(p.PlayerId);
        }
    }

    // The relay host answering a mid-match joiner: the whole confirmed history so far. Served from
    // CatchUpHistory (no trimming needed — reports are confirmed by construction).
    private void ServeRelayCatchUp(int playerId)
    {
        int count = CatchUpHistory.FrameCount;
        var p1 = new ushort[count];
        var p2 = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            p1[i] = CatchUpHistory.P1Inputs[i];
            p2[i] = CatchUpHistory.P2Inputs[i];
        }
        var cu = new MatchCatchUp
        {
            Room = Room,
            StageMinX = CatchUpStageMinX,
            StageMaxX = CatchUpStageMaxX,
            WorldWidth = CatchUpWorldWidth,
            P1StartX = CatchUpP1StartX,
            P1StartY = CatchUpP1StartY,
            P2StartX = CatchUpP2StartX,
            P2StartY = CatchUpP2StartY,
            FrameCount = count,
            P1Inputs = p1,
            P2Inputs = p2,
        };
        _relaySpectatorNextFrame[playerId] = count;
        SendTo(playerId, MsgType.MatchCatchUp, cu);
        GD.Print($"[catchup] relay host sent catch-up to player {playerId}: {count} frames");
    }

    // The relay-host mirror of GameManager.StreamCatchUpSpectators: every tick, push the frames
    // confirmed since each joiner's last batch. Runs only while a relay is live (the host-session
    // configuration streams from GameManager instead).
    private void StreamRelaySpectators()
    {
        if (_relaySpectatorNextFrame.Count == 0) return;
        int upTo = CatchUpHistory.FrameCount - 1;
        if (upTo < 0) return;
        var gone = new List<int>();
        foreach (var kv in _relaySpectatorNextFrame)
        {
            if (!RoomContains(Room, kv.Key)) { gone.Add(kv.Key); continue; }
            if (kv.Value > upTo) continue;
            int start = kv.Value;
            int count = upTo - start + 1;
            var msg = new MatchInputs { StartFrame = start };
            msg.P1 = new ushort[count];
            msg.P2 = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                msg.P1[i] = CatchUpHistory.P1Inputs[start + i];
                msg.P2[i] = CatchUpHistory.P2Inputs[start + i];
            }
            SendTo(kv.Key, MsgType.MatchInputs, msg);
            _relaySpectatorNextFrame[kv.Key] = upTo + 1;
        }
        foreach (int id in gone) _relaySpectatorNextFrame.Remove(id);
    }

    private static bool RoomContains(RoomSnapshot room, int playerId)
    {
        if (room == null) return false;
        foreach (var p in room.Players) if (p.PlayerId == playerId) return true;
        return false;
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

    private void PollLobby()
    {
        Lobby.Poll();
        while (Lobby.TryDequeueEvent(out var e))
        {
            switch (e.Kind)
            {
                case LobbyRoomClient.EventKind.Connected:
                case LobbyRoomClient.EventKind.RoomChanged:
                    Room = Lobby.Room;
                    RoomChanged?.Invoke();
                    break;
                case LobbyRoomClient.EventKind.MatchStarting:
                    BeginMatch(e.Frame.As<StartMatch>());
                    break;
                case LobbyRoomClient.EventKind.MatchEnded:
                    EndMatchLocal();
                    PendingCatchUp = null;
                    PendingStreamInputs.Clear();
                    MatchEnded?.Invoke(e.Frame.As<MatchEnded>());
                    break;
                case LobbyRoomClient.EventKind.MatchCatchUp:
                    PendingCatchUp = e.Frame.As<MatchCatchUp>();
                    CatchUpReceived?.Invoke(PendingCatchUp);
                    break;
                case LobbyRoomClient.EventKind.MatchInputs:
                {
                    var m = e.Frame.As<MatchInputs>();
                    PendingStreamInputs.Add(m);
                    InputsReceived?.Invoke(m);
                    break;
                }
                case LobbyRoomClient.EventKind.LobbyPlayerJoined:
                    // The server's analogue of the LAN host's PlayerJoined: the host player's match
                    // director serves the newcomer a catch-up (GameManager.OnHostPlayerJoined).
                    PlayerJoined?.Invoke(e.Frame.As<LobbyPlayerJoined>().PlayerId);
                    break;
                case LobbyRoomClient.EventKind.InputReport:
                    // A fighter's report the server forwarded to the host player (relay
                    // configuration). playerId is 0 — the forwarded frame carries no sender.
                    if (Room != null && Room.MatchRunning)
                        MergeInputReport(0, e.Frame.As<MatchInputReport>());
                    break;
                case LobbyRoomClient.EventKind.LobbyRooms:
                    LobbyRoomsReceived?.Invoke(e.Frame.As<LobbyRooms>());
                    break;
                case LobbyRoomClient.EventKind.Rejected:
                    // Non-fatal: the lobby menu shows the reason and stays put.
                    LobbyRejected?.Invoke(e.Detail ?? "操作被拒绝");
                    break;
                case LobbyRoomClient.EventKind.RoomClosed:
                    // The room died, the connection did not. Drop everything room- and match-scoped
                    // (the client already dropped the room identity) and let the screens land on the
                    // browser — the same clean-up LeaveLobbyRoom does, minus the Bye we did not send.
                    EndMatchLocal();
                    Room = null;
                    PendingCatchUp = null;
                    PendingStreamInputs.Clear();
                    _lockedDevices.Clear();
                    LobbyRoomClosed?.Invoke(e.Detail ?? "房间已关闭");
                    break;
                case LobbyRoomClient.EventKind.Disconnected:
                {
                    string why = e.Detail ?? Lobby.LastError ?? "与服务器的连接已断开";
                    Lobby.Dispose();
                    Lobby = null;
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
