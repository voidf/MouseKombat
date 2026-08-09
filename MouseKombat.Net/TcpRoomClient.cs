using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MouseKombat.Net;

// The room client. Same polled, single-threaded shape as TcpRoomHost — Poll() from the caller's loop,
// drain events, never touch a Godot node from a socket callback.
//
// Connecting is the one part that cannot be done with a non-blocking check alone (DNS + TCP handshake
// both take time), so those two steps run as Tasks whose completion Poll() observes. No thread of ours
// ever touches this object's state.
public sealed class TcpRoomClient : IDisposable
{
    public enum EventKind { Connecting, Connected, Rejected, RoomChanged, MatchStarting, MatchEnded, MatchCatchUp, MatchInputs, Disconnected, Error }

    public readonly struct ClientEvent
    {
        public readonly EventKind Kind;
        public readonly string Detail;
        public readonly NetFrame Frame;   // payload for MatchStarting / MatchEnded
        public ClientEvent(EventKind kind, string detail, NetFrame frame = default)
        { Kind = kind; Detail = detail; Frame = frame; }
    }

    private enum Stage { Idle, Resolving, Connecting, Handshaking, Ready, Dead }

    private Socket _sock;
    private Task<IPAddress[]> _dns;
    private Task _connect;
    private readonly FrameReader _reader = new FrameReader();
    private readonly Queue<ClientEvent> _events = new();
    private readonly byte[] _rx = new byte[8192];

    private Stage _stage = Stage.Idle;
    private string _host, _name, _gameVersion, _password;
    private int _port;
    private IPAddress[] _addrs;
    private int _addrIndex;

    public int PlayerId { get; private set; }
    public RoomSnapshot Room { get; private set; }
    public bool IsConnected => _stage == Stage.Ready;
    public bool IsBusy => _stage is Stage.Resolving or Stage.Connecting or Stage.Handshaking;
    public string LastError { get; private set; }

    // UDP port this client has already bound for match traffic, sent in Hello. 0 = none announced,
    // which the host turns into a refusal to start rather than a guess.
    public int MatchUdpPort { get; set; }

    // The address actually connected to. The match session dials the same host, so this is what
    // MatchPlan pairs with StartMatch.MatchUdpPort — using the RESOLVED address rather than the typed
    // hostname means a domain name that resolves to several addresses cannot send room traffic to one
    // and match traffic to another.
    public IPAddress ConnectedAddress { get; private set; }

    public bool TryDequeueEvent(out ClientEvent e)
    {
        if (_events.Count == 0) { e = default; return false; }
        e = _events.Dequeue();
        return true;
    }

    // `host` may be a domain name, an IPv4 literal or an IPv6 literal — all three go through
    // Dns.GetHostAddressesAsync, which passes literals straight through. Every returned address is
    // tried in order, so a machine advertising both A and AAAA records still connects when only one
    // stack actually works.
    public void Connect(string host, int port, string playerName, string gameVersion, string roomPassword = "")
    {
        Disconnect(null);
        _host = host; _port = port; _name = playerName; _gameVersion = gameVersion ?? "";
        _password = roomPassword ?? "";
        LastError = null;
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
            case Stage.Ready: PollRead(); break;
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
            // Try the next address before giving up: a dual-stack host often has one reachable family.
            NextAddressOrFail(_connect.Exception?.GetBaseException().Message ?? "连接失败");
            return;
        }

        _sock.Blocking = false;
        _stage = Stage.Handshaking;
        ConnectedAddress = (_sock.RemoteEndPoint as IPEndPoint)?.Address ?? _addrs[_addrIndex];
        Send(MsgType.Hello, new Hello
        {
            Protocol = NetVersion.Protocol,
            GameVersion = _gameVersion,
            Name = _name,
            RoomPassword = _password,
            MatchUdpPort = MatchUdpPort,
        });
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
        // END-OF-STREAM IS NOT AN EXCUSE TO DROP BUFFERED FRAMES. The host's last act is usually to
        // explain itself (Rejected on a version mismatch, Bye when it leaves) and then close, so on a
        // fast link that message and the FIN arrive in the SAME poll. Reporting the disconnect
        // immediately threw away the explanation and left the user with "connection lost".
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
            switch (frame.Type)
            {
                case MsgType.Welcome:
                {
                    var w = frame.As<Welcome>();
                    PlayerId = w.PlayerId;
                    Room = w.Room;
                    _stage = Stage.Ready;
                    Emit(EventKind.Connected, null);
                    Emit(EventKind.RoomChanged, null);
                    break;
                }
                case MsgType.Rejected:
                {
                    var r = frame.As<Rejected>();
                    // Show both sides' versions: "version mismatch" without the numbers is useless to
                    // whoever has to fix it.
                    string detail = r.Reason;
                    if (!string.IsNullOrEmpty(r.HostGameVersion) && r.HostGameVersion != _gameVersion)
                        detail += $"（主机 {r.HostGameVersion} / 本机 {_gameVersion}）";
                    else if (r.HostProtocol != NetVersion.Protocol)
                        detail += $"（主机协议 {r.HostProtocol} / 本机 {NetVersion.Protocol}）";
                    LastError = detail;
                    Emit(EventKind.Rejected, detail);
                    Shutdown();
                    return;
                }
                case MsgType.RoomState:
                    Room = frame.As<RoomSnapshot>();
                    Emit(EventKind.RoomChanged, null);
                    break;
                case MsgType.StartMatch: Emit(EventKind.MatchStarting, null, frame); break;
                case MsgType.MatchEnded: Emit(EventKind.MatchEnded, null, frame); break;
                case MsgType.MatchCatchUp: Emit(EventKind.MatchCatchUp, null, frame); break;
                case MsgType.MatchInputs: Emit(EventKind.MatchInputs, null, frame); break;
                case MsgType.Bye:
                    LastError = frame.As<Bye>().Reason;
                    Emit(EventKind.Disconnected, LastError);
                    Shutdown();
                    return;
            }
        }

        // Only now, after everything buffered has been understood.
        if (eof) Fail("与主机的连接已断开");
    }

    // ---- requests (fire and forget; the host answers with a snapshot, or refuses silently) ----
    public void ClaimSeat(int seat) => Send(MsgType.SeatClaim, new SeatClaim { Seat = seat });
    public void ReleaseSeat() => Send(MsgType.SeatRelease, new SeatRelease());
    public void PickCharacter(int character) => Send(MsgType.CharPick, new CharPick { Character = character });
    public void AddAi(int seat, string model) => Send(MsgType.AddAi, new AddAi { Seat = seat, AiModel = model ?? "" });
    public void ReportMatchResult(int winnerSeat) =>
        Send(MsgType.MatchResult, new MatchResult { WinnerSeat = winnerSeat });

    public void Send<T>(MsgType type, T payload)
    {
        if (_sock == null || _stage is Stage.Idle or Stage.Dead) return;
        byte[] frame = NetCodec.Encode(type, payload);
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
        if (_stage == Stage.Ready && reason != null) Send(MsgType.Bye, new Bye { Reason = reason });
        Shutdown();
    }

    private void Fail(string why)
    {
        LastError = why;
        Emit(EventKind.Disconnected, why);
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
