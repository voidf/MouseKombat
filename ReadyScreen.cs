using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// Pre-fight lobby. Flow per seat (P1 = 0, P2 = 1):
//
//   Seats  ── device confirm ──▶ CharSelect(that device) ── confirm ──▶ seat bound, back to Seats
//     │                              └─ cancel ──▶ seat released, back to Seats
//     └───── backtick ──────▶ CharSelect(keyboard, AI) ── Enter ──▶ AiSelect ── Enter ──▶ bound
//                                    └─ ` ──▶ released          └─ ` ──▶ back to CharSelect
//
// While a panel is open EVERY other device is ignored, so two players cannot fight over the same
// seat mid-selection. The seat being chosen for counts as occupied while its panel is open.
//
// Start: a bound HUMAN device presses confirm, or — when both seats are AI — press Space.
// Esc leaves for the main menu (期3); today it is a no-op placeholder.
// Choices are handed to GameManager through GameSession.
public partial class ReadyScreen : Control
{
    [Export] public TextureRect P1Portrait;
    [Export] public TextureRect P2Portrait;
    [Export] public Label P1Label;     // "P1 · 仓鼠" — the character name half is filled in here
    [Export] public Label P2Label;
    [Export] public Label P1Status;   // "准备中"
    [Export] public Label P2Status;
    [Export] public Label P1Prompt;   // floating lock prompt
    [Export] public Label P2Prompt;
    [Export] public Label P1CancelHint; // device-specific "按 K 取消准备" (shown when P1 bound to a device)
    [Export] public Label P2CancelHint;
    [Export] public Label StartLabel; // bottom-center start prompt

    [Export] public string PromptText = "按手柄A / 键盘J / 数字键1 占用该机位（~键交给AI）";
    [Export] public string StatusText = "准备中";
    [Export] public string StartText = "按确认键开始游戏";
    [Export] public string StartTextAi = "按空格键开始游戏"; // shown when both slots are AI
    [Export] public float BlinkInterval = 0.5f; // seconds per on/off phase
    [Export] public Color FreeTint = new Color(0.45f, 0.45f, 0.45f);
    [Export] public Color BoundTint = new Color(1, 1, 1);

    [Export] public string AiModelDir = "ai_rl_model"; // scanned for *.onnx (relative to res:// and the exe)

    private enum LobbyState { Seats, CharSelect, AiSelect }
    private LobbyState _state = LobbyState.Seats;

    private const int SeatCount = 2;

    // Per-seat choices. Index 0 = P1, 1 = P2. A seat is BOUND once it has a device or an agent.
    private readonly IInputSource[] _dev = new IInputSource[SeatCount];
    private readonly IAgent[] _agent = new IAgent[SeatCount];
    private readonly string[] _aiName = new string[SeatCount];
    private readonly CharacterId[] _char = new CharacterId[SeatCount];

    // Seat currently being configured (-1 = none). Counts as occupied so nobody else can claim it.
    private int _busySeat = -1;

    private readonly List<IInputSource> _sources = new();
    private IInputSource _menuNav;      // keyboard arrows/Enter/backtick — drives the AI seat's panels
    private double _blinkClock;

    private bool Bound(int seat) => _dev[seat] != null || _agent[seat] != null;
    private bool Occupied(int seat) => Bound(seat) || _busySeat == seat;
    private bool BothBound => Bound(0) && Bound(1);
    private bool BothAi => _agent[0] != null && _agent[1] != null;

    // ---- AI model menu (built programmatically; no .tscn changes needed) ----
    private int _menuIndex;
    private readonly List<(string name, bool isOnnx, string path)> _menuItems = new();
    private Control _menuRoot;
    private VBoxContainer _menuList;
    private Label _backspaceHint;

    private CharSelect _charSelect;

    public override void _Ready()
    {
        GameSession.Clear();

        // default picks: the roster order, so P1/P2 start on different characters
        _char[0] = CharacterDb.All[0].Id;
        _char[1] = CharacterDb.All[Mathf.Min(1, CharacterDb.All.Length - 1)].Id;

        _sources.Add(KeyboardSource.LeftSeat());
        _sources.Add(KeyboardSource.RightSeat());
        foreach (int dev in Input.GetConnectedJoypads())
            _sources.Add(new GamepadSource(dev));
        Input.JoyConnectionChanged += OnJoyConnectionChanged;

        _menuNav = KeyboardSource.MenuSeat();

        if (P1Prompt != null) P1Prompt.Text = PromptText;
        if (P2Prompt != null) P2Prompt.Text = PromptText;
        if (P1Status != null) P1Status.Text = StatusText;
        if (P2Status != null) P2Status.Text = StatusText;
        if (StartLabel != null) StartLabel.Text = StartText;

        _charSelect = new CharSelect();
        AddChild(_charSelect);
        _charSelect.Confirmed += OnCharConfirmed;
        _charSelect.Cancelled += OnCharCancelled;

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
            return;
        }

        var src = _sources.Find(s => s.Id == id);
        if (src == null) return;
        _sources.Remove(src);

