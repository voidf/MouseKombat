using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MouseKombat.Net;

// ---- lobby server integration test ----
//
// The lobby server is a Python process (server/lobby_server.py). This test spawns it, drives it
// with the REAL C# codec (NetCodec + the lobby message classes), and kills it. It is the wire-
// compatibility proof between MouseKombat.Net and the Python server: both implement PROTOCOL.md by
// hand, so each must be verified against the other, not just against itself (server/smoke_test.py
// keeps the Python side honest on its own; this file keeps the C# side honest).
//
// Requires a Python 3.11+ with msgpack on PATH (override with MK_PYTHON) and the repo's server/
// directory (override with MK_SERVER_DIR). Skips cleanly with a note when python is missing.
internal static partial class Program
{
    private sealed class LobbyProbe : IDisposable
    {
        private readonly Socket _sock;
        private readonly FrameReader _reader = new FrameReader();
        private readonly byte[] _rx = new byte[8192];
        private readonly Queue<NetFrame> _frames = new Queue<NetFrame>();

        public string Name { get; }
        public int PlayerId { get; private set; }
        public RoomSnapshot Room { get; private set; }
        public bool Dead { get; private set; }
        public UdpClient Udp { get; }                     // the match socket, announced in Hello
        public int MatchUdpPort => ((IPEndPoint)Udp.Client.LocalEndPoint).Port;

        public LobbyProbe(string name)
        {
            Name = name;
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            Udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        }

        public void Connect(string host, int port) => _sock.Connect(host, port);

        public void Send<T>(MsgType type, T payload) => SendRaw(NetCodec.Encode(type, payload));

        public void SendRaw(byte[] frame)
        {
            int sent = 0;
            while (sent < frame.Length) sent += _sock.Send(frame, sent, frame.Length - sent, SocketFlags.None);
        }

        public void Poll()
        {
            if (Dead) return;
            try
            {
                while (_sock.Poll(0, SelectMode.SelectRead))
                {
                    int n = _sock.Receive(_rx, SocketFlags.None);
                    if (n == 0) { Dead = true; break; }   // END-OF-STREAM IS NOT AN EXCUSE TO DROP
                    _reader.Feed(new ReadOnlySpan<byte>(_rx, 0, n));  // BUFFERED FRAMES: the server's
                }                                                    // last Bye + FIN arrive together
            }
            catch (SocketException) { Dead = true; }
            catch (ObjectDisposedException) { Dead = true; }
            if (_reader.Failed) { Dead = true; return; }
            while (_reader.TryRead(out var f)) _frames.Enqueue(f);
        }

        // Poll until a frame of `type` satisfying `ok` arrives. Frames of other types are
        // skipped; frames of the same type failing the predicate are dropped (sequential phases
        // never need an old state back). Returns default(NetFrame) on timeout.
        public NetFrame Wait<T>(MsgType type, Func<T, bool> ok = null, int timeoutMs = 6000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                Poll();
                while (_frames.Count > 0)
                {
                    var f = _frames.Dequeue();
                    if (f.Type != type) continue;
                    if (ok == null || ok(f.As<T>())) return f;
                }
                Thread.Sleep(1);
            }
            return default;
        }

