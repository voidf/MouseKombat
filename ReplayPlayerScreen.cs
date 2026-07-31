using Godot;
using MouseKombat.Sim;

// Replay viewer: a small transport (play / pause / reverse / ±1 frame / scrub) over a ReplaySession.
//
// It renders the match with its own lean director rather than reusing GameManager. GameManager is a
// LIVE match director — it polls devices, drives AI, records replays and runs the win sequence — and
// bolting a second mode onto it would mean guarding every one of those with "unless we are replaying".
// Reusing the parts that matter is cheap instead: CharacterDb.Spawn builds the fighters, and
// Player.SyncFromSim + Player.TickAnimation are exactly the two calls that turn sim state into
// pixels.
//
// Hit FX and SFX are deliberately NOT played. They are one-shot events, and during scrubbing or
// reverse playback a frame gets simulated repeatedly or out of order, so firing them would produce
// bursts of sparks at the wrong moments. This is the same reason a rollback session suppresses them.
//
// Esc goes up one level, back to the list.
public partial class ReplayPlayerScreen : Control
{
    [Export] public string ListScenePath = "res://ReplayList.tscn";
    [Export] public Node2D World;             // fighters are parented here (identity transform)
    [Export] public Node2D P1Slot;            // position + draw-order markers, as in MFEntry
    [Export] public Node2D P2Slot;
    [Export] public ColorRect Hp1Fill;
    [Export] public ColorRect Hp2Fill;
    [Export] public float HpBarFullWidth = 260f;

    [Export] public Label TitleLabel;
    [Export] public Label FrameLabel;
    [Export] public Label WarnLabel;
    [Export] public HSlider Scrub;
    [Export] public Button PlayButton;
    [Export] public Button ReverseButton;
    [Export] public Button StepBackButton;
    [Export] public Button StepForwardButton;

    private ReplaySession _session;
    private Player _p1, _p2;

    private enum Transport { Paused, Forward, Reverse }
    private Transport _mode = Transport.Paused;

    // Set while the code moves the slider, so the resulting value_changed is not mistaken for the
    // user dragging it (which would fight the playhead every frame during playback).
    private bool _syncingScrub;

    public override void _Ready()
    {
        string path = ReplayStore.PendingPath;
        var data = string.IsNullOrEmpty(path) ? null : ReplayStore.Load(path, out _);
        if (data == null)
        {
            Fail(string.IsNullOrEmpty(path) ? "没有选择回放文件" : "回放文件无法读取");
            return;
        }

        // Playback rebuilds the fighters from the CURRENT build's tuning; the file only says which
        // characters played (see ReplayData for why per-match tuning is not persisted).
        _p1 = CharacterDb.Spawn(data.P1Char, World, new Vector2(data.P1StartX, data.P1StartY), 0);
        _p2 = CharacterDb.Spawn(data.P2Char, World, new Vector2(data.P2StartX, data.P2StartY), 1);
        if (_p1 == null || _p2 == null) { Fail("角色场景缺失，无法回放"); return; }
        if (P1Slot != null) World.MoveChild(_p1, P1Slot.GetIndex());
        if (P2Slot != null) World.MoveChild(_p2, P2Slot.GetIndex());

        _session = new ReplaySession(data, _p1.BuildConfig(), _p2.BuildConfig());
        _p1.Bind(_session.Sim.P1);
        _p2.Bind(_session.Sim.P2);

        if (TitleLabel != null)
        {
            string p1 = string.IsNullOrEmpty(data.P1Name) ? "1P" : data.P1Name;
            string p2 = string.IsNullOrEmpty(data.P2Name) ? "2P" : data.P2Name;
            TitleLabel.Text = $"{ReplayStore.ModeLabel(data.Mode)} · "
                              + $"{ReplayStore.FormatBattleTime(data.StartedUnixUtc)} · "
                              + $"{p1} vs {p2}";
        }

        if (Scrub != null)
        {
            Scrub.MinValue = 0;
            Scrub.MaxValue = _session.TotalFrames;
            Scrub.Step = 1;
            Scrub.ValueChanged += OnScrubChanged;
        }
        if (PlayButton != null) PlayButton.Pressed += () => SetMode(_mode == Transport.Forward ? Transport.Paused : Transport.Forward);
        if (ReverseButton != null) ReverseButton.Pressed += () => SetMode(_mode == Transport.Reverse ? Transport.Paused : Transport.Reverse);
        if (StepBackButton != null) StepBackButton.Pressed += () => { SetMode(Transport.Paused); _session.StepBackward(); PushView(); };
        if (StepForwardButton != null) StepForwardButton.Pressed += () => { SetMode(Transport.Paused); _session.StepForward(); PushView(); };

        // Integrity: a replay recorded before a balance change no longer simulates the same way. Say
        // so rather than presenting a match that never happened.
        VerifyAndWarn(data);

        PushView();
        SetMode(Transport.Forward);
    }