        // unplugged while it was driving a panel: bail out, or the seat stays busy forever
        if (ReferenceEquals(src, _panelNav)) { AbortSelection(); return; }
        for (int seat = 0; seat < SeatCount; seat++)
            if (_dev[seat] == src) ReleaseSeat(seat);
    }

    private IInputSource _panelNav;   // device driving the currently open panel

    // ---- keyboard: backtick (hand a seat to an AI), backspace (unbind), space (AI start) ----
    // Panel navigation runs off IInputSource in _PhysicsProcess, not here, so that a gamepad and a
    // keyboard seat share one code path.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        if (_state == LobbyState.AiSelect)
        {
            switch (k.Keycode)
            {
                case Key.Up: MoveMenu(-1); break;
                case Key.Down: MoveMenu(+1); break;
                case Key.Enter:
                case Key.KpEnter: ConfirmMenu(); break;
                case Key.Quoteleft: BackToCharSelectFromAi(); break;
                case Key.Escape: AbortSelection(); break;
            }
            AcceptEvent();
            return;
        }

        if (_state == LobbyState.CharSelect)
        {
            // Esc is a hard bail-out for a stuck panel; everything else belongs to the owning device
            if (k.Keycode == Key.Escape) { AbortSelection(); AcceptEvent(); }
            return;
        }

        switch (k.Keycode)
        {
            case Key.Quoteleft: BeginAiSeat(); AcceptEvent(); break;
            case Key.Backspace: UnbindLast(); AcceptEvent(); break;
            case Key.Space:
                if (BothBound) { StartGame(); AcceptEvent(); }
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _blinkClock += delta;

        foreach (var s in _sources) s.Poll();
        _menuNav.Poll();

        switch (_state)
        {
            case LobbyState.Seats: TickSeats(); break;
            case LobbyState.CharSelect: _charSelect.Tick(); break;
            case LobbyState.AiSelect: break; // driven by _Input
        }

        UpdatePresentation();
    }

    // Seat claiming / releasing / starting. Only reached while no panel is open, which is how the
    // "lock out every other device during selection" rule is enforced.
    private void TickSeats()
    {
        foreach (var s in _sources)
        {
            int seat = SeatOf(s);
            if (seat >= 0)
            {
                if (BothBound && s.ConfirmJustPressed) { StartGame(); return; }
                if (s.CancelJustPressed) ReleaseSeat(seat);
                continue;
            }

            if (!s.ConfirmJustPressed) continue;
            int free = FreeSeat();
            if (free < 0) continue;
            BeginCharSelect(free, s, aiMode: false);
            return;   // one claim per frame: the panel now owns input
        }
    }

    private int SeatOf(IInputSource s)
    {
        for (int seat = 0; seat < SeatCount; seat++)
            if (_dev[seat] == s) return seat;
        return -1;
    }

    private int FreeSeat()
    {
        for (int seat = 0; seat < SeatCount; seat++)
            if (!Occupied(seat)) return seat;
        return -1;
    }

    // ---------- selection flow ----------
    private void BeginCharSelect(int seat, IInputSource nav, bool aiMode)
    {
        _busySeat = seat;
        _panelNav = nav;
        _state = LobbyState.CharSelect;
        CloseMenu();
        _charSelect.Open(seat, nav, _char[seat], aiMode);
    }

    private void BeginAiSeat()
    {
        int seat = FreeSeat();
        if (seat < 0) return;
        BeginCharSelect(seat, _menuNav, aiMode: true);
    }

    private bool AiSeatPending => _panelNav == _menuNav;

    private void OnCharConfirmed(int seat, CharacterId picked)
    {
        _char[seat] = picked;
        if (AiSeatPending)
        {
            // AI seat: character chosen, now pick which brain drives it
            _charSelect.Close();
            OpenAiMenu();
            return;
        }

        _dev[seat] = _panelNav;
        _agent[seat] = null;
        _aiName[seat] = "";
        FinishSelection();
    }

    private void OnCharCancelled(int seat)
    {
        ReleaseSeat(seat);
        FinishSelection();
    }

    // Leaves whatever panel is open and returns the seat to nobody. Used by Esc and by a gamepad
    // being unplugged mid-selection.
    private void AbortSelection()
    {
        if (_busySeat >= 0 && !Bound(_busySeat)) ReleaseSeat(_busySeat);
        FinishSelection();
    }

    private void FinishSelection()
    {
        _charSelect.Close();
        CloseMenu();
        _busySeat = -1;
        _panelNav = null;
        _state = LobbyState.Seats;
    }

    private void ReleaseSeat(int seat)
    {
        _dev[seat] = null;
        _agent[seat] = null;
        _aiName[seat] = "";
    }

    // ---------- AI model menu ----------
    private void OpenAiMenu()
    {
        _menuIndex = 0;
        BuildMenuItems();
        PopulateMenuList();
        _state = LobbyState.AiSelect;
        _menuRoot.Visible = true;
    }

    private void CloseMenu()
    {
        if (_menuRoot != null) _menuRoot.Visible = false;
    }

    private void BackToCharSelectFromAi()
    {
        CloseMenu();
        int seat = _busySeat;
        if (seat < 0) { AbortSelection(); return; }
        _state = LobbyState.CharSelect;
        _charSelect.Open(seat, _menuNav, _char[seat], aiMode: true);
    }

    private void MoveMenu(int dir)
    {
        if (_menuItems.Count == 0) return;
        _menuIndex = Mathf.PosMod(_menuIndex + dir, _menuItems.Count);
        PopulateMenuList();
    }

    private void ConfirmMenu()
    {
        int seat = _busySeat;
        if (seat < 0 || _menuItems.Count == 0) { AbortSelection(); return; }

        var item = _menuItems[_menuIndex];
        IAgent agent;
        string name;
        if (!item.isOnnx)
        {
            agent = new StateMachineAgent(seat);
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
                agent = new StateMachineAgent(seat);
                name = item.name + "(载入失败)";
            }
        }

        _agent[seat] = agent;
        _dev[seat] = null;
        _aiName[seat] = name;
        FinishSelection();
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

        var title = new Label { Text = "选择 AI  (↑↓ 选择 · 回车确定 · ~ 返回选角色)", Position = new Vector2(262, 175), Size = new Vector2(276, 44) };
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _menuRoot.AddChild(title);

        _menuList = new VBoxContainer { Position = new Vector2(266, 224), Size = new Vector2(268, 196) };
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
    private int BackspaceTarget() => (Bound(0) && Bound(1)) ? 2 : (Bound(0) ? 1 : (Bound(1) ? 2 : 0));

    private void UnbindLast()
    {
        int t = BackspaceTarget();
        if (t > 0) ReleaseSeat(t - 1);
    }

    // ---------- presentation ----------
    private void UpdatePresentation()
    {
        bool p1Bound = Bound(0);
        bool p2Bound = Bound(1);
        bool blinkOn = ((int)(_blinkClock / Mathf.Max(0.05f, BlinkInterval))) % 2 == 0;
        bool panelOpen = _state != LobbyState.Seats;

        // A seat shows NOTHING until its player has actually confirmed a character. Showing the
        // default roster picks would read as "P1 is already the hamster", which is a lie: _char[]
        // only holds where that seat's cursor will start.
        SetSeatPortrait(P1Portrait, 0, p1Bound);
        SetSeatPortrait(P2Portrait, 1, p2Bound);
        if (P1Label != null) P1Label.Text = p1Bound ? $"P1 · {CharacterDb.NameOf(_char[0])}" : "P1";
        if (P2Label != null) P2Label.Text = p2Bound ? $"P2 · {CharacterDb.NameOf(_char[1])}" : "P2";

        if (P1Status != null) { P1Status.Visible = p1Bound; P1Status.Text = _agent[0] != null ? _aiName[0] : StatusText; }
        if (P2Status != null) { P2Status.Visible = p2Bound; P2Status.Text = _agent[1] != null ? _aiName[1] : StatusText; }

        // lock prompt only under the lowest free slot; blinks; hidden while a panel is up.
        if (P1Prompt != null) P1Prompt.Visible = !panelOpen && !p1Bound && blinkOn;
        if (P2Prompt != null) P2Prompt.Visible = !panelOpen && p1Bound && !p2Bound && blinkOn;

        // device-specific cancel hint under a device-bound slot; no blink. (AI slots use backspace.)
        SetCancelHint(P1CancelHint, panelOpen ? null : _dev[0]);
        SetCancelHint(P2CancelHint, panelOpen ? null : _dev[1]);

        // start prompt when both bound; blinks; text depends on whether both are AI.
        if (StartLabel != null)
        {
            StartLabel.Text = BothAi ? StartTextAi : StartText;
            StartLabel.Visible = !panelOpen && p1Bound && p2Bound && blinkOn;
        }

        // blinking global backspace hint naming the slot it would unbind
        if (_backspaceHint != null)
        {
            int t = BackspaceTarget();
            if (panelOpen || t == 0) _backspaceHint.Visible = false;
            else
            {
                _backspaceHint.Text = $"按 Backspace 键取消准备 {(t == 1 ? "P1" : "P2")}";
                _backspaceHint.Visible = blinkOn;
            }
        }
    }

    private void SetSeatPortrait(TextureRect rect, int seat, bool picked)
    {
        if (rect == null) return;
        rect.Texture = picked ? CharacterDb.Get(_char[seat]).Portrait : null;
        rect.Modulate = picked ? BoundTint : FreeTint;
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
        GameSession.P1 = _dev[0];
        GameSession.P2 = _dev[1];
        GameSession.P1Agent = _agent[0];
        GameSession.P2Agent = _agent[1];
        GameSession.P1Char = _char[0];
        GameSession.P2Char = _char[1];
        GameSession.P1Name = "1P";
        GameSession.P2Name = "2P";
        GameSession.Configured = true;
        GetTree().ChangeSceneToFile("res://MFEntry.tscn");
    }
}
