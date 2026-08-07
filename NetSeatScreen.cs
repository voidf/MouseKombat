using Godot;
using System.Collections.Generic;
using MouseKombat.Net;
using MouseKombat.Sim;

// Networked seat select. Structurally different from ReadyScreen in one way that matters: here the
// local machine owns exactly ONE device and at most ONE seat, and everything else about the room
// arrives as an authoritative snapshot from the host. So this screen never decides who holds what —
// it sends a request and re-renders whatever comes back.
//
// The AI flow is host-only (PROTOCOL.md § Room state): the AI runs on the host's machine and its
// inputs enter the match as if the host had sent them, so a client is not offered the option at all.
//
// Esc: out of a select panel if one is open, else leave the room — which for the host also means
// telling everyone why before the socket closes.
public partial class NetSeatScreen : Control
{
    [Export] public string LanMenuScenePath = "res://LanMenu.tscn";
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";

    [Export] public VBoxContainer PlayerList;      // top-left roster
    [Export] public Label RoomIdLabel;             // lobby games only
    [Export] public Label TitleLabel;
    [Export] public Label HintLabel;
    [Export] public Button StartButton;

    // Per-seat nodes are resolved by path rather than exported as arrays. A C# Node[] export bound
    // from an array-of-NodePath literal in a .tscn is not something this project can verify without
    // running the editor, and a silently unbound export shows up as an invisible seat panel rather
    // than an error. Fixed paths inside this scene are unambiguous.
    private readonly Label[] _seatName = new Label[RoomState.SeatCount];
    private readonly TextureRect[] _seatPortrait = new TextureRect[RoomState.SeatCount];
    private readonly Label[] _seatStatus = new Label[RoomState.SeatCount];

    [Export] public string AiModelDir = "ai_rl_model";

    private enum Stage { Seats, CharSelect, AiSelect }
    private Stage _stage = Stage.Seats;

    private readonly List<IInputSource> _sources = new();
    private IInputSource _menuNav;      // arrows / Enter / backtick, for the host's AI flow
    private IInputSource _panelNav;     // device driving the open panel
    private CharSelect _charSelect;
    private int _pendingSeat = -1;      // seat the open panel is choosing for
    private bool _pendingIsAi;
    private CharacterId _cursor = CharacterId.Hamster;

    // AI model menu, same shape as ReadyScreen's
    private Control _aiRoot;
    private VBoxContainer _aiList;
    private int _aiIndex;
    private readonly List<(string name, bool isOnnx, string path)> _aiItems = new();

    private AcceptDialog _dropPopup;

    private NetSession Net => NetSession.Instance;

    private void ResolveSeatNodes()
    {
        for (int i = 0; i < RoomState.SeatCount; i++)
        {
            string root = $"Seats/P{i + 1}";
            _seatName[i] = GetNodeOrNull<Label>($"{root}/Name");
            _seatPortrait[i] = GetNodeOrNull<TextureRect>($"{root}/Portrait");
            _seatStatus[i] = GetNodeOrNull<Label>($"{root}/Status");
            if (_seatName[i] == null || _seatPortrait[i] == null || _seatStatus[i] == null)
                GD.PushWarning($"[NetSeat] seat {i} nodes missing under {root}");
        }
    }

    public override void _Ready()
    {
        ResolveSeatNodes();
        _sources.Add(KeyboardSource.LeftSeat());
        _sources.Add(KeyboardSource.RightSeat());
        foreach (int dev in Input.GetConnectedJoypads()) _sources.Add(new GamepadSource(dev));
        _menuNav = KeyboardSource.MenuSeat();

        _charSelect = new CharSelect();
        AddChild(_charSelect);
        _charSelect.Confirmed += OnCharConfirmed;
        _charSelect.Cancelled += OnCharCancelled;

        BuildAiMenu();
        BuildDropPopup();

        if (Net != null)
        {
            Net.RoomChanged += Render;
            Net.Disconnected += OnDisconnected;
            Net.MatchStarting += OnMatchStarting;
            Net.MatchEnded += OnMatchEnded;
        }
        Render();
    }

