using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Backdash;
using Backdash.Network.Client;
using Backdash.Serialization;
using Backdash.Synchronizing.State;
using MouseKombat.Sim;

namespace MouseKombat.Net;

// ---- the rollback session, as the only place Backdash types appear ----
//
// Everything about the netcode library is behind this file. GameSim exposes SaveState/LoadState as
// plain Span<byte> and knows nothing about any of it (that is what keeps the pythonnet RL bridge able
// to load the sim bare), and the Godot director only implements IMatchPresenter. Swapping netcode
// libraries is a rewrite of this one file.
//
// Godot-free ON PURPOSE, same as the rest of MouseKombat.Net: two of these can be driven headlessly
// over loopback in the test runner, which is the only way a preview-API integration gets verified at
// all. See Program.RollbackSessionTests.
//
// The frame loop, as Backdash defines it:
//
//   live frame  : BeginFrame -> AddLocalInput(each local seat) -> SynchronizeInputs
//                 -> sim.Step -> session.AdvanceFrame
//   rollback    : Backdash calls LoadState(confirmed frame) and then AdvanceFrame() (the HANDLER one)
//                 once per frame it needs to redo, each of which repeats the SynchronizeInputs ->
//                 sim.Step -> session.AdvanceFrame part with STORED inputs.
//
// So StepOnce is shared by both, and `rollback` is the only thing that differs — the presenter uses
// it to suppress hit sparks, sound and popups for frames the player has already seen.

// One frame of input as it goes on the wire: the same 10-bit packing a replay file uses
// (ReplayData.Pack — 4 direction bits + 6 just-pressed button bits). Reusing it means a networked
// match and a replay of that match are fed byte-identical input streams.
public interface IMatchPresenter
{
    // Packed input for a seat this machine drives. Never called during a rollback: those frames
    // replay inputs Backdash already has.
    ushort LocalInput(int seat);

    // View state that has to rewind WITH the sim but that the sim knows nothing about — the animation
    // clip, the logic frame within it and the reverse flag, per fighter (see Player.ViewState).
    // Without this a rollback restarts whatever clip is playing, so every predicted frame of an
    // attack flickers.
    void SaveView(ref SimStateWriter w);
    void LoadView(ref SimStateReader r);

    // One advanced frame. rollback = this frame is a re-simulation of something already shown.
    void OnFrame(int frame, InputFrame f0, InputFrame f1, StepResult res, bool rollback);

    void OnRollbackBegin(int frame);
    void OnRollbackEnd(int frame);
}

public enum MatchEventKind
{
    Synchronizing,
    Synchronized,
    Interrupted,
    Resumed,
    Disconnected,
    SyncFailed,
    Desync,
}

public readonly struct MatchEvent
{
    public readonly MatchEventKind Kind;
    public readonly string Text;          // ready to show, already localized
    public readonly int Frame;            // Desync only
    public readonly uint LocalChecksum;   // Desync only
    public readonly uint RemoteChecksum;  // Desync only

    public MatchEvent(MatchEventKind kind, string text, int frame = 0,
                      uint local = 0, uint remote = 0)
    {
        Kind = kind; Text = text; Frame = frame;
        LocalChecksum = local; RemoteChecksum = remote;
    }
}

// How this machine participates. Filled in from the room snapshot + StartMatch.
public sealed class MatchNetSetup
{
    // Seats this machine produces input for. Host + AI can be both; a spectator neither.
    public bool[] LocalSeat = { true, true };

    // Where the other fighter is reached. For LAN this is always the HOST's UDP endpoint — the host
    // is the hub even when both fighters are clients (spec: 走房主中转，不做 P2P).
    public EndPoint RemoteEndPoint;

    // Spectator endpoints, host side only.
    public EndPoint[] Spectators = Array.Empty<EndPoint>();

    // Spectator machines: the host session to follow. Mutually exclusive with LocalSeat/RemoteEndPoint.
    public EndPoint SpectateHost;

