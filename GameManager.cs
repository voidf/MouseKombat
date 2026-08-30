using Godot;
using System.Collections.Generic;
using MouseKombat.Net;
using MouseKombat.Sim;

// Presentation VIEW + match director. Owns the headless GameSim, feeds it two InputFrames
// per physics tick, and renders the returned events: HP bars, hit/guard FX + SFX, command
// popups, projectile view nodes, and the win/reset sequence. All combat logic is in the sim.
//
// The two fighters are NOT part of this scene: the ready screen picks a character per seat, and
// _Ready instantiates the matching Char_*.tscn (see CharacterDb). P1Slot / P2Slot are design-time
// markers that give the fighters their world position and draw order.
public partial class GameManager : Node2D, IMatchPresenter
{
    [Export] public Node2D P1Slot;   // marker: position + draw order for the P1 fighter
    [Export] public Node2D P2Slot;

    // Resolved after the characters are spawned. Everything below reads these, never the slots.
    private Player p1;
    private Player p2;

    // Fallback for opening MFEntry.tscn straight from the editor with no lobby selection.
    [Export] public CharacterId DebugP1Character = CharacterId.Hamster;
    [Export] public CharacterId DebugP2Character = CharacterId.Kangaroo;

    [Export] public ColorRect hp1Fill;
    [Export] public ColorRect hp2Fill;
    [Export] public float HpBarFullWidth = 260f;

    // The info line ABOVE each HP bar: 名字 · 分数 · ping. Only a LOBBY match has the data —
    // scores ride the room snapshot (PlayerInfo.Score), pings are the server heartbeat's
    // PingStats — so LAN and local play keep the plain bars (labels stay hidden).
    [Export] public Label P1HudInfo;
    [Export] public Label P2HudInfo;

    // ONE victory splash node for both sides. Its SpriteFrames is swapped per match from the
    // WINNING CHARACTER's roster entry, because the splash belongs to the character, not the seat:
    // with two side-specific nodes, a P2 win played the kangaroo splash even when P2 had picked the
    // hamster (and mirror matches made that visible immediately).
    //
    // LAYERING (MFEntry canvas layers, lowest first):
    //   0  the world — background, fighters, hit FX, projectiles, the floating name tags
    //   1  HUD        — HP bars
    //   2  WinFx      — this splash
    //   3  WinTextFx  — the victory text
    // Canvas layer, not child order, is what decides this: HUD is a CanvasLayer, so it used to draw
    // over the splash no matter where the splash sat among its siblings.
    [Export] public AnimatedSprite2D WinAnim;
    [Export] public string WinAnimName = "default";

    // ONE pair of victory-text Labels, same reason as WinAnim above: line 1 is the WINNER'S
    // fighting name (it used to be baked per side as "BISON" / "KANGIEFOO"), line 2 is "WINS" for
    // everyone. Line 1's text comes from CharacterDb.WinName at win time.
    [Export] public Label WinTextLine1;
    [Export] public Label WinTextLine2;

    [Export] public float WinTextFlyInSec = 0.4f;
    [Export] public float WinTextDwellSec = 1.0f;
    [Export] public float WinTextFadeOutSec = 0.12f;

    private Vector2 _l1Home, _l2Home;   // fly-in destinations, captured before the labels are hidden
    private float _l1BoxL, _l1BoxR;     // line 1's authored box, for the centring below
    private Tween _line1Tween, _line2Tween;

    // The victory presentation has TWO independent timelines: the splash animation and the text
    // fly-in/dwell/fade. The next round may only start once BOTH are done. Resetting on the
    // animation alone cut the text off mid-show — the hamster splash is 33 frames at 60 fps (0.55 s)
    // while the text needs 0.4 + 1.0 + 0.12 = 1.52 s, so the text was wiped ~0.15 s after arriving,
    // which reads as "the splash is covering the text".
    private bool _winAnimDone, _winTextDone;

    [Export] public Vector2 P1StartPos = new Vector2(120, 560);
    [Export] public Vector2 P2StartPos = new Vector2(650, 560);

    [Export] public float StageMinX = 40f;
    [Export] public float StageMaxX = 760f;

    [Export] public PackedScene HitFxScene;        // FX_Hit.tscn — spawned on a confirmed (unblocked) hit
    [Export] public PackedScene GuardFxScene;      // FX_Guard.tscn — spawned on a block
    [Export] public float HitFxLifetime = 0.2f;    // seconds before a spawned FX is freed

    [Export] public PackedScene CmdPopupScene;     // cmd_popup.tscn — command-success banner (bg + label)

    [Export] public AudioStreamPlayer Bgm;         // looped combat BGM
    [Export] public AudioStreamPlayer SfxHit;      // played on a clean hit
    [Export] public AudioStreamPlayer SfxGuard;    // played on a block
    // ± fractional pitch offset randomized per play for the two hit SFX (0 = off).
    [Export] public float SfxPitchVariation = 0.12f;

    private enum Phase { Fighting, Win, Resetting }
    private Phase _phase = Phase.Fighting;

    // ---- replay recording ----
    // One file per KO: a "match" is a single knockout (the round then resets and a fresh recording
    // starts), which is also the unit the networked modes return to the lobby on.
    [Export] public bool RecordReplays = true;
    private ReplayData _recording;

    // ---- networked match ----
    // Null for local play. When set, the ROLLBACK SESSION owns the clock: this director no longer
    // decides when a frame happens, it implements IMatchPresenter and is called back once per advanced
    // frame — including for frames being re-simulated after a misprediction.
    private RollbackMatch _netMatch;
    private MatchPlan _plan;
    private bool _inRollback;
    private int _stalledTicks;

    // A KO seen on a PREDICTED frame can be taken back. Starting the victory sequence would stop the
    // sim, so the rollback that retracts it would never arrive and the two machines would disagree
    // about whether the match is over. So in a net match the knockout has to stand for this many
    // frames before the splash starts — comfortably more than Backdash's 8 prediction frames.
    [Export] public int NetKoConfirmFrames = 10;

    // Frames of input delay in a networked match. Trades a little local input lag for fewer
    // mispredictions; 2 at 60 fps is ~33 ms, which a LAN does not need much of. Exported because the
    // right value depends on the link, not on the game.
    [Export] public int NetInputDelayFrames = 2;
    private int _koHeldFrames = -1;
    private int _pendingNetWinner = -1;

    // ---- mid-match spectating (host side) ----
    // The host keeps every frame of the current match here (RecordAt semantics: a rollback re-sim
    // overwrites the speculative value). A player joining mid-match gets the CONFIRMED prefix as
    // MatchCatchUp, replays it to the current state, and then follows the per-tick MatchInputs
    // stream — which also serves only CONFIRMED frames (RollbackMatch.ConfirmedFrame): the joiner's
    // sim steps monotonically and can never be rewound, so a speculative frame that a later rollback
    // corrected would diverge the joiner's view forever. _spectatorNextFrame tracks, per joiner,
    // which frame has been delivered, so the stream is gap-free.
    private readonly ReplayData _netHistory = new();
    private readonly Dictionary<int, int> _spectatorNextFrame = new();
    private readonly HashSet<int> _streamNotified = new();   // diagnostics: printed once per joiner