    public override void _ExitTree()
    {
        if (Net == null) return;
        Net.RoomChanged -= Render;
        Net.Disconnected -= OnDisconnected;
        Net.MatchStarting -= OnMatchStarting;
        Net.MatchEnded -= OnMatchEnded;
    }

    // ---- input ----

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        if (_stage == Stage.AiSelect)
        {
            switch (k.Keycode)
            {
                case Key.Up: MoveAi(-1); break;
                case Key.Down: MoveAi(+1); break;
                case Key.Enter:
                case Key.KpEnter: ConfirmAi(); break;
                case Key.Quoteleft: BackToCharSelect(); break;
                case Key.Escape: AbortPanel(); break;
            }
            AcceptEvent();
            return;
        }

        if (_stage == Stage.CharSelect)
        {
            if (k.Keycode == Key.Escape) { AbortPanel(); AcceptEvent(); }
            return;
        }

        switch (k.Keycode)
        {
            // host only: hand a seat to an AI
            case Key.Quoteleft:
                if (Net != null && Net.IsHost) { BeginAiSeat(); AcceptEvent(); }
                break;
            case Key.Escape:
                AcceptEvent();
                LeaveRoom();
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        foreach (var s in _sources) s.Poll();
        _menuNav.Poll();

        if (_stage == Stage.CharSelect) { _charSelect.Tick(); return; }
        if (_stage == Stage.AiSelect) return;      // driven by _Input
        TickSeats();
    }

    // One device, one seat. Confirm on a free seat claims it and opens the picker; cancel releases.
    private void TickSeats()
    {
        if (Net == null || Net.Room == null || Net.Room.MatchRunning) return;
        int mine = Net.LocalSeat();

        foreach (var s in _sources)
        {
            if (s.CancelJustPressed && mine >= 0) { Net.RequestReleaseSeat(); return; }
            if (!s.ConfirmJustPressed) continue;

            if (mine >= 0)
            {
                // already seated: confirm re-opens the character picker for our seat
                BeginCharSelect(mine, s, ai: false);
                return;
            }
            int free = FirstFreeSeat();
            if (free < 0) return;                  // both taken; we are a spectator
            Net.RequestClaimSeat(free);
            // The panel opens once the snapshot confirms we actually got it — the host decides, and a
            // simultaneous claim from someone else must not leave us picking for a seat we lost.
            _pendingSeat = free;
            _panelNav = s;
            return;
        }
    }

    private int FirstFreeSeat()
    {
        for (int i = 0; i < RoomState.SeatCount; i++)
            if (!Net.Seat(i).Occupied) return i;
        return -1;
    }

    // ---- panels ----

    private void BeginCharSelect(int seat, IInputSource nav, bool ai)
    {
        _pendingSeat = seat;
        _panelNav = nav;
        _pendingIsAi = ai;
        _stage = Stage.CharSelect;
        if (_aiRoot != null) _aiRoot.Visible = false;
        var start = Net.Seat(seat).Character >= 0 ? Net.Seat(seat).CharacterId : _cursor;
        _charSelect.Open(seat, nav, start, ai);
    }

    private void BeginAiSeat()
    {
        int seat = FirstFreeSeat();
        if (seat < 0) return;
        BeginCharSelect(seat, _menuNav, ai: true);
    }

    private void OnCharConfirmed(int seat, CharacterId picked)
    {
        _cursor = picked;
        if (_pendingIsAi)
        {
            _charSelect.Close();
            OpenAiMenu();
            return;
        }
        _charSelect.Close();
        _stage = Stage.Seats;
        Net.RequestPickCharacter(picked);
    }

    private void OnCharCancelled(int seat)
    {
        _charSelect.Close();
        _stage = Stage.Seats;
        if (!_pendingIsAi && Net.LocalSeat() == seat) Net.RequestReleaseSeat();
        _pendingSeat = -1;
    }

    private void AbortPanel()
    {
        _charSelect.Close();
        if (_aiRoot != null) _aiRoot.Visible = false;
        _stage = Stage.Seats;
        _pendingSeat = -1;
        _pendingIsAi = false;
        Render();
    }

    // ---- AI model menu (host only) ----

