using Godot;
using System.Collections.Generic;
using System.Linq;
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
    [Export] public string LobbyMenuScenePath = "res://LobbyMenu.tscn";
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";
    [Export] public string MatchScenePath = "res://MFEntry.tscn";
    [Export] public string SpectateScenePath = "res://Spectate.tscn";

    // A mid-match joiner on this machine: the host answered with a catch-up package, so the next
    // physics tick switches to the spectate screen. A flag rather than an immediate scene change:
    // CatchUpReceived fires from the autoload's poll, and changing scene from inside another node's
    // tick is where transitions start doubling up.
    private bool _pendingSpectate;
    // Relay configuration: this machine is the host, holds no seat, and forwards between two client
    // fighters. It can still WATCH — the fighters report their confirmed inputs, and once the first
    // report (which carries the geometry) lands, this screen hands itself to the spectate screen.
    private bool _relayWaiting;

    // What to show while a match is running and this machine is NOT in it — a relay host, or a
    // spectator in a configuration that cannot be watched. Sticky, because DefaultHint runs on every
    // snapshot and would otherwise wipe a one-shot message a frame later.
    private string _matchNote = "";

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
    private AcceptDialog _padBusyPopup;
    private System.Threading.Mutex _padMutex;   // OS pad lock while OUR seat is driven by a pad

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
        Input.JoyConnectionChanged += OnJoyConnectionChanged;
        _menuNav = KeyboardSource.MenuSeat();

        _charSelect = new CharSelect();
        AddChild(_charSelect);
        _charSelect.Confirmed += OnCharConfirmed;
        _charSelect.Cancelled += OnCharCancelled;

        BuildAiMenu();
        BuildDropPopup();
        BuildPadBusyPopup();

        if (Net != null)
        {
            Net.RoomChanged += Render;
            Net.Disconnected += OnDisconnected;
            Net.MatchStarting += OnMatchStarting;
            Net.MatchEnded += OnMatchEnded;
            Net.CatchUpReceived += OnCatchUpReceived;
        }
        // A mid-match joiner connects from the LAN MENU, whose scene is still active when the host's
        // MatchCatchUp arrives — the event fires with no subscriber and would be lost. The buffer is
        // exactly for that window; drain it now that this screen exists.
        if (Net != null && Net.PendingCatchUp != null)
            OnCatchUpReceived(Net.PendingCatchUp);
        Render();
    }

    public override void _ExitTree()
    {
        Input.JoyConnectionChanged -= OnJoyConnectionChanged;
        System.Threading.Mutex m = _padMutex;
        _padMutex = null;
        PadLock.Release(ref m);
        if (Net == null) return;
        Net.RoomChanged -= Render;
        Net.Disconnected -= OnDisconnected;
        Net.MatchStarting -= OnMatchStarting;
        Net.MatchEnded -= OnMatchEnded;
        Net.CatchUpReceived -= OnCatchUpReceived;
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

        // A pad unplugged mid-selection would leave the panel owned by a ghost. Close it, and if
        // our seat was claimed with that pad, give the seat back so the room is not stuck.
        if (ReferenceEquals(src, _panelNav))
        {
            bool hadSeat = Net != null && Net.LocalSeat() >= 0;
            AbortPanel();
            if (hadSeat) Net?.RequestReleaseSeat();
            return;
        }
        if (Net != null && Net.LocalSeat() >= 0 && Net.LockedDevice(Net.LocalSeat()) == src)
            Net.RequestReleaseSeat();
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
            // host only: undo an AI seat (same muscle memory as ReadyScreen's backspace)
            case Key.Backspace:
                if (Net != null && Net.IsHost) { RemoveAiSeat(); AcceptEvent(); }
                break;
            case Key.Escape:
                AcceptEvent();
                LeaveRoom();
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pendingSpectate)
        {
            _pendingSpectate = false;
            GetTree().ChangeSceneToFile(SpectateScenePath);
            return;
        }

        // Relay host: wait for the fighters' first input report, then watch the match like any
        // mid-match joiner would (same spectate screen, same replay-then-follow flow).
        if (_relayWaiting && Net != null && Net.CatchUpReady)
        {
            _relayWaiting = false;
            BuildRelayCatchUp();
            _pendingSpectate = true;
            return;
        }

        foreach (var s in _sources) s.Poll();
        _menuNav.Poll();

        // A press the previous screen's menu consumed is still down (see PadGate): ignore it, so a
        // held A cannot instantly claim a seat in this freshly loaded room.
        bool anyHeld = false;
        foreach (var s in _sources)
            if (s is GamepadSource)
                anyHeld |= s.ConfirmHeld || s.CancelHeld || s.Left || s.Right || s.Up || s.Down;
        bool gateBlocked = PadGate.Blocked(anyHeld);

        if (_stage == Stage.CharSelect) { _charSelect.Tick(); return; }
        if (_stage == Stage.AiSelect) return;      // driven by _Input
        if (!gateBlocked) TickSeats();
    }

    // One device, one seat. Confirm on a free seat claims it and opens the picker; cancel releases.
    //
    // Two-instances-on-one-machine rule: an UNLOCKED gamepad may claim only while its window is
    // focused, because every instance on the machine reads every pad. The first press of the pad in
    // the focused window locks it to that window; from then on it works without focus (and later,
    // as the locked device of that window's in-match inputs) until the player cancels, which
    // releases both the seat and the lock. Keyboards need no gate: the OS already delivers each key
    // to exactly one window.
    private void TickSeats()
    {
        if (Net == null || Net.Room == null || Net.Room.MatchRunning) return;
        int mine = Net.LocalSeat();

        foreach (var s in _sources)
        {
            // only the device that claimed OUR seat may operate it — an unlocked pad pressing
            // B must not release a seat some keyboard claimed
            bool isOwner = mine >= 0 && Net.LockedDevice(mine) == s;

            if (s.CancelJustPressed && isOwner) { Net.RequestReleaseSeat(); return; }
            if (!s.ConfirmJustPressed) continue;

            if (isOwner)
            {
                // already seated: confirm re-opens the character picker for our seat
                BeginCharSelect(mine, s, ai: false);
                return;
            }
            if (mine >= 0) continue;   // our seat belongs to another device; stay quiet

            if (s is GamepadSource && !GetWindow().HasFocus()) continue;
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

    // host only: backspace removes the newest AI seat, mirroring ReadyScreen's unbind-last
    private void RemoveAiSeat()
    {
        if (Net == null || Net.Room == null || Net.Room.MatchRunning) return;
        for (int i = RoomState.SeatCount - 1; i >= 0; i--)
            if (Net.Seat(i).IsAi) { Net.RequestRemoveAi(i); return; }
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
            // AI flow keeps _pendingSeat: the AI-model menu needs it to place the AI.
            _charSelect.Close();
            OpenAiMenu();
            return;
        }
        _charSelect.Close();
        _stage = Stage.Seats;
        // MUST clear the pending claim here. Render() re-opens the picker when a pending claim is
        // confirmed by a snapshot, and this pick round-trips through a snapshot of its own — leaving
        // _pendingSeat set would re-open the panel a moment after the player confirmed a character.
        _pendingSeat = -1;
        _panelNav = null;
        Net.RequestPickCharacter(picked);
    }

    private void OnCharCancelled(int seat)
    {
        _charSelect.Close();
        _stage = Stage.Seats;
        _pendingSeat = -1;
        _panelNav = null;
        if (!_pendingIsAi && Net.LocalSeat() == seat) Net.RequestReleaseSeat();
    }

    private void AbortPanel()
    {
        // Esc from the picker means "give the seat up" — but only while the pick is still the FIRST
        // one. Once a character is chosen the seat counts as bound, and Esc on a re-pick keeps it,
        // exactly like ReadyScreen. AI seats are never claimed in RoomState until the model is
        // confirmed, so there is nothing to release for them.
        bool release = !_pendingIsAi && Net != null && Net.LocalSeat() == _pendingSeat
                       && Net.Seat(_pendingSeat).Character < 0;
        _charSelect.Close();
        if (_aiRoot != null) _aiRoot.Visible = false;
        _stage = Stage.Seats;
        _pendingSeat = -1;
        _panelNav = null;
        _pendingIsAi = false;
        Render();
        if (release) Net.RequestReleaseSeat();
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

    // The host decided the match is on. NetSession has already worked out what this machine does in it
    // (MatchPlan), so this only has to either enter the match scene or say why it is staying put.
    private void OnMatchStarting(StartMatch setup)
    {
        var net = Net;
        var plan = net?.Plan;
        if (plan == null) { _matchNote = "对局已开始，但本机未取得对局分配"; Render(); return; }

        switch (plan.Role)
        {
            case MatchRole.Fighter:
            case MatchRole.Spectator:
                EnterMatch(net, setup, plan);
                return;
            case MatchRole.Relay:
                // The host holds no seat and both fighters are clients, so it forwards their UDP and
                // has no simulation of its own to show (see UdpMatchRelay). It can still WATCH: the
                // fighters report their confirmed inputs, and once the first report lands this screen
                // hands itself to the spectate screen.
                _relayWaiting = true;
                _matchNote = "正在获取对局数据…";
                break;
            default:
                _matchNote = plan.Problem ?? "本机未参与本局";
                break;
        }
        Render();
    }

    private void EnterMatch(NetSession net, StartMatch setup, MatchPlan plan)
    {
        var room = setup.Room ?? net.Room;
        var s0 = room.Seats[0];
        var s1 = room.Seats[1];

        GameSession.Clear();
        GameSession.P1Char = s0.CharacterId;
        GameSession.P2Char = s1.CharacterId;
        GameSession.P1Name = SeatName(room, 0);
        GameSession.P2Name = SeatName(room, 1);
        GameSession.Mode = net.IsLobby ? ReplayData.ModeLobby : ReplayData.ModeLan;
        GameSession.RoomId = room.RoomId ?? "";
        GameSession.Host = $"{net.HostAddress}:{net.Port}";

        // Only the seats THIS machine drives get a device or an agent. Everything else arrives over the
        // wire, and the locked device is the one this window claimed the seat with (see PadLock).
        for (int seat = 0; seat < RoomState.SeatCount; seat++)
        {
            if (!plan.LocalSeat[seat]) continue;
            if (plan.AiSeat[seat]) SetSeatAgent(seat, room.Seats[seat].AiModel);
            else SetSeatDevice(seat, net.LockedDevice(seat));
        }

        GameSession.NetPlan = plan;
        GameSession.Configured = true;
        GetTree().ChangeSceneToFile(MatchScenePath);
    }

    private static void SetSeatDevice(int seat, IInputSource src)
    {
        if (seat == 0) GameSession.P1 = src; else GameSession.P2 = src;
    }

    // Built here rather than carried in the snapshot: the model is a path on the HOST's disk and the AI
    // only ever runs on the host, so nobody else could load it anyway.
    private static void SetSeatAgent(int seat, string model)
    {
        IAgent agent;
        if (string.IsNullOrEmpty(model)) agent = new StateMachineAgent(seat);
        else
        {
            // Fall back to the state machine on any load failure, same as ReadyScreen: a bad model file
            // must not strand the whole room in a match that cannot start.
            try { agent = new OnnxAgent(ProjectSettings.GlobalizePath(model)); }
            catch (System.Exception e)
            {
                GD.PushError($"[NetSeat] failed to load ONNX {model}: {e.Message}; using state machine.");
                agent = new StateMachineAgent(seat);
            }
        }
        if (seat == 0) GameSession.P1Agent = agent; else GameSession.P2Agent = agent;
    }

    // Whose name goes over that fighter's head. An AI seat shows the model rather than a player name.
    private static string SeatName(RoomSnapshot room, int seat)
    {
        var s = room.Seats[seat];
        if (s.IsAi) return string.IsNullOrEmpty(s.AiModel) ? "AI" : s.AiModel.GetFile();
        foreach (var p in room.Players)
            if (p.PlayerId == s.OccupantPlayerId) return p.Name;
        return $"{seat + 1}P";
    }

    // The host answered a mid-match join: this machine has no seat (seats are frozen while the match
    // runs), so the catch-up can only mean "you are a spectator — here is what has happened so far".
    // Package the history into GameSession and switch to the spectate screen.
    private void OnCatchUpReceived(MatchCatchUp cu)
    {
        if (Net == null || cu == null || cu.Room == null) return;
        if (Net.LocalSeat() >= 0) return;            // seated players are in the match, not watching it
        if (cu.Room.Seats.Length < RoomState.SeatCount) return;
        GD.Print($"[catchup] joiner received catch-up: {cu.FrameCount} frames, "
                 + $"match running={cu.Room.MatchRunning}");

        var d = new ReplayData
        {
            Mode = Net.IsLobby ? ReplayData.ModeLobby : ReplayData.ModeLan,
            P1Char = cu.Room.Seats[0].CharacterId,
            P2Char = cu.Room.Seats[1].CharacterId,
            P1Name = SeatName(cu.Room, 0),
            P2Name = SeatName(cu.Room, 1),
            StageMinX = cu.StageMinX,
            StageMaxX = cu.StageMaxX,
            WorldWidth = cu.WorldWidth,
            P1StartX = cu.P1StartX, P1StartY = cu.P1StartY,
            P2StartX = cu.P2StartX, P2StartY = cu.P2StartY,
            RoomId = cu.Room.RoomId ?? "",
            Host = $"{Net.HostAddress}:{Net.Port}",
        };
        d.P1Inputs.AddRange(cu.P1Inputs);
        d.P2Inputs.AddRange(cu.P2Inputs);
        GameSession.CatchUpData = d;
        GameSession.P1Name = d.P1Name;
        GameSession.P2Name = d.P2Name;
        GameSession.Mode = Net.IsLobby ? ReplayData.ModeLobby : ReplayData.ModeLan;
        GameSession.RoomId = d.RoomId;
        GameSession.Host = d.Host;
        _pendingSpectate = true;
        // Consumed: the package is now GameSession.CatchUpData and the spectate screen owns it. Left
        // in the autoload it would be re-consumed the next time this screen loads (a spectator
        // pressing ESC, or a match that has since moved thousands of frames on) and replay the match
        // from frame 0 forever.
        Net.ClearPendingCatchUp();
    }

    // Package the relay host's accumulated catch-up state (fighters' reports, geometry) into
    // GameSession and switch to the spectate screen — the same data a mid-match joiner receives over
    // the wire, read from the host's own buffer instead.
    private void BuildRelayCatchUp()
    {
        var net = Net;
        if (net?.Room == null) return;
        var room = net.Room;
        var d = new ReplayData
        {
            Mode = net.IsLobby ? ReplayData.ModeLobby : ReplayData.ModeLan,
            P1Char = room.Seats[0].CharacterId,
            P2Char = room.Seats[1].CharacterId,
            P1Name = SeatName(room, 0),
            P2Name = SeatName(room, 1),
            StageMinX = net.CatchUpStageMinX,
            StageMaxX = net.CatchUpStageMaxX,
            WorldWidth = net.CatchUpWorldWidth,
            P1StartX = net.CatchUpP1StartX,
            P1StartY = net.CatchUpP1StartY,
            P2StartX = net.CatchUpP2StartX,
            P2StartY = net.CatchUpP2StartY,
            RoomId = room.RoomId ?? "",
            Host = $"{net.HostAddress}:{net.Port}",
        };
        d.P1Inputs.AddRange(net.CatchUpHistory.P1Inputs);
        d.P2Inputs.AddRange(net.CatchUpHistory.P2Inputs);
        GameSession.CatchUpData = d;
        GameSession.P1Name = d.P1Name;
        GameSession.P2Name = d.P2Name;
        GameSession.Mode = net.IsLobby ? ReplayData.ModeLobby : ReplayData.ModeLan;
        GameSession.RoomId = d.RoomId;
        GameSession.Host = d.Host;
    }

    private void OnMatchEnded(MatchEnded ended)
    {
        _matchNote = "";
        _relayWaiting = false;
        Render();
    }

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

    private void BuildPadBusyPopup()
    {
        _padBusyPopup = new AcceptDialog
        {
            Title = "手柄已被占用",
            DialogText = "这个手柄已被另一个窗口的玩家占用，请等待其释放机位，或换一个手柄。",
            OkButtonText = "确定",
            Exclusive = true,
        };
        AddChild(_padBusyPopup);
    }

    private void ShowPadBusy()
    {
        if (_padBusyPopup != null && !_padBusyPopup.Visible) _padBusyPopup.PopupCentered();
    }

    private void LeaveRoom()
    {
        if (Net == null) { GetTree().ChangeSceneToFile(LanMenuScenePath); return; }
        if (Net.IsLobby)
        {
            // The room dies with its host player and everyone else is told (spec: 主持玩家 ESC/强退
            // 时其它玩家弹窗提示连接断开) — but THIS connection is not the room, and closing it would
            // throw the host back to the main menu and make it retype the whole lobby form. Both host
            // and member therefore keep the lobby connection and land on the room browser.
            Net.LeaveLobbyRoom(Net.IsHost ? "主持玩家已离开房间" : "玩家离开了房间");
            GetTree().ChangeSceneToFile(LobbyMenuScenePath);
            return;
        }
        bool wasHost = Net.IsHost;
        Net.Leave(wasHost ? "主机已离开房间" : "玩家离开了房间");
        GetTree().ChangeSceneToFile(wasHost ? MainMenuScenePath : LanMenuScenePath);
    }

    public void OnStartPressed()
    {
        if (Net == null || !Net.IsHost || !Net.BothSeatsReady()) return;

        // Refuse with the real reason rather than starting a match that cannot synchronize. The one
        // case this actually catches is a fighter that never announced a match port, which no amount of
        // waiting fixes (see MatchPlan).
        var preview = Net.PreviewPlan();
        if (preview != null && preview.Role == MatchRole.Idle)
        {
            SetHint(preview.Problem ?? "当前无法开始对局");
            return;
        }
        Net.RequestStartMatch(new StartMatch());
    }

    // ---- rendering ----

    private void Render()
    {
        var net = Net;
        if (net == null) return;

        // A pending claim only becomes a picker once the host confirms we hold that seat. Locking
        // the device here — not at the press — means the lock follows the host's decision: a claim
        // we lost never locks anything.
        if (_stage == Stage.Seats && _pendingSeat >= 0 && _panelNav != null
            && net.LocalSeat() == _pendingSeat)
        {
            int seat = _pendingSeat;
            var nav = _panelNav;
            _pendingSeat = -1;
            if (nav is GamepadSource gs)
            {
                // One pad, one window (see PadLock). Another window already holds this pad's OS
                // mutex: undo the claim with a "手柄已被占用" popup instead of sharing the device.
                _padMutex = PadLock.TryAcquire(gs.Device);
                if (_padMutex == null)
                {
                    ShowPadBusy();
                    net.RequestReleaseSeat();
                    return;
                }
            }
            Net.LockDevice(seat, nav);
            BeginCharSelect(seat, nav, ai: false);
        }

        // A seat we were locked to that is no longer ours (released, stolen, match ended) frees the
        // lock, so the pad can claim again from this window once it is focused.
        for (int i = 0; i < RoomState.SeatCount; i++)
        {
            if (Net.LockedDevice(i) != null && net.LocalSeat() != i)
            {
                Net.UnlockDevice(i);
                System.Threading.Mutex m = _padMutex;
                _padMutex = null;
                PadLock.Release(ref m);
            }
        }

        if (TitleLabel != null)
        {
            if (net.IsLobby)
            {
                string id = net.Room?.RoomId ?? "";
                TitleLabel.Text = net.IsHost
                    ? $"大厅房间（主持中） · 房间号 {id}"
                    : $"大厅房间 · 房间号 {id}";
            }
            else
            {
                TitleLabel.Text = net.IsHost
                    ? $"局域网房间（主持中） · 端口 {net.Port}"
                    : $"局域网房间 · {net.HostAddress}:{net.Port}";
            }
        }

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
            StartButton.Disabled = !net.BothSeatsReady() || (net.Room != null && net.Room.MatchRunning);
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
        if (net.Room != null && net.Room.MatchRunning)
            return string.IsNullOrEmpty(_matchNote) ? "对局进行中…" : _matchNote;
        int mine = net.LocalSeat();
        var parts = new List<string>();
        parts.Add(mine >= 0 ? "确认键改角色 · 取消键让位" : "确认键占一个空位");
        if (net.IsHost) parts.Add("~ 键给空位加 AI");
        bool hasAi = net.Room != null && net.Room.Seats.Any(s => s.IsAi);
        if (net.IsHost && hasAi) parts.Add("Backspace 取消 AI");
        parts.Add(net.IsHost ? "Esc 关闭房间" : "Esc 离开房间");
        if (net.IsHost && !net.BothSeatsReady()) parts.Add("两个机位都选好角色后才能开始");
        return string.Join(" · ", parts);
    }

    private void SetHint(string text)
    {
        if (HintLabel != null) HintLabel.Text = text;
    }
}