    // A socket bound BEFORE the match (so its port could be announced during the handshake), or null
    // to let Backdash bind Port itself.
    public IPeerSocket Socket;
    public int Port;

    public int InputDelayFrames = 2;
    public int FrameRate = 60;

    // Tests only: pretend the link is this slow, which forces prediction and therefore rollbacks.
    public TimeSpan SimulatedLatency = TimeSpan.Zero;

    // Where Backdash's own diagnostics go. Left null it writes to the console, which in a Godot export
    // means nowhere useful — the director passes GD.Print so a prediction-barrier warning during a bad
    // connection is visible in the same log as everything else.
    public Action<string> LogSink;

    public bool AllSeatsLocal
    {
        get
        {
            for (int i = 0; i < LocalSeat.Length; i++) if (!LocalSeat[i]) return false;
            return true;
        }
    }
}

public sealed class RollbackMatch : INetcodeSessionHandler, IDisposable
{
    public const int SeatCount = RoomState.SeatCount;

    // Sim state (2 KB budget) plus the two fighters' view state. The view state is two short clip
    // names and two ints each, so 256 bytes is many times over.
    private const int ViewStateBudget = 256;

    private readonly GameSim _sim;
    private readonly IMatchPresenter _view;
    private readonly INetcodeSession<ushort> _session;
    private readonly NetcodePlayer[] _seatPlayer = new NetcodePlayer[SeatCount];
    private readonly bool[] _localSeat;
    private readonly byte[] _scratch = new byte[SimState.MaxSize + ViewStateBudget];
    private readonly ushort[] _inputs = new ushort[SeatCount];

    // Backdash's socket IO runs on its own thread, so OnPeerEvent can arrive off the game thread.
    // Events are therefore QUEUED and drained by the owner from Tick/PollEvents — same polled shape
    // as TcpRoomHost, and the reason no presenter callback can ever touch a Godot node off-thread.
    private readonly Queue<MatchEvent> _events = new();
    private readonly object _eventLock = new();

    private int _stallFrames;
    private bool _closed;

    public SessionMode Mode { get; }

    // Is there anyone on the other end at all? A Local session (this machine drives BOTH seats and
    // nobody is watching over UDP — host + AI, or AI vs AI) has no peer, so it never reports
    // Synchronizing/Synchronized and never stalls. A UI that shows "正在与对方同步…" until the
    // Synchronized event arrives would leave that text on screen for the whole match (the user's
    // AI-vs-AI lobby round), so the caller must ask this before saying anything about a peer.
    public bool HasRemotePeer => Mode != SessionMode.Local;

    // Diagnostics. The handler callbacks (SaveState/LoadState/AdvanceFrame) are expected to run on
    // the thread that calls Tick, because that is the thread driving the session — but "expected" is
    // not "verified", so count violations instead of assuming. The test asserts this stays 0.
    public int OwnerThreadId { get; }
    public int ForeignThreadCallbacks { get; private set; }

    public int RollbackCount { get; private set; }
    public int FramesAdvanced { get; private set; }
    public bool Synchronized { get; private set; }
    public bool PeerDisconnected { get; private set; }

    public int Frame => _session.GetInfo().CurrentFrame.Number;
    public bool IsInRollback => _session.GetInfo().IsInRollback;

    // The highest frame both sides have CONFIRMED: everything at or below it is final and a rollback
    // can never take it back. The live head (Frame) can be several frames ahead on predictions, so
    // anything that must never be corrected — the mid-match spectator stream — reads THIS, not Frame.
    // Backdash exposes the gap directly (FramesBehind = live - confirmed), so no internals leak.
    //
    // CLAMPED both ways on purpose. The confirmation is driven by the REMOTE players' acks, and a
    // session with no remote input players (host + AI, or AI + AI) never receives any: Backdash then
    // sets its confirmed frame to Frame.MaxValue, which would make FramesBehind a huge negative
    // number. For such a session the right answer is that the live head IS the confirmed point —
    // nothing is predicted, so nothing can be taken back. The clamp yields exactly that, and keeps a
    // broken value from ever leaking past [0, the live head].
    public int ConfirmedFrame
    {
        get
        {
            var info = _session.GetInfo();
            int behind = Math.Max(0, info.FramesBehind.Frames);
            return Math.Max(0, Math.Min(info.CurrentFrame.Number, info.CurrentFrame.Number - behind));
        }
    }

