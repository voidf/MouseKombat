using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Backdash.Network.Client;
using MouseKombat.Net;
using MouseKombat.Sim;

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
        // A per-run temp dir: the account DB must not leak between runs (scores would break the
        // "fresh account starts at 1000" assertions), and the matchmaking heartbeat runs fast so
        // the pairing scenarios do not crawl.
        string tmpDir = Path.Combine(Path.GetTempPath(), "mk_lobby_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string cfgPath = Path.Combine(tmpDir, "config.json");
        string dbPath = Path.Combine(tmpDir, "accounts.db");
        File.WriteAllText(cfgPath,
            "{\"matchmaking\":{\"tick_interval_seconds\":0.2,\"auto_start\":true,\"auto_characters\":[0,1,2]},"
            + "\"ping\":{\"interval_seconds\":0.2}}");
        var psi = new ProcessStartInfo(python,
            $"\"{serverPy}\" --host 127.0.0.1 --port {port} --udp-port {port} "
            + $"--game-version {Ver} --protocol {NetVersion.Protocol} "
            + $"--config \"{cfgPath}\" --db \"{dbPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(serverPy),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        // Drain the server's output CONTINUOUSLY on a background thread. Python's logging blocks
        // once the redirected pipe's buffer fills (8 KB), which freezes the asyncio event loop —
        // the server then silently stops accepting connections and the lobby "hangs" for every
        // following client. The queue also preserves the log for the failure dump.
        var serverLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Drain(TextReader r)
        {
            try
            {
                string line;
                while ((line = r.ReadLine()) != null) serverLog.Enqueue(line);
            }
            catch { }
        }
        var drainErr = new Thread(() => Drain(proc.StandardError)) { IsBackground = true };
        var drainOut = new Thread(() => Drain(proc.StandardOutput)) { IsBackground = true };
        drainErr.Start();
        drainOut.Start();

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
            LobbyEnvelopeTests();
            RunLobbyClientScenario(port, Ver);
            UdpSocketBareSendTest();
            LobbyRelayMatchTest(port, Ver);
            LobbyAiVsHumanRelayTest(port, Ver);
            LobbyRematchRelayTest(port, Ver);
            LobbyWatcherSlotTest(port, Ver);
            RunAccountScenarios(port, Ver);
            LobbyAccountClientTest(port, Ver);
        }
        catch (Exception e)
        {
            // A crash inside a scenario must still kill the server: an NRE leaked a python process
            // (and its port) on every failing run before this guard existed.
            Console.WriteLine($"FAIL lobby scenario crashed: {e.Message}");
            _fail++;
        }
        finally
        {
            // Diagnose a failed relay by dumping what the server actually logged (dropped UDP,
            // refused matches, etc.) — the room channel's stdout is redirected and would otherwise
            // vanish on failure. Kill FIRST: the process is still alive and ReadToEnd blocks on EOF.
            if (_fail > 0)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(2000); } catch { }
                Console.WriteLine("--- lobby server log ---");
                foreach (string line in serverLog) Console.WriteLine(line);
            }
            else
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(3000); } catch { }
            }
        }
    }

    // A seatless WATCHER that leaves mid-match must free its human slot. Reserving it (the rule that
    // belongs to FIGHTERS, whose seat the opponent is still simulating against) is what left a room
    // advertising free space while refusing every joiner with 房间已满 — and every watcher that came
    // and went leaked one more slot. Driven through the real LobbyRoomClient against the real server.
    private static void LobbyWatcherSlotTest(int port, string ver)
    {
        using var host = new LobbyRoomClient { MatchUdpPort = 46010 };
        host.Connect("127.0.0.1", port, "看客房主", ver);
        host.CreateRoom(3, "", true);        // 3 humans: host + fighter + exactly one watcher
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected),
            "watcher slot: room created with a 3-human cap");
        string roomId = host.Room.RoomId;

        using var fighter = new LobbyRoomClient { MatchUdpPort = 46011 };
        fighter.Connect("127.0.0.1", port, "拳手", ver);
        fighter.JoinRoom(roomId, "");
        Check(LobbyWait(fighter, LobbyRoomClient.EventKind.Connected), "watcher slot: the fighter joined");

        host.ClaimSeat(0);
        fighter.ClaimSeat(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[1].OccupantPlayerId == fighter.PlayerId);
        host.PickCharacter(0);
        fighter.PickCharacter(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        host.RequestMatchStart(new MatchStart());
        Check(LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
                   f => f.As<RoomSnapshot>().MatchRunning),
            "watcher slot: the match is running");

        // the third human joins MID-MATCH as a watcher, which fills the room
        using var watcher = new LobbyRoomClient { MatchUdpPort = 46012 };
        watcher.Connect("127.0.0.1", port, "观战者", ver);
        watcher.JoinRoom(roomId, "");
        Check(LobbyWait(watcher, LobbyRoomClient.EventKind.Connected),
            "watcher slot: the watcher joined mid-match");
        int watcherId = watcher.PlayerId;
        using var latecomer = new LobbyRoomClient { MatchUdpPort = 46013 };
        latecomer.Connect("127.0.0.1", port, "迟到者", ver);
        latecomer.JoinRoom(roomId, "");
        Check(LobbyWait(latecomer, LobbyRoomClient.EventKind.Rejected, null, 3000),
            "watcher slot: a full room refuses the next joiner");

        // ESC on the spectate screen: leave mid-match, holding no seat
        watcher.LeaveRoom("玩家离开了房间");
        Check(LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
                   f => Array.Find(f.As<RoomSnapshot>().Players, p => p.PlayerId == watcherId) == null),
            "watcher slot: the watcher is removed from the room, not reserved");
        Check(host.Room != null && host.Room.Seats[1].OccupantPlayerId == fighter.PlayerId
              && host.Room.MatchRunning,
            "watcher slot: the FIGHTER still holds its seat while the match runs");
        latecomer.JoinRoom(roomId, "");
        Check(LobbyWait(latecomer, LobbyRoomClient.EventKind.Connected),
            "watcher slot: the freed slot lets another player in to watch");

        // closing the room mid-match reaches the fighter as a ROOM event, not a lost connection
        host.LeaveRoom("主持玩家已离开房间");
        Check(LobbyWait(fighter, LobbyRoomClient.EventKind.RoomClosed),
            "watcher slot: closing the room mid-match tells the fighter why");
        Check(fighter.IsConnected && !fighter.IsInRoom,
            "watcher slot: the fighter's lobby connection survives the room");
    }

    // The match-channel envelope (PROTOCOL.md § Relay): pure byte contract, no sockets.
    private static void LobbyEnvelopeTests()
    {
        byte[] env = LobbyEnvelope.Pack(482275, 0, 1, new byte[] { 9, 8, 7 });
        Check(env.Length == 9, "lobby envelope: 6 header + 3 payload bytes");
        Check(BitConverter.IsLittleEndian && BitConverter.ToInt32(env, 0) == 482275,
            "lobby envelope: room id is the little-endian u32");
        Check(env[4] == 0 && env[5] == 1, "lobby envelope: src/dst slots in the right places");

        bool ok = LobbyEnvelope.TryUnpack(env, out int rid, out int src, out int dst,
                                          out ReadOnlySpan<byte> payload);
        Check(ok && rid == 482275 && src == 0 && dst == 1 && payload.Length == 3 && payload[2] == 7,
            "lobby envelope: unpack round-trips the envelope");
        Check(!LobbyEnvelope.TryUnpack(new byte[] { 1, 2, 3 }, out _, out _, out _, out _),
            "lobby envelope: a foreign short datagram is refused, not crashed on");
    }

    // Poll a lobby client until an event of `kind` arrives. Frames of other kinds are skipped;
    // a frame-less event (e.g. RoomChanged right after Welcome) cannot satisfy a frame predicate.
    private static bool LobbyWait(LobbyRoomClient c, LobbyRoomClient.EventKind kind,
                                  Func<NetFrame, bool> ok = null, int timeoutMs = 6000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            c.Poll();
            while (c.TryDequeueEvent(out var e))
            {
                if (e.Kind != kind) continue;
                if (ok == null) return true;
                if (e.Frame.Body == null) continue;
                if (ok(e.Frame)) return true;
            }
            Thread.Sleep(1);
        }
        return false;
    }

    // Two REAL rollback sessions through the REAL lobby server. The fighters' UDP goes through the
    // server's relay wrapped in the LobbyMatchSocket envelope — the exact path a lobby game uses —
    // so this is the assertion that the server relay + the client envelope actually synchronize a
    // match (the loopback version of the user's 两个真人玩家卡在同步中). The room is driven by the
    // real LobbyRoomClient up to MatchStart, then the sessions take over on the announced ports.
    private static void LobbyRelayMatchTest(int port, string ver)
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        using var sockA = BindUdp();
        using var sockB = BindUdp();

        using var host = new LobbyRoomClient { MatchUdpPort = sockA.Port };
        host.Connect("127.0.0.1", port, "房主", ver);
        host.CreateRoom(4, "", true);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected), "lobby relay: host player created the room");
        string roomId = host.Room.RoomId;

        using var mem = new LobbyRoomClient { MatchUdpPort = sockB.Port };
        mem.Connect("127.0.0.1", port, "玩家乙", ver);
        mem.JoinRoom(roomId, "");
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.Connected), "lobby relay: member joined");

        host.ClaimSeat(0);
        mem.ClaimSeat(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == host.PlayerId);
        LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[1].OccupantPlayerId == mem.PlayerId);
        host.PickCharacter(0);
        mem.PickCharacter(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        host.RequestMatchStart(new MatchStart());
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.MatchStarting), "lobby relay: match started");
        LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged, f => f.As<RoomSnapshot>().MatchRunning);

        int roomIdNum = int.Parse(roomId);
        var epServer = new IPEndPoint(IPAddress.Loopback, port);   // server UDP port == TCP port

        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);

        // Each session believes its opponent lives at the SERVER's endpoint; the envelope carries
        // {roomId, mySeat, otherSeat} so the server knows where to forward.
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockA, roomIdNum, 0, 1),
            InputDelayFrames = delay,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockB, roomIdNum, 1, 0),
            InputDelayFrames = delay,
        });

        int target = NetFrames + NetOvershoot;
        var sw = Stopwatch.StartNew();
        bool ok = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            host.Poll();
            mem.Poll();
            viewA.SetLocalFrame(a.Frame); a.Tick();
            viewB.SetLocalFrame(b.Frame); b.Tick();
            if (a.Frame >= target && b.Frame >= target) { ok = true; break; }
            Thread.Yield();
        }

        Check(ok, $"lobby relay: both fighters reached frame {target} (A={a.Frame} B={b.Frame})");
        Check(a.Synchronized && b.Synchronized,
            "lobby relay: both fighters synchronized through the server");
        Check(SameValues(expected, viewA.Value, NetFrames, out string whyA),
            "lobby relay: side A matches a never-rewound run" + whyA);
        Check(SameValues(expected, viewB.Value, NetFrames, out string whyB),
            "lobby relay: side B matches a never-rewound run" + whyB);
        DrainEvents(a, "lobby relay A");
        DrainEvents(b, "lobby relay B");
        Thread.Sleep(600);   // let the sessions' final in-flight packets drain before the next match
    }

    // Minimal probe: does a Backdash UdpSocket's SendToAsync actually emit datagrams? The failing
    // rematch sessions "send" with no exception yet nothing reaches the server — if a bare
    // UdpSocket cannot deliver either, the bug is inside Backdash's socket, not our envelope.
    private static void UdpSocketBareSendTest()
    {
        const int attempts = 5;
        using var sock = BindUdp();
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var dst = (IPEndPoint)probe.Client.LocalEndPoint;
        byte[] payload = { 1, 2, 3, 4 };
        var from = new IPEndPoint(IPAddress.Any, 0);
        bool ok = false;
        for (int i = 0; i < attempts; i++)
        {
            var t = sock.SendToAsync(payload, dst, CancellationToken.None);
            t.AsTask().Wait(2000);
            if (t.IsCompletedSuccessfully && t.Result == payload.Length)
            {
                probe.Client.ReceiveTimeout = 500;
                try { probe.Receive(ref from); ok = true; break; } catch (SocketException) { }
            }
            Thread.Sleep(20);
        }
        Console.WriteLine($"[dbg] UdpSocket bare send (EndPoint): {(ok ? "DELIVERED" : "NOT DELIVERED")}");
        Check(ok, "udp-socket probe: a bare Backdash UdpSocket delivers its datagrams");

        // The SOCKETADDRESS overload is what a Backdash session actually calls (see the SEND logs);
        // it must deliver too.
        bool okAddr = false;
        for (int i = 0; i < attempts; i++)
        {
            var sa = new SocketAddress(AddressFamily.InterNetwork);
            sa[2] = (byte)(dst.Port >> 8); sa[3] = (byte)dst.Port;
            sa[4] = dst.Address.GetAddressBytes()[0]; sa[5] = dst.Address.GetAddressBytes()[1];
            sa[6] = dst.Address.GetAddressBytes()[2]; sa[7] = dst.Address.GetAddressBytes()[3];
            var t = sock.SendToAsync(payload, sa, CancellationToken.None);
            t.AsTask().Wait(2000);
            if (t.IsCompletedSuccessfully && t.Result == payload.Length)
            {
                probe.Client.ReceiveTimeout = 500;
                try { probe.Receive(ref from); okAddr = true; break; } catch (SocketException) { }
            }
            Thread.Sleep(20);
        }
        Console.WriteLine($"[dbg] UdpSocket bare send (SocketAddress): {(okAddr ? "DELIVERED" : "NOT DELIVERED")}");
        Check(okAddr, "udp-socket probe: the SocketAddress overload delivers too");
    }

    // The user's AI-vs-human lobby match: the HOST PLAYER holds NO seat and drives an AI seat
    // instead, against a human member (machine B). The host machine's session therefore sends
    // datagrams claiming the AI seat as src — the server must forward those (only the host may
    // drive an AI seat, but its packets are as real as any fighter's). This is the configuration
    // that froze at "正在等待对方同步" in the user's room.
    private static void LobbyAiVsHumanRelayTest(int port, string ver)
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        using var sockA = BindUdp();
        using var sockB = BindUdp();

        using var host = new LobbyRoomClient { MatchUdpPort = sockA.Port };
        host.Connect("127.0.0.1", port, "房主", ver);
        host.CreateRoom(4, "", true);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected), "lobby ai-vs-human: room created");
        string roomId = host.Room.RoomId;

        using var mem = new LobbyRoomClient { MatchUdpPort = sockB.Port };
        mem.Connect("127.0.0.1", port, "玩家乙", ver);
        mem.JoinRoom(roomId, "");
        LobbyWait(mem, LobbyRoomClient.EventKind.Connected);

        // seat 0 = AI driven by the host player's machine (the host holds no seat itself),
        // seat 1 = the human member.
        host.AddAi(0, 0, "");
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].IsAi);
        mem.ClaimSeat(1);
        mem.PickCharacter(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        host.RequestMatchStart(new MatchStart());
        LobbyWait(host, LobbyRoomClient.EventKind.MatchStarting);
        LobbyWait(mem, LobbyRoomClient.EventKind.MatchStarting);

        int roomIdNum = int.Parse(roomId);
        var epServer = new IPEndPoint(IPAddress.Loopback, port);

        // The host machine drives the AI seat (local) and reaches the human through the server.
        // The human machine drives its own seat, also through the server.
        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockA, roomIdNum, 0, 1),
            InputDelayFrames = delay,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockB, roomIdNum, 1, 0),
            InputDelayFrames = delay,
        });

        int target = NetFrames + NetOvershoot;
        var sw = Stopwatch.StartNew();
        bool ok = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            host.Poll();
            mem.Poll();
            viewA.SetLocalFrame(a.Frame); a.Tick();
            viewB.SetLocalFrame(b.Frame); b.Tick();
            if (a.Frame >= target && b.Frame >= target) { ok = true; break; }
            Thread.Yield();
        }

        Check(ok, $"lobby ai-vs-human: the AI-vs-human match synchronizes (A={a.Frame} B={b.Frame})");
        Check(a.Synchronized && b.Synchronized,
            "lobby ai-vs-human: both sides synchronized through the server");
        Check(SameValues(expected, viewA.Value, NetFrames, out string whyA),
            "lobby ai-vs-human: host side (AI) matches a never-rewound run" + whyA);
        Check(SameValues(expected, viewB.Value, NetFrames, out string whyB),
            "lobby ai-vs-human: human side matches a never-rewound run" + whyB);
        DrainEvents(a, "lobby ai-vs-human A");
        DrainEvents(b, "lobby ai-vs-human B");
        Thread.Sleep(600);   // see above
    }

    // The user's exact lobby rematch scenario: a first match driven ENTIRELY by the host player
    // (its own seat + an AI seat — a Local session with no UDP and no socket at all), then a second
    // match against a human in the same room. The second match must synchronize through the server
    // exactly like a fresh one; any leftover state from the Local first match would freeze it at
    // "等待对方同步".
    private static void LobbyRematchRelayTest(int port, string ver)
    {
        const int delay = 2;
        using var sockA = BindUdp();
        using var sockB = BindUdp();

        using var host = new LobbyRoomClient { MatchUdpPort = sockA.Port };
        host.Connect("127.0.0.1", port, "房主", ver);
        host.CreateRoom(4, "", true);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected), "lobby rematch: room created");
        string roomId = host.Room.RoomId;

        using var mem = new LobbyRoomClient { MatchUdpPort = sockB.Port };
        mem.Connect("127.0.0.1", port, "玩家乙", ver);
        mem.JoinRoom(roomId, "");
        LobbyWait(mem, LobbyRoomClient.EventKind.Connected);

        // ---- first match: host + AI, a Local session (no UDP, no socket) ----
        host.ClaimSeat(0);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == host.PlayerId);
        host.PickCharacter(0);                    // the real seat screen always picks first
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready);
        host.AddAi(1, 0, "");
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[1].IsAi);
        host.RequestMatchStart(new MatchStart());
        Check(LobbyWait(host, LobbyRoomClient.EventKind.MatchStarting),
            "lobby rematch: the host+AI first match starts");

        var simL = MakeSim(240, 520);
        var viewL = new TestPresenter(simL, NetScript);
        using (var local = RollbackMatch.Create(simL, viewL, new MatchNetSetup
        {
            LocalSeat = new[] { true, true },
            Socket = null,
            InputDelayFrames = delay,
        }))
        {
            for (int i = 0; i < 200 && local.Frame < 60; i++)
            {
                viewL.SetLocalFrame(local.Frame);
                local.Tick();
                Thread.Yield();
            }
            Check(local.Frame >= 60,
                $"lobby rematch: the host+AI first match runs (frames={local.Frame})");
        }
        host.ReportMatchResult(0);
        LobbyWait(mem, LobbyRoomClient.EventKind.MatchEnded);
        LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged,
            f => !f.As<RoomSnapshot>().MatchRunning && f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == 0);

        // ---- second match: host + HUMAN, Remote sessions through the server ----
        // The sockets are the SAME ones the first match would have used (sockA/sockB): the real
        // game reuses its ONE MatchSocket for every match of the room (see MatchSocket /
        // NetSession), so this is the exact reuse pattern. The first match was Local (host + AI,
        // no socket at all), which is the user's scenario: any leftover Backdash state would
        // freeze this second match at "等待对方同步".
        host.ClaimSeat(0);
        mem.ClaimSeat(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == host.PlayerId
                 && f.As<RoomSnapshot>().Seats[1].OccupantPlayerId == mem.PlayerId);
        host.PickCharacter(1);
        mem.PickCharacter(2);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        host.RequestMatchStart(new MatchStart());
        Console.WriteLine($"[dbg] first-match MatchStarting wait: "+
            LobbyWait(host, LobbyRoomClient.EventKind.MatchStarting));
        LobbyWait(mem, LobbyRoomClient.EventKind.MatchStarting);

        int roomIdNum = int.Parse(roomId);
        var epServer = new IPEndPoint(IPAddress.Loopback, port);
        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockA, roomIdNum, 0, 1),
            InputDelayFrames = delay,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true },
            RemoteEndPoint = epServer,
            Socket = new LobbyMatchSocket(sockB, roomIdNum, 1, 0),
            InputDelayFrames = delay,
        });

        int target = NetFrames + NetOvershoot;
        var sw = Stopwatch.StartNew();
        bool ok = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            host.Poll();
            mem.Poll();
            viewA.SetLocalFrame(a.Frame); a.Tick();
            viewB.SetLocalFrame(b.Frame); b.Tick();
            if (a.Frame >= target && b.Frame >= target) { ok = true; break; }
            Thread.Yield();
        }
        Check(ok, $"lobby rematch: the second (human vs human) match synchronizes (A={a.Frame} B={b.Frame})");
        Check(a.Synchronized && b.Synchronized,
            "lobby rematch: the second match synchronized through the server");
        DrainEvents(a, "lobby rematch A");
        DrainEvents(b, "lobby rematch B");
        Thread.Sleep(600);   // let the sessions' final in-flight packets drain before the next match
    }

    // The REAL client class (LobbyRoomClient) against the live server: browse, create, join, seats,
    // match start, the catch-up routing and the end. The raw-probe scenario above pins the wire
    // bytes; this one pins the client's behaviour on top of them.
    private static void RunLobbyClientScenario(int port, string ver)
    {
        // browse: one connection pages through the room list
        using (var browser = new LobbyRoomClient { MatchUdpPort = 0 })
        {
            browser.Connect("127.0.0.1", port, "浏览者", ver);
            browser.ListRooms(0);
            Check(LobbyWait(browser, LobbyRoomClient.EventKind.LobbyRooms,
                       f => f.As<LobbyRooms>().Page == 0),
                "lobby client: browses the room list page");
        }

        // create: this connection becomes the host player
        using var host = new LobbyRoomClient { MatchUdpPort = 46000 };
        host.Connect("127.0.0.1", port, "房主", ver);
        host.CreateRoom(4, "", true);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected), "lobby client: create lands in the room");
        Check(host.IsHostPlayer, "lobby client: the creator is the host player");
        string roomId = host.Room.RoomId;
        Check(roomId.Length == 6, "lobby client: the room id is 6 digits");

        // join: a second connection becomes a member, and the host player is told who it is
        using var mem = new LobbyRoomClient { MatchUdpPort = 46001 };
        mem.Connect("127.0.0.1", port, "玩家乙", ver);
        mem.JoinRoom(roomId, "");
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.Connected) && !mem.IsHostPlayer,
            "lobby client: a member joins as a non-host");
        Check(LobbyWait(host, LobbyRoomClient.EventKind.LobbyPlayerJoined,
                   f => f.As<LobbyPlayerJoined>().PlayerId == mem.PlayerId),
            "lobby client: the host player is told who joined");

        // seats + characters + match start
        host.ClaimSeat(0);
        mem.ClaimSeat(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
             f => f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == host.PlayerId);
        LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged,
             f => f.As<RoomSnapshot>().Seats[1].OccupantPlayerId == mem.PlayerId);
        host.PickCharacter(0);
        mem.PickCharacter(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
             f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        host.RequestMatchStart(new MatchStart { P2StartX = 651f });
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.MatchStarting,
                   f => f.As<StartMatch>().P2StartX == 651f),
            "lobby client: StartMatch arrives with the host player's geometry");
        LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged, f => f.As<RoomSnapshot>().MatchRunning);

        // UDP relay with the envelope bytes the match socket actually produces
        bool relayOk = false;
        using (var ua = new UdpClient(new IPEndPoint(IPAddress.Loopback, 46000)))
        using (var ub = new UdpClient(new IPEndPoint(IPAddress.Loopback, 46001)))
        {
            ua.Client.ReceiveTimeout = ub.Client.ReceiveTimeout = 4000;
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] pa = { 1, 2, 3, 4 };
            ua.Send(LobbyEnvelope.Pack(int.Parse(roomId), 0, 1, pa), 6 + pa.Length, "127.0.0.1", port);
            try { relayOk = ub.Receive(ref from).Length == pa.Length; } catch (SocketException) { }
        }
        Check(relayOk, "lobby client: the server relays the enveloped datagram");

        // catch-up routing: the host player streams a MatchInputs frame to the member via HostSendTo
        var inputs = new MatchInputs { StartFrame = 7, P1 = new ushort[] { 1 }, P2 = new ushort[] { 2 } };
        byte[] frame = NetCodec.Encode(MsgType.MatchInputs, inputs);
        var body = new byte[frame.Length - NetCodec.HeaderBytes];
        Buffer.BlockCopy(frame, NetCodec.HeaderBytes, body, 0, body.Length);
        host.HostSendTo(mem.PlayerId, MsgType.MatchInputs, body);
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.MatchInputs,
                   f => f.As<MatchInputs>().StartFrame == 7),
            "lobby client: HostSendTo delivers the stream frame");

        // the fighter's report reaches the host player through the server
        var rep = new MatchInputReport { StartFrame = 3, P1 = new ushort[] { 5 }, P2 = new ushort[] { 6 } };
        mem.Send(MsgType.MatchInputReport, rep);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.InputReport,
                   f => f.As<MatchInputReport>().StartFrame == 3),
            "lobby client: the host player receives the fighter's report");

        // MatchResult ends the match and clears the seats
        host.ReportMatchResult(0);
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.MatchEnded,
                   f => f.As<MatchEnded>().WinnerSeat == 0),
            "lobby client: MatchResult ends the match");
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.RoomChanged,
                   f => !f.As<RoomSnapshot>().MatchRunning
                        && f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == 0),
            "lobby client: seats cleared after the match");

        // AI placement carries its character in the message (the seat was never PickCharacter'd)
        host.AddAi(1, 2, "");
        Check(LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
                   f => f.As<RoomSnapshot>().Seats[1].IsAi
                        && f.As<RoomSnapshot>().Seats[1].Character == 2),
            "lobby client: the host player places an AI with its character");
        host.RemoveAi(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => !f.As<RoomSnapshot>().Seats[1].IsAi);

        // a member leaving KEEPS the lobby connection (back to browse on the same socket)
        host.ClaimSeat(0);
        mem.ClaimSeat(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].OccupantPlayerId == host.PlayerId);
        host.PickCharacter(0);
        mem.PickCharacter(1);
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => f.As<RoomSnapshot>().Seats[0].Ready && f.As<RoomSnapshot>().Seats[1].Ready);
        mem.LeaveRoom("玩家离开了房间");
        LobbyWait(host, LobbyRoomClient.EventKind.RoomChanged,
            f => Array.Find(f.As<RoomSnapshot>().Players, p => p.PlayerId == mem.PlayerId) == null);
        mem.ListRooms(0);
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.LobbyRooms,
                   f => f.As<LobbyRooms>().Page == 0),
            "lobby client: leaving a room keeps the connection browsable");
        mem.JoinRoom(roomId, "");
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.Connected) && !mem.IsHostPlayer,
            "lobby client: the same connection can re-join a room");

        // the host player leaving destroys the room — but KEEPS every connection, its own and the
        // members', all of them in the browse phase (spec: 建房后 ESC 保持大厅连接、回到选房界面;
        // 主持玩家退房后其它玩家保持连接回到选房界面). Closing them would send everyone back to the main
        // menu and make them retype the whole lobby form.
        host.LeaveRoom("主持玩家已离开房间");
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.RoomClosed),
            "lobby client: the member is told the ROOM closed when the host player leaves");
        Check(mem.IsConnected && !mem.IsInRoom && mem.Room == null && mem.PlayerId == 0,
            "lobby client: that member keeps a browsable connection and no room identity");
        mem.ListRooms(0);
        Check(LobbyWait(mem, LobbyRoomClient.EventKind.LobbyRooms,
                   f => Array.TrueForAll(f.As<LobbyRooms>().Entries, e => e.RoomId != roomId)),
            "lobby client: it re-lists on the same socket and the dead room is gone");
        Check(!host.IsHostPlayer && host.Room == null && host.PlayerId == 0,
            "lobby client: a host player that left keeps no room identity");
        host.ListRooms(0);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.LobbyRooms,
                   f => f.As<LobbyRooms>().Page == 0),
            "lobby client: the host player browses again on the same connection");
        host.CreateRoom(4, "", true);
        Check(LobbyWait(host, LobbyRoomClient.EventKind.Connected) && host.IsHostPlayer,
            "lobby client: and can host another room without reconnecting");
        host.Disconnect("测试结束");
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
        string hostHash = new string('a', 32) + "deadbeef";   // stand-in Heroes/ md5
        host.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "房主", MatchUdpPort = host.MatchUdpPort, AssetHash = hostHash });
        host.Send(MsgType.LobbyCreate, new LobbyCreate { MaxPlayers = 4, Password = "", Searchable = true, AssetHash = hostHash });
        var w = host.Wait<Welcome>(MsgType.Welcome, ww => ww.IsHost);
        Check(w.Body != null, "lobby: create is answered with Welcome isHost=true");
        int hostId = w.As<Welcome>().PlayerId;
        string roomId = w.As<Welcome>().Room.RoomId;
        Check(roomId.Length == 6 && int.TryParse(roomId, out _), "lobby: room id is 6 digits");
        Check(w.As<Welcome>().Room.MaxPlayers == 4, "lobby: snapshot carries maxPlayers=4");

        // ---- asset-hash gate: a different Heroes/ hash is refused with BOTH hashes ----
        using (var stranger = new LobbyProbe("外乡人"))
        {
            stranger.Connect("127.0.0.1", port);
            string badHash = new string('f', 32) + "123456";
            stranger.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "外乡人", MatchUdpPort = stranger.MatchUdpPort, AssetHash = badHash });
            stranger.Send(MsgType.LobbyJoin, new LobbyJoin { RoomId = roomId, Password = "" });
            var rej = stranger.Wait<Rejected>(MsgType.Rejected);
            var r = rej.As<Rejected>();
            Check(r.Reason == "资源版本不一致，无法进房", "lobby: an asset-hash mismatch refuses the join");
            Check(r.HostAssetHash == hostHash && r.YourAssetHash == badHash,
                "lobby: the rejection carries both sides' hashes for the popup");
        }
        // the room list shows the room's hash (a browsing connection, not the in-room host)
        using (var hashLister = new LobbyProbe("看房"))
        {
            hashLister.Connect("127.0.0.1", port);
            hashLister.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "看房", MatchUdpPort = hashLister.MatchUdpPort, AssetHash = hostHash });
            hashLister.Send(MsgType.LobbyList, new LobbyList { Page = 0 });
            var listed = hashLister.Wait<LobbyRooms>(MsgType.LobbyRooms, f => f.Entries.Length > 0);
            Check(listed.Body != null && listed.As<LobbyRooms>().Entries[0].AssetHash == hostHash,
                "lobby: the room list entry carries the full asset hash");
        }

        // ---- join + the host player's LobbyPlayerJoined hook ----
        using var mem = new LobbyProbe("玩家乙");
        mem.Connect("127.0.0.1", port);
        mem.Send(MsgType.Hello, new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = "玩家乙", MatchUdpPort = mem.MatchUdpPort, AssetHash = hostHash });
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
        // The envelope is the REAL LobbyEnvelope bytes the match socket wraps around every datagram.
        byte[] Wrap(int src, int dst, byte[] payload) => LobbyEnvelope.Pack(roomIdNum, src, dst, payload);

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

        // ---- host player leaving destroys the room, but NOT the members' connections ----
        // Bye ends the ROOM (spec: 主持玩家退房后其它玩家保持连接回到选房界面刷新房间列表). Closing the
        // member's socket is what used to throw everyone back to the main menu with the lobby form to
        // retype.
        host.Send(MsgType.Bye, new Bye { Reason = "房主离开" });
        var bye = mem.Wait<Bye>(MsgType.Bye);
        Check(bye.Body != null && bye.As<Bye>().Reason == "房主离开",
            "lobby: host leaving broadcasts Bye to members, with the reason");
        mem.Send(MsgType.LobbyList, new LobbyList { Page = 0 });
        var afterBye = mem.Wait<LobbyRooms>(MsgType.LobbyRooms);
        Check(afterBye.Body != null && !mem.Dead,
            "lobby: the member keeps its connection and browses again after the room died");
        Check(Array.TrueForAll(afterBye.As<LobbyRooms>().Entries, e => e.RoomId != roomId),
            "lobby: the destroyed room is gone from the list");
    }

    // ---- accounts (登录/顶号), quick match (快速匹配) and Elo settle, on the raw wire ----
    private static void RunAccountScenarios(int port, string ver)
    {
        Hello Handshake(LobbyProbe p, string name) =>
            new Hello { Protocol = NetVersion.Protocol, GameVersion = ver, Name = name, MatchUdpPort = p.MatchUdpPort };

        // login: a fresh name is registered with the initial score
        ulong accId;
        using (var a = new LobbyProbe("积分甲"))
        {
            a.Connect("127.0.0.1", port);
            a.Send(MsgType.Hello, Handshake(a, "积分甲"));
            var ok = a.Wait<LoginOk>(MsgType.LoginOk);
            Check(ok.Body != null, "account: a fresh name is answered with LoginOk");
            Check(ok.As<LoginOk>().PlayerAccountId > 0,
                $"account: playerid is a positive integer ({ok.As<LoginOk>().PlayerAccountId})");
            Check(ok.As<LoginOk>().Score == 1000,
                $"account: a fresh account starts at 1000 ({ok.As<LoginOk>().Score})");
            accId = ok.As<LoginOk>().PlayerAccountId;

            // duplicate login: the 顶号 handshake
            using (var b = new LobbyProbe("积分甲"))
            {
                b.Connect("127.0.0.1", port);
                b.Send(MsgType.Hello, Handshake(b, "积分甲"));
                var kc = b.Wait<KickConfirm>(MsgType.KickConfirm);
                Check(kc.Body != null && kc.As<KickConfirm>().Name == "积分甲",
                    "account: a same-name login gets KickConfirm");
                Check(kc.Body != null && kc.As<KickConfirm>().Score == 1000,
                    "account: KickConfirm carries the score for the popup");
                b.Send(MsgType.KickLogin, new KickLogin { Name = "积分甲" });
                var ok2 = b.Wait<LoginOk>(MsgType.LoginOk);
                Check(ok2.Body != null && ok2.As<LoginOk>().PlayerAccountId == accId,
                    "account: the confirm binds the SAME account");
                var kick = a.Wait<Kicked>(MsgType.Kicked);
                Check(kick.Body != null && kick.As<Kicked>().Reason.Contains("其他设备"),
                    "account: the old session is told why before the close");
                int spin = 0;
                while (!a.Dead && spin++ < 2000) { a.Poll(); Thread.Sleep(1); }
                Check(a.Dead, "account: the old connection is closed right after Kicked");
            }
        }

        // quick match: two queued players pair into an auto room, the match auto-starts, Elo settles
        using (var p1 = new LobbyProbe("积分乙"))
        using (var p2 = new LobbyProbe("积分丙"))
        {
            p1.Connect("127.0.0.1", port);
            p1.Send(MsgType.Hello, Handshake(p1, "积分乙"));
            p2.Connect("127.0.0.1", port);
            p2.Send(MsgType.Hello, Handshake(p2, "积分丙"));
            p1.Send(MsgType.MatchmakeJoin, new MatchmakeJoin { Name = "积分乙" });
            var st = p1.Wait<MatchmakeStatus>(MsgType.MatchmakeStatus);
            Check(st.Body != null && st.As<MatchmakeStatus>().Searching,
                "mm: joining the pool answers status searching=true");
            p2.Send(MsgType.MatchmakeJoin, new MatchmakeJoin { Name = "积分丙" });
            var w1 = p1.Wait<Welcome>(MsgType.Welcome, ww => ww.Room != null, 8000);
            var w2 = p2.Wait<Welcome>(MsgType.Welcome, ww => ww.Room != null, 8000);
            Check(w1.Body != null && w2.Body != null, "mm: two queued players pair into a room");
            Check(w1.Body != null && w2.Body != null
                  && w1.As<Welcome>().Room.RoomId == w2.As<Welcome>().Room.RoomId,
                "mm: both land in the SAME room");
            Check(w1.Body != null && w1.As<Welcome>().IsHost, "mm: the first queuer is the host player");
            Check(w1.Body != null && Array.TrueForAll(w1.As<Welcome>().Room.Players,
                    pp => pp.AccountId > 0 && pp.Score == 1000),
                "mm: snapshot players carry accountid + score");
            Check(p1.Wait<StartMatch>(MsgType.StartMatch, ss => ss.Room != null, 8000).Body != null,
                "mm: the match auto-starts (no room screen round-trip)");
            Check(p1.Wait<PingStats>(MsgType.PingStats, null, 4000).Body != null,
                "ping: a seated fighter receives RTT stats");
            p1.Send(MsgType.MatchResult, new MatchResult { WinnerSeat = 0 });
            Check(p1.Wait<MatchEnded>(MsgType.MatchEnded, null, 4000).Body != null,
                "mm: MatchEnded follows the result");
            var rs = p1.Wait<RoomSnapshot>(MsgType.RoomState, f => !f.MatchRunning
                && Array.Exists(f.Players, pp => pp.Name == "积分乙" && pp.Score == 1016), 4000);
            Check(rs.Body != null, "mm: Elo settled 1016/984 for equal scores");
            Check(rs.Body != null && Array.Exists(rs.As<RoomSnapshot>().Players,
                    pp => pp.Name == "积分丙" && pp.Score == 984),
                "mm: the loser's score moved to 984");
        }
        // persistence: the settled score survives a reconnect
        using (var a2 = new LobbyProbe("积分乙"))
        {
            a2.Connect("127.0.0.1", port);
            a2.Send(MsgType.Hello, Handshake(a2, "积分乙"));
            Check(a2.Wait<LoginOk>(MsgType.LoginOk, l => l.Score == 1016, 4000).Body != null,
                "persist: the settled score survives a reconnect");
        }

        // 顶号 while IN GAME: the kicked fighter surrenders, the opponent is awarded the match
        using (var e = new LobbyProbe("积分丁"))
        using (var g = new LobbyProbe("积分戊"))
        {
            e.Connect("127.0.0.1", port);
            e.Send(MsgType.Hello, Handshake(e, "积分丁"));
            e.Send(MsgType.LobbyCreate, new LobbyCreate { MaxPlayers = 2, Password = "", Searchable = true });
            var we = e.Wait<Welcome>(MsgType.Welcome, ww => ww.IsHost);
            Check(we.Body != null, "surrender: room ready");
            string roomId = we.As<Welcome>().Room.RoomId;
            g.Connect("127.0.0.1", port);
            g.Send(MsgType.Hello, Handshake(g, "积分戊"));
            g.Send(MsgType.LobbyJoin, new LobbyJoin { RoomId = roomId, Password = "" });
            Check(g.Wait<Welcome>(MsgType.Welcome).Body != null, "surrender: the opponent joined");
            e.Send(MsgType.SeatClaim, new SeatClaim { Seat = 0 });
            g.Send(MsgType.SeatClaim, new SeatClaim { Seat = 1 });
            e.Send(MsgType.CharPick, new CharPick { Character = 0 });
            g.Send(MsgType.CharPick, new CharPick { Character = 1 });
            Check(g.Wait<RoomSnapshot>(MsgType.RoomState, f => f.MatchRunning == false
                    && f.Seats[0].Ready && f.Seats[1].Ready, 4000).Body != null,
                "surrender: both seats ready");
            e.Send(MsgType.MatchStart, new MatchStart());
            Check(g.Wait<StartMatch>(MsgType.StartMatch, null, 4000).Body != null,
                "surrender: the match is running");
            using (var h = new LobbyProbe("积分丁"))
            {
                h.Connect("127.0.0.1", port);
                h.Send(MsgType.Hello, Handshake(h, "积分丁"));
                Check(h.Wait<KickConfirm>(MsgType.KickConfirm, null, 4000).Body != null,
                    "surrender: the popup appears for the in-game account");
                h.Send(MsgType.KickLogin, new KickLogin { Name = "积分丁" });
                Check(h.Wait<LoginOk>(MsgType.LoginOk, null, 4000).Body != null,
                    "surrender: the new session is bound");
            }
            Check(e.Wait<Kicked>(MsgType.Kicked, null, 4000).Body != null,
                "surrender: the old session is told");
            var meG = g.Wait<MatchEnded>(MsgType.MatchEnded, null, 4000);
            Check(meG.Body != null && meG.As<MatchEnded>().WinnerSeat == 1,
                "surrender: the opponent is awarded the match (winner seat 1)");
            Check(g.Wait<RoomSnapshot>(MsgType.RoomState, f => !f.MatchRunning
                    && Array.Exists(f.Players, pp => pp.Name == "积分戊" && pp.Score == 1016)
                    && Array.Exists(f.Players, pp => pp.Name == "积分丁" && pp.Score == 984),
                4000).Body != null,
                "surrender: the Elo settled against the kicked player");
            int spin = 0;
            while (!e.Dead && spin++ < 2000) { e.Poll(); Thread.Sleep(1); }
            Check(e.Dead, "surrender: the kicked connection is closed");
        }
    }

    // The REAL client class through the same wire: the login events, the 顶号 popup event, the
    // auto-pong (which is what makes the HUD's PingStats arrive at all) and the pool join.
    private static void LobbyAccountClientTest(int port, string ver)
    {
        using var a = new LobbyRoomClient { MatchUdpPort = 46020 };
        a.Connect("127.0.0.1", port, "客户端甲", ver);
        Check(LobbyWait(a, LobbyRoomClient.EventKind.LoginOk),
            "client: LoginOk is surfaced as an event");

        using var b = new LobbyRoomClient { MatchUdpPort = 46021 };
        b.Connect("127.0.0.1", port, "客户端甲", ver);
        Check(LobbyWait(b, LobbyRoomClient.EventKind.KickConfirm),
            "client: KickConfirm is surfaced as an event");
        b.ConfirmKick();
        Check(LobbyWait(b, LobbyRoomClient.EventKind.LoginOk),
            "client: ConfirmKick completes the login");
        Check(LobbyWait(a, LobbyRoomClient.EventKind.Disconnected),
            "client: the kicked client reports Disconnected (with the takeover reason)");

        b.MatchmakeJoin();
        Check(LobbyWait(b, LobbyRoomClient.EventKind.MatchmakeStatus,
                  f => f.As<MatchmakeStatus>().Searching),
            "client: MatchmakeJoin flips the status to searching");
        using var c = new LobbyRoomClient { MatchUdpPort = 46022 };
        c.Connect("127.0.0.1", port, "客户端乙", ver);
        Check(LobbyWait(c, LobbyRoomClient.EventKind.LoginOk),
            "client: the second account logs in");
        c.MatchmakeJoin();
        Check(LobbyWait(b, LobbyRoomClient.EventKind.Connected, null, 8000)
              && LobbyWait(c, LobbyRoomClient.EventKind.Connected, null, 8000),
            "client: matchmaking lands both clients in an auto room");
        Check(LobbyWait(b, LobbyRoomClient.EventKind.PingStats, null, 5000),
            "client: auto-pong produces PingStats for the match HUD");
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