    [Export] public string NetSeatScenePath = "res://NetSeat.tscn";

    private Label _netStatus;
    private AcceptDialog _netDropPopup;
    private string _netDropTarget;        // scene the drop popup lands on when confirmed
    private bool _leavingNetMatch;

    private GameSim _sim;
    private readonly Dictionary<int, Projectile> _projViews = new(); // sim projectile id -> view node
    private readonly HashSet<int> _liveIds = new();
    private readonly List<int> _toRemove = new();

    public override void _Ready()
    {
        StartBgm();

        if (!SpawnFighters()) return; // nothing to run a match with; the errors are already logged

        // device bindings chosen in the ready screen; null Source => InputMap fallback.
        // An Agent (state-machine or ONNX policy) overrides the device when set.
        if (GameSession.Configured)
        {
            p1.Source = GameSession.P1; p1.Agent = GameSession.P1Agent;
            p2.Source = GameSession.P2; p2.Agent = GameSession.P2Agent;
        }
        else
        {
            // Dev/testing hook: with no lobby config, env vars can bind an AI to each slot.
            //   MK_AI_P1 / MK_AI_P2 = "statemachine"  OR  a model path (e.g. res://ai_rl_model/x.onnx)
            // No effect in normal play. Enables headless AI-vs-AI runs.
            BindDebugAgent(p1, OS.GetEnvironment("MK_AI_P1"), 0);
            BindDebugAgent(p2, OS.GetEnvironment("MK_AI_P2"), 1);
        }

        // Build the sim from the players' exported tuning; force start pos/facing to the
        // director's own values (matches the original reset convention: p1 faces right, p2 left).
        var cfg1 = p1.BuildConfig();
        cfg1.SetStart(P1StartPos.X, P1StartPos.Y, facingRight: true);
        var cfg2 = p2.BuildConfig();
        cfg2.SetStart(P2StartPos.X, P2StartPos.Y, facingRight: false);

        float worldViewWidth = GetViewport().GetVisibleRect().Size.X;
        _sim = new GameSim(cfg1, cfg2, StageMinX, StageMaxX, worldViewWidth);
        p1.Bind(_sim.P1);
        p2.Bind(_sim.P2);

        if (WinAnim != null)
        {
            WinAnim.Visible = false;
            WinAnim.AnimationFinished += OnWinAnimFinished;
        }
        if (WinTextLine1 != null) { _l1BoxL = WinTextLine1.OffsetLeft; _l1BoxR = WinTextLine1.OffsetRight; }
        CacheAndHideLabel(WinTextLine1, ref _l1Home);
        CacheAndHideLabel(WinTextLine2, ref _l2Home);
        UpdateHpBars();
        StartRecording();
        StartNetMatch();
    }

    // ---- networked match setup ----

    private void StartNetMatch()
    {
        _plan = GameSession.NetPlan;
        if (_plan == null) return;

        var net = NetSession.Instance;
        BuildNetStatusLabel();
        BuildNetDropPopup();
        if (net != null)
        {
            net.MatchEnded += OnNetMatchEnded;
            net.Disconnected += OnNetDisconnected;
            net.LobbyRoomClosed += OnNetRoomClosed;
            net.PlayerJoined += OnHostPlayerJoined;
        }

        var setup = new MatchNetSetup
        {
            LocalSeat = new[] { _plan.LocalSeat[0], _plan.LocalSeat[1] },
            RemoteEndPoint = _plan.RemoteEndPoint,
            Spectators = _plan.Spectators,
            SpectateHost = _plan.SpectateHost,
            // A client hands over the socket it bound during the handshake (its port was announced in
            // Hello and must not change); the host just names the room port and lets Backdash bind it.
            // In a lobby EVERY machine is a client of the server, and the session's socket is the
            // envelope wrapper (see LobbyMatchSocket) — except an all-local lobby match, which has no
            // UDP traffic at all and passes null like the LAN host.
            Socket = net != null && net.IsLobby ? net.LobbySocket
                  : (net != null && !net.IsHost ? net.MatchSocket : null),
            Port = net?.MatchUdpPort ?? 0,
            InputDelayFrames = NetInputDelayFrames,
            FrameRate = Engine.PhysicsTicksPerSecond,
            LogSink = msg => GD.Print(msg),
        };

        try
        {
            _netMatch = RollbackMatch.Create(_sim, this, setup);
            // Nothing to wait for in a session with no peer (this machine drives both seats: host +
            // AI, or AI vs AI). Backdash emits no Synchronizing/Synchronized for a Local session, so
            // a "正在与对方同步…" set here would never be cleared and would sit on screen for the
            // whole match — which is exactly what the AI-vs-AI lobby round showed.
            SetNetStatus(!_netMatch.HasRemotePeer ? ""
                       : _plan.Role == MatchRole.Spectator ? "正在连接对局…"
                       : "正在与对方同步…");
        }
        catch (System.Exception e)
        {
            // Keep the full picture in the log: which socket/port the session was handed decides
            // whether a lobby match can even start, and a null socket with a leftover LAN port is
            // exactly the kind of mismatch that shows up only in a real room.
            GD.PushError($"[GameManager] rollback session failed: {e}");
            GD.PushError($"[GameManager] setup: role={_plan?.Role} lobbySocket={net?.LobbySocket?.GetType().Name}" +
                         $" port={net?.MatchUdpPort} remote={_plan?.RemoteEndPoint}");
            SetNetStatus($"无法建立对局同步：{e.Message}");
            _recording = null;
            ShowNetDrop($"无法建立对局同步：{e.Message}");
        }
    }

    // A recording captures ONE knockout. The header carries everything playback needs to rebuild the
    // match except the per-character tuning, which is deliberately left out (see ReplayData).
    private void StartRecording()
    {
        if (!RecordReplays || _sim == null) { _recording = null; return; }
        _recording = new ReplayData
        {
            Mode = GameSession.Mode,
            GameVersion = (string)ProjectSettings.GetSetting("application/config/version", ""),
            StartedUnixUtc = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            P1Name = GameSession.P1Name,
            P2Name = GameSession.P2Name,
            P1Char = p1.Character,
            P2Char = p2.Character,
            StageMinX = StageMinX,
            StageMaxX = StageMaxX,
            WorldWidth = GetViewport().GetVisibleRect().Size.X,
            P1StartX = P1StartPos.X, P1StartY = P1StartPos.Y,
            P2StartX = P2StartPos.X, P2StartY = P2StartPos.Y,
            RoomId = GameSession.RoomId,
            Host = GameSession.Host,
        };
    }

