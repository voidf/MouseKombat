using Godot;
using System.Collections.Generic;

// Settings panel. A CanvasLayer, not a plain Control, so it draws over whatever spawned it without
// caring about that scene's node order — the point of this being a popup rather than its own scene
// is that the pause menu inside a match can reuse it later.
//
// Built in code for the same reason CharSelect is: several screens instantiate it and there is no
// shared .tscn to keep in sync.
//
// Gamepad: the d-pad / left stick moves between the options (BGM, SFX, replay cap, the ✕ button),
// left/right adjusts the highlighted one (volume in 10% steps, replay cap in 1), A presses the
// focused control and B closes. Like every menu, only the focused window's pads are read; the
// built-in ui_* actions were already stripped of joypad by MenuPad.
public partial class SettingsPopup : CanvasLayer
{
    [Signal] public delegate void ClosedEventHandler();

    [Export] public int PopupLayer = 100;

    private const double VolumeStep = 0.1;
    private const int ReplayStep = 1;

    private HSlider _bgm, _sfx;
    private Label _bgmValue, _sfxValue;
    private SpinBox _replayMax;
    private Button _close;

    private enum Option { Bgm, Sfx, Replay, Close }
    private Option _option = Option.Bgm;

    private readonly List<GamepadSource> _pads = new();
    private int _vDir, _vHold;    // up/down hold state for the repeat timer
    private int _hDir, _hHold;    // left/right hold state

    [Export] public int NavRepeatFirstFrames = 18;
    [Export] public int NavRepeatFrames = 6;

    public override void _Ready()
    {
        Layer = PopupLayer;
        BuildUi();
        Hide();

        foreach (int dev in Input.GetConnectedJoypads()) _pads.Add(new GamepadSource(dev));
        Input.JoyConnectionChanged += OnJoyConnectionChanged;
    }

