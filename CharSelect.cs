using Godot;
using System;
using MouseKombat.Sim;

// Character-select panel: a grid of portraits, one highlighted, navigated by ONE device.
//
// Deliberately driven by an injected IInputSource rather than by _Input, for two reasons:
//   * the same panel then serves keyboard seat 1 (WASD), keyboard seat 2 (arrows), a gamepad
//     (d-pad or left stick) and an AI seat (KeyboardSource.MenuSeat) with no per-device branching;
//   * while a panel is open the owner ignores every OTHER source, which is what "lock out the other
//     devices until this player is done" means — the lock lives in the owner's poll loop, and this
//     panel simply never sees anyone else's input.
//
// Laid out entirely with containers (VBox / Center / Grid / Margin) rather than hand-set positions:
// an earlier hand-positioned version had the portraits drift out of their cells.
//
// Built in code rather than as a .tscn so the pre-fight lobby (ReadyScreen) and the networked
// lobby (期3) can both instantiate it without a shared scene to keep in sync.
public partial class CharSelect : Control
{
    // seat (0 = P1, 1 = P2), chosen character
    public event Action<int, CharacterId> Confirmed;
    // seat — the player backed out; the caller should release the seat
    public event Action<int> Cancelled;

    public bool IsOpen { get; private set; }
    public int Seat { get; private set; }
    public CharacterId Selected => CharacterDb.All[_index].Id;

    [Export] public int Columns = 3;
    [Export] public Vector2 CellSize = new Vector2(150, 176);
    [Export] public int NavRepeatFirstFrames = 18;  // frames held before the cursor auto-repeats
    [Export] public int NavRepeatFrames = 6;        // frames between repeats after that

    private IInputSource _nav;
    private bool _aiMode;
    private int _index;

    // The panel refuses confirm/cancel until the driving device has released BOTH. The key that
    // opens a panel is frequently the same key that would close it — the AI seat's ` is both
    // "hand this seat to an AI" and "back out" — and that press is still physically down on the
    // frame the panel appears. Without this the panel opened and closed in the same tick, which
    // showed up as "pressing ` does nothing, or flashes the panel for an instant".
    private bool _armed;

    private Label _title;
    private Label _hint;
    private readonly System.Collections.Generic.List<Panel> _cells = new();
    private readonly System.Collections.Generic.List<TextureRect> _portraits = new();
    private readonly System.Collections.Generic.List<Label> _names = new();

    private int _hDir, _vDir;       // direction held last frame, for edge detection
    private int _hHold, _vHold;     // frames the current direction has been held

    // OPAQUE, not a dim: this panel must fully hide the lobby behind it, otherwise P1's already
    // chosen portrait shows through while P2 is picking.
    private static readonly Color Backdrop = new Color(0.05f, 0.055f, 0.07f);
    private static readonly Color CellFree = new Color(0.13f, 0.14f, 0.18f);
    private static readonly Color CellPicked = new Color(0.22f, 0.2f, 0.12f);
    private static readonly Color Accent = new Color(0.95f, 0.78f, 0.25f);

    public override void _Ready()
    {
        // Full-rect BEFORE building, and via SetAnchorsAndOffsetsPreset rather than
        // SetAnchorsPreset: this node is created in code, so it starts with a zero rect, and only
        // the "AndOffsets" variant writes the offsets that actually give it the parent's size.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        Visible = false;
    }

    // nav: the device that owns this panel. initial: cursor starts on this character.
    // aiMode changes only the wording — the caller decides what confirming means.
    public void Open(int seat, IInputSource nav, CharacterId initial, bool aiMode)
    {
        Seat = seat;
        _nav = nav;
        _aiMode = aiMode;
        _index = CharacterDb.IndexOf(initial);
        _hDir = _vDir = 0;
        _hHold = _vHold = 0;
        _armed = false;
        IsOpen = true;
        Visible = true;
        RefreshTexts();
        RefreshHighlight();
    }

    public void Close()
    {
        IsOpen = false;
        Visible = false;
        _nav = null;
    }

    // Called once per physics tick by the owner, AFTER it has polled the input sources.
    public void Tick()
    {
        if (!IsOpen || _nav == null) return;

        if (!_armed)
        {
            if (_nav.ConfirmHeld || _nav.CancelHeld) return;
            _armed = true;
        }

        int h = (_nav.Right ? 1 : 0) - (_nav.Left ? 1 : 0);
        int v = (_nav.Down ? 1 : 0) - (_nav.Up ? 1 : 0);
        if (StepAxis(ref _hDir, ref _hHold, h)) MoveCursor(h, 0);
        if (StepAxis(ref _vDir, ref _vHold, v)) MoveCursor(0, v);

        if (_nav.ConfirmJustPressed)
        {
            Confirmed?.Invoke(Seat, Selected);
            return;
        }
        if (_nav.CancelJustPressed) Cancelled?.Invoke(Seat);
    }