    private void VerifyAndWarn(ReplayData data)
    {
        if (WarnLabel == null) return;
        bool ok = _session.Verify(out uint expected, out uint actual);
        _session.Restart();
        if (ok)
        {
            WarnLabel.Visible = false;
            return;
        }
        WarnLabel.Visible = true;
        WarnLabel.Text = $"⚠ 此回放录制于版本 {(string.IsNullOrEmpty(data.GameVersion) ? "未知" : data.GameVersion)}，"
                         + $"与当前版本的战斗数值不一致（校验 {expected:X8} ≠ {actual:X8}），画面可能与当时不同。";
    }

    private void Fail(string message)
    {
        if (TitleLabel != null) TitleLabel.Text = message;
        if (WarnLabel != null) { WarnLabel.Visible = true; WarnLabel.Text = message; }
        SetControlsEnabled(false);
    }

    private void SetControlsEnabled(bool on)
    {
        if (PlayButton != null) PlayButton.Disabled = !on;
        if (ReverseButton != null) ReverseButton.Disabled = !on;
        if (StepBackButton != null) StepBackButton.Disabled = !on;
        if (StepForwardButton != null) StepForwardButton.Disabled = !on;
        if (Scrub != null) Scrub.Editable = on;
    }

    private void SetMode(Transport m)
    {
        _mode = m;
        if (PlayButton != null) PlayButton.Text = _mode == Transport.Forward ? "⏸" : "▶";
        if (ReverseButton != null) ReverseButton.Text = _mode == Transport.Reverse ? "⏸" : "◀◀";
    }

    private void OnScrubChanged(double v)
    {
        if (_syncingScrub || _session == null) return;
        SetMode(Transport.Paused);
        _session.SeekTo((int)v);
        PushView();
    }

    // Playback advances on the PHYSICS tick, not the render frame: one recorded frame is one logic
    // frame, and the animation clock is the physics tick too (see Player.TickAnimation).
    public override void _PhysicsProcess(double delta)
    {
        if (_session == null) return;

        if (_mode == Transport.Forward && !_session.StepForward())
            SetMode(Transport.Paused);                       // reached the end
        else if (_mode == Transport.Reverse && !_session.StepBackward())
            SetMode(Transport.Paused);                       // reached the start

        // Tick the view every frame regardless of whether the playhead moved, so looping idle
        // animations keep breathing while paused — the same reason GameManager ticks them during the
        // win sequence.
        PushView();
    }

    // One path for both stepping and seeking: a seek goes through GameSim.LoadState, which already
    // clears the players' queued AnimEvents, so there is never a stale intent to discard here.
    private void PushView()
    {
        if (_session == null) return;

        _p1.SyncFromSim();
        _p2.SyncFromSim();
        _p1.TickAnimation();
        _p2.TickAnimation();

        UpdateHpBars();

        if (FrameLabel != null)
            FrameLabel.Text = $"{_session.Frame} / {_session.TotalFrames}   "
                              + $"({ReplayStore.FormatDuration(_session.Frame)} / {ReplayStore.FormatDuration(_session.TotalFrames)})";

        if (Scrub != null && !Scrub.HasFocus())
        {
            _syncingScrub = true;
            Scrub.Value = _session.Frame;
            _syncingScrub = false;
        }
    }

    private void UpdateHpBars()
    {
        var s = _session.Sim;
        if (Hp1Fill != null)
        {
            var sz = Hp1Fill.Size;
            sz.X = HpBarFullWidth * Mathf.Clamp(s.P1.Hp / (float)s.P1.MaxHp, 0, 1);
            Hp1Fill.Size = sz;
        }
        if (Hp2Fill != null)
        {
            float w = HpBarFullWidth * Mathf.Clamp(s.P2.Hp / (float)s.P2.MaxHp, 0, 1);
            var sz = Hp2Fill.Size; sz.X = w; Hp2Fill.Size = sz;
            var pos = Hp2Fill.Position; pos.X = HpBarFullWidth - w; Hp2Fill.Position = pos;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;
        switch (k.Keycode)
        {
            case Key.Escape:
                GetViewport().SetInputAsHandled();
                GetTree().ChangeSceneToFile(ListScenePath);
                break;
            case Key.Space:
                GetViewport().SetInputAsHandled();
                SetMode(_mode == Transport.Forward ? Transport.Paused : Transport.Forward);
                break;
            case Key.Left:
                GetViewport().SetInputAsHandled();
                SetMode(Transport.Paused); _session?.StepBackward(); PushView();
                break;
            case Key.Right:
                GetViewport().SetInputAsHandled();
                SetMode(Transport.Paused); _session?.StepForward(); PushView();
                break;
        }
    }

    public void OnBackPressed() => GetTree().ChangeSceneToFile(ListScenePath);
}
