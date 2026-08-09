using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Backdash;
using Backdash.Network.Client;
using MouseKombat.Net;
using MouseKombat.Sim;

// ---- headless verification of the Backdash integration (MouseKombat.Net/RollbackMatch.cs) ----
//
// Backdash 0.7.8-PREVIEW is pinned exactly because its API moves; that pin is worth nothing unless
// something actually exercises the API. Nothing here needs Godot, so two (and three) sessions are
// driven over loopback UDP in-process and compared against a plain, never-rewound GameSim.
//
// What each assertion is really for:
//
//   * per-frame equality with the reference run  — the rollback contract itself. A predicted frame is
//     re-simulated with corrected inputs, and the final value for every frame must equal what a run
//     that never mispredicted would have produced.
//   * VIEW state folded into the compared value  — SaveState/LoadState take `ref readonly` structs in
//     this Backdash version. Calling a mutating member through `ref readonly` can silently operate on
//     a defensive COPY, which would make the writer look empty and the reader never advance. Folding
//     the presenter's own accumulating counter into the compared checksum means a broken round-trip
//     shows up as a mismatch instead of as nothing at all.
//   * RollbackCount > 0                          — without a real misprediction the test would pass
//     on a session that never rolls back, proving nothing. Simulated latency forces prediction.
//   * ForeignThreadCallbacks == 0                — Backdash runs its socket IO on its own thread. The
//     Godot director touches nodes from the presenter callbacks, so if those ever arrive off the
//     driving thread the design is wrong, not just slow.
internal static partial class Program
{
    // Compared frame window, and how far past it to keep driving. Frames within the prediction window
    // of the newest one may still be speculative, so the comparison stops well behind the head.
    private const int NetFrames = 180;
    private const int NetOvershoot = 40;

    private static void RollbackSessionTests()
    {
        LocalSessionTest();
        RemotePairTest();
        SpectatorTest();
        SpectatorOfLocalPairTest();
        RematchSameRoomTest();
        CatchUpHistoryTest();
        MatchPlanTests();
        RelayMatchTest();
    }

    // ---- the presenter double ----
    //
    // Mirrors what the Godot director will do: keep a little view state that the sim knows nothing
    // about, advance it once per frame, and reset it when something happens in the sim. The state
    // ACCUMULATES (ClipFrame++), which is what makes a failed rewind detectable.
    private sealed class TestPresenter : IMatchPresenter
    {
        private readonly GameSim _sim;
        private readonly Func<int, int, ushort> _script;

        public readonly Dictionary<int, uint> Value = new();  // frame -> sim checksum ^ view hash
        public int LiveFrames, RollbackFrames, RollbackBegins, RollbackEnds;

        // The host's mid-match spectator history: every frame's inputs as the SIM consumed them,
        // overwritten by re-simulations (RecordAt semantics — see GameManager._netHistory). The
        // CatchUpHistoryTest replays this to verify the joiner's catch-up reproduces the reference.
        public readonly List<ushort> RecP1 = new();
        public readonly List<ushort> RecP2 = new();

        private string _clip = "IDLE";
        private int _clipFrame;
        private bool _reverse;

        public TestPresenter(GameSim sim, Func<int, int, ushort> script)
        {
            _sim = sim;
            _script = script;
        }

        public ushort LocalInput(int seat) => _script(seat, _nextLocalFrame);

        // The director reads its device once per tick; here the script is a function of frame, so the
        // frame has to come from somewhere. Backdash asks for input BEFORE the frame number advances,
        // so the session's current frame is the right one — set by the driver just before Tick.
        private int _nextLocalFrame;
        public void SetLocalFrame(int f) => _nextLocalFrame = f;

        public void SaveView(ref SimStateWriter w)
        {
            w.ShortString(_clip);
            w.Int(_clipFrame);
            w.Bool(_reverse);
        }

