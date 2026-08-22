using Godot;

// The FTG move editor screen (MKEditor.tscn): a Photoshop-like layout with the main option
// tabs on the left (fixed width, drag the splitter), the canvas on the top right and the
// timeline at the bottom right (fixed height, drag the splitter). The splitters keep their
// offsets while the window resizes, so the option width and timeline height stay put and the
// canvas absorbs the rest — the layout contract in the design doc.
//
// The editor deliberately runs OUTSIDE the game's stretch/aspect lock: window content scaling
// is disabled for the scene's lifetime so every font and control renders at true pixel size,
// and AspectLock is suspended so the user can freely resize the window (both restored on exit).
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
    private Window.ContentScaleModeEnum _savedScaleMode;
    private Window.ContentScaleAspectEnum _savedScaleAspect;
    private double _playAcc;      // 60 fps playback accumulator (render fps != logic fps)
    private ConfirmationDialog _exitDialog;

    public override void _Ready()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        // ---- true-pixel UI: drop the game-wide stretch while the editor is open ----
        var win = GetWindow();
        _savedScaleMode = win.ContentScaleMode;
        _savedScaleAspect = win.ContentScaleAspect;
        win.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
        win.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
        if (AspectLock.Instance != null) AspectLock.Instance.Suspended = true;

        _project = EditorProject.LoadDefault();

        var split = new HSplitContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(split);

        // ---- left: the five tabs (fixed width, splitter-draggable) ----
        _tabs = new EditorTabs
        {
            CustomMinimumSize = new Vector2(300, 0),
        };
        split.AddChild(_tabs);

        // ---- right: canvas (expands) over the timeline (fixed height) ----
        var vSplit = new VSplitContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            CustomMinimumSize = new Vector2(320, 0),
        };
        split.AddChild(vSplit);

        _canvas = new EditorCanvas { Project = _project };
        vSplit.AddChild(_canvas);
        _timeline = new EditorTimeline { Project = _project };
        var timelineBox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 150),
        };
        timelineBox.AddChild(BuildTransport());
        _timeline.SizeFlagsVertical = SizeFlags.ExpandFill;
        timelineBox.AddChild(_timeline);
        vSplit.AddChild(timelineBox);

        // initial splitter offsets AFTER the first layout pass, or they get clamped away
        split.CallDeferred("set", "split_offset", 340);
        vSplit.CallDeferred("set", "split_offset", -(200 + 40));

        BuildTopRightBar();
        BuildStatusBar();
        BuildExitDialog();

        // ---- wire everything together ----
        _canvas.Project = _project;
        _timeline.Project = _project;
        _tabs.Project = _project;
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

    public override void _ExitTree()
    {
        // restore the game-wide presentation the rest of the app expects
        var win = GetWindow();
        if (win != null)
        {
            win.ContentScaleMode = _savedScaleMode;
            win.ContentScaleAspect = _savedScaleAspect;
        }
        if (AspectLock.Instance != null) AspectLock.Instance.Suspended = false;
    }

    private void BuildTopRightBar()
    {
        var bar = new HBoxContainer
        {
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 0,
            OffsetLeft = -128, OffsetRight = -8, OffsetTop = 6, OffsetBottom = 42,
        };
        AddChild(bar);
        var back = new Button { Text = "⟨ 返回主界面", CustomMinimumSize = new Vector2(120, 36) };
        back.Pressed += RequestExit;
        bar.AddChild(back);
    }

    private Control BuildTransport()
    {
        var bar = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

        _playButton = new Button { Text = "▶ 播放", CustomMinimumSize = new Vector2(84, 34) };
        _playButton.Pressed += () => { _timeline.TogglePlay(); UpdateTransport(); };
        _reverseButton = new Button { Text = "◀◀ 倒放", CustomMinimumSize = new Vector2(84, 34) };
        _reverseButton.Pressed += () => { _timeline.PlayReverse(); UpdateTransport(); };
        var prev = new Button { Text = "◀ 上一帧", CustomMinimumSize = new Vector2(84, 34) };
        prev.Pressed += () => { _timeline.StepFrame(-1); UpdateTransport(); };
        var next = new Button { Text = "下一帧 ▶", CustomMinimumSize = new Vector2(84, 34) };
        next.Pressed += () => { _timeline.StepFrame(1); UpdateTransport(); };
        _loopCheck = new CheckBox { Text = "循环" };
        _loopCheck.Toggled += v => _timeline.LoopPlayback = v;

        bar.AddChild(_playButton);
        bar.AddChild(_reverseButton);
        bar.AddChild(prev);
        bar.AddChild(next);
        bar.AddChild(_loopCheck);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var save = new Button { Text = "保存 Ctrl+S", CustomMinimumSize = new Vector2(104, 34) };
        save.Pressed += Save;
        bar.AddChild(save);
        var saveBack = new Button { Text = "保存并返回", CustomMinimumSize = new Vector2(104, 34) };
        saveBack.Pressed += () => { Save(); BackToMenu(); };
        bar.AddChild(saveBack);
        return bar;
    }

    private void BuildStatusBar()
    {
        var bar = new HBoxContainer
        {
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -28, OffsetBottom = -4, OffsetLeft = 8, OffsetRight = -8,
        };
        AddChild(bar);

        _undoLabel = new Label { Text = "0/50" };
        bar.AddChild(_undoLabel);
        bar.AddChild(new Label { Text = "  历史步数 " });
        var depth = new SpinBox { MinValue = 10, MaxValue = 500, Step = 10, Value = _project.UndoDepth,
            CustomMinimumSize = new Vector2(70, 22) };
        depth.ValueChanged += v => { _project.UndoDepth = (int)v; UpdateStatus(); };
        bar.AddChild(depth);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _statusLabel = new Label { Modulate = new Color(1f, 0.75f, 0.4f) };
        bar.AddChild(_statusLabel);
    }

    private void BuildExitDialog()
    {
        _exitDialog = new ConfirmationDialog
        {
            Title = "未保存的修改",
            DialogText = "有未保存的修改。保存并退出，还是直接退出？",
            OkButtonText = "保存并退出",
            CancelButtonText = "直接退出",
        };
        AddChild(_exitDialog);
        _exitDialog.Confirmed += () => { Save(); BackToMenu(); };
        _exitDialog.Canceled += BackToMenu;
    }

    private void RequestExit()
    {
        if (_project.Dirty) _exitDialog.PopupCentered();
        else BackToMenu();
    }

    private void BackToMenu() => GetTree().ChangeSceneToFile(MainMenuScenePath);

    // ---- refresh plumbing ----

    private void OnModelChanged()
    {
        _canvas.QueueRedraw();
        _timeline.InvalidateThumbnails();
        _timeline.QueueRedraw();
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
        _statusLabel.Text = _project.Dirty ? "● 未保存（Ctrl+S 保存）" : "";
        _undoLabel.Text = $"{_project.UndoCount}/{_project.UndoDepth}";
    }

    // ---- keyboard: undo / redo / save / play / exit ----

    private bool _spaceDown;
    private bool _spaceMoved;

    public override void _Process(double delta)
    {
        // fixed 60 fps playback regardless of the monitor's refresh rate (a 120 Hz screen
        // was advancing two timeline frames per render frame — visibly 2x speed)
        if (_timeline != null && _timeline.Playing)
        {
            _playAcc += delta;
            float step = 1f / 60f;
            while (_playAcc >= step)
            {
                _timeline.StepPlayback();
                _playAcc -= step;
            }
        }
        else _playAcc = 0;

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
        else if (k.Keycode == Key.Escape)
        {
            if (_timeline != null && _timeline.BrushRange != null)
            {
                _timeline.BrushRange = null;   // cancel range brushing first
                _timeline.QueueRedraw();
            }
            else RequestExit();
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
}
