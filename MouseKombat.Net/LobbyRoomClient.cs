using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MouseKombat.Net;

// The lobby client: one connection to the lobby server (server/lobby_server.py) that covers BOTH
// phases of the lobby protocol (PROTOCOL.md § Lobby):
//
//   * browse phase: after Hello the caller pages through the room list (ListRooms) and then creates
//     or joins a room on the SAME connection;
//   * room phase: after CreateRoom / JoinRoom the connection IS the room membership — the server
//     plays the host's role (it authors Welcome/RoomState/StartMatch/MatchEnded/Bye), and this
//     client speaks the same room messages a LAN client does, plus the lobby-only routing messages
//     (MatchStart / HostSendTo / LobbyPlayerJoined / forwarded MatchInputReport).
//
// The room creator is the "host player": the server grants it host-only rights (AddAi/RemoveAi/
// MatchStart/HostSendTo). Everything about the room is authoritative on the server, so this client
// never mutates state optimistically — exactly like TcpRoomClient.
//
// Same polled, single-threaded shape as TcpRoomClient: DNS + TCP connect run as Tasks whose
// completion Poll() observes; nothing ever runs on a background thread of ours.
public sealed class LobbyRoomClient : IDisposable
{
    public enum EventKind
    {
        Connecting, Connected, Rejected, Disconnected,
        RoomChanged, MatchStarting, MatchEnded, MatchCatchUp, MatchInputs,
        LobbyRooms,        // a LobbyList page arrived (browse phase)
        LobbyPlayerJoined, // the server tells the HOST PLAYER someone joined (catch-up hook)
        InputReport,       // the server forwards a fighter's MatchInputReport to the HOST PLAYER
    }

    public readonly struct ClientEvent
    {
        public readonly EventKind Kind;
        public readonly string Detail;
        public readonly NetFrame Frame;   // payload for the frame-carrying kinds
        public ClientEvent(EventKind kind, string detail, NetFrame frame = default)
        { Kind = kind; Detail = detail; Frame = frame; }
    }

    private enum Stage { Idle, Resolving, Connecting, Handshaking, Lobby, Room, Dead }

    private Socket _sock;
    private Task<IPAddress[]> _dns;
    private Task _connect;
    private readonly FrameReader _reader = new FrameReader();
    private readonly Queue<ClientEvent> _events = new();
    private readonly byte[] _rx = new byte[8192];

    private Stage _stage = Stage.Idle;
    private string _host, _name, _gameVersion;
    private int _port;
    private IPAddress[] _addrs;
    private int _addrIndex;

    // UDP port this client already bound for match traffic, sent in Hello (see TcpRoomClient).
    public int MatchUdpPort { get; set; }

    // The first lobby op (list/create/join) is sent by the caller right after Connect(), which is
    // BEFORE the DNS/TCP steps finish. The socket does not exist yet, so the op is parked here and
    // flushed in PollConnect, right after the Hello — the server processes the two in order, which
    // is exactly what the browse-then-create flow expects.
    private (byte type, byte[] frame)? _pendingOp;

    public int PlayerId { get; private set; }
    public bool IsHostPlayer { get; private set; }
    public RoomSnapshot Room { get; private set; }
    public bool IsConnected => _stage is Stage.Lobby or Stage.Room;
    public bool IsInRoom => _stage == Stage.Room;
    public bool IsBusy => _stage is Stage.Resolving or Stage.Connecting or Stage.Handshaking;
    public string LastError { get; private set; }

    // The address actually connected to (see TcpRoomClient.ConnectedAddress for why this matters:
    // the match traffic dials the same resolved address).
    public IPAddress ConnectedAddress { get; private set; }

    public bool TryDequeueEvent(out ClientEvent e)
    {
        if (_events.Count == 0) { e = default; return false; }
        e = _events.Dequeue();
        return true;
    }

