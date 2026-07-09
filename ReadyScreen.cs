using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// Pre-fight lobby. A slot (P1/P2) can be claimed by a DEVICE (keyboard seat / gamepad, via its
// confirm key) or by an AI (backtick opens a menu listing the state-machine AI + every .onnx in
// /ai_rl_model). Backspace unbinds the "last" ready slot. Start: a bound human presses confirm, or
// — when both slots are AI — press Space. Bindings are handed to GameManager via GameSession.
public partial class ReadyScreen : Control
{
    [Export] public TextureRect P1Portrait;
    [Export] public TextureRect P2Portrait;
    [Export] public Label P1Status;   // "准备中"
    [Export] public Label P2Status;
    [Export] public Label P1Prompt;   // floating lock prompt
    [Export] public Label P2Prompt;
    [Export] public Label P1CancelHint; // device-specific "按 K 取消准备" (shown when P1 bound to a device)
    [Export] public Label P2CancelHint;
    [Export] public Label StartLabel; // bottom-center start prompt

    [Export] public string PromptText = "按手柄A / 键盘J / 数字键1 锁定该角色（~键选择AI）";
    [Export] public string StatusText = "准备中";
    [Export] public string StartText = "按确认键开始游戏";
    [Export] public string StartTextAi = "按空格键开始游戏"; // shown when both slots are AI
    [Export] public float BlinkInterval = 0.5f; // seconds per on/off phase
    [Export] public Color FreeTint = new Color(0.45f, 0.45f, 0.45f);
    [Export] public Color BoundTint = new Color(1, 1, 1);

    [Export] public string AiModelDir = "ai_rl_model"; // scanned for *.onnx (relative to res:// and the exe)

    private readonly List<IInputSource> _sources = new();
    private IInputSource _devP1, _devP2;   // human device bindings
    private IAgent _agP1, _agP2;           // AI bindings
    private string _aiNameP1 = "", _aiNameP2 = "";
    private double _blinkClock;

    private bool P1Bound => _devP1 != null || _agP1 != null;
    private bool P2Bound => _devP2 != null || _agP2 != null;
    private bool BothAi => _agP1 != null && _agP2 != null;

    // ---- AI menu (built programmatically; no .tscn changes needed) ----
    private bool _menuOpen;
    private int _menuSlot;   // 0 = P1, 1 = P2
    private int _menuIndex;
    private readonly List<(string name, bool isOnnx, string path)> _menuItems = new();
    private Control _menuRoot;
    private VBoxContainer _menuList;
    private Label _backspaceHint;

    public override void _Ready()
    {
        GameSession.Clear();

        _sources.Add(KeyboardSource.LeftSeat());
        _sources.Add(KeyboardSource.RightSeat());
        foreach (int dev in Input.GetConnectedJoypads())
            _sources.Add(new GamepadSource(dev));
        Input.JoyConnectionChanged += OnJoyConnectionChanged;

        if (P1Prompt != null) P1Prompt.Text = PromptText;
        if (P2Prompt != null) P2Prompt.Text = PromptText;
        if (P1Status != null) P1Status.Text = StatusText;
        if (P2Status != null) P2Status.Text = StatusText;
        if (StartLabel != null) StartLabel.Text = StartText;

        BuildMenuUi();
        BuildBackspaceHint();
        UpdatePresentation();
    }