    private RollbackMatch(GameSim sim, IMatchPresenter view, MatchNetSetup setup)
    {
        _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        OwnerThreadId = Environment.CurrentManagedThreadId;

        bool spectating = setup.SpectateHost != null;
        // A spectator drives no seat, whatever the caller filled in.
        _localSeat = spectating ? new bool[SeatCount] : (bool[])setup.LocalSeat.Clone();

        var builder = RollbackNetcode
            .WithInputType<ushort>(x => x.Integer<ushort>())
            .WithFrameRate(setup.FrameRate)
            .WithInputDelayFrames(setup.InputDelayFrames)
            .WithStateSizeHint(SimState.MaxSize + ViewStateBudget)
            // The sim's own FNV-1a over the savestate. Sharing one checksum function with the replay
            // verifier and the golden-checksum test means a desync Backdash reports is the same
            // number the tests compare, instead of a second opinion computed a second way.
            .WithChecksumProvider(new ChecksumDelegate(SimState.Checksum));

        if (setup.SimulatedLatency > TimeSpan.Zero)
            builder = builder.WithNetworkLatency(setup.SimulatedLatency);

        if (setup.LogSink != null)
        {
            var sink = setup.LogSink;
            builder = builder.WithLogWriter((level, msg) => sink($"[backdash/{level}] {msg}"));
        }

        // A local session has no peer, so it binds nothing and needs no port. Anything else does.
        Mode = spectating ? SessionMode.Spectator
             : (setup.AllSeatsLocal && setup.Spectators.Length == 0) ? SessionMode.Local
             : SessionMode.Remote;

        if (Mode != SessionMode.Local)
        {
            // WithPort is required even when the socket is supplied: Backdash validates
            // NetcodeOptions.LocalPort (it must be nonzero) before the factory is ever consulted. So
            // the port a pre-bound socket ALREADY holds is what gets declared here — which is also the
            // port the peer was told during the room handshake, so the two cannot drift apart.
            int port = setup.Socket?.Port ?? setup.Port;
            if (port <= 0)
                throw new ArgumentException(
                    "MatchNetSetup needs either a bound Socket or a nonzero Port; 0 means "
                    + "\"pick anything\", which cannot be announced to the peer in advance.");
            builder = builder.WithPort(port);
            if (setup.Socket != null) builder = builder.WithSocketFactory((_, _) => setup.Socket);
        }

        switch (Mode)
        {
            case SessionMode.Spectator:
                builder = builder.ForSpectator(setup.SpectateHost).WithPlayerCount(SeatCount);
                break;
            case SessionMode.Local:
                // Host drives both seats and nobody is watching: no peer exists, so there is nothing
                // to predict and no rollback can happen. Still the same handler and the same loop, so
                // this path is not a second implementation of the match.
                builder = builder.ForLocal().WithPlayers(BuildPlayers(setup));
                break;
            default:
                builder = builder.ForRemote().WithPlayers(BuildPlayers(setup));
                break;
        }

        _session = builder.Build();
    }

    private NetcodePlayer[] BuildPlayers(MatchNetSetup setup)
    {
        // ORDER IS THE CONTRACT: Backdash assigns NetcodePlayer.Index by position in this array, and
        // GetInputs fills its span in index order, so seat i must be player i. Everything downstream
        // (StepOnce feeding P1/P2, the replay recorder) depends on that.
        var list = new List<NetcodePlayer>(SeatCount + setup.Spectators.Length);
        for (int seat = 0; seat < SeatCount; seat++)
        {
            var p = _localSeat[seat]
                ? NetcodePlayer.CreateLocal()
                : NetcodePlayer.CreateRemote(setup.RemoteEndPoint
                    ?? throw new ArgumentException(
                        $"seat {seat} is remote but MatchNetSetup.RemoteEndPoint is null"));
            _seatPlayer[seat] = p;
            list.Add(p);
        }
        foreach (var ep in setup.Spectators) list.Add(NetcodePlayer.CreateSpectator(ep));
        return list.ToArray();
    }

