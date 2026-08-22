using Godot;

// The FTG move editor screen (MKEditor.tscn): a Photoshop-like layout with the main option
// tabs on the left (fixed width, drag the splitter), the canvas on the top right and the
// timeline at the bottom right (fixed height, drag the splitter). The splitters keep their
// offsets while the window resizes, so the option width and timeline height stay put and the
// canvas absorbs the rest — the layout contract in the design doc.
public partial class MKEditorScreen : Control
{
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";

    private EditorProject _project;
    private EditorCanvas _canvas;
    private EditorTimeline _timeline;
    private EditorTabs _tabs;
    private Button _playButton, _reverseButton;
    private CheckBox _loopCheck;
    private Label _statusLabel, _undoLabel;

    public override void _Ready()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        _project = EditorProject.LoadDefault();

        var split = new HSplitContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(split);

        // ---- left: the five tabs, fixed width (drag the splitter to change) ----
        _tabs = new EditorTabs
        {
            CustomMinimumSize = new Vector2(340, 0),
            Project = _project,
        };
        split.AddChild(_tabs);
        split.SplitOffset = 340;

        // ---- right: canvas (expands) over the timeline (fixed height) ----
        var vSplit = new VSplitContainer { AnchorRight = 1f, AnchorBottom = 1f };
        split.AddChild(vSplit);