    public override void _ExitTree()
    {
        Input.JoyConnectionChanged -= OnJoyConnectionChanged;
    }

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        string id = "pad" + device;
        if (connected)
        {
            if (_sources.Find(s => s.Id == id) == null)
                _sources.Add(new GamepadSource((int)device));
        }
        else
        {
            var src = _sources.Find(s => s.Id == id);
            if (src != null)
            {
                if (_devP1 == src) _devP1 = null;
                if (_devP2 == src) _devP2 = null;
                _sources.Remove(src);
            }
        }
    }

    // ---- keyboard: backtick (AI menu), backspace (unbind), space (AI start), menu nav ----
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        if (_menuOpen)
        {
            switch (k.Keycode)
            {
                case Key.Up: MoveMenu(-1); break;
                case Key.Down: MoveMenu(+1); break;
                case Key.Enter:
                case Key.KpEnter: ConfirmMenu(); break;
                case Key.Quoteleft:      // backtick — toggle closed
                case Key.Escape: CloseMenu(); break;
            }
            AcceptEvent();
            return;
        }

        switch (k.Keycode)
        {
            case Key.Quoteleft: OpenMenuForFreeSlot(); AcceptEvent(); break;
            case Key.Backspace: UnbindLast(); AcceptEvent(); break;
            case Key.Space:
                if (P1Bound && P2Bound) { StartGame(); AcceptEvent(); }
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _blinkClock += delta;

        foreach (var s in _sources) s.Poll();

        // device binding is suspended while the AI menu is open
        if (!_menuOpen)
        {
            bool bothBound = P1Bound && P2Bound;
            foreach (var s in _sources)
            {
                bool bound = s == _devP1 || s == _devP2;
                if (bound)
                {
                    if (bothBound && s.ConfirmJustPressed) { StartGame(); return; }
                    if (s.CancelJustPressed)
                    {
                        if (_devP1 == s) _devP1 = null;
                        else if (_devP2 == s) _devP2 = null;
                    }
                }
                else if (s.ConfirmJustPressed)
                {
                    if (!P1Bound) _devP1 = s;       // lowest free slot first
                    else if (!P2Bound) _devP2 = s;
                }
            }
        }

        UpdatePresentation();
    }

    // ---------- AI menu ----------
    private void OpenMenuForFreeSlot()
    {
        int slot = !P1Bound ? 0 : (!P2Bound ? 1 : -1);
        if (slot < 0) return; // both bound; nothing to assign an AI to
        _menuSlot = slot;
        _menuIndex = 0;
        BuildMenuItems();
        PopulateMenuList();
        _menuOpen = true;
        _menuRoot.Visible = true;
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        _menuRoot.Visible = false;
    }

    private void MoveMenu(int dir)
    {
        if (_menuItems.Count == 0) return;
        _menuIndex = Mathf.PosMod(_menuIndex + dir, _menuItems.Count);
        PopulateMenuList();
    }

    private void ConfirmMenu()
    {
        if (_menuItems.Count == 0) { CloseMenu(); return; }
        var item = _menuItems[_menuIndex];
        IAgent agent;
        string name;
        if (!item.isOnnx)
        {
            agent = new StateMachineAgent(_menuSlot);
            name = item.name;
        }
        else
        {
            // load the trained policy; on any failure fall back to the state machine so the
            // lobby never breaks on a bad/missing model file.
            try
            {
                string osPath = ProjectSettings.GlobalizePath(item.path);
                agent = new OnnxAgent(osPath);
                name = item.name;
            }
            catch (System.Exception e)
            {
                GD.PushError($"[ReadyScreen] failed to load ONNX {item.path}: {e.Message}; using state machine.");
                agent = new StateMachineAgent(_menuSlot);
                name = item.name + "(载入失败)";
            }
        }

        if (_menuSlot == 0) { _agP1 = agent; _devP1 = null; _aiNameP1 = name; }
        else { _agP2 = agent; _devP2 = null; _aiNameP2 = name; }

        CloseMenu();
    }

    private void BuildMenuItems()
    {
        _menuItems.Clear();
        _menuItems.Add(("状态机 AI", false, "")); // built-in
        foreach (var (name, path) in ScanOnnxModels())
            _menuItems.Add((name, true, path));
    }

    // Scan res://<dir> (editor) and the executable's <dir> (exported) for *.onnx.
    private IEnumerable<(string name, string path)> ScanOnnxModels()
    {
        var seen = new HashSet<string>();
        foreach (string root in new[] { "res://" + AiModelDir, OS.GetExecutablePath().GetBaseDir() + "/" + AiModelDir })
        {
            var dir = DirAccess.Open(root);
            if (dir == null) continue;
            dir.ListDirBegin();
            for (string f = dir.GetNext(); f != ""; f = dir.GetNext())
            {
                if (dir.CurrentIsDir()) continue;
                if (!f.ToLower().EndsWith(".onnx")) continue;
                if (seen.Add(f)) yield return (f, root + "/" + f);
            }
            dir.ListDirEnd();
        }
    }

    private void BuildMenuUi()
    {
        _menuRoot = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _menuRoot.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_menuRoot);

        var bg = new ColorRect { Color = new Color(0, 0, 0, 0.82f), Position = new Vector2(250, 165), Size = new Vector2(300, 270) };
        _menuRoot.AddChild(bg);

        var title = new Label { Text = "选择 AI  (↑↓ 选择 · 回车确定 · ~ 取消)", Position = new Vector2(262, 175), Size = new Vector2(276, 24) };
        _menuRoot.AddChild(title);

        _menuList = new VBoxContainer { Position = new Vector2(266, 210), Size = new Vector2(268, 210) };
        _menuRoot.AddChild(_menuList);
    }

    private void PopulateMenuList()
    {
        foreach (var c in _menuList.GetChildren()) c.QueueFree();
        for (int i = 0; i < _menuItems.Count; i++)
        {
            bool sel = i == _menuIndex;
            var l = new Label { Text = (sel ? "▶ " : "   ") + _menuItems[i].name };
            l.Modulate = sel ? new Color(1f, 0.9f, 0.3f) : new Color(0.8f, 0.8f, 0.8f);
            _menuList.AddChild(l);
        }
    }

    private void BuildBackspaceHint()
    {
        _backspaceHint = new Label { Visible = false, Position = new Vector2(210, 500), Size = new Vector2(380, 24) };
        _backspaceHint.Modulate = new Color(1f, 0.6f, 0.6f);
        AddChild(_backspaceHint);
    }

    // ---------- unbind ----------
    // both ready -> unbind P2; else unbind whichever is ready; none -> nothing.
    private int BackspaceTarget() => (P1Bound && P2Bound) ? 2 : (P1Bound ? 1 : (P2Bound ? 2 : 0));

    private void UnbindLast()
    {
        switch (BackspaceTarget())
        {
            case 1: _devP1 = null; _agP1 = null; _aiNameP1 = ""; break;
            case 2: _devP2 = null; _agP2 = null; _aiNameP2 = ""; break;
        }
    }

    // ---------- presentation ----------
    private void UpdatePresentation()
    {
        bool p1Bound = P1Bound;
        bool p2Bound = P2Bound;
        bool blinkOn = ((int)(_blinkClock / Mathf.Max(0.05f, BlinkInterval))) % 2 == 0;

        if (P1Portrait != null) P1Portrait.Modulate = p1Bound ? BoundTint : FreeTint;
        if (P2Portrait != null) P2Portrait.Modulate = p2Bound ? BoundTint : FreeTint;

        if (P1Status != null) { P1Status.Visible = p1Bound; P1Status.Text = _agP1 != null ? _aiNameP1 : StatusText; }
        if (P2Status != null) { P2Status.Visible = p2Bound; P2Status.Text = _agP2 != null ? _aiNameP2 : StatusText; }

        // lock prompt only under the lowest free slot; blinks.
        if (P1Prompt != null) P1Prompt.Visible = !p1Bound && blinkOn;
        if (P2Prompt != null) P2Prompt.Visible = p1Bound && !p2Bound && blinkOn;

        // device-specific cancel hint under a device-bound slot; no blink. (AI slots use backspace.)
        SetCancelHint(P1CancelHint, _devP1);
        SetCancelHint(P2CancelHint, _devP2);

        // start prompt when both bound; blinks; text depends on whether both are AI.
        if (StartLabel != null)
        {
            StartLabel.Text = BothAi ? StartTextAi : StartText;
            StartLabel.Visible = p1Bound && p2Bound && blinkOn;
        }

        // blinking global backspace hint naming the slot it would unbind
        if (_backspaceHint != null)
        {
            int t = BackspaceTarget();
            if (t == 0) _backspaceHint.Visible = false;
            else
            {
                _backspaceHint.Text = $"按 Backspace 键取消准备 {(t == 1 ? "P1" : "P2")}";
                _backspaceHint.Visible = blinkOn;
            }
        }
    }

    private static void SetCancelHint(Label l, IInputSource src)
    {
        if (l == null) return;
        if (src == null) { l.Visible = false; return; }
        l.Text = "按 " + src.CancelLabel + " 取消准备";
        l.Visible = true;
    }

    private void StartGame()
    {
        GameSession.P1 = _devP1;
        GameSession.P2 = _devP2;
        GameSession.P1Agent = _agP1;
        GameSession.P2Agent = _agP2;
        GameSession.Configured = true;
        GetTree().ChangeSceneToFile("res://MFEntry.tscn");
    }
}