    // Fires on the press edge, then auto-repeats while held. Returns true when the cursor should
    // move this frame.
    private bool StepAxis(ref int dir, ref int hold, int now)
    {
        if (now == 0) { dir = 0; hold = 0; return false; }
        if (now != dir) { dir = now; hold = 0; return true; }
        hold++;
        int threshold = hold <= NavRepeatFirstFrames ? NavRepeatFirstFrames : NavRepeatFrames;
        if (hold >= threshold) { hold = 0; return true; }
        return false;
    }

    private void MoveCursor(int dx, int dy)
    {
        int n = CharacterDb.All.Length;
        int cols = Mathf.Max(1, Columns);
        if (dx != 0)
        {
            // wraps within the whole roster, so a 3-wide grid with 3 characters cycles naturally
            _index = Mathf.PosMod(_index + dx, n);
        }
        if (dy != 0)
        {
            int next = _index + dy * cols;
            if (next >= 0 && next < n) _index = next;   // no vertical wrap: rows are ragged
        }
        RefreshHighlight();
    }

    private void BuildUi()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Layout rule used throughout: create -> AddChild -> SetAnchorsAndOffsetsPreset. Applying a
        // preset BEFORE the node is in the tree computes it against a nonexistent parent rect, which
        // is what left the whole panel collapsed in the top-left corner.
        var backdrop = new ColorRect { Color = Backdrop, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // MarginContainer supplies the screen padding, so no hand-written offsets are involved.
        var frame = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(frame);
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeConstantOverride("margin_left", 24);
        frame.AddThemeConstantOverride("margin_right", 24);
        frame.AddThemeConstantOverride("margin_top", 40);
        frame.AddThemeConstantOverride("margin_bottom", 28);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 20);
        frame.AddChild(column);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 30);
        column.AddChild(_title);

        // CenterContainer keeps the grid centred whatever the roster size, with no magic offsets
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(center);

        var grid = new GridContainer { Columns = Mathf.Max(1, Columns) };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 18);
        center.AddChild(grid);

        foreach (var entry in CharacterDb.All)
        {
            var cell = new Panel { CustomMinimumSize = CellSize };
            var style = new StyleBoxFlat { BgColor = CellFree, BorderColor = Accent };
            style.SetCornerRadiusAll(6);
            style.SetBorderWidthAll(0);
            cell.AddThemeStyleboxOverride("panel", style);
            grid.AddChild(cell);
            _cells.Add(cell);

            // Margin+VBox inside the Panel: the containers own the layout, so the portrait cannot
            // escape its cell no matter what the source texture's size is.
            var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            cell.AddChild(pad);
            pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, 8);

            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 4);
            pad.AddChild(box);

            var portrait = new TextureRect
            {
                Texture = entry.Portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(portrait);
            _portraits.Add(portrait);

            var name = new Label
            {
                Text = entry.DisplayName,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            name.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(name);
            _names.Add(name);
        }

        _hint = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeFontSizeOverride("font_size", 16);
        _hint.AddThemeColorOverride("font_color", new Color(0.82f, 0.84f, 0.9f));
        column.AddChild(_hint);
    }

    private void RefreshTexts()
    {
        _title.Text = $"P{Seat + 1} 选择角色";
        string dirs = _aiMode ? "方向键" : NavLabel(_nav);
        string ok = _aiMode ? "回车" : "确认键";
        string back = _aiMode ? "`" : (_nav?.CancelLabel ?? "取消键");
        string tail = _aiMode ? "确定后选择 AI 模型" : "双方可以选同一个角色";
        _hint.Text = $"{dirs} 选择 · {ok} 确定 · {back} 或 Esc 返回（放弃该机位）\n{tail}";
    }

    private static string NavLabel(IInputSource src) => src?.Id switch
    {
        "kbL" => "WASD",
        "kbR" => "方向键",
        null => "方向键",
        _ => src.Id.StartsWith("pad") ? "十字键 / 左摇杆" : "方向键",
    };

    private void RefreshHighlight()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            bool sel = i == _index;
            if (_cells[i].GetThemeStylebox("panel") is StyleBoxFlat sb)
            {
                sb.SetBorderWidthAll(sel ? 4 : 0);
                sb.BgColor = sel ? CellPicked : CellFree;
            }
            _portraits[i].Modulate = sel ? Colors.White : new Color(0.55f, 0.55f, 0.6f);
            _names[i].AddThemeColorOverride("font_color", sel ? Accent : new Color(0.7f, 0.7f, 0.75f));
        }
    }
}