        public void LoadView(ref SimStateReader r)
        {
            _clip = r.ShortString() ?? "";
            _clipFrame = r.Int();
            _reverse = r.Bool();
        }

        public void OnFrame(int frame, InputFrame f0, InputFrame f1, StepResult res, bool rollback)
        {
            _clipFrame++;
            if (res.Hits.Count > 0) { _clip = "HURT" + res.Hits.Count; _clipFrame = 0; }
            else if (res.SpawnedProjectileIds.Count > 0) { _clip = "FIRE"; _clipFrame = 0; }
            _reverse = f0.Left || f1.Right;

            while (RecP1.Count <= frame) { RecP1.Add(0); RecP2.Add(0); }
            RecP1[frame] = ReplayData.Pack(f0);
            RecP2[frame] = ReplayData.Pack(f1);

            if (rollback) RollbackFrames++; else LiveFrames++;
            // Last write wins: a frame re-simulated after a misprediction overwrites its speculative
            // value, so what is compared at the end is the CONFIRMED result of that frame.
            Value[frame] = _sim.Checksum() ^ ViewHash();
        }

        public void OnRollbackBegin(int frame) => RollbackBegins++;
        public void OnRollbackEnd(int frame) => RollbackEnds++;

        public uint ViewHash()
        {
            uint h = 2166136261u;
            foreach (char c in _clip) { h ^= c; h *= 16777619u; }
            unchecked
            {
                h ^= (uint)_clipFrame; h *= 16777619u;
                h ^= _reverse ? 1u : 0u; h *= 16777619u;
            }
            return h;
        }
    }

    // Deterministic two-seat script: both walk toward each other and throw out normals and a fireball
    // motion, so hits, blocks and projectiles all land inside the compared window.
    private static ushort NetScript(int seat, int frame)
    {
        int i = frame + seat * 11;
        int m = 0;
        if (i % 19 == 0) m |= Mask((AttackButton)(i / 19 % 6));
        bool down = i % 31 is 0 or 1;
        bool fwd = i % 31 is 2 or 3;
        if (i % 31 == 3) m |= Mask(AttackButton.MP);   // 236+P -> fireball
        bool right = seat == 0 ? (i % 23 < 14 || fwd) : (i % 29 < 5);
        bool left = seat == 0 ? (i % 41 < 4) : (i % 23 < 14 || fwd);
        return ReplayData.Pack(new InputFrame(left, right, i % 53 < 2, i % 37 < 5 || down, m));
    }

    // The answer every session has to reproduce: one sim, stepped once per frame, never rewound, with
    // the same view-state bookkeeping applied on top.
    //
    // `delay` mirrors NetcodeOptions.InputDelayFrames. Input delay is a pure shift of the input
    // pipeline — frame f consumes what was pressed at f-delay, and the first `delay` frames are
    // neutral — so reproducing it here keeps the comparison EXACT instead of having to allow a window.
    private static Dictionary<int, uint> ReferenceRun(int frames, int delay)
    {
        var sim = MakeSim(240, 520);
        var view = new TestPresenter(sim, NetScript);
        for (int f = 0; f < frames; f++)
        {
            int src = f - delay;
            var f0 = src < 0 ? InputFrame.Neutral : ReplayData.Unpack(NetScript(0, src));
            var f1 = src < 0 ? InputFrame.Neutral : ReplayData.Unpack(NetScript(1, src));
            var res = sim.Step(f0, f1);
            view.OnFrame(f, f0, f1, res, rollback: false);
        }
        return view.Value;
    }

    // The plain sim checksum after each frame of the reference run — no view hash folded in. The
    // catch-up parity test replays a recorded history in a fresh sim and compares against this.
    private static Dictionary<int, uint> ReferenceSimChecksums(int frames, int delay)
    {
        var sim = MakeSim(240, 520);
        var d = new Dictionary<int, uint>(frames);
        for (int f = 0; f < frames; f++)
        {
            int src = f - delay;
            var f0 = src < 0 ? InputFrame.Neutral : ReplayData.Unpack(NetScript(0, src));
            var f1 = src < 0 ? InputFrame.Neutral : ReplayData.Unpack(NetScript(1, src));
            sim.Step(f0, f1);
            d[f] = sim.Checksum();
        }
        return d;
    }