    // Stamp the end-state checksum and write the file. The checksum is what lets playback detect that
    // this build no longer simulates these inputs the same way (a balance change since recording).
    private void FinishRecording()
    {
        if (_recording == null) return;
        if (_recording.FrameCount > 0)
        {
            _recording.FinalChecksum = _sim.Checksum();
            string path = ReplayStore.Save(_recording);
            if (path != null) GD.Print($"[replay] saved {path} ({_recording.FrameCount} frames)");
        }
        _recording = null;
    }

    // Instantiate the two selected characters. The slot markers are DESIGN-TIME anchors: when
    // present their position wins over the exports, so the stage layout is editable in the editor.
    // The fighters themselves are parented to this director, not to the markers — SimPlayer.Position
    // is world space (see CharacterDb.Spawn).
    private bool SpawnFighters()
    {
        var c1 = GameSession.Configured ? GameSession.P1Char : DebugP1Character;
        var c2 = GameSession.Configured ? GameSession.P2Char : DebugP2Character;

        if (P1Slot != null) P1StartPos = P1Slot.Position;
        if (P2Slot != null) P2StartPos = P2Slot.Position;

        p1 = CharacterDb.Spawn(c1, this, P1StartPos, 0);
        p2 = CharacterDb.Spawn(c2, this, P2StartPos, 1);

        // Draw order WITHIN the world layer: a runtime AddChild lands last, which would put the
        // fighters over the hit FX and name tags. Slot each one in where its marker sits among the
        // director's children instead, so the marker controls layering as well as position (the
        // background TextureRect stays behind them). The victory splash and text are on their own
        // canvas layers and are unaffected by this — see the layering note on WinAnim.
        if (p1 != null && P1Slot != null) MoveChild(p1, P1Slot.GetIndex());
        if (p2 != null && P2Slot != null) MoveChild(p2, P2Slot.GetIndex());

        if (p1 == null || p2 == null)
        {
            GD.PushError($"[GameManager] failed to spawn fighters ({c1} vs {c2}).");
            return false;
        }

        BuildNameTags();
        return true;
    }

    // ---- name tags ----
    // A "name ▼" label floating over each fighter. Local play shows 1P / 2P; online fills in the
    // player-supplied name (display only — never an identity). Built in code rather than as a scene
    // because the fighters themselves are now created at runtime.
    //
    // These live in the WORLD (canvas layer 0), which is what puts them under the victory splash and
    // text. Draw order within the world comes from child order: they are added after the fighters, so
    // they sit above them. See the layering note on WinAnim.
    [Export] public int TagFontSize = 15;
    [Export] public Color P1TagColor = new Color(0.55f, 0.85f, 1f);
    [Export] public Color P2TagColor = new Color(1f, 0.72f, 0.55f);

    private Label _p1Tag, _p2Tag;

    private void BuildNameTags()
    {
        _p1Tag = MakeTag(GameSession.P1Name, P1TagColor);
        _p2Tag = MakeTag(GameSession.P2Name, P2TagColor);
        UpdateNameTags();
    }

    private Label MakeTag(string text, Color color)
    {
        var l = new Label
        {
            Text = (string.IsNullOrWhiteSpace(text) ? "?" : text) + "\n▼",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(180, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", TagFontSize);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 4);
        AddChild(l);
        return l;
    }

    // Follows the fighter's feet anchor, so it tracks jumps and knockdowns rather than hovering at
    // a fixed height. Called on the logic tick alongside the animation.
    private void UpdateNameTags()
    {
        PlaceTag(_p1Tag, p1);
        PlaceTag(_p2Tag, p2);
    }

    private static void PlaceTag(Label tag, Player who)
    {
        if (tag == null || who == null) return;
        tag.Position = who.Position + new Vector2(-tag.Size.X * 0.5f, who.TagOffsetY);
    }

    [Export] public string ReadyScenePath = "res://ReadyScreen.tscn";
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";
    [Export] public string LobbyMenuScenePath = "res://LobbyMenu.tscn";

    // Esc bails out of the match and returns to the ready screen so devices can be re-bound.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            // Explicitly DISABLED in a networked match (spec: 局内 ESC 回主界面禁用). One player walking
            // out mid-round would leave the other simulating against a seat nobody is driving, and the
            // room's own rules already say when a match ends.
            if (_netMatch != null) return;
            // Drop the partial recording: a replay file represents a completed knockout, and half a
            // round with no result is not something the list should offer.
            _recording = null;
            GameSession.Clear();
            GetTree().ChangeSceneToFile(ReadyScenePath);
        }
    }

    private static void CacheAndHideLabel(Label l, ref Vector2 home)
    {
        if (l == null) return;
        home = l.GlobalPosition;
        l.Visible = false;
        l.Modulate = new Color(1, 1, 1, 1);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_sim == null) return; // SpawnFighters failed; errors already logged in _Ready

        UpdateHudInfo();
        if (_netMatch != null) DrainNetEvents();

        if (_phase != Phase.Fighting)
        {
            // The sim is paused, but the ANIMATION clock is the physics tick now (see
            // Player.TickAnimation), so it still has to be ticked or the fighters freeze
            // mid-pose during the win sequence.
            p1.TickAnimation();
            p2.TickAnimation();
            UpdateNameTags();
            UpdateHpBars();
            return;
        }

        if (_netMatch != null) { NetTick(); return; }

        // Record the frames ACTUALLY fed to the sim, which includes anything an AI decided. That is
        // what makes an AI match replayable without shipping the model: the replay carries the
        // resulting inputs, not the policy.
        var f1 = FrameFor(p1, 0);
        var f2 = FrameFor(p2, 1);
        _recording?.Record(f1, f2);
        var res = _sim.Step(f1, f2);
        PresentFrame(res, rollback: false);