    public static RollbackMatch Create(GameSim sim, IMatchPresenter view, MatchNetSetup setup)
    {
        var m = new RollbackMatch(sim, view, setup);
        // SetHandler must precede Start (Backdash docs), and doing it here rather than through
        // WithHandler in the constructor keeps a half-constructed object from being called back into
        // by OnSessionStart.
        m._session.SetHandler(m);
        m._session.Start(CancellationToken.None);
        return m;
    }

    public bool TryDequeueEvent(out MatchEvent e)
    {
        lock (_eventLock)
        {
            if (_events.Count == 0) { e = default; return false; }
            e = _events.Dequeue();
            return true;
        }
    }

    // ---- the live frame ----

    // Returns true when the sim advanced. False means this tick produced no frame — waiting for the
    // peer to synchronize, holding back because we are ahead (TimeSync), or the input queue is full.
    // The caller must NOT step the sim itself in that case; it should keep presenting the last frame.
    public bool Tick()
    {
        if (_closed) return false;

        // TimeSync asked us to give the other side time to catch up. Skipping whole frames is the
        // standard fix and it is what keeps both sims on the same frame number over a long match.
        if (_stallFrames > 0) { _stallFrames--; return false; }

        _session.BeginFrame();

        for (int seat = 0; seat < SeatCount; seat++)
        {
            if (!_localSeat[seat]) continue;
            ushort packed = _view.LocalInput(seat);
            if (_session.AddLocalInput(_seatPlayer[seat], packed) != ResultCode.Ok) return false;
        }

        if (_session.SynchronizeInputs() != ResultCode.Ok) return false;
        StepOnce(rollback: false);
        return true;
    }

    private void StepOnce(bool rollback)
    {
        var sync = _session.CurrentSynchronizedInputs;
        for (int i = 0; i < SeatCount; i++)
        {
            // A player who dropped mid-match has NEUTRAL inputs for the rest of the round
            // (PROTOCOL.md § Match lifecycle: the kick happens when the match ends, not mid-round).
            // Both machines read the same Disconnected flag from the same confirmed frames, so this
            // substitution is deterministic and cannot itself cause a desync.
            _inputs[i] = i < sync.Length && !sync[i].Disconnected ? sync[i].Input : (ushort)0;
        }

        var f0 = ReplayData.Unpack(_inputs[0]);
        var f1 = ReplayData.Unpack(_inputs[1]);
        int frame = _session.GetInfo().CurrentFrame.Number;

        var res = _sim.Step(f0, f1);
        FramesAdvanced++;
        _view.OnFrame(frame, f0, f1, res, rollback);

        _session.AdvanceFrame();
    }

    // ---- INetcodeSessionHandler ----

    public void OnSessionStart() { Mark(); }

    public void OnSessionClose() { Mark(); }

    public void SaveState(Frame frame, ref readonly BinaryBufferWriter writer)
    {
        Mark();
        int simLen = _sim.SaveState(_scratch);
        var vw = new SimStateWriter(_scratch.AsSpan(simLen));
        _view.SaveView(ref vw);
        int viewLen = vw.BytesWritten;

        // Layout: [i32 simLen][i32 viewLen][sim bytes][view bytes], written through ONE AllocSpan.
        // Deliberately not built out of Backdash's primitive writers: the state layout stays ours, so
        // GameSim.SaveState remains the single definition of what a state is and the replay scrubber
        // reads the same bytes.
        var dst = writer.AllocSpan<byte>(8 + simLen + viewLen);
        WriteI32(dst, 0, simLen);
        WriteI32(dst, 4, viewLen);
        _scratch.AsSpan(0, simLen + viewLen).CopyTo(dst.Slice(8));
    }

