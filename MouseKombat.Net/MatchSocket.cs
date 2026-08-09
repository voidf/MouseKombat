using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Backdash.Network.Client;

namespace MouseKombat.Net;

// A UDP socket for match traffic that OUTLIVES the rollback session using it.
//
// The reason this exists: a client has to bind its match port during the room handshake, because the
// port number is announced in Hello (see Hello.MatchUdpPort) and binding after announcing would leave
// a window in which something else could take it. But a room hosts many matches in a row, and
// disposing a Backdash session tears the socket down — PeerClient.Dispose calls IDisposable.Dispose
// on the handed socket, NOT Close. Rebinding the same port between matches is a race with every other
// process on the machine, and the failure lands exactly where it is least welcome — at "开始对战" (the
// second match in a room never synchronizes: both Close AND Dispose must be no-ops here).
//
// So both Close() and Dispose() are no-ops and only CloseNow() really closes. NetSession owns the
// lifetime: one socket per room, closed when leaving the room.
public sealed class MatchSocket : IPeerSocket, IDisposable
{
    private readonly UdpSocket _inner;
    private bool _disposed;

    public int Port { get; }

    // useIPv6 = false binds IPv4 on every interface. IPv6-only LANs would need a dual-mode socket
    // here, the same way TcpRoomHost does for its listener; nothing in the protocol assumes IPv4, so
    // that is a change to this constructor alone.
    public MatchSocket(int port, bool useIPv6 = false)
    {
        if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port),
            "a match socket must bind a REAL port: 0 would hide the assigned port from the peer.");
        _inner = new UdpSocket(port, useIPv6);
        Port = port;
    }

    // Binds a free port and reports which one, for the client side of the handshake.
    public static MatchSocket BindFree() =>
        new MatchSocket(Backdash.Network.NetUtils.FindFreePort());

    public AddressFamily AddressFamily => _inner.AddressFamily;

    public ValueTask<SocketReceiveFromResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
        _inner.ReceiveAsync(buffer, ct);

    public ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress address, CancellationToken ct) =>
        _inner.ReceiveFromAsync(buffer, address, ct);

    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, SocketAddress addr, CancellationToken ct) =>
        _inner.SendToAsync(buffer, addr, ct);

    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint ep, CancellationToken ct) =>
        _inner.SendToAsync(buffer, ep, ct);

    public void Update() => ((IPeerSocket)_inner).Update();

    // Both deliberately do nothing: see the note above. The session that "closes" or "disposes" this
    // is being torn down BETWEEN matches and the room is not over; the port it holds was announced in
    // Hello and the next match needs it again.
    public void Close() { }

    public void Dispose() { }

    // The room is ending: release the port for good. The one call site is NetSession.Leave.
    public void CloseNow()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.Dispose(); } catch { }
    }
}
