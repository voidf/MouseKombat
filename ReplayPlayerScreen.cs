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

    // ---- precomputed timeline ----
    // The whole replay is simulated once at load, capturing each frame's ANIMATION state for both
    // fighters. Sim states are captured too, by giving ReplaySession a keyframe spacing of 1, so a
    // seek costs a state load instead of up to `spacing` re-simulated steps.
    //
    // This is not only a speed win. Animation position is view state the sim knows nothing about, and
    // Player.TickAnimation only ever advances it FORWARD — so before this, a paused replay kept
    // animating and a reversed replay animated forwards. Having the exact pose for every frame makes
    // pause freeze on the right frame and reverse actually run the animation backwards.
    private Player.ViewState[] _v1, _v2;

    // Budget for the per-frame savestates. At ~640 bytes a frame this is ~27 minutes at spacing 1;
    // a knockout is seconds to a minute, so spacing 1 is what actually happens. Longer recordings
    // degrade to a wider spacing (slower seeks) rather than to an unbounded allocation.
    private const long StateBudgetBytes = 64L * 1024 * 1024;

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

        int spacing = 1;
        long need = (long)(data.FrameCount + 1) * (SimState.MaxSize / 3);   // states are ~630 B
        if (need > StateBudgetBytes) spacing = (int)(need / StateBudgetBytes) + 1;

        _session = new ReplaySession(data, _p1.BuildConfig(), _p2.BuildConfig(), spacing);
        _p1.Bind(_session.Sim.P1);
        _p2.Bind(_session.Sim.P2);
        Precompute();

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
        if (StepBackButton != null) StepBackButton.Pressed += () => { SetMode(Transport.Paused); ShowFrame(_session.Frame - 1); };
        if (StepForwardButton != null) StepForwardButton.Pressed += () => { SetMode(Transport.Paused); ShowFrame(_session.Frame + 1); };

        // Integrity: a replay recorded before a balance change no longer simulates the same way. Say
        // so rather than presenting a match that never happened.
        VerifyAndWarn(data);

        ShowFrame(0);
        SetMode(Transport.Forward);
    }

    // Run the recording once, front to back, recording each frame's animation state. Frame index i
    // holds the state AFTER i steps, so index 0 is the untouched start and TotalFrames is the end.
    private void Precompute()
    {
        int n = _session.TotalFrames;
        _v1 = new Player.ViewState[n + 1];
        _v2 = new Player.ViewState[n + 1];

        _session.Restart();
        _p1.SyncFromSim(); _p2.SyncFromSim();
        _p1.TickAnimation(); _p2.TickAnimation();
        _v1[0] = _p1.SaveView(); _v2[0] = _p2.SaveView();

        for (int i = 1; i <= n; i++)
        {
            _session.StepForward();
            _p1.SyncFromSim(); _p2.SyncFromSim();
            _p1.TickAnimation(); _p2.TickAnimation();   // forward-only, exactly as live play would
            _v1[i] = _p1.SaveView(); _v2[i] = _p2.SaveView();
        }
    }

    private void VerifyAndWarn(ReplayData data)
    {
        if (WarnLabel == null) return;
        bool ok = _session.Verify(out uint expected, out uint actual);
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
        ShowFrame((int)v);
    }

    // Playback advances on the PHYSICS tick, not the render frame: one recorded frame is one logic
    // frame, and the animation clock is the physics tick too (see Player.TickAnimation).
    public override void _PhysicsProcess(double delta)
    {
        if (_session == null) return;

        // Paused means PAUSED: nothing is re-ticked, so the pose stays exactly the one belonging to
        // this frame. (An earlier version kept ticking the animation to "keep the fighters
        // breathing", which read as the replay still running while stopped.)
        if (_mode == Transport.Forward) ShowFrame(_session.Frame + 1);
        else if (_mode == Transport.Reverse) ShowFrame(_session.Frame - 1);
    }

    // Put the view exactly at `frame`: restore the sim state, restore the precomputed animation
    // state, then render it WITHOUT advancing. Same result whether the frame was reached forwards,
    // backwards or by dragging the scrub bar.
    private void ShowFrame(int frame)
    {
        if (_session == null) return;

        int target = Mathf.Clamp(frame, 0, _session.TotalFrames);
        // A request that had to be clamped means we ran off one end — that is the stop condition for
        // both play directions, and for the ±1 buttons at the boundaries.
        if (target != frame) SetMode(Transport.Paused);

        _session.SeekTo(target);
        if (_v1 != null && target < _v1.Length) { _p1.LoadView(_v1[target]); _p2.LoadView(_v2[target]); }

        _p1.SyncFromSim();
        _p2.SyncFromSim();
        _p1.ApplyAnimation();
        _p2.ApplyAnimation();

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
                SetMode(Transport.Paused); if (_session != null) ShowFrame(_session.Frame - 1);
                break;
            case Key.Right:
                GetViewport().SetInputAsHandled();
                SetMode(Transport.Paused); if (_session != null) ShowFrame(_session.Frame + 1);
                break;
        }
    }

    public void OnBackPressed() => GetTree().ChangeSceneToFile(ListScenePath);
}