    // `host` may be a domain name, an IPv4 literal or an IPv6 literal (see TcpRoomClient.Connect).
    public void Connect(string host, int port, string playerName, string gameVersion)
    {
        Disconnect(null);
        _host = host; _port = port; _name = playerName; _gameVersion = gameVersion ?? "";
        LastError = null;
        _pendingOp = null;
        _stage = Stage.Resolving;
        Emit(EventKind.Connecting, $"正在解析 {host}");
        try { _dns = Dns.GetHostAddressesAsync(host); }
        catch (Exception e) { Fail($"地址无效：{e.Message}"); }
    }

    public void Poll()
    {
        switch (_stage)
        {
            case Stage.Resolving: PollDns(); break;
            case Stage.Connecting: PollConnect(); break;
            case Stage.Handshaking:
            case Stage.Lobby:
            case Stage.Room: PollRead(); break;
        }
    }

    private void PollDns()
    {
        if (_dns == null || !_dns.IsCompleted) return;
        if (_dns.IsFaulted || _dns.Result == null || _dns.Result.Length == 0)
        {
            Fail($"无法解析主机 {_host}");
            return;
        }
        _addrs = _dns.Result;
        _addrIndex = 0;
        BeginConnectCurrent();
    }

    private void BeginConnectCurrent()
    {
        if (_addrs == null || _addrIndex >= _addrs.Length)
        {
            Fail($"无法连接到 {_host}:{_port}");
            return;
        }
        var addr = _addrs[_addrIndex];
        try
        {
            _sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _connect = _sock.ConnectAsync(new IPEndPoint(addr, _port));
            _stage = Stage.Connecting;
            Emit(EventKind.Connecting, $"正在连接 {addr}:{_port}");
        }
        catch (Exception e) { NextAddressOrFail(e.Message); }
    }

    private void PollConnect()
    {
        if (_connect == null || !_connect.IsCompleted) return;
        if (_connect.IsFaulted)
        {
            NextAddressOrFail(_connect.Exception?.GetBaseException().Message ?? "连接失败");
            return;
        }

        _sock.Blocking = false;
        _stage = Stage.Handshaking;
        ConnectedAddress = (_sock.RemoteEndPoint as IPEndPoint)?.Address ?? _addrs[_addrIndex];
        // The server version-checks THIS Hello itself (PROTOCOL.md § Lobby); a mismatch is a
        // fatal Rejected. The lobby op (list/create/join) is sent right after by the caller.
        Send(MsgType.Hello, new Hello
        {
            Protocol = NetVersion.Protocol,
            GameVersion = _gameVersion,
            Name = _name,
            RoomPassword = "",
            MatchUdpPort = MatchUdpPort,
        });
        if (_pendingOp.HasValue)
        {
            SendFrame(_pendingOp.Value.type, _pendingOp.Value.frame);
            _pendingOp = null;
        }
        _stage = Stage.Lobby;
    }

    private void NextAddressOrFail(string why)
    {
        try { _sock?.Close(); } catch { }
        _sock = null;
        _addrIndex++;
        if (_addrs != null && _addrIndex < _addrs.Length) BeginConnectCurrent();
        else Fail(why);
    }

