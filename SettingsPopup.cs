using Godot;

// Settings panel. A CanvasLayer, not a plain Control, so it draws over whatever spawned it without
// caring about that scene's node order — the point of this being a popup rather than its own scene
// is that the pause menu inside a match can reuse it later.
//
// Built in code for the same reason CharSelect is: several screens instantiate it and there is no
// shared .tscn to keep in sync.
public partial class SettingsPopup : CanvasLayer
{
    [Signal] public delegate void ClosedEventHandler();

    [Export] public int PopupLayer = 100;

    private HSlider _bgm, _sfx;
    private Label _bgmValue, _sfxValue;
    private SpinBox _replayMax;

    public override void _Ready()
    {
        Layer = PopupLayer;
        BuildUi();
        Hide();
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
        Show();
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
        var close = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(34, 34),
            TooltipText = "关闭 (Esc)",
        };
        close.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.42f));
        close.AddThemeColorOverride("font_hover_color", new Color(1f, 0.7f, 0.68f));
        close.AddThemeFontSizeOverride("font_size", 20);
        close.Pressed += Close;
        header.AddChild(close);

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
