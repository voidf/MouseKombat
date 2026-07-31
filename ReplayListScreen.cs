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
    [Export] public Label EmptyLabel;
    [Export] public Label CountLabel;

    private readonly List<ReplayStore.Entry> _entries = new();

    public override void _Ready()
    {
        Refresh();
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
        foreach (var e in _entries) Rows.AddChild(BuildRow(e));

        if (EmptyLabel != null) EmptyLabel.Visible = _entries.Count == 0;
        if (CountLabel != null)
        {
            int max = AppSettings.Instance?.ReplayMax ?? AppSettings.DefaultReplayMax;
            CountLabel.Text = $"共 {_entries.Count} 个回放（每种模式上限 {max}）";
        }
    }

    private Control BuildRow(ReplayStore.Entry e)
    {
        var row = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(0.12f, 0.13f, 0.17f, 0.92f) };
        style.SetCornerRadiusAll(4);
        style.ContentMarginLeft = 10; style.ContentMarginRight = 10;
        style.ContentMarginTop = 6; style.ContentMarginBottom = 6;
        row.AddThemeStyleboxOverride("panel", style);

        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 10);
        row.AddChild(line);

        line.AddChild(Cell(ReplayStore.ModeLabel(e.Mode), 70));

        if (e.Header == null)
        {
            var bad = Cell($"损坏：{e.Error}", 0, expand: true);
            bad.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.45f));
            line.AddChild(bad);
        }
        else
        {
            line.AddChild(Cell(ReplayStore.FormatBattleTime(e.Header.StartedUnixUtc), 140));
            line.AddChild(Cell(ReplayStore.FormatDuration(e.Header.FrameCount), 60));
            line.AddChild(Cell(ReplayStore.FormatSource(e), 120));

            string p1 = string.IsNullOrEmpty(e.Header.P1Name) ? "1P" : e.Header.P1Name;
            string p2 = string.IsNullOrEmpty(e.Header.P2Name) ? "2P" : e.Header.P2Name;
            var vs = Cell($"{p1} ({CharacterDb.NameOf(e.Header.P1Char)})  vs  "
                          + $"{p2} ({CharacterDb.NameOf(e.Header.P2Char)})", 0, expand: true);
            line.AddChild(vs);

            var play = new Button { Text = "播放", CustomMinimumSize = new Vector2(64, 28) };
            string path = e.Path;
            play.Pressed += () => OpenReplay(path);
            line.AddChild(play);
        }

        // one-click delete, no confirmation (per spec)
        var del = new Button
        {
            Text = "🗑",
            CustomMinimumSize = new Vector2(34, 28),
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
