using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace MouseKombat.Net;

// The room host: accepts connections, enforces the rules in RoomState, broadcasts snapshots.
// See PROTOCOL.md. A LAN game runs one of these in-process; the lobby server is the same protocol
// implemented in Python.
//
// DELIBERATELY SINGLE-THREADED AND POLLED. Sockets are non-blocking and Poll() is called from the
// caller's own loop, so:
//   * nothing ever touches Godot nodes from a background thread,
//   * there is no lock to get wrong, and
//   * tests are deterministic — pump both ends by hand and assert, with no sleeps or races.
// Room traffic is a few frames per seat click, so once per game frame is ample. The MATCH channel is
// completely separate (UDP, driven by the rollback library) and is not throttled by this.
public sealed class TcpRoomHost : IDisposable
{
    public enum EventKind { PlayerJoined, PlayerLeft, RoomChanged, Rejected, Error, MatchResult, InputReport }

    public readonly struct HostEvent
    {
        public readonly EventKind Kind;
        public readonly int PlayerId;
        public readonly string Detail;
        public readonly int Value;      // MatchResult: the winning seat
        public readonly MatchInputReport Report;   // InputReport: the confirmed frames + geometry
        public HostEvent(EventKind kind, int playerId, string detail, int value = 0,
                         MatchInputReport report = null)
        { Kind = kind; PlayerId = playerId; Detail = detail; Value = value; Report = report; }
    }

    private sealed class Conn
    {
        public Socket Sock;
        public FrameReader Reader = new FrameReader();
        public int PlayerId;              // 0 until Hello is accepted
        public bool Closing;
        public string CloseReason;
        // Where this client's MATCH traffic goes: the source address of this TCP connection paired
        // with the UDP port it announced in Hello. Built once at handshake so starting a match needs
        // no discovery step (see PROTOCOL.md § Match lifecycle).
        public IPEndPoint MatchEndPoint;
    }

    private Socket _listener;
    private readonly List<Conn> _conns = new();
    private readonly Queue<HostEvent> _events = new();
    private readonly byte[] _rx = new byte[8192];

    public RoomState Room { get; } = new RoomState();
    public string GameVersion { get; private set; } = "";
    public int Port { get; private set; }
    public bool Listening => _listener != null;

    // The host is a player too (it holds a seat and can add AI), so it exists in RoomState without a
    // socket. Its id is what AddAi checks against.
    public int HostPlayerId { get; private set; }

    public bool TryDequeueEvent(out HostEvent e)
    {
        if (_events.Count == 0) { e = default; return false; }
        e = _events.Dequeue();
        return true;
    }

    // bindAddress "" or "0.0.0.0" listens on every interface. An IPv6 dual-mode socket is used for
    // that case so a client reaching us over either stack works without a second listener.
    public void Start(string bindAddress, int port, string hostName, string gameVersion)
    {
        GameVersion = gameVersion ?? "";
        var host = Room.AddPlayer(hostName, isHost: true);
        HostPlayerId = host.PlayerId;

        bool any = string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "0.0.0.0" || bindAddress == "::";
        IPEndPoint ep;
        if (any && Socket.OSSupportsIPv6)
        {
            _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listener.DualMode = true;
            ep = new IPEndPoint(IPAddress.IPv6Any, port);
        }
        else
        {
            var addr = any ? IPAddress.Any : IPAddress.Parse(bindAddress);
            _listener = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ep = new IPEndPoint(addr, port);
        }

        _listener.Blocking = false;
        _listener.Bind(ep);
        _listener.Listen(16);
        Port = ((IPEndPoint)_listener.LocalEndPoint).Port;   // resolves port 0 to what was assigned
        Emit(EventKind.RoomChanged, 0, null);
    }

    public void Poll()
    {
        AcceptPending();
        ReadAll();
        Reap();
    }

    private void AcceptPending()
    {
        if (_listener == null) return;
        while (true)
        {
            Socket s;
            try
            {
                if (!_listener.Poll(0, SelectMode.SelectRead)) return;
                s = _listener.Accept();
            }
            catch (SocketException) { return; }

            s.Blocking = false;
            s.NoDelay = true;   // seat clicks are tiny; Nagle would add latency for nothing
            // Linger on close so a final Rejected / Bye actually reaches the peer. Without it the
            // client sees a bare disconnect and can only report "connection lost", which is exactly
            // the information the user does not need — see HardClose.
            s.LingerState = new LingerOption(true, 2);
            _conns.Add(new Conn { Sock = s });
        }
    }