        public void Dispose()
        {
            _sock.Close();
            Udp.Close();
        }
    }

    private static void LobbyServerTests()
    {
        const string Ver = "0.0.7";
        string python = FindPython();
        string serverPy = FindLobbyServer();
        if (python == null || serverPy == null)
        {
            Console.WriteLine("SKIP lobby server tests (python or server/lobby_server.py not found)");
            return;
        }
        int port = PickPortPair();
        var psi = new ProcessStartInfo(python,
            $"\"{serverPy}\" --host 127.0.0.1 --port {port} --udp-port {port} "
            + $"--game-version {Ver} --protocol {NetVersion.Protocol}")
        {
            WorkingDirectory = Path.GetDirectoryName(serverPy),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);

        // Wait until the TCP listener accepts, or the process dies on us (bad import etc.).
        bool ready = false;
        for (int i = 0; i < 300 && !ready; i++)
        {
            if (proc.HasExited)
            {
                Console.WriteLine($"FAIL lobby server died at startup: {proc.StandardError.ReadToEnd().Trim()}");
                _fail++;
                return;
            }
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Connect("127.0.0.1", port);
                ready = true;
            }
            catch (SocketException) { Thread.Sleep(50); }
        }
        if (!ready) { Console.WriteLine("FAIL lobby server did not come up in time"); _fail++; return; }

        try
        {
            RunLobbyScenarios(port, Ver);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(3000); } catch { }
        }
    }

    private static void RunLobbyScenarios(int port, string ver)
    {
        // ---- version gate: a mismatched game version is refused before anything else ----
        using (var bad = new LobbyProbe("bad"))
        {
            bad.Connect("127.0.0.1", port);
            bad.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = "0.0.6", Name = "旧版" });
            var rej = bad.Wait<Rejected>(MsgType.Rejected);
            Check(rej.Body != null && rej.As<Rejected>().Reason == "游戏版本不一致",
                "lobby: a wrong game version is refused at the handshake");
            Check(rej.As<Rejected>().HostGameVersion == ver,
                "lobby: Rejected carries the server's version for display");
        }

        // ---- create: the connection becomes the host player ----
        using var host = new LobbyProbe("房主");
        host.Connect("127.0.0.1", port);
        host.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "房主", MatchUdpPort = host.MatchUdpPort });
        host.Send(MsgType.LobbyCreate, new LobbyCreate { MaxPlayers = 4, Password = "", Searchable = true });
        var w = host.Wait<Welcome>(MsgType.Welcome, ww => ww.IsHost);
        Check(w.Body != null, "lobby: create is answered with Welcome isHost=true");
        int hostId = w.As<Welcome>().PlayerId;
        string roomId = w.As<Welcome>().Room.RoomId;
        Check(roomId.Length == 6 && int.TryParse(roomId, out _), "lobby: room id is 6 digits");
        Check(w.As<Welcome>().Room.MaxPlayers == 4, "lobby: snapshot carries maxPlayers=4");

        // ---- join + the host player's LobbyPlayerJoined hook ----
        using var mem = new LobbyProbe("玩家乙");
        mem.Connect("127.0.0.1", port);
        mem.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "玩家乙", MatchUdpPort = mem.MatchUdpPort });
        mem.Send(MsgType.LobbyJoin, new LobbyJoin { RoomId = roomId, Password = "" });
        w = mem.Wait<Welcome>(MsgType.Welcome, ww => !ww.IsHost);
        Check(w.Body != null, "lobby: join is answered with Welcome isHost=false");
        int memId = w.As<Welcome>().PlayerId;
        // The join broadcast arrives BEFORE LobbyPlayerJoined; waiting for the event first would
        // discard the RoomState, so wait for the state, then for the hook.
        var both = host.Wait<RoomSnapshot>(MsgType.RoomState, s => s.Players.Length == 2);
        Check(both.Body != null, "lobby: RoomState broadcast shows 2 players");
        var joined = host.Wait<LobbyPlayerJoined>(MsgType.LobbyPlayerJoined, j => j.PlayerId == memId);
        Check(joined.Body != null, "lobby: the host player is told LobbyPlayerJoined");

        // ---- seats, characters, match start ----
        host.Send(MsgType.SeatClaim, new SeatClaim { Seat = 0 });
        mem.Send(MsgType.SeatClaim, new SeatClaim { Seat = 1 });
        host.Wait<RoomSnapshot>(MsgType.RoomState,
            s => s.Seats[0].OccupantPlayerId == hostId && s.Seats[1].OccupantPlayerId == memId);
        host.Send(MsgType.CharPick, new CharPick { Character = 0 });
        mem.Send(MsgType.CharPick, new CharPick { Character = 1 });
        host.Wait<RoomSnapshot>(MsgType.RoomState, s => s.Seats[0].Ready && s.Seats[1].Ready);

        host.Send(MsgType.MatchStart, new MatchStart { P2StartX = 651f });   // non-default geometry on purpose
        var sm = mem.Wait<StartMatch>(MsgType.StartMatch);
        Check(sm.Body != null, "lobby: StartMatch is broadcast to members");
        var setup = sm.As<StartMatch>();
        Check(setup.MatchUdpPort == port, "lobby: MatchUdpPort is the server's UDP port");
        Check(!setup.SpectatingAvailable, "lobby: SpectatingAvailable=false (spectate via data stream)");
        Check(setup.P2StartX == 651f, "lobby: the host player's geometry survives the trip");
        Check(setup.Seat0Endpoint == "" && setup.Seat1Endpoint == "",
            "lobby: seat endpoints stay empty (the server is the hub)");
        var running = mem.Wait<RoomSnapshot>(MsgType.RoomState, s => s.MatchRunning);
        Check(running.Body != null, "lobby: snapshot says MatchRunning");
        int roomIdNum = int.Parse(roomId);

        // ---- UDP relay: fighter A -> B -> A through the server ----
        byte[] Wrap(int src, int dst, byte[] payload)
        {
            var b = new byte[6 + payload.Length];
            b[0] = (byte)roomIdNum; b[1] = (byte)(roomIdNum >> 8);
            b[2] = (byte)(roomIdNum >> 16); b[3] = (byte)(roomIdNum >> 24);
            b[4] = (byte)src; b[5] = (byte)dst;
            Buffer.BlockCopy(payload, 0, b, 6, payload.Length);
            return b;
        }

        bool roundtripOk = false;
        var ua = host.Udp;
        var ub = mem.Udp;
        ua.Client.ReceiveTimeout = ub.Client.ReceiveTimeout = 4000;
        var from = new IPEndPoint(IPAddress.Any, 0);
        byte[] pa = { 1, 2, 3, 4 };
        ua.Send(Wrap(0, 1, pa), 6 + pa.Length, "127.0.0.1", port);
        try { roundtripOk = ub.Receive(ref from).Length == pa.Length; } catch (SocketException) { }
        if (roundtripOk)
        {
            byte[] pb = { 9, 8, 7 };
            ub.Send(Wrap(1, 0, pb), 6 + pb.Length, "127.0.0.1", port);
            try { roundtripOk = ua.Receive(ref from).Length == pb.Length; } catch (SocketException) { roundtripOk = false; }
        }
        Check(roundtripOk, "lobby: UDP relay forwards both directions");

        // ---- catch-up routing: HostSendTo (host player -> member) ----
        var inputs = new MatchInputs { StartFrame = 100, P1 = new ushort[] { 1, 2, 3 }, P2 = new ushort[] { 4, 5 } };
        var body = NetCodec.Encode(MsgType.MatchInputs, inputs);   // strip the 5-byte frame header:
        var hostTo = new HostSendTo { TargetPlayerId = memId, Type = (byte)MsgType.MatchInputs,
                                      Body = new byte[body.Length - 5] };
        Buffer.BlockCopy(body, 5, hostTo.Body, 0, hostTo.Body.Length);
        host.Send(MsgType.HostSendTo, hostTo);
        var got = mem.Wait<MatchInputs>(MsgType.MatchInputs, m => m.StartFrame == 100);
        Check(got.Body != null && got.As<MatchInputs>().P1.Length == 3 && got.As<MatchInputs>().P2[1] == 5,
            "lobby: HostSendTo delivers a MatchInputs frame verbatim");

        // ---- catch-up routing: fighter -> host player (MatchInputReport) ----
        var report = new MatchInputReport { StartFrame = 50, P1 = new ushort[] { 7 }, P2 = new ushort[] { 8 },
            P2StartX = 651f };
        mem.Send(MsgType.MatchInputReport, report);
        var gotRep = host.Wait<MatchInputReport>(MsgType.MatchInputReport, r => r.StartFrame == 50);
        Check(gotRep.Body != null && gotRep.As<MatchInputReport>().P2StartX == 651f,
            "lobby: the fighter's MatchInputReport reaches the host player");

        // ---- match end: MatchResult -> MatchEnded + cleared seats ----
        host.Send(MsgType.MatchResult, new MatchResult { WinnerSeat = 0 });
        var ended = mem.Wait<MatchEnded>(MsgType.MatchEnded);
        Check(ended.Body != null && ended.As<MatchEnded>().WinnerSeat == 0,
            "lobby: MatchResult ends the match with MatchEnded");
        var cleared = mem.Wait<RoomSnapshot>(MsgType.RoomState,
            s => !s.MatchRunning && s.Seats[0].OccupantPlayerId == 0 && s.Seats[1].OccupantPlayerId == 0);
        Check(cleared.Body != null, "lobby: seats cleared after the match");

        // ---- room list: only searchable rooms, paged ----
        using var lister = new LobbyProbe("浏览者");
        lister.Connect("127.0.0.1", port);
        lister.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "浏览者" });
        lister.Send(MsgType.LobbyList, new LobbyList { Page = 0 });
        var page = lister.Wait<LobbyRooms>(MsgType.LobbyRooms);
        Check(page.Body != null && page.As<LobbyRooms>().Page == 0, "lobby: room list answers a page");
        var entry = Array.Find(page.As<LobbyRooms>().Entries, e => e.RoomId == roomId);
        Check(entry != null && entry.HostName == "房主" && entry.Players == 2 && entry.MaxPlayers == 4,
            "lobby: the room appears with host name / player counts");

        // ---- host player leaving destroys the room ----
        host.Send(MsgType.Bye, new Bye { Reason = "房主离开" });
        var bye = mem.Wait<Bye>(MsgType.Bye);
        Check(bye.Body != null, "lobby: host leaving broadcasts Bye to members");
        bool eof = false;
        long deadline = Environment.TickCount64 + 3000;
        while (!eof && Environment.TickCount64 < deadline)
        {
            mem.Poll();
            eof = mem.Dead;
            if (!eof) Thread.Sleep(10);
        }
        Check(eof, "lobby: the server closes the member's connection");
    }

    private static string FindPython()
    {
        string env = Environment.GetEnvironmentVariable("MK_PYTHON");
        if (!string.IsNullOrEmpty(env)) return env;
        foreach (string cand in new[] { "python", "python3" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(cand, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p.WaitForExit(3000) && p.ExitCode == 0) return cand;
            }
            catch { /* not on PATH; try the next */ }
        }
        return null;
    }

    private static string FindLobbyServer()
    {
        string env = Environment.GetEnvironmentVariable("MK_SERVER_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "lobby_server.py")))
            return Path.Combine(env, "lobby_server.py");
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "server", "lobby_server.py");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static int PickPortPair()
    {
        for (int i = 0; i < 20; i++)
        {
            var t = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            t.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int p = ((IPEndPoint)t.LocalEndPoint).Port;
            t.Close();
            var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { u.Bind(new IPEndPoint(IPAddress.Loopback, p)); u.Close(); return p; }
            catch (SocketException) { u.Close(); }
        }
        throw new InvalidOperationException("no free TCP+UDP port pair for the lobby server");
    }
}
