using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// The replay browser: every recording across all three modes, newest first.
//
// Mouse-driven (rows, a trash icon per row, mouse wheel / drag to scroll), matching the spec's split
// between the keyboard-only pre-fight screens and the pointer-driven browsing screens.
//
// Rows are built for every file the folder contains, INCLUDING ones that fail to parse: those show
// their error in place. A corrupt file is a thing that happens, and hiding it would leave the user
// with a file they can see on disk but not delete from the game.
//
// Esc goes up one level, to the main menu.
public partial class ReplayListScreen : Control
{
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";
    [Export] public string PlayerScenePath = "res://ReplayPlayer.tscn";
    [Export] public VBoxContainer Rows;          // inside a ScrollContainer
    [Export] public VBoxContainer HeaderHost;    // the column-title row is built into this
    [Export] public Label EmptyLabel;
    [Export] public Label CountLabel;

    // ONE definition of the columns, used to build both the header and every data row. Width 0 means
    // "take the remaining space". The header used to be a hand-spaced Label while rows were HBox
    // cells, which is why the two could never line up: two layout systems describing one table.
    private static readonly (string Title, int Width)[] Columns =
    {
        ("分类", 70),
        ("战斗时间", 140),
        ("时长", 60),
        ("房间号/主机", 120),
        ("对战双方", 0),
    };

    // Trailing per-row buttons the header has to reserve space for, so the last column ends in the
    // same place in both.
    private const int PlayButtonWidth = 64;
    private const int DeleteButtonWidth = 34;

    private readonly List<ReplayStore.Entry> _entries = new();
    private MenuPad _menuPad;
    private Button _firstPlay;   // first row's play button, the gamepad's default focus

    public override void _Ready()
    {
        _menuPad = new MenuPad();
        _menuPad.Cancelled += () => GetTree().ChangeSceneToFile(MainMenuScenePath);   // B = Esc
        AddChild(_menuPad);
        BuildHeader();
        Refresh();
    }

    // Built with the same container recipe as a data row — same margins, same separation, same widths
    // — so alignment is structural rather than arithmetic.
    private void BuildHeader()
    {
        if (HeaderHost == null) return;
        foreach (var c in HeaderHost.GetChildren()) c.QueueFree();

        var line = RowShell(new Color(0, 0, 0, 0), out Control shell);
        foreach (var (title, width) in Columns)
        {
            var l = Cell(title, width, expand: width == 0);
            l.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.68f));
            l.AddThemeFontSizeOverride("font_size", 13);
            line.AddChild(l);
        }
        line.AddChild(new Control { CustomMinimumSize = new Vector2(PlayButtonWidth, 0) });
        line.AddChild(new Control { CustomMinimumSize = new Vector2(DeleteButtonWidth, 0) });
        HeaderHost.AddChild(shell);
    }

    // The shared row chrome: a PanelContainer with fixed content margins wrapping an HBox with fixed
    // separation. Returns the HBox to fill, and the shell to parent.
    private static HBoxContainer RowShell(Color bg, out Control shell)
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = bg };
        style.SetCornerRadiusAll(4);
        style.ContentMarginLeft = 10; style.ContentMarginRight = 10;
        style.ContentMarginTop = 6; style.ContentMarginBottom = 6;
        panel.AddThemeStyleboxOverride("panel", style);

        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 10);
        panel.AddChild(line);
        shell = panel;
        return line;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            GetTree().ChangeSceneToFile(MainMenuScenePath);
        }
    }

    public void OnOpenFolderPressed() => ReplayStore.OpenFolder();

    public void OnBackPressed() => GetTree().ChangeSceneToFile(MainMenuScenePath);

    public void Refresh()
    {
        _entries.Clear();
        _entries.AddRange(ReplayStore.ListAll());

        foreach (var c in Rows.GetChildren()) c.QueueFree();
        _firstPlay = null;
        foreach (var e in _entries) Rows.AddChild(BuildRow(e));

        if (_menuPad != null)
        {
            // the pad lands on the newest replay's play button; on an empty list, on the back button
            _menuPad.DefaultFocus = _firstPlay ?? GetNodeOrNull<Button>("Footer/Back");
        }

        if (EmptyLabel != null) EmptyLabel.Visible = _entries.Count == 0;
        if (CountLabel != null)
        {
            int max = AppSettings.Instance?.ReplayMax ?? AppSettings.DefaultReplayMax;
            CountLabel.Text = $"共 {_entries.Count} 个回放（每种模式上限 {max}）";
        }
    }

    private Control BuildRow(ReplayStore.Entry e)
    {
        var line = RowShell(new Color(0.12f, 0.13f, 0.17f, 0.92f), out Control row);

        // Column 0 is always the mode; a broken file fills the rest with its error so the row still
        // has a working delete button.
        line.AddChild(Cell(ReplayStore.ModeLabel(e.Mode), Columns[0].Width));

        if (e.Header == null)
        {
            var bad = Cell($"损坏：{e.Error}", 0, expand: true);
            bad.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.45f));
            line.AddChild(bad);
            line.AddChild(new Control { CustomMinimumSize = new Vector2(PlayButtonWidth, 0) });
        }
        else
        {
            line.AddChild(Cell(ReplayStore.FormatBattleTime(e.Header.StartedUnixUtc), Columns[1].Width));
            line.AddChild(Cell(ReplayStore.FormatDuration(e.Header.FrameCount), Columns[2].Width));
            line.AddChild(Cell(ReplayStore.FormatSource(e), Columns[3].Width));

            string p1 = string.IsNullOrEmpty(e.Header.P1Name) ? "1P" : e.Header.P1Name;
            string p2 = string.IsNullOrEmpty(e.Header.P2Name) ? "2P" : e.Header.P2Name;
            line.AddChild(Cell($"{p1} ({CharacterDb.NameOf(e.Header.P1Char)})  vs  "
                               + $"{p2} ({CharacterDb.NameOf(e.Header.P2Char)})", 0, expand: true));

            var play = new Button { Text = "播放", CustomMinimumSize = new Vector2(PlayButtonWidth, 28) };
            string path = e.Path;
            play.Pressed += () => OpenReplay(path);
            line.AddChild(play);
            if (_firstPlay == null) _firstPlay = play;
        }

        // one-click delete, no confirmation (per spec)
        var del = new Button
        {
            Text = "🗑",
            CustomMinimumSize = new Vector2(DeleteButtonWidth, 28),
            TooltipText = "删除该回放（立即生效，无二次确认）",
        };
        del.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
        string delPath = e.Path;
        del.Pressed += () => { ReplayStore.Delete(delPath); Refresh(); };
        line.AddChild(del);

        return row;
    }

    private static Label Cell(string text, int minWidth, bool expand = false)
    {
        var l = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (expand)
        {
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        l.AddThemeFontSizeOverride("font_size", 15);
        return l;
    }

    private void OpenReplay(string path)
    {
        ReplayStore.PendingPath = path;
        GetTree().ChangeSceneToFile(PlayerScenePath);
    }
}