    private void ReadAll()
    {
        for (int i = 0; i < _conns.Count; i++)
        {
            var c = _conns[i];
            if (c.Closing) continue;

            // Drain everything available this tick. A single Receive can return several frames or
            // half of one; FrameReader owns that reassembly. End-of-stream is recorded but NOT acted
            // on until the buffered frames have been handled — a client that sends Bye and closes in
            // the same breath must still have its Bye read (same trap as TcpRoomClient.PollRead).
            bool eof = false;
            while (true)
            {
                int n;
                try
                {
                    if (!c.Sock.Poll(0, SelectMode.SelectRead)) break;
                    n = c.Sock.Receive(_rx, SocketFlags.None);
                }
                catch (SocketException) { Close(c, "连接中断"); break; }
                catch (ObjectDisposedException) { Close(c, "连接已关闭"); break; }

                if (n == 0) { eof = true; break; }
                c.Reader.Feed(new ReadOnlySpan<byte>(_rx, 0, n));
            }

            if (c.Closing) continue;
            if (c.Reader.Failed) { Close(c, c.Reader.Error); continue; }

            while (c.Reader.TryRead(out var frame))
            {
                Handle(c, frame);
                if (c.Closing) break;
            }
            if (!c.Closing && c.Reader.Failed) Close(c, c.Reader.Error);
            if (!c.Closing && eof) Close(c, "对方断开");
        }
    }

    private void Handle(Conn c, NetFrame frame)
    {
        // Nothing but Hello is accepted before the handshake: an unauthenticated peer must not be able
        // to move seats around.
        if (c.PlayerId == 0 && frame.Type != MsgType.Hello)
        {
            Reject(c, "未完成握手");
            return;
        }

        switch (frame.Type)
        {
            case MsgType.Hello: OnHello(c, frame.As<Hello>()); break;
            case MsgType.SeatClaim: Apply(Room.ClaimSeat(c.PlayerId, frame.As<SeatClaim>().Seat)); break;
            case MsgType.SeatRelease: Apply(Room.ReleaseSeat(c.PlayerId)); break;
            case MsgType.CharPick: Apply(Room.PickCharacter(c.PlayerId, frame.As<CharPick>().Character)); break;
            case MsgType.AddAi:
            {
                var m = frame.As<AddAi>();
                // Host-only, enforced by RoomState. A client asking for it is refused silently rather
                // than disconnected: a stale UI could send it after losing host status.
                Apply(Room.AddAi(c.PlayerId, m.Seat, Room.Seat(m.Seat).Character, m.AiModel));
                break;
            }
            case MsgType.RemoveAi:
            {
                // Host-only like AddAi: only the host may free the AI seat it placed.
                Apply(Room.RemoveAi(c.PlayerId, frame.As<RemoveAi>().Seat));
                break;
            }
            case MsgType.MatchResult:
            {
                // Only meaningful while a match is running. The rules for ending one live in RoomState;
                // this just forwards the fact upward, because deciding it here would put match policy
                // in the socket handler.
                if (Room.MatchRunning)
                    Emit(EventKind.MatchResult, c.PlayerId, null, frame.As<MatchResult>().WinnerSeat);
                break;
            }
            case MsgType.MatchInputReport:
            {
                // A fighter's confirmed-input report (relay configuration). Decoded here rather than
                // in the caller so the host layer stays Godot-free; the caller (NetSession) merges it
                // into the catch-up history and re-broadcasts the frames to mid-match joiners.
                if (Room.MatchRunning)
                    Emit(EventKind.InputReport, c.PlayerId, null, report: frame.As<MatchInputReport>());
                break;
            }
            case MsgType.Bye: Close(c, frame.As<Bye>().Reason); break;
            default: break;   // unknown / host-only types from a client are ignored
        }
    }

    private void OnHello(Conn c, Hello h)
    {
        if (c.PlayerId != 0) return;                       // duplicate Hello
        if (h.Protocol != NetVersion.Protocol || h.GameVersion != GameVersion)
        {
            Reject(c, h.Protocol != NetVersion.Protocol ? "协议版本不一致" : "游戏版本不一致");
            return;
        }

        var p = Room.AddPlayer(h.Name, isHost: false);
        if (p == null) { Reject(c, "房间已满"); return; }

        c.PlayerId = p.PlayerId;
        c.MatchEndPoint = MatchEndPointFrom(c.Sock, h.MatchUdpPort);
        Send(c, MsgType.Welcome, new Welcome { PlayerId = p.PlayerId, IsHost = false, Room = Room.Snapshot() });
        Emit(EventKind.PlayerJoined, p.PlayerId, p.Name);
        BroadcastRoom();
    }

    // The client's match endpoint, or null when it announced no port (an older build, or a bind that
    // failed on its side). A null here is what makes MatchPlan refuse to start rather than guess.
    public IPEndPoint MatchEndPointOf(int playerId)
    {
        if (playerId == 0) return null;
        var c = _conns.Find(x => x.PlayerId == playerId);
        return c?.MatchEndPoint;
    }