    public override void _ExitTree() => Input.JoyConnectionChanged -= OnJoyConnectionChanged;

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        string id = "pad" + device;
        if (connected)
        {
            if (_pads.Find(p => p.Id == id) == null) _pads.Add(new GamepadSource((int)device));
            return;
        }
        _pads.RemoveAll(p => p.Id == id);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Visible || _pads.Count == 0) return;
        if (!GetWindow().HasFocus()) return;   // only the focused window's pads steer this popup

        foreach (var p in _pads) p.Poll();

        int v = (_pads.Exists(p => p.Down) ? 1 : 0) - (_pads.Exists(p => p.Up) ? 1 : 0);
        int h = (_pads.Exists(p => p.Right) ? 1 : 0) - (_pads.Exists(p => p.Left) ? 1 : 0);
        if (StepAxis(ref _vDir, ref _vHold, v)) MoveOption(v);
        if (StepAxis(ref _hDir, ref _hHold, h)) Adjust(h);

        if (_pads.Exists(p => p.ConfirmJustPressed)) MenuPad.PressFocused(GetViewport());
        if (_pads.Exists(p => p.CancelJustPressed)) { Close(); GetViewport().SetInputAsHandled(); }
    }

    // Fires on the press edge, then auto-repeats while held — same pacing as CharSelect/MenuPad.
    private bool StepAxis(ref int dir, ref int hold, int now)
    {
        if (now == 0) { dir = 0; hold = 0; return false; }
        if (now != dir) { dir = now; hold = 0; return true; }
        hold++;
        int threshold = hold <= NavRepeatFirstFrames ? NavRepeatFirstFrames : NavRepeatFrames;
        if (hold >= threshold) { hold = 0; return true; }
        return false;
    }

    private void MoveOption(int d)
    {
        _option = (Option)Mathf.PosMod((int)_option + d, 4);
        switch (_option)
        {
            case Option.Bgm: _bgm.GrabFocus(); break;
            case Option.Sfx: _sfx.GrabFocus(); break;
            case Option.Replay: _replayMax.GrabFocus(); break;
            case Option.Close: _close.GrabFocus(); break;
        }
    }

    private void Adjust(int d)
    {
        switch (_option)
        {
            case Option.Bgm:
                _bgm.Value = Mathf.Clamp(_bgm.Value + VolumeStep * d, 0.0, 1.0);
                break;
            case Option.Sfx:
                _sfx.Value = Mathf.Clamp(_sfx.Value + VolumeStep * d, 0.0, 1.0);
                break;
            case Option.Replay:
                _replayMax.Value = Mathf.Clamp(_replayMax.Value + ReplayStep * d, 1, 999);
                break;
            case Option.Close: break;
        }
    }

    public void Open()
    {
        var s = AppSettings.Instance;
        if (s != null)
        {
            // Set the controls WITHOUT writing back: assigning Value fires value_changed, which would
            // save the settings we just loaded (harmless, but it also fights a partially-typed value).
            _bgm.SetValueNoSignal(s.BgmVolume);
            _sfx.SetValueNoSignal(s.SfxVolume);
            _replayMax.SetValueNoSignal(s.ReplayMax);
        }
        RefreshValueLabels();
        _option = Option.Bgm;
        Show();
        _bgm.GrabFocus();   // after Show: focusing a hidden control would not stick
    }

    public void Close()
    {
        Hide();
        EmitSignal(SignalName.Closed);
    }

    public bool IsOpen => Visible;

    // Esc closes. _Input rather than _UnhandledInput so the screen underneath does not also act on
    // the same Esc (which would, for instance, take the main menu somewhere while closing this).
    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new Panel();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.12f, 0.16f, 0.98f),
            BorderColor = new Color(0.35f, 0.38f, 0.48f),
        };
        style.SetCornerRadiusAll(8);
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);
        // centred by anchors + symmetric offsets; no dependence on the viewport size
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -230; panel.OffsetRight = 230;
        panel.OffsetTop = -150; panel.OffsetBottom = 150;

        var pad = new MarginContainer();
        panel.AddChild(pad);
        pad.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 18);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        var title = new Label { Text = "设置", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 26);
        header.AddChild(title);

        // red X, top-right of the panel
        _close = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(34, 34),
            TooltipText = "关闭 (Esc)",
        };
        _close.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.42f));
        _close.AddThemeColorOverride("font_hover_color", new Color(1f, 0.7f, 0.68f));
        _close.AddThemeFontSizeOverride("font_size", 20);
        _close.Pressed += Close;
        header.AddChild(_close);

        _bgm = AddSlider(col, "BGM 音量", out _bgmValue, v =>
        {
            if (AppSettings.Instance != null) AppSettings.Instance.BgmVolume = (float)v;
            RefreshValueLabels();
        });
        _sfx = AddSlider(col, "音效 音量", out _sfxValue, v =>
        {
            if (AppSettings.Instance != null) AppSettings.Instance.SfxVolume = (float)v;
            RefreshValueLabels();
        });

        col.AddChild(new HSeparator());

        var replayRow = new HBoxContainer();
        replayRow.AddThemeConstantOverride("separation", 10);
        col.AddChild(replayRow);
        var replayLabel = new Label
        {
            Text = "回放保存上限",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        replayRow.AddChild(replayLabel);
        _replayMax = new SpinBox
        {
            MinValue = 1, MaxValue = 999, Step = 1,
            Value = AppSettings.DefaultReplayMax,
            CustomMinimumSize = new Vector2(90, 0),
        };
        _replayMax.ValueChanged += v =>
        {
            if (AppSettings.Instance != null) AppSettings.Instance.ReplayMax = (int)v;
        };
        replayRow.AddChild(_replayMax);

        var note = new Label
        {
            Text = "本地 / 局域网 / 大厅 三种模式各自独立计数：填 50 表示每种最多 50 个回放。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 13);
        note.AddThemeColorOverride("font_color", new Color(0.68f, 0.7f, 0.78f));
        col.AddChild(note);
    }

    private static HSlider AddSlider(VBoxContainer parent, string label, out Label valueLabel,
                                     System.Action<double> onChanged)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        parent.AddChild(row);

        var head = new HBoxContainer();
        row.AddChild(head);
        head.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        valueLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.45f));
        head.AddChild(valueLabel);

        var slider = new HSlider
        {
            MinValue = 0.0, MaxValue = 1.0, Step = 0.01,
            Value = 1.0,
            CustomMinimumSize = new Vector2(0, 20),
        };
        slider.ValueChanged += v => onChanged(v);
        row.AddChild(slider);
        return slider;
    }

    private void RefreshValueLabels()
    {
        if (_bgmValue != null) _bgmValue.Text = $"{Mathf.RoundToInt(_bgm.Value * 100)}%";
        if (_sfxValue != null) _sfxValue.Text = $"{Mathf.RoundToInt(_sfx.Value * 100)}%";
    }
}
