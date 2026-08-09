using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Backdash;
using MouseKombat.Net;
using MouseKombat.Sim;

// ---- who does what in a match, and the host-as-forwarder path ----
//
// MatchPlan is pure logic precisely so these cases can be asserted instead of discovered by two people
// on a LAN. Every branch here is a rule in PROTOCOL.md § Match lifecycle.
internal static partial class Program
{
    private static void MatchPlanTests()
    {
        // Fixed endpoints; nothing is dialed, only decided.
        var epC1 = new IPEndPoint(IPAddress.Parse("10.0.0.11"), 40001);
        var epC2 = new IPEndPoint(IPAddress.Parse("10.0.0.12"), 40002);
        var epC3 = new IPEndPoint(IPAddress.Parse("10.0.0.13"), 40003);
        var epHost = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5835);

        // playerId -> endpoint, as the host knows them from Hello.
        IPEndPoint Client(int id) => id switch { 2 => epC1, 3 => epC2, 4 => epC3, _ => null };

        // A. host in seat 0, one client in seat 1 — the ordinary LAN 1v1.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0), P(2, "C", host: false, seat: 1) },
                seat0: Human(1, CharacterId.Hamster), seat1: Human(2, CharacterId.Kangaroo));

            var host = MatchPlan.Build(room, 1, true, null, Client);
            Check(host.Role == MatchRole.Fighter, "plan: host with a seat is a fighter");
            Check(host.LocalSeat[0] && !host.LocalSeat[1], "plan: host drives only its own seat");
            Check(Equals(host.RemoteEndPoint, epC1), "plan: host dials the client directly");
            Check(host.Spectators.Length == 0, "plan: nobody spectating");

            var cli = MatchPlan.Build(room, 2, false, epHost, null);
            Check(cli.Role == MatchRole.Fighter, "plan: seated client is a fighter");
            Check(!cli.LocalSeat[0] && cli.LocalSeat[1], "plan: client drives only its own seat");
            Check(Equals(cli.RemoteEndPoint, epHost), "plan: a client always dials the host");
        }

        // B. host in seat 0, AI in seat 1: BOTH seats are the host's, so there is no peer at all.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0) },
                seat0: Human(1, CharacterId.Hamster), seat1: Ai(CharacterId.Squirrel, "x.onnx"));

            var host = MatchPlan.Build(room, 1, true, null, Client);
            Check(host.Role == MatchRole.Fighter && host.LocalSeat[0] && host.LocalSeat[1],
                "plan: an AI seat is driven by the host, so the host drives both seats");
            Check(host.AiSeat[1] && !host.AiSeat[0], "plan: only the AI seat is marked as AI");
            Check(host.RemoteEndPoint == null, "plan: no remote endpoint when every seat is local");
        }

        // C. host holds nothing, two AI seats: still entirely the host's match.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: -1), P(2, "C", host: false, seat: -1) },
                seat0: Ai(CharacterId.Hamster, ""), seat1: Ai(CharacterId.Kangaroo, ""));

            var host = MatchPlan.Build(room, 1, true, null, Client);
            Check(host.Role == MatchRole.Fighter && host.LocalSeat[0] && host.LocalSeat[1],
                "plan: two AI seats run on the host");
            Check(host.Spectators.Length == 1 && Equals(host.Spectators[0], epC1),
                "plan: the seatless client is a spectator of the host's session");

            var cli = MatchPlan.Build(room, 2, false, epHost, null);
            Check(cli.Role == MatchRole.Spectator && Equals(cli.SpectateHost, epHost),
                "plan: a seatless client spectates the host");
        }

        // D. TWO CLIENT FIGHTERS, host holds nothing — the one case with no session on the host.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: -1),
                                 P(2, "A", host: false, seat: 0),
                                 P(3, "B", host: false, seat: 1),
                                 P(4, "S", host: false, seat: -1) },
                seat0: Human(2, CharacterId.Hamster), seat1: Human(3, CharacterId.Kangaroo));

            var host = MatchPlan.Build(room, 1, true, null, Client);
            Check(host.Role == MatchRole.Relay, "plan: host with no seat and two client fighters relays");
            Check(Equals(host.RelayA, epC1) && Equals(host.RelayB, epC2),
                "plan: the relay knows both fighters' endpoints");

            var a = MatchPlan.Build(room, 2, false, epHost, null);
            Check(a.Role == MatchRole.Fighter && Equals(a.RemoteEndPoint, epHost),
                "plan: client vs client still aims at the host — no P2P, and no peer address leaks");

            // Spectating needs the host to be RUNNING a session, and here it is a bare forwarder.
            var spec = MatchPlan.Build(room, 4, false, epHost, null);
            Check(spec.Role == MatchRole.Idle, "plan: no spectating when the host only relays");
            Check(!string.IsNullOrEmpty(spec.Problem), "plan: and it says why");
        }

        // E. a fighter that never announced a match port cannot be started against.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0), P(9, "C", host: false, seat: 1) },
                seat0: Human(1, CharacterId.Hamster), seat1: Human(9, CharacterId.Kangaroo));

            var host = MatchPlan.Build(room, 1, true, null, Client);   // Client(9) == null
            Check(host.Role == MatchRole.Idle && host.Problem != null,
                "plan: a fighter with no announced match port blocks the start with a reason");
        }

        // F. a client that lost the room snapshot's host port has nothing to dial.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0), P(2, "C", host: false, seat: 1) },
                seat0: Human(1, CharacterId.Hamster), seat1: Human(2, CharacterId.Kangaroo));
            var cli = MatchPlan.Build(room, 2, false, null, null);
            Check(cli.Role == MatchRole.Idle && cli.Problem != null,
                "plan: a client with no host endpoint refuses rather than guesses");
        }

        // ---- lobby (期3-5): the server is the hub, not the host player ----
        // G. lobby host player with a seat dials the SERVER like any client.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0), P(2, "C", host: false, seat: 1) },
                seat0: Human(1, CharacterId.Hamster), seat1: Human(2, CharacterId.Kangaroo));
            var epServer = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 4954);

            var host = MatchPlan.Build(room, 1, true, epServer, Client, lobby: true);
            Check(host.Role == MatchRole.Fighter && host.LocalSeat[0] && !host.LocalSeat[1],
                "lobby plan: the host player drives only its own seat");
            Check(Equals(host.RemoteEndPoint, epServer),
                "lobby plan: the host player dials the server, never the other fighter");
            Check(host.Spectators.Length == 0,
                "lobby plan: no session spectators in a lobby");

            var cli = MatchPlan.Build(room, 2, false, epServer, null, lobby: true);
            Check(cli.Role == MatchRole.Fighter && Equals(cli.RemoteEndPoint, epServer),
                "lobby plan: a client fighter dials the server");
        }

        // H. lobby host player with no seat: Relay role (catch-up authority) WITHOUT endpoints —
        // the server relays the UDP, the host player only merges reports and serves the data stream.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: -1),
                                 P(2, "A", host: false, seat: 0),
                                 P(3, "B", host: false, seat: 1) },
                seat0: Human(2, CharacterId.Hamster), seat1: Human(3, CharacterId.Kangaroo));
            var host = MatchPlan.Build(room, 1, true, null, Client, lobby: true);
            Check(host.Role == MatchRole.Relay && host.RelayA == null && host.RelayB == null,
                "lobby plan: the host player relays by DATA, the server by UDP");
        }

        // I. a seatless lobby member never dials a spectator session: lobby spectating is the data
        // stream served by the host player over TCP.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0), P(2, "S", host: false, seat: -1) },
                seat0: Human(1, CharacterId.Hamster), seat1: Ai(CharacterId.Kangaroo, ""));
            var spec = MatchPlan.Build(room, 2, false, new IPEndPoint(IPAddress.Loopback, 4954), null, lobby: true);
            Check(spec.Role == MatchRole.Idle && spec.SpectateHost == null,
                "lobby plan: a seatless member waits for the data stream, not a session");
        }

        // J. lobby host player + AI seat: everything local, no peer, no port problem.
        {
            var room = Room(
                players: new[] { P(1, "H", host: true, seat: 0) },
                seat0: Human(1, CharacterId.Hamster), seat1: Ai(CharacterId.Kangaroo, ""));
            var host = MatchPlan.Build(room, 1, true, null, Client, lobby: true);
            Check(host.Role == MatchRole.Fighter && host.LocalSeat[0] && host.LocalSeat[1] && host.AiSeat[1],
                "lobby plan: the host player drives an AI seat like a LAN host");
            Check(host.RemoteEndPoint == null, "lobby plan: no peer when every seat is local");
        }
    }

    // ---- the host as a UDP forwarder ----
    //
    // Two fighters that only know the host's address, exactly as MatchPlan case D arranges them. The
    // assertion that matters is the same one as for a direct pair: both sides must land on the
    // never-rewound reference run, which they can only do if every datagram survived the extra hop.
    private static void RelayMatchTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        using var sockA = BindUdp();
        using var sockB = BindUdp();
        int relayPort = Backdash.Network.NetUtils.FindFreePort();

        var epA = new IPEndPoint(IPAddress.Loopback, sockA.Port);
        var epB = new IPEndPoint(IPAddress.Loopback, sockB.Port);
        var epRelay = new IPEndPoint(IPAddress.Loopback, relayPort);

        using var relay = new UdpMatchRelay(relayPort, epA, epB);

        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);

        // Both sides believe their opponent lives at the relay's endpoint, and neither ever learns the
        // other's address. That is the whole point of routing through the host.
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false }, RemoteEndPoint = epRelay,
            Socket = sockA, InputDelayFrames = delay,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true }, RemoteEndPoint = epRelay,
            Socket = sockB, InputDelayFrames = delay,
        });

        int target = NetFrames + NetOvershoot;
        var sw = Stopwatch.StartNew();
        bool ok = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            relay.Poll();
            viewA.SetLocalFrame(a.Frame); a.Tick();
            viewB.SetLocalFrame(b.Frame); b.Tick();
            if (a.Frame >= target && b.Frame >= target) { ok = true; break; }
            Thread.Yield();
        }

        Check(ok, $"relay: both fighters reached frame {target} (A={a.Frame} B={b.Frame})");
        Check(a.Synchronized && b.Synchronized, "relay: both fighters synchronized through the host");
        Check(relay.ForwardedAToB > 0 && relay.ForwardedBToA > 0,
            $"relay: traffic went both ways (A->B {relay.ForwardedAToB}, B->A {relay.ForwardedBToA})");
        Check(relay.Dropped == 0, $"relay: nothing arrived from a non-fighter ({relay.Dropped} dropped)");
        Check(relay.LastError == null, $"relay: no socket error ({relay.LastError})");
        Check(SameValues(expected, viewA.Value, NetFrames, out string whyA),
            "relay: side A matches a never-rewound run" + whyA);
        Check(SameValues(expected, viewB.Value, NetFrames, out string whyB),
            "relay: side B matches a never-rewound run" + whyB);
        DrainEvents(a, "relay A");
        DrainEvents(b, "relay B");
    }

    // ---- builders for a fake room snapshot ----

    private static RoomSnapshot Room(PlayerInfo[] players, SeatInfo seat0, SeatInfo seat1) =>
        new RoomSnapshot { Players = players, Seats = new[] { seat0, seat1 }, MatchRunning = true };

    private static PlayerInfo P(int id, string name, bool host, int seat) =>
        new PlayerInfo { PlayerId = id, Name = name, IsHost = host, Seat = seat, Connected = true };

    private static SeatInfo Human(int playerId, CharacterId c) =>
        new SeatInfo { OccupantPlayerId = playerId, Character = (int)c };

    private static SeatInfo Ai(CharacterId c, string model) =>
        new SeatInfo { OccupantPlayerId = 0, Character = (int)c, IsAi = true, AiModel = model };
}