    private void PollRead()
    {
        // END-OF-STREAM IS NOT AN EXCUSE TO DROP BUFFERED FRAMES: the server's last act is usually
        // to explain itself (Rejected on a version mismatch, Bye when the host player leaves) and
        // then close, and on a fast link that frame and the FIN arrive in the SAME poll.
        bool eof = false;
        while (true)
        {
            int n;
            try
            {
                if (_sock == null || !_sock.Poll(0, SelectMode.SelectRead)) break;
                n = _sock.Receive(_rx, SocketFlags.None);
            }
            catch (SocketException) { Fail("连接中断"); return; }
            catch (ObjectDisposedException) { Fail("连接已关闭"); return; }

            if (n == 0) { eof = true; break; }
            _reader.Feed(new ReadOnlySpan<byte>(_rx, 0, n));
        }

        if (_reader.Failed) { Fail(_reader.Error); return; }

        while (_reader.TryRead(out var frame))
        {
            // Frames that belong to a ROOM are ignored unless we are in one. After LeaveRoom the
            // server may still have a RoomState (or a stream batch) in flight for the room we just
            // left; accepting it would repopulate Room and bounce the player straight back into the
            // room screen they pressed ESC out of.
            if (_stage != Stage.Room && frame.Type is MsgType.RoomState or MsgType.StartMatch
                    or MsgType.MatchEnded or MsgType.MatchCatchUp or MsgType.MatchInputs
                    or MsgType.MatchInputReport or MsgType.LobbyPlayerJoined)
                continue;
            switch (frame.Type)
            {
                case MsgType.Welcome:
                {
                    var w = frame.As<Welcome>();
                    PlayerId = w.PlayerId;
                    IsHostPlayer = w.IsHost;
                    Room = w.Room;
                    _stage = Stage.Room;
                    Emit(EventKind.Connected, null);
                    Emit(EventKind.RoomChanged, null);
                    break;
                }
                case MsgType.Rejected:
                {
                    // NON-FATAL in the lobby: a wrong password, a full room or a malformed create
                    // form leave the browser connected so the player can retry or pick another
                    // room. Only the version handshake is fatal (the server closes right after).
                    var r = frame.As<Rejected>();
                    string detail = r.Reason;
                    if (!string.IsNullOrEmpty(r.HostGameVersion) && r.HostGameVersion != _gameVersion)
                        detail += $"（服务器 {r.HostGameVersion} / 本机 {_gameVersion}）";
                    LastError = detail;
                    Emit(EventKind.Rejected, detail);
                    bool versionRefused =
                        (r.HostGameVersion != null && r.HostGameVersion.Length > 0 && r.HostGameVersion != _gameVersion)
                        || r.HostProtocol != NetVersion.Protocol;
                    if (versionRefused || _stage == Stage.Handshaking) { Shutdown(); return; }
                    break;
                }
                case MsgType.LobbyRooms:
                    Emit(EventKind.LobbyRooms, null, frame);
                    break;
                case MsgType.LobbyPlayerJoined:
                    Emit(EventKind.LobbyPlayerJoined, null, frame);
                    break;
                case MsgType.RoomState:
                    Room = frame.As<RoomSnapshot>();
                    // The frame rides along so a consumer can predicate on the snapshot instead of
                    // re-reading this client's mutable Room field.
                    Emit(EventKind.RoomChanged, null, frame);
                    break;
                case MsgType.StartMatch: Emit(EventKind.MatchStarting, null, frame); break;
                case MsgType.MatchEnded: Emit(EventKind.MatchEnded, null, frame); break;
                case MsgType.MatchCatchUp: Emit(EventKind.MatchCatchUp, null, frame); break;
                case MsgType.MatchInputs: Emit(EventKind.MatchInputs, null, frame); break;
                case MsgType.MatchInputReport:
                    // A fighter's report the server forwarded to the HOST PLAYER (relay
                    // configuration): the host player merges it into its catch-up buffer.
                    Emit(EventKind.InputReport, null, frame);
                    break;
                case MsgType.Bye:
                    LastError = frame.As<Bye>().Reason;
                    Emit(EventKind.Disconnected, LastError);
                    Shutdown();
                    return;
            }
        }

        // Only now, after everything buffered has been understood.
        if (eof) Fail("与服务器的连接已断开");
    }

    // ---- browse phase ----
    public void ListRooms(int page) => Send(MsgType.LobbyList, new LobbyList { Page = page });
    public void CreateRoom(int maxPlayers, string password, bool searchable) =>
        Send(MsgType.LobbyCreate, new LobbyCreate { MaxPlayers = maxPlayers, Password = password ?? "", Searchable = searchable });
    public void JoinRoom(string roomId, string password) =>
        Send(MsgType.LobbyJoin, new LobbyJoin { RoomId = roomId ?? "", Password = password ?? "" });