    private void BuildAiMenu()
    {
        _aiRoot = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_aiRoot);
        _aiRoot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = new Color(0.05f, 0.055f, 0.07f, 0.96f),
                                 Position = new Vector2(250, 165), Size = new Vector2(300, 270) };
        _aiRoot.AddChild(bg);
        var title = new Label { Text = "选择 AI  (↑↓ 选择 · 回车确定 · ~ 返回选角色)",
                                Position = new Vector2(262, 175), Size = new Vector2(276, 44),
                                AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _aiRoot.AddChild(title);
        _aiList = new VBoxContainer { Position = new Vector2(266, 224), Size = new Vector2(268, 196) };
        _aiRoot.AddChild(_aiList);
    }

    private void OpenAiMenu()
    {
        _aiItems.Clear();
        _aiItems.Add(("状态机 AI", false, ""));
        foreach (var (name, path) in ScanOnnx()) _aiItems.Add((name, true, path));
        _aiIndex = 0;
        _stage = Stage.AiSelect;
        _aiRoot.Visible = true;
        PopulateAi();
    }

    private IEnumerable<(string name, string path)> ScanOnnx()
    {
        var seen = new HashSet<string>();
        foreach (string root in new[] { "res://" + AiModelDir,
                                        OS.GetExecutablePath().GetBaseDir() + "/" + AiModelDir })
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

    private void PopulateAi()
    {
        foreach (var c in _aiList.GetChildren()) c.QueueFree();
        for (int i = 0; i < _aiItems.Count; i++)
        {
            bool sel = i == _aiIndex;
            var l = new Label { Text = (sel ? "▶ " : "   ") + _aiItems[i].name };
            l.Modulate = sel ? new Color(1f, 0.9f, 0.3f) : new Color(0.8f, 0.8f, 0.8f);
            _aiList.AddChild(l);
        }
    }

    private void MoveAi(int d)
    {
        if (_aiItems.Count == 0) return;
        _aiIndex = Mathf.PosMod(_aiIndex + d, _aiItems.Count);
        PopulateAi();
    }

    private void ConfirmAi()
    {
        if (_pendingSeat < 0 || _aiItems.Count == 0) { AbortPanel(); return; }
        var item = _aiItems[_aiIndex];
        _aiRoot.Visible = false;
        _stage = Stage.Seats;
        Net.RequestAddAi(_pendingSeat, _cursor, item.isOnnx ? item.path : "");
        _pendingSeat = -1;
        _pendingIsAi = false;
    }

    private void BackToCharSelect()
    {
        _aiRoot.Visible = false;
        if (_pendingSeat < 0) { AbortPanel(); return; }
        BeginCharSelect(_pendingSeat, _menuNav, ai: true);
    }

    // ---- room events ----

    private void OnMatchStarting(StartMatch setup)
    {
        // 期3-4(4/4) hooks the rollback session up here. Until then the flow stops at "ready".
        SetHint("局内同步（rollback）将在下一提交接入，本提交只完成房间与选人流程。");
    }

    private void OnMatchEnded(MatchEnded ended) => Render();

    private void OnDisconnected(string reason)
    {
        // Spec: when the host leaves, everyone else gets a popup and returns to the main menu on
        // confirm. Same treatment for any drop — the player needs to know it was not their own doing.
        _dropPopup.DialogText = string.IsNullOrEmpty(reason) ? "与房间的连接已断开。" : reason;
        _dropPopup.PopupCentered();
    }

    private void BuildDropPopup()
    {
        _dropPopup = new AcceptDialog { Title = "连接断开", OkButtonText = "确定", Exclusive = true };
        AddChild(_dropPopup);
        _dropPopup.Confirmed += () => GetTree().ChangeSceneToFile(MainMenuScenePath);
        _dropPopup.Canceled += () => GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    private void LeaveRoom()
    {
        if (Net == null) { GetTree().ChangeSceneToFile(LanMenuScenePath); return; }
        bool wasHost = Net.IsHost;
        Net.Leave(wasHost ? "主机已离开房间" : "玩家离开了房间");
        GetTree().ChangeSceneToFile(wasHost ? MainMenuScenePath : LanMenuScenePath);
    }

    public void OnStartPressed()
    {
        if (Net == null || !Net.IsHost || !Net.BothSeatsReady()) return;
        Net.RequestStartMatch(new StartMatch());
    }

    // ---- rendering ----

    private void Render()
    {
        var net = Net;
        if (net == null) return;

        // A pending claim only becomes a picker once the host confirms we hold that seat.
        if (_stage == Stage.Seats && _pendingSeat >= 0 && _panelNav != null
            && net.LocalSeat() == _pendingSeat)
        {
            int seat = _pendingSeat;
            var nav = _panelNav;
            _pendingSeat = -1;
            BeginCharSelect(seat, nav, ai: false);
        }

        if (TitleLabel != null)
            TitleLabel.Text = net.IsHost
                ? $"局域网房间（主持中） · 端口 {net.Port}"
                : $"局域网房间 · {net.HostAddress}:{net.Port}";

        if (RoomIdLabel != null)
        {
            string id = net.Room?.RoomId ?? "";
            RoomIdLabel.Visible = !string.IsNullOrEmpty(id);
            RoomIdLabel.Text = $"房间号 {id}";
        }

        RenderPlayers(net);
        for (int i = 0; i < RoomState.SeatCount; i++) RenderSeat(net, i);

        if (StartButton != null)
        {
            StartButton.Visible = net.IsHost;
            StartButton.Disabled = !net.BothSeatsReady();
        }
        SetHint(DefaultHint(net));
    }

    private void RenderPlayers(NetSession net)
    {
        if (PlayerList == null) return;
        foreach (var c in PlayerList.GetChildren()) c.QueueFree();
        if (net.Room == null) return;

        foreach (var p in net.Room.Players)
        {
            string tag = p.Seat >= 0 ? $"P{p.Seat + 1}" : "观战";
            string mark = p.IsHost ? " ★" : "";
            string state = p.Connected ? "" : "（掉线）";
            var l = new Label { Text = $"{p.Name}{mark} · {tag}{state}" };
            l.AddThemeFontSizeOverride("font_size", 14);
            if (p.PlayerId == net.LocalPlayerId)
                l.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.45f));
            else if (!p.Connected)
                l.AddThemeColorOverride("font_color", new Color(0.7f, 0.5f, 0.5f));
            PlayerList.AddChild(l);
        }
    }

    private void RenderSeat(NetSession net, int i)
    {
        var seat = net.Seat(i);
        bool picked = seat.Character >= 0;

        if (_seatPortrait[i] != null)
        {
            _seatPortrait[i].Texture = picked ? CharacterDb.Get(seat.CharacterId).Portrait : null;
            _seatPortrait[i].Modulate = picked ? Colors.White : new Color(0.45f, 0.45f, 0.45f);
        }
        if (_seatName[i] != null)
            _seatName[i].Text = picked
                ? $"P{i + 1} · {CharacterDb.NameOf(seat.CharacterId)}"
                : $"P{i + 1}";

        if (_seatStatus[i] == null) return;
        string who;
        if (seat.IsAi)
            who = string.IsNullOrEmpty(seat.AiModel) ? "状态机 AI" : seat.AiModel.GetFile();
        else if (seat.OccupantPlayerId != 0)
            who = NameOf(net, seat.OccupantPlayerId);
        else
            who = "空位";
        _seatStatus[i].Text = who;
    }

    private static string NameOf(NetSession net, int playerId)
    {
        if (net.Room == null) return "?";
        foreach (var p in net.Room.Players) if (p.PlayerId == playerId) return p.Name;
        return "?";
    }

    private string DefaultHint(NetSession net)
    {
        if (net.Room != null && net.Room.MatchRunning) return "对局进行中…";
        int mine = net.LocalSeat();
        var parts = new List<string>();
        parts.Add(mine >= 0 ? "确认键改角色 · 取消键让位" : "确认键占一个空位");
        if (net.IsHost) parts.Add("~ 键给空位加 AI");
        parts.Add(net.IsHost ? "Esc 关闭房间" : "Esc 离开房间");
        if (net.IsHost && !net.BothSeatsReady()) parts.Add("两个机位都选好角色后才能开始");
        return string.Join(" · ", parts);
    }

    private void SetHint(string text)
    {
        if (HintLabel != null) HintLabel.Text = text;
    }
}
