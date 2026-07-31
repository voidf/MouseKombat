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
    [Export] public Vector2 CellSize = new Vector2(150, 150);
    [Export] public int NavRepeatFirstFrames = 18;  // frames held before the cursor auto-repeats
    [Export] public int NavRepeatFrames = 6;        // frames between repeats after that

    private IInputSource _nav;
    private bool _aiMode;
    private int _index;

    private Control _root;
    private Label _title;
    private Label _hint;
    private readonly System.Collections.Generic.List<Panel> _cells = new();
    private readonly System.Collections.Generic.List<TextureRect> _portraits = new();
    private readonly System.Collections.Generic.List<Label> _names = new();

    private int _hDir, _vDir;       // direction held last frame, for edge detection
    private int _hHold, _vHold;     // frames the current direction has been held

    private static readonly Color CellFree = new Color(0.13f, 0.14f, 0.18f, 0.92f);
    private static readonly Color CellSelected = new Color(0.95f, 0.78f, 0.25f, 0.95f);

    public override void _Ready()
    {
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

        int h = (_nav.Right ? 1 : 0) - (_nav.Left ? 1 : 0);
        int v = (_nav.Down ? 1 : 0) - (_nav.Up ? 1 : 0);
        if (StepAxis(ref _hDir, ref _hHold, h)) MoveCursor(h, 0);
        if (StepAxis(ref _vDir, ref _vHold, v)) MoveCursor(0, v);

        // Confirm/cancel are read as edges by IInputSource itself, so a key still held from the
        // seat-claim press cannot immediately confirm a character.
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
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        _root = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_root);

        _title = new Label
        {
            Position = new Vector2(0, 84), Size = new Vector2(800, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _title.AddThemeFontSizeOverride("font_size", 28);
        _root.AddChild(_title);

        int cols = Mathf.Max(1, Columns);
        int rows = (CharacterDb.All.Length + cols - 1) / cols;
        var gridSize = new Vector2(cols * CellSize.X + (cols - 1) * 16,
                                   rows * (CellSize.Y + 32) + (rows - 1) * 16);
        var grid = new GridContainer
        {
            Columns = cols,
            Position = new Vector2((800 - gridSize.X) * 0.5f, 150),
            Size = gridSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 16);
        _root.AddChild(grid);

        for (int i = 0; i < CharacterDb.All.Length; i++)
        {
            var entry = CharacterDb.All[i];

            var cell = new Panel { CustomMinimumSize = new Vector2(CellSize.X, CellSize.Y + 32) };
            var style = new StyleBoxFlat { BgColor = CellFree, BorderColor = CellSelected };
            style.SetCornerRadiusAll(6);
            style.SetBorderWidthAll(0);
            cell.AddThemeStyleboxOverride("panel", style);
            grid.AddChild(cell);
            _cells.Add(cell);

            var portrait = new TextureRect
            {
                Texture = entry.Portrait,
                Position = new Vector2(8, 8),
                Size = new Vector2(CellSize.X - 16, CellSize.Y - 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            cell.AddChild(portrait);
            _portraits.Add(portrait);

            var name = new Label
            {
                Text = entry.DisplayName,
                Position = new Vector2(0, CellSize.Y - 4),
                Size = new Vector2(CellSize.X, 28),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            name.AddThemeFontSizeOverride("font_size", 18);
            cell.AddChild(name);
            _names.Add(name);
        }

        _hint = new Label
        {
            Position = new Vector2(0, 150 + gridSize.Y + 26), Size = new Vector2(800, 60),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeFontSizeOverride("font_size", 16);
        _hint.AddThemeColorOverride("font_color", new Color(0.82f, 0.84f, 0.9f));
        _root.AddChild(_hint);
    }

    private void RefreshTexts()
    {
        _title.Text = $"P{Seat + 1} 选择角色";
        string dirs = _aiMode ? "方向键" : NavLabel(_nav);
        string ok = _aiMode ? "回车" : "确认键";
        string back = _aiMode ? "`" : (_nav?.CancelLabel ?? "取消键");
        string tail = _aiMode ? "确定后选择 AI 模型" : "双方可以选同一个角色";
        _hint.Text = $"{dirs} 选择 · {ok} 确定 · {back} 返回（放弃该机位）\n{tail}";
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
                sb.BgColor = sel ? new Color(0.22f, 0.2f, 0.12f, 0.95f) : CellFree;
            }
            _portraits[i].Modulate = sel ? Colors.White : new Color(0.55f, 0.55f, 0.6f);
            _names[i].AddThemeColorOverride("font_color", sel ? CellSelected : new Color(0.7f, 0.7f, 0.75f));
        }
    }
}