        _canvas = new EditorCanvas { Project = _project };
        vSplit.AddChild(_canvas);
        _timeline = new EditorTimeline { Project = _project };
        var timelineBox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 168),
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        timelineBox.AddChild(BuildTransport());
        _timeline.SizeFlagsVertical = SizeFlags.ExpandFill;
        timelineBox.AddChild(_timeline);
        vSplit.AddChild(timelineBox);
        vSplit.SplitOffset = -(168 + 40);   // collapsed offset measured from the bottom

        BuildStatusBar();

        // ---- wire everything together ----
        _canvas.Project = _project;
        _timeline.Project = _project;
        _tabs.Canvas = _canvas;
        _canvas.Changed += OnModelChanged;
        _timeline.Changed += OnModelChanged;
        _timeline.PlaybackToggled += UpdateTransport;
        _tabs.Changed += OnModelChanged;
        _tabs.StructureChanged += OnStructureChanged;
        _tabs.BrushTarget += r =>
        {
            _timeline.BrushRange = r;
            _timeline.QueueRedraw();
        };

        OnStructureChanged();
    }

    private Control BuildTransport()
    {
        var bar = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

        _playButton = new Button { Text = "▶ 播放", CustomMinimumSize = new Vector2(88, 36) };
        _playButton.Pressed += () => { _timeline.TogglePlay(); UpdateTransport(); };
        _reverseButton = new Button { Text = "◀◀ 倒放", CustomMinimumSize = new Vector2(88, 36) };
        _reverseButton.Pressed += () => { _timeline.PlayReverse(); UpdateTransport(); };
        var prev = new Button { Text = "◀ 上一帧", CustomMinimumSize = new Vector2(88, 36) };
        prev.Pressed += () => { _timeline.StepFrame(-1); UpdateTransport(); };
        var next = new Button { Text = "下一帧 ▶", CustomMinimumSize = new Vector2(88, 36) };
        next.Pressed += () => { _timeline.StepFrame(1); UpdateTransport(); };
        _loopCheck = new CheckBox { Text = "循环" };
        _loopCheck.Toggled += v => _timeline.LoopPlayback = v;

        bar.AddChild(_playButton);
        bar.AddChild(_reverseButton);
        bar.AddChild(prev);
        bar.AddChild(next);
        bar.AddChild(_loopCheck);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var back = new Button { Text = "保存并返回", CustomMinimumSize = new Vector2(110, 36) };
        back.Pressed += SaveAndBack;
        bar.AddChild(back);
        var save = new Button { Text = "保存", CustomMinimumSize = new Vector2(70, 36) };
        save.Pressed += Save;
        bar.AddChild(save);
        return bar;
    }

    private void BuildStatusBar()
    {
        var bar = new HBoxContainer
        {
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -30, OffsetBottom = -4, OffsetLeft = 8, OffsetRight = -8,
        };
        AddChild(bar);

        _undoLabel = new Label { Text = "0/50" };
        bar.AddChild(_undoLabel);
        bar.AddChild(new Label { Text = "  历史步数 " });
        var depth = new SpinBox { MinValue = 10, MaxValue = 500, Step = 10, Value = _project.UndoDepth,
            CustomMinimumSize = new Vector2(70, 24) };
        depth.ValueChanged += v => { _project.UndoDepth = (int)v; UpdateStatus(); };
        bar.AddChild(depth);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _statusLabel = new Label { Modulate = new Color(1f, 0.8f, 0.5f) };
        bar.AddChild(_statusLabel);
    }

    // ---- refresh plumbing ----

    private void OnModelChanged()
    {
        _canvas.QueueRedraw();
        _timeline.InvalidateThumbnails();
        _timeline.QueueRedraw();
        // constants rows read live objects, so values update on rebuild only; structural
        // changes go through OnStructureChanged
        UpdateStatus();
    }

    private void OnStructureChanged()
    {
        _tabs.RebuildAll();
        _timeline.InvalidateThumbnails();
        _timeline.FrameChangedExternally();
        _canvas.ResetView();
        _canvas.QueueRedraw();
        UpdateStatus();
    }

    private void UpdateTransport()
    {
        _playButton.Text = _timeline.Playing
            ? (_timeline.ReversePlayback ? "❚❚ 倒放中" : "❚❚ 暂停")
            : "▶ 播放";
        _loopCheck.ButtonPressed = _timeline.LoopPlayback;
    }

    private void UpdateStatus()
    {
        string dirty = _project.Dirty ? " ● 未保存" : "";
        var hero = HeroLibrary.Instance;
        _statusLabel.Text = dirty;
        _undoLabel.Text = $"{_project.UndoCount}/{_project.UndoDepth}";
    }

    // ---- keyboard: undo / redo / save / space-play ----

    private bool _spaceDown;
    private bool _spaceMoved;

    public override void _Process(double delta)
    {
        if (_timeline != null) _timeline.StepPlayback();

        bool space = Input.IsKeyPressed(Key.Space);
        if (space && !_spaceDown)
        {
            _spaceDown = true;
            _spaceMoved = false;
        }
        if (space && _canvas != null && _canvas.IsDragging) _spaceMoved = true;
        if (!space && _spaceDown)
        {
            _spaceDown = false;
            if (!_spaceMoved && _timeline != null)
            {
                _timeline.TogglePlay();
                UpdateTransport();
            }
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        if (k.CtrlPressed && k.Keycode == Key.Z)
        {
            if (_project.Undo()) AfterHistory();
            GetViewport().SetInputAsHandled();
        }
        else if (k.CtrlPressed && k.Keycode == Key.Y)
        {
            if (_project.Redo()) AfterHistory();
            GetViewport().SetInputAsHandled();
        }
        else if (k.CtrlPressed && k.Keycode == Key.S)
        {
            Save();
            GetViewport().SetInputAsHandled();
        }
        else if (k.Keycode == Key.Escape && _timeline != null && _timeline.BrushRange != null)
        {
            _timeline.BrushRange = null;   // cancel range brushing
            _timeline.QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
    }

    private void AfterHistory()
    {
        _tabs.RebuildAll();
        _timeline.InvalidateThumbnails();
        _timeline.FrameChangedExternally();
        _canvas.QueueRedraw();
        UpdateStatus();
    }

    private void Save()
    {
        _project.SaveAll();
        UpdateStatus();
    }

    private void SaveAndBack()
    {
        _project.SaveAll();
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }
}