        // winner index: 1 = P2 won (P1 dead), 0 = P1 won (P2 dead) — matches old CheckKO mapping
        if (res.MatchOverWinner >= 0)
        {
            FinishRecording();
            BeginWin(res.MatchOverWinner);
        }
    }

    // Everything the view does for ONE advanced logic frame. Shared by local play and by the rollback
    // session, which is what keeps a re-simulated frame from being presented differently to a live one.
    //
    // `rollback` suppresses only what would fire TWICE for a frame the player already saw: hit sparks,
    // sound, and the command banner. Position, animation and HP are recomputed either way because they
    // are pure functions of the state — and because a whole rollback plus the following live frame
    // happen inside ONE physics tick, only the last of them is ever drawn.
    private void PresentFrame(StepResult res, bool rollback)
    {
        // push logic -> views (position + this frame's animation commands), then advance the
        // animation by exactly one logic frame
        p1.SyncFromSim();
        p2.SyncFromSim();
        p1.TickAnimation();
        p2.TickAnimation();
        UpdateNameTags();

        // Views are reconciled BY ID rather than created from the spawn event, so a projectile that a
        // rollback re-creates gets one node, not a second one. See SyncProjectileViews.
        SyncProjectileViews();

        if (!rollback)
        {
            foreach (var h in res.Hits)
                PlayHitFeedback(h.Result, h.WorldHitbox.ToGodot(), h.DefenderIndex == 0 ? p1 : p2);

            foreach (var pop in res.Popups) ShowCommandPopup(pop.PlayerIndex, pop.Text);
        }

        UpdateHpBars();
    }

    // ---- the networked clock ----

    private void NetTick()
    {
        if (!_netMatch.Tick())
        {
            // The session produced no frame: still synchronizing, or holding back because we are ahead
            // of the peer. Nothing is ticked — the sim did not move and the animation is locked to the
            // sim (see Player.TickAnimation), so freezing is the right picture of "waiting".
            _stalledTicks++;
            // A knockout has the same symptom WITHOUT a network problem: the opponent's session stops
            // driving its session the moment IT enters its win sequence, so OUR session stalls until
            // the host's MatchEnded arrives. Over a finished KO that must not read as "waiting for
            // the opponent" — the sim already says the round is over.
            if (_stalledTicks == 30 && _netMatch.Synchronized && !_sim.MatchOver) SetNetStatus("等待对方…");
            return;
        }
        if (_stalledTicks > 0) { _stalledTicks = 0; SetNetStatus(""); }
        ServeLobbySpectators();
        StreamCatchUpSpectators();
        ReportConfirmedFrames();

        // A CONFIRMED knockout, not a predicted one — see NetKoConfirmFrames.
        if (_koHeldFrames >= NetKoConfirmFrames)
        {
            _koHeldFrames = -1;
            FinishRecording();
            BeginWin(_pendingNetWinner);
        }
    }

    // ---- relay-config spectating: the fighter's report to the host ----
    // When both fighters are clients the host has no simulation and cannot learn the inputs any
    // other way, so every non-host fighter reports the frames it CONFIRMED since the last report
    // (MatchInputReport), plus the match geometry for the host's catch-up. The report is a slice of
    // _netHistory between a cursor and RollbackMatch.ConfirmedFrame, so only frames a rollback can
    // never take back ever reach the host.
    private int _reportedUpTo;

    private void ReportConfirmedFrames()
    {
        if (_plan == null || _plan.Role != MatchRole.Fighter) return;
        var net = NetSession.Instance;
        if (net == null || net.IsHost) return;   // the host player has its own history
        int upTo = System.Math.Min(_netMatch.ConfirmedFrame + 1, _netHistory.FrameCount);
        if (upTo <= _reportedUpTo) return;
        int count = upTo - _reportedUpTo;
        var rep = new MatchInputReport
        {
            StartFrame = _reportedUpTo,
            StageMinX = StageMinX,
            StageMaxX = StageMaxX,
            WorldWidth = GetViewport().GetVisibleRect().Size.X,
            P1StartX = P1StartPos.X,
            P1StartY = P1StartPos.Y,
            P2StartX = P2StartPos.X,
            P2StartY = P2StartPos.Y,
        };
        rep.P1 = new ushort[count];
        rep.P2 = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            rep.P1[i] = _netHistory.P1Inputs[_reportedUpTo + i];
            rep.P2[i] = _netHistory.P2Inputs[_reportedUpTo + i];
        }
        // LAN: straight to the host. Lobby: to the server, which forwards it to the host player.
        if (net.Lobby != null) net.Lobby.Send(MsgType.MatchInputReport, rep);
        else net.Client?.Send(MsgType.MatchInputReport, rep);
        _reportedUpTo = upTo;
    }

    // ---- mid-match spectating: serving the confirmed-input stream ----
    // Runs once per tick on the host, after the session advanced. Each mid-match joiner has a "next
    // frame to deliver" cursor; everything between the cursor and the CONFIRMED frame goes out as one
    // batch. On a healthy link that is one frame per physics tick; a burst after a stall carries more.
    private void StreamCatchUpSpectators()
    {
        if (_spectatorNextFrame.Count == 0 || !IsHostNetMatch) return;
        var net = NetSession.Instance;
        if (net == null) return;
        // Belt and braces under the RollbackMatch clamp: never read past the recorded history, even
        // if a future change makes ConfirmedFrame lie again — an out-of-range crash inside a poll
        // would take the whole match down.
        int upTo = System.Math.Min(_netMatch.ConfirmedFrame, _netHistory.FrameCount - 1);
        if (upTo < 0) return;
        var gone = new List<int>();
        foreach (var kv in _spectatorNextFrame)
        {
            // A joiner who left the room mid-match has no stream to serve any more. The room snapshot
            // is refreshed on PlayerLeft, so membership is answerable from the host's own copy.
            if (!RoomContains(net.Room, kv.Key)) { gone.Add(kv.Key); continue; }
            if (kv.Value > upTo) continue;   // nothing new confirmed yet
            int start = kv.Value;
            int count = upTo - start + 1;
            var msg = new MatchInputs { StartFrame = start };
            msg.P1 = new ushort[count];
            msg.P2 = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                msg.P1[i] = _netHistory.P1Inputs[start + i];
                msg.P2[i] = _netHistory.P2Inputs[start + i];
            }
            net.SendTo(kv.Key, MsgType.MatchInputs, msg);
            _spectatorNextFrame[kv.Key] = upTo + 1;
            if (_streamNotified.Add(kv.Key))
                GD.Print($"[catchup] host stream to player {kv.Key}: first batch frames {start}..{upTo}");
        }
        foreach (int id in gone) _spectatorNextFrame.Remove(id);
    }

    private static bool RoomContains(RoomSnapshot room, int playerId)
    {
        if (room == null) return false;
        foreach (var p in room.Players) if (p.PlayerId == playerId) return true;
        return false;
    }

    // A player joined while the match is running: hand them the CONFIRMED history and open a stream
    // from the next frame. Only the host can (it is the only machine that knows both seats' inputs),
    // and only while it runs the session itself — a relay host has no history and therefore sends
    // nothing, which is exactly the current "cannot watch this configuration" behavior.
    private void OnHostPlayerJoined(int playerId)
    {
        if (!IsHostNetMatch || playerId <= 0) return;
        var net = NetSession.Instance;
        if (net == null || net.Room == null || !net.Room.MatchRunning) return;

        // The speculative tail of the history (frames newer than the confirmed point) is not sent:
        // the joiner must land on a state nothing can take back. If the session has not confirmed
        // any frame yet the history is still empty; FrameCount=0 is a legal catch-up (the joiner
        // starts at frame 0 and the stream feeds everything).
        int count = System.Math.Min(_netMatch.ConfirmedFrame + 1, _netHistory.FrameCount);
        var p1 = new ushort[count];
        var p2 = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            p1[i] = _netHistory.P1Inputs[i];
            p2[i] = _netHistory.P2Inputs[i];
        }
        var cu = new MatchCatchUp
        {
            Room = net.Room,
            StageMinX = StageMinX,
            StageMaxX = StageMaxX,
            WorldWidth = GetViewport().GetVisibleRect().Size.X,
            P1StartX = P1StartPos.X,
            P1StartY = P1StartPos.Y,
            P2StartX = P2StartPos.X,
            P2StartY = P2StartPos.Y,
            FrameCount = count,
            P1Inputs = p1,
            P2Inputs = p2,
        };
        _spectatorNextFrame[playerId] = count;
        net.SendTo(playerId, MsgType.MatchCatchUp, cu);
        GD.Print($"[catchup] host sent catch-up to player {playerId}: {count} frames");
    }

    // A lobby's spectators all watch the DATA stream (PROTOCOL.md § Lobby): the host player serves a
    // catch-up to EVERY seatless member, not just those who joined mid-match. Runs once per tick;
    // anyone without a cursor yet is treated exactly like a fresh joiner, so a spectator who was
    // already in the room when the match started gets the same package as one who joined after.
    private void ServeLobbySpectators()
    {
        var net = NetSession.Instance;
        if (net == null || !net.IsLobby || !net.IsHost || net.Room == null
            || !net.Room.MatchRunning || _netMatch == null) return;
        foreach (var p in net.Room.Players)
        {
            if (p.PlayerId == net.LocalPlayerId) continue;   // the host player, not a watcher
            if (p.Seat >= 0 || !p.Connected) continue;       // fighters watch their own match
            if (_spectatorNextFrame.ContainsKey(p.PlayerId)) continue;
            OnHostPlayerJoined(p.PlayerId);
        }
    }

    private bool IsHostNetMatch => _netMatch != null && NetSession.Instance?.IsHost == true;

    private void DrainNetEvents()
    {
        while (_netMatch.TryDequeueEvent(out var e))
        {
            switch (e.Kind)
            {
                case MatchEventKind.Desync:
                    // Never repaired, always reported: from here on the two machines are watching
                    // different fights, and pretending otherwise is worse than saying so.
                    GD.PushError($"[net] {e.Text} local={e.LocalChecksum:X8} remote={e.RemoteChecksum:X8}");
                    SetNetStatus(e.Text + " —— 双方画面可能已不一致");
                    break;
                case MatchEventKind.Synchronized:
                    SetNetStatus("");
                    break;
                case MatchEventKind.SyncFailed:
                    // The handshake never completed, and Backdash does not retry after this. Every
                    // machine in the room would otherwise sit there forever — the fighters frozen on
                    // frame 0 with "同步失败", the spectators on "正在获取对局数据…", and the room
                    // stuck in MatchRunning with ESC disabled. So END the match: the host clears the
                    // room state, a client reports the result, and everyone lands back on the seat
                    // screen where the round can be started again.
                    GD.PushWarning($"[net] {e.Text} — aborting the match back to seat select");
                    SetNetStatus(e.Text);
                    _pendingNetWinner = -1;
                    _recording = null;
                    if (_phase != Phase.Win) ReturnToSeatScreen();
                    return;
                default:
                    SetNetStatus(e.Text);
                    break;
            }
        }
    }

    // ---- IMatchPresenter ----

    public ushort LocalInput(int seat) => ReplayData.Pack(FrameFor(seat == 0 ? p1 : p2, seat));

    // The three fields per fighter a rewind has to restore alongside the sim state, because the sim
    // knows nothing about them (see Player.ViewState).
    public void SaveView(ref SimStateWriter w)
    {
        WriteView(ref w, p1.SaveView());
        WriteView(ref w, p2.SaveView());
    }

    public void LoadView(ref SimStateReader r)
    {
        p1.LoadView(ReadView(ref r));
        p2.LoadView(ReadView(ref r));
    }

    private static void WriteView(ref SimStateWriter w, Player.ViewState v)
    {
        w.ShortString(v.Clip);
        w.Int(v.Frame);
        w.Bool(v.Reverse);
    }

    private static Player.ViewState ReadView(ref SimStateReader r)
    {
        string clip = r.ShortString() ?? "";
        int frame = r.Int();
        bool rev = r.Bool();
        return new Player.ViewState(clip, frame, rev);
    }

    public void OnFrame(int frame, InputFrame f0, InputFrame f1, StepResult res, bool rollback)
    {
        // RecordAt, not Record: a frame first simulated with a PREDICTED opponent input is re-simulated
        // with the real one, and the replay has to keep the confirmed version.
        _recording?.RecordAt(frame, f0, f1);
        // The catch-up history is the same data, kept independently of the replay-recording setting:
        // a mid-match joiner must be able to catch up even when no replay files are written. The
        // host uses its own copy to serve joiners; a non-host fighter reports the CONFIRMED prefix of
        // its copy to the host (relay configuration), and the speculative tail never reaches anyone.
        if (_netMatch != null) _netHistory.RecordAt(frame, f0, f1);
        PresentFrame(res, rollback);

        // How long the knockout has stood. A rollback that erases it clears MatchOver on the sim, which
        // is exactly what this reads — no second copy of the fact to get out of step.
        //
        // The sim reports the winner on EVERY frame from the knockout on (a dead player stays Dead, so
        // CheckKO keeps re-setting MatchOverWinner), so a bare "res.MatchOverWinner >= 0" reset the
        // counter to 0 every frame and the KO was never confirmed — the match hung in Fighting with the
        // loser frozen and the winner free to act. Start the count on the first such frame, increment it
        // on the live frames that follow, and count nothing for re-simulated (predicted) frames: those
        // are exactly the ones that can still be taken back.
        if (res.MatchOverWinner >= 0)
        {
            _pendingNetWinner = res.MatchOverWinner;
            if (_koHeldFrames < 0) _koHeldFrames = 0;
            else if (!rollback) _koHeldFrames++;
        }
        else if (!_sim.MatchOver) { _pendingNetWinner = -1; _koHeldFrames = -1; }
        else if (_koHeldFrames >= 0 && !rollback) _koHeldFrames++;
    }

    public void OnRollbackBegin(int frame) => _inRollback = true;

    // The last re-simulated frame already wrote its sprite frame through PresentFrame, so there is
    // nothing to repair here. The flag exists so "am I re-simulating" is answerable at all.
    public void OnRollbackEnd(int frame) => _inRollback = false;

    // ---- leaving a networked match ----

    private void OnNetMatchEnded(MatchEnded ended)
    {
        // Normally each machine reaches the knockout on its own and leaves when its victory sequence
        // finishes. This is the safety net: if we never see the KO (a desync, a stalled peer), the
        // host's MatchEnded still gets everyone back to seat select.
        if (_phase == Phase.Win) return;   // let the local sequence finish first
        ReturnToSeatScreen();
    }

    private void OnNetDisconnected(string reason)
    {
        if (_leavingNetMatch) return;
        _recording = null;   // half a round with no result is not a replay
        ShowNetDrop(string.IsNullOrEmpty(reason) ? "与房间的连接已断开。" : reason,
                    IsLobbyGame() ? LobbyMenuScenePath : MainMenuScenePath);
    }

    // The lobby ROOM closed under a running match (its host player left / quit). The lobby CONNECTION
    // survives, so this is not a disconnect: the match is over for us either way, but the player lands
    // on the room browser with the connection intact instead of back at the main menu. Without this the
    // match would simply hang — the server's Bye no longer means "connection lost".
    private void OnNetRoomClosed(string reason)
    {
        if (_leavingNetMatch) return;
        _recording = null;
        ShowNetDrop(string.IsNullOrEmpty(reason) ? "房间已关闭。" : reason, LobbyMenuScenePath);
    }

    private bool IsLobbyGame() =>
        NetSession.Instance != null && NetSession.Instance.Mode == ReplayData.ModeLobby;

    private void ReturnToSeatScreen()
    {
        if (_leavingNetMatch) return;
        _leavingNetMatch = true;
        var net = NetSession.Instance;
        // Only the host may change the room's state: ending the match clears both seats and kicks
        // whoever dropped mid-round (PROTOCOL.md § Match lifecycle). A client instead REPORTS the
        // result, which is the only way the match ends at all when the host is merely relaying between
        // two client fighters and has no simulation of its own.
        if (net == null) { }
        else if (net.IsHost) net.RequestEndMatch(_pendingNetWinner);
        else if (_plan != null && _plan.Role == MatchRole.Fighter) net.ReportMatchResult(_pendingNetWinner);
        GameSession.NetPlan = null;
        GetTree().ChangeSceneToFile(NetSeatScenePath);
    }

    private void BuildNetStatusLabel()
    {
        var layer = new CanvasLayer { Layer = 4 };   // above the victory text; see the WinAnim note
        AddChild(layer);
        _netStatus = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        layer.AddChild(_netStatus);
        // Anchors before offsets, and only AFTER AddChild — a preset applied to a parentless Control
        // computes against a rect that does not exist yet (the CharSelect bug).
        _netStatus.AnchorLeft = 0f; _netStatus.AnchorRight = 1f;
        _netStatus.AnchorTop = 0f; _netStatus.AnchorBottom = 0f;
        _netStatus.OffsetLeft = 0; _netStatus.OffsetRight = 0;
        _netStatus.OffsetTop = 92; _netStatus.OffsetBottom = 124;
        _netStatus.AddThemeFontSizeOverride("font_size", 17);
        _netStatus.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.5f));
        _netStatus.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _netStatus.AddThemeConstantOverride("outline_size", 5);
    }

    private void SetNetStatus(string text)
    {
        if (_netStatus == null) return;
        _netStatus.Text = text ?? "";
        _netStatus.Visible = !string.IsNullOrEmpty(text);
    }

    private void BuildNetDropPopup()
    {
        _netDropPopup = new AcceptDialog { Title = "对局中断", OkButtonText = "确定", Exclusive = true };
        AddChild(_netDropPopup);
        _netDropPopup.Confirmed += LeaveToDropTarget;
        _netDropPopup.Canceled += LeaveToDropTarget;
    }

    private void ShowNetDrop(string text, string target = null)
    {
        _netDropTarget = target;
        if (_netDropPopup == null || _netDropPopup.Visible) return;
        _netDropPopup.DialogText = text;
        _netDropPopup.PopupCentered();
    }

    // A closed lobby room keeps the connection (the room browser re-lists on it); anything else tears
    // the session down on the way to the main menu.
    private void LeaveToDropTarget()
    {
        if (string.IsNullOrEmpty(_netDropTarget) || _netDropTarget == MainMenuScenePath)
        {
            LeaveToMainMenu();
            return;
        }
        if (_leavingNetMatch) return;
        _leavingNetMatch = true;
        GameSession.Clear();
        GetTree().ChangeSceneToFile(_netDropTarget);
    }

    private void LeaveToMainMenu()
    {
        if (_leavingNetMatch) return;
        _leavingNetMatch = true;
        NetSession.Instance?.Leave(null);
        GameSession.Clear();
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    public override void _ExitTree()
    {
        var net = NetSession.Instance;
        if (net != null)
        {
            net.MatchEnded -= OnNetMatchEnded;
            net.Disconnected -= OnNetDisconnected;
            net.LobbyRoomClosed -= OnNetRoomClosed;
            net.PlayerJoined -= OnHostPlayerJoined;
        }
        _netMatch?.Dispose();
        _netMatch = null;
    }

    // AI agent overrides device input when present; else poll the device / InputMap.
    private InputFrame FrameFor(Player p, int index)
        => p.Agent != null ? p.Agent.Decide(_sim, index) : p.BuildInputFrame();

    private static void BindDebugAgent(Player p, string spec, int seed)
    {
        if (p == null || string.IsNullOrEmpty(spec)) return;
        if (spec.ToLower() == "statemachine") { p.Agent = new StateMachineAgent(seed); GD.Print($"[dbg] P{seed + 1} = StateMachine"); return; }
        try { p.Agent = new OnnxAgent(ProjectSettings.GlobalizePath(spec)); GD.Print($"[dbg] P{seed + 1} = ONNX {spec}"); }
        catch (System.Exception e) { GD.PushError($"[dbg] P{seed + 1} ONNX load failed: {e.Message}"); }
    }

    private SimProjectile FindProjectile(int id)
    {
        var list = _sim.Projectiles;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return list[i];
        return null;
    }

    private void SpawnProjectileView(int id)
    {
        var pr = FindProjectile(id);
        if (pr == null) return;
        var owner = pr.OwnerIndex == 0 ? p1 : p2;
        // data-driven prefabs win; a legacy character falls back to its scene export
        var scene = HeroLibrary.Instance?.FireballScene(pr.PrefabId) ?? owner.ProjectileScene;
        if (scene == null) return; // no visual; the logic projectile still runs
        var node = scene.Instantiate<Projectile>();
        node.Position = pr.Position.ToGodot();
        AddChild(node);
        node.Init(pr.Dir);
        _projViews[id] = node;
    }

    // Reconcile the view nodes against the live sim projectiles: create one for any id that has none,
    // move the ones that do, free the ones whose projectile ended.
    //
    // Creating from the ID SET rather than from the spawn event is what makes this rollback-safe.
    // Projectile ids come out of the savestate, so a rewind re-creates the same projectile with the
    // same id; reacting to StepResult.SpawnedProjectileIds would then add a second node for it every
    // time that frame was re-simulated, and nothing would ever free the extra.
    private void SyncProjectileViews()
    {
        _liveIds.Clear();
        var list = _sim.Projectiles;
        for (int i = 0; i < list.Count; i++)
        {
            var pr = list[i];
            _liveIds.Add(pr.Id);
            if (!_projViews.TryGetValue(pr.Id, out var node) || !IsInstanceValid(node))
            {
                SpawnProjectileView(pr.Id);
                _projViews.TryGetValue(pr.Id, out node);
            }
            if (node != null && IsInstanceValid(node))
                node.Position = pr.Position.ToGodot();
        }

        _toRemove.Clear();
        foreach (var kv in _projViews)
            if (!_liveIds.Contains(kv.Key)) _toRemove.Add(kv.Key);
        foreach (int id in _toRemove)
        {
            if (_projViews.TryGetValue(id, out var node) && IsInstanceValid(node)) node.QueueFree();
            _projViews.Remove(id);
        }
    }

    private void FreeProjectileViews()
    {
        foreach (var kv in _projViews)
            if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _projViews.Clear();
    }

    private void ShowCommandPopup(int playerIndex, string text)
    {
        if (string.IsNullOrEmpty(text) || CmdPopupScene == null) return;
        var hud = GetNodeOrNull<CanvasLayer>("HUD");
        if (hud == null) return;

        var popup = CmdPopupScene.Instantiate<Control>();
        popup.GetNode<Label>("Label").Text = text + " 成功";
        popup.Position = new Vector2(playerIndex == 0 ? 142 : 658, 560);
        hud.AddChild(popup);

        var t = CreateTween();
        t.TweenInterval(0.9);
        t.TweenProperty(popup, "modulate:a", 0f, 0.4f).SetTrans(Tween.TransitionType.Linear);
        var pref = popup;
        t.TweenCallback(Callable.From(() => pref.QueueFree()));
    }

    private void StartBgm()
    {
        if (Bgm == null) return;
        Bgm.Finished += () => Bgm.Play(); // loop regardless of the stream's import loop setting
        if (!Bgm.Playing) Bgm.Play();
    }

    // Spawns the hit/guard spark + plays the matching SFX at the contact point. Shared by melee
    // and projectiles (via sim HitFeedback events) so a fireball impact reads like a normal strike.
    public void PlayHitFeedback(HitResult res, Rect2 hitBox, Player defender)
    {
        if (res == HitResult.None) return;

        var pt = HitContactPoint(hitBox, defender);
        // FX oriented by the DEFENDER's facing: art authored facing-left; mirror when facing right.
        bool flip = defender.Sim.FacingRight;
        if (res == HitResult.Grabbed)
        {
            // grab connected: just the contact thud. No impact spark and no damage number yet —
            // the throw's damage arrives as a normal Hit feedback at its release frame.
            PlaySfx(SfxHit);
            return;
        }
        if (res == HitResult.Hit)
        {
            SpawnFx(HitFxScene, pt, flip);
            PlaySfx(SfxHit);
        }
        else // Blocked
        {
            SpawnFx(GuardFxScene, pt, flip);
            PlaySfx(SfxGuard);
        }
    }

    private void PlaySfx(AudioStreamPlayer p)
    {
        if (p == null) return;
        // Belt and braces. PresentFrame already withholds feedback for re-simulated frames, but a sound
        // is the one thing a player NOTICES firing twice, and a future caller that forgets the flag
        // would be hard to hear as a bug rather than as sloppy audio.
        if (_inRollback) return;
        float v = SfxPitchVariation;
        p.PitchScale = v > 0f ? Mathf.Max(0.01f, 1f + (GD.Randf() * 2f - 1f) * v) : 1f;
        p.Play();
    }

    // center of the hitbox ∩ hurtbox overlap; falls back to the midpoint of the two box centers.
    private static Vector2 HitContactPoint(Rect2 hitBox, Player defender)
    {
        var hurt = defender.Sim.GetWorldHurtbox().ToGodot();
        var inter = hitBox.Intersection(hurt);
        if (inter.Size.X > 0f && inter.Size.Y > 0f)
            return inter.Position + inter.Size * 0.5f;
        return (hitBox.GetCenter() + hurt.GetCenter()) * 0.5f;
    }

    // flip = mirror on X (directional FX faces the correct way for the defender)
    private void SpawnFx(PackedScene scene, Vector2 worldPos, bool flip)
    {
        if (scene == null || _inRollback) return;   // see PlaySfx
        var fx = scene.Instantiate<Node2D>();
        fx.GlobalPosition = worldPos;
        fx.Scale = new Vector2(flip ? -1f : 1f, 1f);
        AddChild(fx);

        foreach (var n in fx.FindChildren("*", "GPUParticles2D", true, false))
            if (n is GpuParticles2D ps) ps.Restart();

        var timer = GetTree().CreateTimer(HitFxLifetime);
        var fxRef = fx;
        timer.Timeout += () => { if (IsInstanceValid(fxRef)) fxRef.QueueFree(); };
    }

    private void UpdateHpBars()
    {
        if (p1?.Sim == null || p2?.Sim == null) return;
        if (hp1Fill != null)
        {
            var s = hp1Fill.Size;
            s.X = HpBarFullWidth * Mathf.Clamp(p1.Sim.Hp / (float)p1.Sim.MaxHp, 0, 1);
            hp1Fill.Size = s;
        }
        if (hp2Fill != null)
        {
            float w = HpBarFullWidth * Mathf.Clamp(p2.Sim.Hp / (float)p2.Sim.MaxHp, 0, 1);
            var s = hp2Fill.Size; s.X = w; hp2Fill.Size = s;
            var pos = hp2Fill.Position; pos.X = HpBarFullWidth - w; hp2Fill.Position = pos;
        }
    }

    // ---- the lobby HUD info line ----
    // `名字  分数  ping` above each bar, refreshed every frame (the values behind it move at
    // heartbeat pace; the guard on text churn is not worth it at two labels). A spectator sees
    // both seats with "--ms": the server only pushes PingStats to machines holding a seat.
    private void UpdateHudInfo()
    {
        var net = NetSession.Instance;
        bool show = _netMatch != null && _plan != null && net != null && net.IsLobby
                    && net.Room != null;
        if (!show)
        {
            if (P1HudInfo != null) P1HudInfo.Visible = false;
            if (P2HudInfo != null) P2HudInfo.Visible = false;
            return;
        }
        int mySeat = _plan.LocalSeat[0] ? 0 : _plan.LocalSeat[1] ? 1 : -1;
        SetHudInfo(P1HudInfo, net, 0, mySeat);
        SetHudInfo(P2HudInfo, net, 1, mySeat);
    }

    private static void SetHudInfo(Label label, NetSession net, int seat, int mySeat)
    {
        if (label == null) return;
        var room = net.Room;
        var s = seat >= 0 && seat < room.Seats.Length ? room.Seats[seat] : null;
        if (s == null || !s.Occupied) { label.Visible = false; return; }

        string name;
        bool hasScore = false, hasPing = false;
        int score = 0, ping = 0;
        if (s.IsAi)
        {
            name = string.IsNullOrEmpty(s.AiModel) ? "AI" : s.AiModel.GetFile();
        }
        else
        {
            PlayerInfo p = null;
            foreach (var cand in room.Players)
                if (cand.PlayerId == s.OccupantPlayerId) { p = cand; break; }
            name = p?.Name ?? "?";
            hasScore = p is { Score: > 0 };
            score = p?.Score ?? 0;
            hasPing = true;
            ping = seat == mySeat ? net.SelfPingMs : net.OpponentPingMs;
        }

        var text = name;
        if (hasScore) text += $"  {score}分";
        if (hasPing) text += ping > 0 ? $"  {ping}ms" : "  --ms";
        label.Text = text;
        label.Visible = true;
    }

    // winnerIndex: 0 = P1 won, 1 = P2 won (matches StepResult.MatchOverWinner).
    private void BeginWin(int winnerIndex)
    {
        _phase = Phase.Win;
        bool p1Won = winnerIndex == 0;

        var winner = p1Won ? p1 : p2;
        var frames = CharacterDb.Get(winner.Character).WinFrames;
        bool playing = false;
        // HasAnimation guard: the squirrel splash is a placeholder copy today, and whatever art
        // replaces it must still contain the WinAnimName clip. Without this a mismatched resource
        // would leave the match stuck in Phase.Win with no animation to finish it.
        if (WinAnim != null && frames != null && frames.HasAnimation(WinAnimName))
        {
            WinAnim.SpriteFrames = frames;
            WinAnim.Visible = true;
            WinAnim.SetFrameAndProgress(0, 0f);
            WinAnim.Play(WinAnimName);
            playing = true;
        }
        else if (WinAnim != null)
        {
            GD.PushWarning($"[GameManager] no '{WinAnimName}' clip in the win splash for "
                           + $"{winner.Character}; skipping the victory animation.");
        }

        if (WinTextLine1 != null) WinTextLine1.Text = CharacterDb.Get(winner.Character).WinName;
        PlayWinTextFlyIn(WinTextLine1, Line1Destination(), fromLeft: true, ref _line1Tween);
        PlayWinTextFlyIn(WinTextLine2, _l2Home, fromLeft: false, ref _line2Tween);

        // A missing splash node / missing art counts as "already finished", so the text still gets
        // its full run and the match cannot hang in Phase.Win forever.
        _winAnimDone = !playing;
        _winTextDone = false;
        var textTimer = GetTree().CreateTimer(WinTextFlyInSec + WinTextDwellSec + WinTextFadeOutSec);
        textTimer.Timeout += () => { _winTextDone = true; TryFinishWin(); };
        TryFinishWin();
    }

    // Where line 1 should come to rest, for the name it is CURRENTLY showing.
    //
    // At 128 px every fighting name is WIDER than the authored 395 px box, so the Label relies on
    // grow_horizontal = Both: the box expands around its own centre and the text is drawn from the
    // expanded left edge, which is what makes names of different lengths look centred. The fly-in
    // then pins GlobalPosition, so a destination cached at _Ready — when the label still held the
    // "NAME" placeholder, narrow enough not to grow — pinned every name to the placeholder's left
    // edge instead. KANGIEFOO then ran off the right of the screen.
    //
    // So reproduce Godot's grow-Both arithmetic here against the label's authored box (anchors are 0,
    // so Offset* ARE absolute coordinates) and the width of the text actually set. No magic numbers,
    // and it holds for any future name — an over-long one overflows symmetrically rather than to one
    // side, which is the best available outcome.
    private Vector2 Line1Destination()
    {
        if (WinTextLine1 == null) return _l1Home;
        float boxW = _l1BoxR - _l1BoxL;
        float w = Mathf.Max(boxW, WinTextLine1.GetCombinedMinimumSize().X);
        return new Vector2((_l1BoxL + _l1BoxR) * 0.5f - w * 0.5f, _l1Home.Y);
    }

    private void PlayWinTextFlyIn(Label l, Vector2 home, bool fromLeft, ref Tween slot)
    {
        if (l == null) return;
        if (slot != null && slot.IsValid()) slot.Kill();

        float screenW = GetViewport().GetVisibleRect().Size.X;
        float startX = fromLeft ? -l.Size.X - 50f : screenW + 50f;

        l.Visible = true;
        l.Modulate = new Color(1, 1, 1, 1);
        l.GlobalPosition = new Vector2(startX, home.Y);

        var t = CreateTween();
        t.TweenProperty(l, "global_position", home, WinTextFlyInSec)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        t.TweenInterval(WinTextDwellSec);
        t.TweenProperty(l, "modulate:a", 0f, WinTextFadeOutSec)
            .SetTrans(Tween.TransitionType.Linear);
        var labelRef = l;
        t.TweenCallback(Callable.From(() => { labelRef.Visible = false; }));
        slot = t;
    }

    private void OnWinAnimFinished()
    {
        _winAnimDone = true;
        TryFinishWin();
    }

    // Start the next round only once the splash AND the victory text have both finished.
    private void TryFinishWin()
    {
        if (_phase != Phase.Win) return;
        if (!_winAnimDone || !_winTextDone) return;
        // A networked "match" is ONE knockout: everyone goes back to seat select with the seats cleared
        // and picks again (spec: 每局游戏结束后都返回到占座选人界面). Local play resets in place instead,
        // so the two devices keep their bindings.
        if (_netMatch != null) { ReturnToSeatScreen(); return; }
        ResetMatch();
    }

    private void ResetMatch()
    {
        _phase = Phase.Resetting;
        if (_line1Tween != null && _line1Tween.IsValid()) _line1Tween.Kill();
        if (_line2Tween != null && _line2Tween.IsValid()) _line2Tween.Kill();
        RestoreLabel(WinTextLine1, Line1Destination());
        RestoreLabel(WinTextLine2, _l2Home);
        if (WinAnim != null) { WinAnim.Visible = false; WinAnim.Stop(); }

        FreeProjectileViews();
        _sim.Reset();
        p1.Agent?.Reset();   // clear per-round agent state (AI edge-detection/timers)
        p2.Agent?.Reset();
        p1.SyncFromSim();
        p2.SyncFromSim();
        p1.TickAnimation();
        p2.TickAnimation();

        UpdateHpBars();
        StartRecording();   // the next knockout is a new replay file
        _phase = Phase.Fighting;
    }

    private static void RestoreLabel(Label l, Vector2 home)
    {
        if (l == null) return;
        l.Visible = false;
        l.Modulate = new Color(1, 1, 1, 1);
        l.GlobalPosition = home;
    }
}