    // ---- room phase (same requests a LAN client makes; the server is the authority) ----
    public void ClaimSeat(int seat) => Send(MsgType.SeatClaim, new SeatClaim { Seat = seat });
    public void ReleaseSeat() => Send(MsgType.SeatRelease, new SeatRelease());
    public void PickCharacter(int character) => Send(MsgType.CharPick, new CharPick { Character = character });
    // The character travels WITH the AI: the pick never went to the server (the seat belongs to
    // nobody yet), so the server cannot read it back from the seat.
    public void AddAi(int seat, int character, string model) =>
        Send(MsgType.AddAi, new AddAi { Seat = seat, Character = character, AiModel = model ?? "" });
    public void RemoveAi(int seat) => Send(MsgType.RemoveAi, new RemoveAi { Seat = seat });
    public void ReportMatchResult(int winnerSeat) => Send(MsgType.MatchResult, new MatchResult { WinnerSeat = winnerSeat });

    // ---- lobby-only room-phase requests (host player only; the server refuses the rest) ----

    // Ask the server to start the match, carrying the stage geometry it has no scene to read.
    // The server answers with the standard StartMatch broadcast.
    public void RequestMatchStart(MatchStart setup) => Send(MsgType.MatchStart, setup);

    // Route one frame (a MatchCatchUp or MatchInputs) from this machine's match director to a
    // member of the room. The body is the RAW msgpack body — never re-packed.
    public void HostSendTo(int targetPlayerId, MsgType type, byte[] body)
    {
        if (body == null) return;
        Send(MsgType.HostSendTo, new HostSendTo
        {
            TargetPlayerId = targetPlayerId,
            Type = (byte)type,
            Body = body,
        });
    }

    public void Send<T>(MsgType type, T payload)
    {
        if (_stage is Stage.Idle or Stage.Dead) return;
        byte[] frame = NetCodec.Encode(type, payload);
        if (_sock == null)
        {
            // Still resolving/connecting: park the op, flushed after the Hello (see _pendingOp).
            _pendingOp = ((byte)type, frame);
            return;
        }
        SendFrame((byte)type, frame);
    }

    private void SendFrame(byte type, byte[] frame)
    {
        try
        {
            int sent = 0;
            while (sent < frame.Length)
            {
                int n = _sock.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                if (n <= 0) break;
                sent += n;
            }
        }
        catch (SocketException) { Fail("发送失败"); }
        catch (ObjectDisposedException) { Fail("连接已关闭"); }
    }

    public void Disconnect(string reason)
    {
        if (_stage == Stage.Room && reason != null) Send(MsgType.Bye, new Bye { Reason = reason });
        Shutdown();
    }

    // Leave the room but KEEP the connection: the server returns it to the browse phase, so the
    // caller can page the room list and create/join again on the same socket (spec: ESC 退出房间
    // 后回到选房界面，不断开大厅连接). The HOST PLAYER may use this too — the room is destroyed and
    // the other members are dropped, but this connection survives and lands on the browser.
    public void LeaveRoom(string reason)
    {
        if (_stage != Stage.Room) return;
        if (reason != null) Send(MsgType.Bye, new Bye { Reason = reason });
        // Back to the browse phase locally as well, and with none of the room's identity left: a
        // stale IsHostPlayer would make the screens believe this connection still owns a room (it
        // would offer AI seats and the start button in the NEXT room it joined as a member).
        _stage = Stage.Lobby;
        IsHostPlayer = false;
        PlayerId = 0;
        Room = null;
    }

    private void Fail(string why)
    {
        // A previous explanation beats a generic one: the version-mismatch Rejected sets LastError
        // and the server closes right after, so the EOF that follows must not overwrite "游戏版本不
        // 一致" with "连接已断开".
        if (string.IsNullOrEmpty(LastError)) LastError = why;
        Emit(EventKind.Disconnected, LastError);
        Shutdown();
    }

    private void Shutdown()
    {
        try { _sock?.Shutdown(SocketShutdown.Both); } catch { }
        try { _sock?.Close(); } catch { }
        _sock = null;
        _stage = Stage.Dead;
    }

    private void Emit(EventKind kind, string detail, NetFrame frame = default) =>
        _events.Enqueue(new ClientEvent(kind, detail, frame));

    public void Dispose() => Disconnect(null);
}