    // ---- A. local session: both seats on one machine ----
    // This is the host-plus-AI case. No peer exists, so nothing can be predicted — the point is that
    // the SAME loop and the SAME handler produce the reference result with no network at all.
    private static void LocalSessionTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);
        var sim = MakeSim(240, 520);
        var view = new TestPresenter(sim, NetScript);
        var setup = new MatchNetSetup { LocalSeat = new[] { true, true }, InputDelayFrames = delay };

        using var match = RollbackMatch.Create(sim, view, setup);
        Check(match.Mode == SessionMode.Local, "rollback: two local seats -> SessionMode.Local");

        int advanced = 0;
        for (int guard = 0; advanced < NetFrames && guard < NetFrames * 8; guard++)
        {
            view.SetLocalFrame(match.Frame);
            if (match.Tick()) advanced++;
        }
        Check(advanced == NetFrames, $"rollback local: advanced {advanced}/{NetFrames} frames");
        Check(match.RollbackCount == 0, "rollback local: no peer, so no rollback");
        Check(match.ForeignThreadCallbacks == 0, "rollback local: every handler callback on the driving thread");
        Check(SameValues(expected, view.Value, NetFrames, out string why),
            "rollback local: frame-for-frame identical to a plain GameSim run" + why);
    }

    // ---- B. remote pair: one seat each, over loopback UDP, with enough latency to force prediction ----
    private static void RemotePairTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        // Bind both sockets FIRST so each side can be told the other's port. This is exactly what the
        // LAN flow does: the port is reserved during the room handshake, not guessed at match start.
        using var sockA = BindUdp();
        using var sockB = BindUdp();
        var epA = new IPEndPoint(IPAddress.Loopback, sockA.Port);
        var epB = new IPEndPoint(IPAddress.Loopback, sockB.Port);

        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);

        // 16 ms each way with 2 frames of input delay: the sides must predict ~1 frame most of the
        // time and mispredict whenever the script changes, which is what produces the rollbacks.
        var lat = TimeSpan.FromMilliseconds(16);
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false }, RemoteEndPoint = epB,
            Socket = sockA, InputDelayFrames = delay, SimulatedLatency = lat,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true }, RemoteEndPoint = epA,
            Socket = sockB, InputDelayFrames = delay, SimulatedLatency = lat,
        });
        Check(a.Mode == SessionMode.Remote && b.Mode == SessionMode.Remote,
            "rollback remote: one remote seat -> SessionMode.Remote on both sides");

        int target = NetFrames + NetOvershoot;
        bool ok = Drive(new[] { (a, viewA), (b, viewB) }, target, TimeSpan.FromSeconds(30));
        Check(ok, $"rollback remote: both sides reached frame {target} (A={a.Frame} B={b.Frame})");
        Check(a.Synchronized && b.Synchronized, "rollback remote: both sides report Synchronized");

        Check(a.RollbackCount > 0 || b.RollbackCount > 0,
            $"rollback remote: at least one real rollback happened (A={a.RollbackCount} B={b.RollbackCount})");
        Check(viewA.RollbackFrames > 0 || viewB.RollbackFrames > 0,
            $"rollback remote: frames were re-simulated (A={viewA.RollbackFrames} B={viewB.RollbackFrames})");
        Check(a.ForeignThreadCallbacks == 0 && b.ForeignThreadCallbacks == 0,
            "rollback remote: no handler callback arrived off the driving thread");
        Check(viewA.RollbackBegins == viewA.RollbackEnds && viewB.RollbackBegins == viewB.RollbackEnds,
            "rollback remote: every BeginRollback is matched by an EndRollback");

        Check(SameValues(expected, viewA.Value, NetFrames, out string whyA),
            "rollback remote: side A matches a never-rewound run" + whyA);
        Check(SameValues(expected, viewB.Value, NetFrames, out string whyB),
            "rollback remote: side B matches a never-rewound run" + whyB);
        // Deliberately NOT asserting that the two live sims agree once both stand on the same frame.
        // Standing on frame N is not the same as having CONFIRMED frame N: whichever side is ahead of
        // the other's inputs holds a prediction there, and a prediction is allowed to be wrong — that is
        // the entire premise. The confirmed value of every frame is what has to match, which is what the
        // two comparisons above check.
        DrainEvents(a, "A");
        DrainEvents(b, "B");
    }

    // ---- F. mid-match spectator data: the confirmed-input history ----
    //
    // The host answers a mid-match join with the CONFIRMED prefix of its input history (see
    // GameManager._netHistory / RollbackMatch.ConfirmedFrame). This pins the facts that contract
    // stands on, for BOTH session shapes a host can run:
    //   * host + client: a remote peer drives confirmation, so the confirmed point lags the live
    //     head — which is exactly why the spectator stream must read ConfirmedFrame and not Frame;
    //   * host + AI / AI + AI (all-local seats): Backdash's confirmation is driven by REMOTE acks
    //     and never advances for this shape, so ConfirmedFrame must clamp to the live head instead
    //     of leaking a bogus huge value (the crash seen in the field);
    // and for both:
    //   * the recorded history is, frame for frame, the reference input stream (the joiner replays
    //     exactly this),
    //   * replaying the history up to the confirmed point in a fresh sim reproduces the reference
    //     state at that frame — which is what the joiner's catch-up does.
    private static void CatchUpHistoryTest()
    {
        const int delay = 2;
        int frames = NetFrames;
        var refChecksums = ReferenceSimChecksums(frames, delay);

        // Shape A: host + client over loopback with enough latency to force predictions.
        {
            using var sockA = BindUdp();
            using var sockB = BindUdp();
            var epA = new IPEndPoint(IPAddress.Loopback, sockA.Port);
            var epB = new IPEndPoint(IPAddress.Loopback, sockB.Port);
            var simA = MakeSim(240, 520);
            var simB = MakeSim(240, 520);
            var viewA = new TestPresenter(simA, NetScript);
            var viewB = new TestPresenter(simB, NetScript);
            var lat = TimeSpan.FromMilliseconds(16);
            using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
            {
                LocalSeat = new[] { true, false }, RemoteEndPoint = epB,
                Socket = sockA, InputDelayFrames = delay, SimulatedLatency = lat,
            });
            using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
            {
                LocalSeat = new[] { false, true }, RemoteEndPoint = epA,
                Socket = sockB, InputDelayFrames = delay, SimulatedLatency = lat,
            });

            int target = frames + NetOvershoot;
            Check(Drive(new[] { (a, viewA), (b, viewB) }, target, TimeSpan.FromSeconds(30)),
                $"catch-up: both sides reached frame {target} (A={a.Frame} B={b.Frame})");
            Check(a.RollbackCount > 0 || b.RollbackCount > 0,
                "catch-up: rollbacks happened (the parity checks would be vacuous without them)");
            VerifyHistoryAndParity(a, viewA, refChecksums, frames, delay, "catch-up host+client");
        }

        // Shape B: all-local seats plus a spectator — the host+AI room. No remote input player
        // exists, so nothing drives Backdash's confirmation; ConfirmedFrame must clamp to the live
        // head instead of computing a bogus huge number.
        {
            using var sockH = BindUdp();
            using var sockS = BindUdp();
            var epH = new IPEndPoint(IPAddress.Loopback, sockH.Port);
            var epS = new IPEndPoint(IPAddress.Loopback, sockS.Port);
            var simH = MakeSim(240, 520);
            var simS = MakeSim(240, 520);
            var viewH = new TestPresenter(simH, NetScript);
            var viewS = new TestPresenter(simS, NetScript);
            using var h = RollbackMatch.Create(simH, viewH, new MatchNetSetup
            {
                LocalSeat = new[] { true, true },
                Spectators = new EndPoint[] { epS },
                Socket = sockH, InputDelayFrames = delay,
            });
            using var s = RollbackMatch.Create(simS, viewS, new MatchNetSetup
            {
                SpectateHost = epH, Socket = sockS, InputDelayFrames = delay,
            });

            int target = frames + NetOvershoot;
            Check(Drive(new[] { (h, viewH), (s, viewS) }, target, TimeSpan.FromSeconds(30)),
                $"catch-up all-local: host reached frame {target} (H={h.Frame})");
            VerifyHistoryAndParity(h, viewH, refChecksums, frames, delay, "catch-up all-local");
        }
    }

    // The shared part of the catch-up contract: the recorded history equals the reference input
    // stream, ConfirmedFrame is a sane frame of the session, and replaying the history up to it
    // reproduces the reference state at that frame.
    private static void VerifyHistoryAndParity(RollbackMatch m, TestPresenter view,
                                               Dictionary<int, uint> refChecksums, int frames,
                                               int delay, string label)
    {
        Check(m.ConfirmedFrame >= 0 && m.ConfirmedFrame <= m.Frame,
            $"{label}: confirmed is a frame of the session (confirmed {m.ConfirmedFrame}, live {m.Frame})");

        // The recorded history must be the reference input stream: the host hands exactly this to a
        // mid-match joiner, and the joiner's replay only lands on the right state if it is.
        bool inputsMatch = true;
        string why = "";
        for (int f = 0; f < frames; f++)
        {
            int src = f - delay;
            ushort want1 = src < 0 ? (ushort)0 : NetScript(0, src);
            ushort want2 = src < 0 ? (ushort)0 : NetScript(1, src);
            if (view.RecP1[f] != want1 || view.RecP2[f] != want2)
            {
                inputsMatch = false;
                why = $" — frame {f}: host recorded ({view.RecP1[f]},{view.RecP2[f]}), "
                      + $"reference ({want1},{want2})";
                break;
            }
        }
        Check(inputsMatch, $"{label}: the recorded history equals the reference input stream" + why);

        // The joiner's actual catch-up, headless: replay the host's recorded history up to its
        // confirmed frame in a fresh sim, and demand the reference state at that frame.
        int cf = Math.Min(m.ConfirmedFrame, frames - 1);
        var fresh = MakeSim(240, 520);
        for (int i = 0; i <= cf; i++)
            fresh.Step(ReplayData.Unpack(view.RecP1[i]), ReplayData.Unpack(view.RecP2[i]));
        uint got = fresh.Checksum();
        Check(got == refChecksums[cf],
            $"{label}: replaying the confirmed history reproduces the reference state at frame {cf} "
            + $"({got:X8} vs {refChecksums[cf]:X8})");
    }

    // ---- E. two matches in one room ----
    //
    // A room plays many matches: after a knockout everyone returns to seat select and the NEXT
    // StartMatch creates fresh sessions on the SAME ports — the host binds the room port again, and
    // each client hands over the same MatchSocket it announced in Hello. MatchSocket must survive a
    // session teardown: Backdash's PeerClient.Dispose calls IDisposable.Dispose on the handed socket
    // (not just Close), and this test pins both no-ops. It replays the exact lifecycle: run a pair to
    // synchronization, dispose the sessions (the scene change between matches), then create them
    // again on the same ports and demand the same result. A second match that cannot even
    // synchronize is the failure reported in the field.
    private static void RematchSameRoomTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        int hostPort = Backdash.Network.NetUtils.FindFreePort();
        var epHost = new IPEndPoint(IPAddress.Loopback, hostPort);

        // Round 2 reuses the room's sockets; round 3 is a control with FRESH ones on different
        // ports, which isolates "the reused socket died" from "the host cannot rebind its port".
        var reusedSock = new MatchSocket(Backdash.Network.NetUtils.FindFreePort());
        var reusedSpecSock = new MatchSocket(Backdash.Network.NetUtils.FindFreePort());

        try
        {
            for (int round = 1; round <= 3; round++)
            {
                var simH = MakeSim(240, 520);
                var simB = MakeSim(240, 520);
                var simS = MakeSim(240, 520);
                var viewH = new TestPresenter(simH, NetScript);
                var viewB = new TestPresenter(simB, NetScript);
                var viewS = new TestPresenter(simS, NetScript);

                // Round 3 deliberately breaks the "same room" invariant (fresh client sockets, new
                // ports) to prove the HOST side of the reuse story: if the host could not rebind its
                // port this round fails too, and the bug is on the host, not in the reused sockets.
                using var freshSock = new MatchSocket(Backdash.Network.NetUtils.FindFreePort());
                using var freshSpecSock = new MatchSocket(Backdash.Network.NetUtils.FindFreePort());
                var clientSock = round == 3 ? freshSock : reusedSock;
                var specSock = round == 3 ? freshSpecSock : reusedSpecSock;
                var epClient = new IPEndPoint(IPAddress.Loopback, clientSock.Port);
                var epSpec = new IPEndPoint(IPAddress.Loopback, specSock.Port);

                // Host: no handed socket, Backdash binds the room port fresh — exactly what
                // MatchNetSetup.Port + Socket == null does in the LAN flow. The spectator is
                // declared up front, like StartMatch does.
                var h = RollbackMatch.Create(simH, viewH, new MatchNetSetup
                {
                    LocalSeat = new[] { true, false },
                    RemoteEndPoint = epClient,
                    Spectators = new EndPoint[] { epSpec },
                    Port = hostPort,
                    InputDelayFrames = delay,
                });
                // Client: rounds 1 and 2 hand over the same MatchSocket the room announced in Hello;
                // round 3 hands a fresh one. Same for the spectator.
                var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
                {
                    LocalSeat = new[] { false, true },
                    RemoteEndPoint = epHost,
                    Socket = clientSock,
                    InputDelayFrames = delay,
                });
                var s = RollbackMatch.Create(simS, viewS, new MatchNetSetup
                {
                    SpectateHost = epHost,
                    Socket = specSock,
                    InputDelayFrames = delay,
                });

                int target = NetFrames + NetOvershoot;
                bool ok = Drive(new[] { (h, viewH), (b, viewB), (s, viewS) }, target,
                                TimeSpan.FromSeconds(30));
                Check(ok, $"rematch round {round}: both sides reached frame {target} "
                          + $"(H={h.Frame} B={b.Frame} S={s.Frame}, "
                          + $"sync H={h.Synchronized} B={b.Synchronized})");
                Check(s.FramesAdvanced >= NetFrames,
                    $"rematch round {round}: spectator watched at least {NetFrames} frames "
                    + $"(got {s.FramesAdvanced})");
                Check(SameValues(expected, viewH.Value, NetFrames, out string whyH),
                    $"rematch round {round}: host matches a never-rewound run" + whyH);
                Check(SameValues(expected, viewB.Value, NetFrames, out string whyB),
                    $"rematch round {round}: client matches a never-rewound run" + whyB);
                Check(SameValues(expected, viewS.Value, Math.Min(NetFrames, s.FramesAdvanced),
                        out string whyS),
                    $"rematch round {round}: spectator sees the same match" + whyS);

                // The scene change: the director disposes its session. Dispose must leave the
                // clients' sockets usable for the next round.
                h.Dispose();
                b.Dispose();
                s.Dispose();
            }
        }
        finally
        {
            reusedSock.CloseNow();
            reusedSpecSock.CloseNow();
        }
    }


    private static void SpectatorTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        using var sockA = BindUdp();
        using var sockB = BindUdp();
        using var sockS = BindUdp();
        var epA = new IPEndPoint(IPAddress.Loopback, sockA.Port);
        var epB = new IPEndPoint(IPAddress.Loopback, sockB.Port);
        var epS = new IPEndPoint(IPAddress.Loopback, sockS.Port);

        var simA = MakeSim(240, 520);
        var simB = MakeSim(240, 520);
        var simS = MakeSim(240, 520);
        var viewA = new TestPresenter(simA, NetScript);
        var viewB = new TestPresenter(simB, NetScript);
        var viewS = new TestPresenter(simS, NetScript);

        // Spectators are declared on the host BEFORE the session starts, so their endpoints have to be
        // known at StartMatch time. That is a real constraint on the room protocol, not an artifact of
        // the test: a machine that joins mid-match cannot be added to a running session.
        using var a = RollbackMatch.Create(simA, viewA, new MatchNetSetup
        {
            LocalSeat = new[] { true, false }, RemoteEndPoint = epB,
            Spectators = new EndPoint[] { epS }, Socket = sockA, InputDelayFrames = delay,
        });
        using var b = RollbackMatch.Create(simB, viewB, new MatchNetSetup
        {
            LocalSeat = new[] { false, true }, RemoteEndPoint = epA,
            Socket = sockB, InputDelayFrames = delay,
        });
        using var s = RollbackMatch.Create(simS, viewS, new MatchNetSetup
        {
            SpectateHost = epA, Socket = sockS, InputDelayFrames = delay,
        });
        Check(s.Mode == SessionMode.Spectator, "rollback spectator: SessionMode.Spectator");

        int target = NetFrames + NetOvershoot;
        bool ok = Drive(new[] { (a, viewA), (b, viewB), (s, viewS) }, target, TimeSpan.FromSeconds(30));
        Check(ok, $"rollback spectator: fighters reached frame {target} (A={a.Frame} B={b.Frame} S={s.Frame})");
        Check(s.FramesAdvanced >= NetFrames,
            $"rollback spectator: watched at least {NetFrames} frames (got {s.FramesAdvanced})");
        Check(s.ForeignThreadCallbacks == 0, "rollback spectator: callbacks on the driving thread");
        Check(SameValues(expected, viewS.Value, Math.Min(NetFrames, s.FramesAdvanced), out string whyS),
            "rollback spectator: sees exactly the same match as the fighters" + whyS);
        DrainEvents(s, "S");
    }

    // ---- D. host drives BOTH seats and somebody watches ----
    //
    // The host-plus-AI room with a spectator in it. Worth its own case because the session has two LOCAL
    // players and no remote one, which is a shape nothing else exercises: with no spectator it would be
    // SessionMode.Local, and the moment one appears it has to become Remote instead — a configuration
    // that is easy to assume works and is used by the very first thing a player will try (host, add an
    // AI, let a friend watch).
    private static void SpectatorOfLocalPairTest()
    {
        const int delay = 2;
        var expected = ReferenceRun(NetFrames, delay);

        using var sockH = BindUdp();
        using var sockS = BindUdp();
        var epH = new IPEndPoint(IPAddress.Loopback, sockH.Port);
        var epS = new IPEndPoint(IPAddress.Loopback, sockS.Port);

        var simH = MakeSim(240, 520);
        var simS = MakeSim(240, 520);
        var viewH = new TestPresenter(simH, NetScript);
        var viewS = new TestPresenter(simS, NetScript);

        using var h = RollbackMatch.Create(simH, viewH, new MatchNetSetup
        {
            LocalSeat = new[] { true, true },
            Spectators = new EndPoint[] { epS },
            Socket = sockH, InputDelayFrames = delay,
        });
        using var s2 = RollbackMatch.Create(simS, viewS, new MatchNetSetup
        {
            SpectateHost = epH, Socket = sockS, InputDelayFrames = delay,
        });

        Check(h.Mode == SessionMode.Remote,
            $"rollback host+AI: two local seats plus a spectator must be Remote, not Local (got {h.Mode})");

        int target = NetFrames + NetOvershoot;
        bool ok = Drive(new[] { (h, viewH), (s2, viewS) }, target, TimeSpan.FromSeconds(30));
        Check(ok, $"rollback host+AI: host reached frame {target} (H={h.Frame} S={s2.Frame})");
        Check(h.RollbackCount == 0, "rollback host+AI: no remote INPUT, so still nothing to predict");
        Check(SameValues(expected, viewH.Value, NetFrames, out string whyH),
            "rollback host+AI: the host's own run is unaffected by being watched" + whyH);
        Check(s2.FramesAdvanced >= NetFrames,
            $"rollback host+AI: the spectator watched at least {NetFrames} frames (got {s2.FramesAdvanced})");
        Check(SameValues(expected, viewS.Value, Math.Min(NetFrames, s2.FramesAdvanced), out string whyS),
            "rollback host+AI: the spectator sees the same match" + whyS);
        DrainEvents(h, "host+AI H");
        DrainEvents(s2, "host+AI S");
    }

    // Binds a UDP socket on a port that can be ANNOUNCED. Port 0 is not usable for this: Backdash's
    // UdpSocket.Port reports the port it was constructed with, so a socket bound with 0 hides the
    // ephemeral port the OS actually assigned and there is nothing to tell the peer. The real LAN flow
    // has the same constraint, and solves it the same way — pick a free port, bind it immediately so
    // nothing else can take it, then send that number in the handshake.
    private static UdpSocket BindUdp() =>
        new UdpSocket(Backdash.Network.NetUtils.FindFreePort(), useIPv6: false);

    // ---- driver ----
    //
    // Ticks every session in a tight loop until the FIGHTERS reach `target`, or the deadline expires.
    // No pacing on purpose: each side runs ahead only until its prediction window fills, so the
    // network itself throttles the loop. A spectator (no local seats) is not required to keep up.
    private static bool Drive((RollbackMatch m, TestPresenter v)[] sides, int target, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < budget)
        {
            bool allThere = true;
            foreach (var (m, v) in sides)
            {
                if (m.Mode == SessionMode.Spectator)
                {
                    m.Tick();
                    continue;
                }
                if (m.Frame < target) allThere = false;
                v.SetLocalFrame(m.Frame);
                m.Tick();
            }
            if (allThere) return true;
            Thread.Yield();
        }
        return false;
    }

    // Compares the confirmed value of every frame below `count`. Reports the FIRST divergence with
    // both values, because "some frame differs" is not something you can act on.
    private static bool SameValues(Dictionary<int, uint> expected, Dictionary<int, uint> actual,
                                  int count, out string detail)
    {
        detail = "";
        for (int f = 0; f < count; f++)
        {
            if (!actual.TryGetValue(f, out uint got))
            {
                detail = $" — frame {f} was never simulated";
                return false;
            }
            uint want = expected[f];
            if (got != want)
            {
                detail = $" — frame {f}: expected {want:X8}, got {got:X8}";
                return false;
            }
        }
        return true;
    }

    // Session events are informational, but a Desync or SyncFailed among them means the assertions
    // above passed for the wrong reason, so they fail the run.
    private static void DrainEvents(RollbackMatch m, string label)
    {
        while (m.TryDequeueEvent(out var e))
        {
            if (e.Kind == MatchEventKind.Desync)
                Check(false, $"rollback {label}: desync reported at frame {e.Frame} "
                             + $"({e.LocalChecksum:X8} vs {e.RemoteChecksum:X8})");
            else if (e.Kind == MatchEventKind.SyncFailed)
                Check(false, $"rollback {label}: {e.Text}");
        }
    }
}
