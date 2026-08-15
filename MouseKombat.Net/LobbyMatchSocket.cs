using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Backdash.Network.Client;
namespace MouseKombat.Net;

// The match-channel envelope for a lobby game (PROTOCOL.md § Relay):
//
//     u32  roomId   little-endian, the numeric room id (RoomSnapshot.RoomId is the 6-digit string)
//     u8   srcSlot  the sender's seat, 0 or 1
//     u8   dstSlot  the target seat, 0 or 1 (never equal to srcSlot)
//     ...  opaque rollback payload
//
// Every datagram between the fighters goes through the lobby server, which unwraps the envelope
// and forwards the payload to the dstSlot holder. The envelope is pure logic so the test runner can
// pin its bytes without any sockets.
public static class LobbyEnvelope
{
    public const int HeaderBytes = 6;

    public static byte[] Pack(int roomId, int srcSlot, int dstSlot, ReadOnlySpan<byte> payload)
    {
        var b = new byte[HeaderBytes + payload.Length];
        b[0] = (byte)roomId; b[1] = (byte)(roomId >> 8);
        b[2] = (byte)(roomId >> 16); b[3] = (byte)(roomId >> 24);
        b[4] = (byte)srcSlot; b[5] = (byte)dstSlot;
        payload.CopyTo(b.AsSpan(HeaderBytes));
        return b;
    }

    // Returns false when the datagram is too short to be ours (a foreign UDP packet hitting the
    // match port must not crash the receive path).
    public static bool TryUnpack(ReadOnlySpan<byte> data, out int roomId, out int srcSlot,
                                 out int dstSlot, out ReadOnlySpan<byte> payload)
    {
        if (data.Length < HeaderBytes)
        {
            roomId = 0; srcSlot = 0; dstSlot = 0; payload = default;
            return false;
        }
        roomId = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        srcSlot = data[4];
        dstSlot = data[5];
        payload = data.Slice(HeaderBytes);
        return true;
    }
}

// A socket that wraps every rollback datagram in the lobby envelope, for the ONE peer a lobby
// machine's session ever has: the server. In a lobby game the server sits in the middle, so the
// session's peer endpoint is the server's UDP port, and every datagram carries the same envelope —
// {roomId, mySeat, otherSeat} — in both directions. There are no spectators over UDP (lobby
// spectating is the data stream), so the envelope never changes over the life of the session.
//
// Backdash identifies peers by SOURCE endpoint; all datagrams arrive from the server's endpoint, so
// the single-peer session is unaffected by the stripping.
public sealed class LobbyMatchSocket : IPeerSocket, IDisposable
{
    // Backdash datagrams are MTU-ish; the server's own relay buffer is 2 KiB. A datagram bigger
    // than this is dropped rather than torn (it would never have crossed the relay anyway).
    private const int ScratchSize = 4096;

    private readonly IPeerSocket _inner;
    private readonly byte[] _envelope = new byte[LobbyEnvelope.HeaderBytes];

    public LobbyMatchSocket(IPeerSocket inner, int roomId, int srcSlot, int dstSlot)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _envelope[0] = (byte)roomId; _envelope[1] = (byte)(roomId >> 8);
        _envelope[2] = (byte)(roomId >> 16); _envelope[3] = (byte)(roomId >> 24);
        _envelope[4] = (byte)srcSlot; _envelope[5] = (byte)dstSlot;
    }

    public AddressFamily AddressFamily => _inner.AddressFamily;

    public int Port => _inner.Port;

    // The server strips the envelope before forwarding (it relays data[6:] verbatim), so a received
    // datagram IS the raw rollback payload — pass it through untouched. Stripping here would cut
    // six bytes out of every frame (16-byte handshake packets became 10 and never synchronized).
    public async ValueTask<SocketReceiveFromResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        byte[] scratch = new byte[ScratchSize];   // per call: concurrent receives must not share
        var res = await _inner.ReceiveAsync(scratch, ct);
        scratch.AsSpan(0, res.ReceivedBytes).CopyTo(buffer.Span);
        return new SocketReceiveFromResult { ReceivedBytes = res.ReceivedBytes, RemoteEndPoint = res.RemoteEndPoint };
    }

    public async ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress address, CancellationToken ct)
    {
        byte[] scratch = new byte[ScratchSize];   // per call: concurrent receives must not share
        int n = await _inner.ReceiveFromAsync(scratch, address, ct);
        scratch.AsSpan(0, n).CopyTo(buffer.Span);
        return n;
    }

    // Backdash's PeerClient asserts that the bytes it handed us equal the bytes it believes it
    // sent (sentSize == bodySize), so the returned count must be the PAYLOAD length, not the
    // enveloped length — the +6 header is ours, not the session's.
    public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, SocketAddress addr, CancellationToken ct)
    {
        var outbound = PackInto(buffer);
        int sent = await _inner.SendToAsync(outbound, addr, ct);
        return Math.Max(0, sent - LobbyEnvelope.HeaderBytes);
    }

    public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint ep, CancellationToken ct)
    {
        var outbound = PackInto(buffer);
        int sent = await _inner.SendToAsync(outbound, ep, ct);
        return Math.Max(0, sent - LobbyEnvelope.HeaderBytes);
    }

    private ReadOnlyMemory<byte> PackInto(ReadOnlyMemory<byte> payload)
    {
        // A payload too big for the scratch cannot be enveloped; send nothing rather than an
        // unwrapped frame the server would misroute (the relay's own buffer is 2 KiB anyway).
        if (payload.Length + LobbyEnvelope.HeaderBytes > ScratchSize) return ReadOnlyMemory<byte>.Empty;
        var outbound = new byte[payload.Length + LobbyEnvelope.HeaderBytes];
        outbound[0] = _envelope[0]; outbound[1] = _envelope[1];
        outbound[2] = _envelope[2]; outbound[3] = _envelope[3];
        outbound[4] = _envelope[4]; outbound[5] = _envelope[5];
        payload.Span.CopyTo(outbound.AsSpan(LobbyEnvelope.HeaderBytes));
        return outbound;
    }

    public void Update() => _inner.Update();

    // Both delegate to the inner socket, whose Close/Dispose are deliberately no-ops between
    // matches (see MatchSocket): the lobby socket is created PER MATCH, and its disposal must not
    // release the room's announced match port.
    public void Close() => _inner.Close();
    public void Dispose() => _inner.Dispose();
}