    public void LoadState(Frame frame, ref readonly BinaryBufferReader reader)
    {
        Mark();
        var src = reader.CurrentBuffer;
        int simLen = ReadI32(src, 0);
        int viewLen = ReadI32(src, 4);

        _sim.LoadState(src.Slice(8, simLen));
        var vr = new SimStateReader(src.Slice(8 + simLen, viewLen));
        _view.LoadView(ref vr);

        reader.Advance(8 + simLen + viewLen);
    }

    // Called once per frame Backdash needs to redo, after LoadState.
    public void AdvanceFrame()
    {
        Mark();
        if (_session.SynchronizeInputs() != ResultCode.Ok) return;
        StepOnce(rollback: true);
    }

    public void BeginRollback(Frame frame)
    {
        Mark();
        RollbackCount++;
        _view.OnRollbackBegin(frame.Number);
    }

    public void EndRollback(Frame frame)
    {
        Mark();
        _view.OnRollbackEnd(frame.Number);
    }

    public void TimeSync(FrameSpan framesAhead)
    {
        Mark();
        _stallFrames = Math.Max(_stallFrames, framesAhead.Frames);
    }

    public void OnPeerEvent(NetcodePlayer player, in PeerEventInfo evt)
    {
        Mark();
        switch (evt.Type)
        {
            case PeerEvent.Synchronizing:
                Emit(MatchEventKind.Synchronizing, "正在与对方同步…");
                break;
            case PeerEvent.Synchronized:
                Synchronized = true;
                Emit(MatchEventKind.Synchronized,
                    $"已同步（延迟 {evt.Synchronized.Ping.TotalMilliseconds:F0} ms）");
                break;
            case PeerEvent.ConnectionInterrupted:
                Emit(MatchEventKind.Interrupted,
                    $"网络中断，{evt.ConnectionInterrupted.DisconnectTimeout.TotalSeconds:F0} 秒内未恢复将判定掉线");
                break;
            case PeerEvent.ConnectionResumed:
                Emit(MatchEventKind.Resumed, "网络已恢复");
                break;
            case PeerEvent.Disconnected:
                PeerDisconnected = true;
                Emit(MatchEventKind.Disconnected, "对方已掉线，其输入按空输入处理");
                break;
            case PeerEvent.SynchronizationFailure:
                Emit(MatchEventKind.SyncFailed, "同步失败，无法开始对局");
                break;
            case PeerEvent.ChecksumMismatch:
            {
                var info = evt.ChecksumMismatch;
                // A desync means the two machines are no longer simulating the same match. It is
                // reported, never repaired: silently continuing would show each player a different
                // fight. Loud is the whole point (see the golden-checksum test).
                Emit(new MatchEvent(MatchEventKind.Desync,
                    $"状态校验不一致（第 {info.MismatchFrame.Number} 帧）",
                    info.MismatchFrame.Number,
                    info.LocalChecksum.Value, info.RemoteChecksum.Value));
                break;
            }
        }
    }

    // SyncTest debug only; the project's desync detection is the checksum above.
    public object CreateState(Frame frame, ref readonly BinaryBufferReader reader) { Mark(); return null; }

    public void OnReplayCompleted() { Mark(); }

    // ---- helpers ----

    private void Mark()
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId) ForeignThreadCallbacks++;
    }

    private void Emit(MatchEventKind kind, string text) => Emit(new MatchEvent(kind, text));

    private void Emit(MatchEvent e)
    {
        lock (_eventLock) _events.Enqueue(e);
    }

    private static void WriteI32(Span<byte> b, int at, int v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    private static int ReadI32(ReadOnlySpan<byte> b, int at) =>
        b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { (_session as IDisposable)?.Dispose(); } catch { }
    }
}
