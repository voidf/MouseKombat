using System;
using System.Net;
using System.Net.Sockets;

namespace MouseKombat.Net;

// The host as a dumb UDP forwarder, for the one LAN configuration where it is not a participant: both
// fighting seats are held by CLIENTS and the host holds nothing.
//
// The spec rules out P2P (走房主中转，不做 P2P), so the two clients never learn each other's address —
// they both aim their rollback traffic at the host's match port, and this pushes each datagram to the
// other one. Because the forward is verbatim, each client sees its opponent's packets arriving from the
// host's endpoint, which is exactly the address its session was configured with.
//
// Nothing is parsed. A rollback packet is opaque here, and it has to stay that way: the moment this
// understood the payload it would be a second implementation of the netcode.
//
// Polled and non-blocking like every other transport in this assembly, so it runs on the game's own
// tick with no thread and no lock.
public sealed class UdpMatchRelay : IDisposable
{
    // One MTU-ish datagram. Backdash's own UdpPacketBufferSize defaults well under this.
    private readonly byte[] _rx = new byte[2048];
    private Socket _sock;

    private readonly IPEndPoint _a, _b;
    // Reused receive-from slot. Socket.ReceiveFrom overwrites it, so it is never read after the
    // comparison below.
    private EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

    public int Port { get; }
    public long ForwardedAToB { get; private set; }
    public long ForwardedBToA { get; private set; }
    public long Dropped { get; private set; }     // datagrams from neither fighter
    public string LastError { get; private set; }

    public UdpMatchRelay(int port, IPEndPoint fighterA, IPEndPoint fighterB)
    {
        _a = fighterA ?? throw new ArgumentNullException(nameof(fighterA));
        _b = fighterB ?? throw new ArgumentNullException(nameof(fighterB));
        Port = port;

        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
        _sock.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    // Drains everything queued this tick. A relay that only moved one datagram per frame would cap the
    // match at 60 packets/second per direction, which is below what rollback sends during a rollback
    // storm.
    public void Poll()
    {
        if (_sock == null) return;
        while (true)
        {
            int n;
            try
            {
                if (!_sock.Poll(0, SelectMode.SelectRead)) return;
                n = _sock.ReceiveFrom(_rx, SocketFlags.None, ref _from);
            }
            // A UDP send to a closed port comes back as ConnectionReset on Windows and would otherwise
            // kill the relay: the peer restarting mid-match must not take the room down.
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch (SocketException e) { LastError = e.Message; return; }
            catch (ObjectDisposedException) { _sock = null; return; }

            if (n <= 0) continue;
            if (Same(_from, _a)) { SendTo(_b, n); ForwardedAToB++; }
            else if (Same(_from, _b)) { SendTo(_a, n); ForwardedBToA++; }
            else Dropped++;   // not a fighter: ignored, never forwarded
        }
    }

    private void SendTo(IPEndPoint dst, int n)
    {
        try { _sock.SendTo(_rx, 0, n, SocketFlags.None, dst); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { }
        catch (SocketException e) { LastError = e.Message; }
        catch (ObjectDisposedException) { _sock = null; }
    }

    // A datagram's source must match the endpoint announced in Hello, address AND port, or the relay
    // would forward whatever happened to arrive at the port.
    private static bool Same(EndPoint from, IPEndPoint known) =>
        from is IPEndPoint f && f.Port == known.Port && f.Address.Equals(known.Address);

    public void Dispose()
    {
        try { _sock?.Close(); } catch { }
        _sock = null;
    }
}