    // The ADDRESS comes from the connection, never from the message: a client that lies about its own
    // IP would otherwise make the host send match traffic wherever it liked. Only the port is taken on
    // trust, and the worst a wrong one does is break that client's own match.
    private static IPEndPoint MatchEndPointFrom(Socket sock, int udpPort)
    {
        if (udpPort <= 0 || udpPort > 65535) return null;
        if (sock?.RemoteEndPoint is not IPEndPoint peer) return null;
        var addr = peer.Address;
        // A dual-mode listener reports IPv4 peers as ::ffff:a.b.c.d. The match socket is IPv4, so map
        // it back or every LAN client would be unreachable over a v6 listener.
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        return new IPEndPoint(addr, udpPort);
    }

    // Accepted request => everyone needs the new snapshot. Refused => nothing changed, so nothing is
    // sent; the requester's UI keeps showing the authoritative state it already has.
    private void Apply(bool accepted)
    {
        if (accepted) BroadcastRoom();
    }

    private void Reject(Conn c, string reason)
    {
        Send(c, MsgType.Rejected, new Rejected
        {
            Reason = reason,
            HostProtocol = NetVersion.Protocol,
            HostGameVersion = GameVersion,
        });
        Emit(EventKind.Rejected, 0, reason);
        Close(c, reason);
    }

    public void BroadcastRoom()
    {
        var snap = Room.Snapshot();
        Broadcast(MsgType.RoomState, snap);
        Emit(EventKind.RoomChanged, 0, null);
    }

    public void Broadcast<T>(MsgType type, T payload)
    {
        byte[] frame = NetCodec.Encode(type, payload);
        for (int i = 0; i < _conns.Count; i++)
            if (_conns[i].PlayerId != 0 && !_conns[i].Closing) SendRaw(_conns[i], frame);
    }

    public void SendTo<T>(int playerId, MsgType type, T payload)
    {
        var c = _conns.Find(x => x.PlayerId == playerId && !x.Closing);
        if (c != null) Send(c, type, payload);
    }

    // Host-initiated removal (the match ended and this player had dropped).
    public void Kick(int playerId, string reason)
    {
        var c = _conns.Find(x => x.PlayerId == playerId);
        if (c != null) { Send(c, MsgType.Bye, new Bye { Reason = reason }); Close(c, reason); }
        else { Room.RemovePlayer(playerId); BroadcastRoom(); }
    }

    // Host leaving: everyone is told why before the socket goes away, so clients can show "connection
    // closed" rather than an unexplained drop (spec: 主机在选人界面 ESC 退出时其它玩家弹窗提示).
    public void Stop(string reason)
    {
        Broadcast(MsgType.Bye, new Bye { Reason = reason });
        for (int i = 0; i < _conns.Count; i++) HardClose(_conns[i]);
        _conns.Clear();
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private void Send<T>(Conn c, MsgType type, T payload) => SendRaw(c, NetCodec.Encode(type, payload));

    private void SendRaw(Conn c, byte[] frame)
    {
        try
        {
            int sent = 0;
            while (sent < frame.Length)
            {
                int n = c.Sock.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                if (n <= 0) break;
                sent += n;
            }
        }
        catch (SocketException) { Close(c, "发送失败"); }
        catch (ObjectDisposedException) { Close(c, "连接已关闭"); }
    }

    private void Close(Conn c, string reason)
    {
        if (c.Closing) return;
        c.Closing = true;
        c.CloseReason = reason;
    }

    // Closing is deferred to the end of the tick so a socket is never disposed while the loop that is
    // iterating it still holds a reference.
    private void Reap()
    {
        for (int i = _conns.Count - 1; i >= 0; i--)
        {
            var c = _conns[i];
            if (!c.Closing) continue;

            int pid = c.PlayerId;
            HardClose(c);
            _conns.RemoveAt(i);

            if (pid == 0) continue;
            if (Room.MatchRunning)
            {
                // Mid-match: keep the seat and the player, mark them dropped. The opponent is still
                // simulating against that seat, so freeing it now would change the match.
                Room.MarkDisconnected(pid);
            }
            else
            {
                Room.RemovePlayer(pid);
            }
            Emit(EventKind.PlayerLeft, pid, c.CloseReason);
            BroadcastRoom();
        }
    }

    // Shutdown(SEND), not Both. The host's last act on a connection is usually to explain itself —
    // Rejected on a version mismatch, Bye when the host leaves — and Both tears down the send side
    // immediately, so those bytes were being dropped and the client reported a bare "connection
    // lost". Send flushes what is queued and then sends FIN, so the peer reads the explanation first
    // and only then sees end-of-stream.
    private static void HardClose(Conn c)
    {
        try { c.Sock?.Shutdown(SocketShutdown.Send); } catch { }
        try { c.Sock?.Close(); } catch { }
    }

    private void Emit(EventKind kind, int playerId, string detail, int value = 0,
                      MatchInputReport report = null) =>
        _events.Enqueue(new HostEvent(kind, playerId, detail, value, report));

    public void Dispose() => Stop("主机关闭");
}
